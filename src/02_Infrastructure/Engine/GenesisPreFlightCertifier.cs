// ============================================================================
// GenesisPreFlightCertifier — v7.4.0 무할당 종합 점검 엔진(실전 점화 전 정교성 인증)
//
//  물리 이중 키(ValveWired ∧ LiveArmingWired)가 무장되기 직전 + 무장 지속 중 주기적으로, 시스템 전역의 가용
//  상태를 δ=0 로 채점(0~100점)하고 '단 1개 항목이라도 부적격이면' 무장을 fail-closed 로 거부(Veto)한다.
//  포렌식 에러 코드(실패 서브체크 비트마스크)를 남겨 어느 항목이 왜 막았는지 즉시 규명한다.
//
//  ── 4대 체크리스트(각 25점·서브체크 가중 합산) ────────────────────────────────
//   ① 브로커 소켓: IPC 결선 · RTT 지연 임계 이내 · rate-limit 여유분.
//   ② CPU/서멀   : P-Core 핀 그리드 지원·상주 · 서멀 안전홀드 미격발 · 유효주파수 안정.
//   ③ 계좌/VaR   : 증거금 여유(headroom>0) · VaR 리스크 밸브 미격발 · 공분산 특이점 퓨즈 미격발.
//   ④ 디스크/MMF : 로그 디스크 잔여 용량 · I/O 방어선 정상 · 페이지 폴트 홀드 미격발.
//
//  ★설계: Certify(in PreFlightInputs) 는 '순수 함수' — 입력 구조체(δ=0·주입)만으로 결정론적으로 채점한다.
//    입력 수집(RingTelemetry/LatencySnapshot/DriveInfo)은 콜드 샘플러가 담당해 이 엔진을 결정론·테스트가능·δ=0 으로 유지.
//    본 엔진은 IBroker·소켓·onSend 참조 0 — 오직 '점수/거부 판정'만 산출한다(liveArmed:false·발주 유발 경로 부재).
//    Veto 는 무장 술어 blockWhile 에 tighten-only 로 결선돼 무장을 '거둘' 뿐 부여하지 못한다.
//  ★불변: δ=0(Certify/accessor 힙 0·Volatile long) · 결정론(입력 순수 함수) · fail-closed(Score<100 → Veto·
//    미인증 기본값도 Veto) · liveArmed:false. ★한국색상은 UI 계층.
// ============================================================================
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>Pre-Flight 점검 입력(δ=0 블리터블 값타입·콜드 샘플러가 스냅샷에서 수집·주입). 단위 주석 참조.</summary>
    public readonly struct PreFlightInputs
    {
        // ① 브로커 소켓
        public readonly bool IpcWired;               // 브릿지 IPC 결선(핑퐁 활성)
        public readonly long RttMicros;              // 현재 IPC 왕복 지연(µs)
        public readonly long RateHeadroomPermille;   // rate-limit 여유 ‰(1000=만공, 0=고갈)
        // ② CPU/서멀
        public readonly bool CpuSupported;           // P-Core 그리드 지원(감지 성공)
        public readonly bool PCorePinned;            // 핀 상주(PinnedThreads>0)
        public readonly bool ThermalSafeHold;        // 서멀 스로틀 안전홀드 활성(true=부적격)
        public readonly long EffFreqPermille;        // 유효주파수 ‰(1000=최고)
        // ③ 계좌/VaR
        public readonly long MarginHeadroomRaw;      // 증거금 여유 = Budget-Reserved(raw 10^8)
        public readonly bool VarLimitBreached;       // VaR 리스크 밸브 격발(true=부적격)
        public readonly int  CovGuardState;          // 0 정상/1 정규화/2 특이점 퓨즈
        // ④ 디스크/MMF
        public readonly long DiskFreeMB;             // 로그 디스크 잔여(MB)
        public readonly long IoStatusCode;           // 0 정상/1 페이지 홀드/2 디스크 stall
        public readonly bool PageFaultHold;          // 페이지 폴트 스래시 홀드 활성(true=부적격)

        public PreFlightInputs(bool ipcWired, long rttMicros, long rateHeadroomPermille,
                               bool cpuSupported, bool pCorePinned, bool thermalSafeHold, long effFreqPermille,
                               long marginHeadroomRaw, bool varLimitBreached, int covGuardState,
                               long diskFreeMB, long ioStatusCode, bool pageFaultHold)
        {
            IpcWired = ipcWired; RttMicros = rttMicros; RateHeadroomPermille = rateHeadroomPermille;
            CpuSupported = cpuSupported; PCorePinned = pCorePinned; ThermalSafeHold = thermalSafeHold; EffFreqPermille = effFreqPermille;
            MarginHeadroomRaw = marginHeadroomRaw; VarLimitBreached = varLimitBreached; CovGuardState = covGuardState;
            DiskFreeMB = diskFreeMB; IoStatusCode = ioStatusCode; PageFaultHold = pageFaultHold;
        }
    }

    /// <summary>실전 점화 전 종합 점검 인증 엔진(δ=0·순수 함수 채점·fail-closed veto·포렌식 코드).</summary>
    public sealed class GenesisPreFlightCertifier
    {
        // 포렌식 비트마스크(실패 서브체크) — Veto 시 어느 조건이 막았는지 즉시 규명.
        public const long F_IPC_DOWN      = 1L << 0;
        public const long F_RTT_HIGH      = 1L << 1;
        public const long F_RATE_LOW      = 1L << 2;
        public const long F_CPU_UNSUP     = 1L << 3;
        public const long F_PIN_LOST      = 1L << 4;
        public const long F_THERMAL_HOLD  = 1L << 5;
        public const long F_FREQ_LOW      = 1L << 6;
        public const long F_MARGIN_LOW    = 1L << 7;
        public const long F_VAR_BREACH    = 1L << 8;
        public const long F_COV_FUSE      = 1L << 9;
        public const long F_DISK_LOW      = 1L << 10;
        public const long F_IO_ABNORMAL   = 1L << 11;
        public const long F_PAGE_HOLD     = 1L << 12;

        // 서브체크 가중치(합 100). 항목당 25.
        private const long W_IPC = 10, W_RTT = 8, W_RATE = 7;                 // ① 25
        private const long W_CPU = 5, W_PIN = 8, W_THERM = 7, W_FREQ = 5;     // ② 25
        private const long W_MARGIN = 10, W_VAR = 10, W_COV = 5;              // ③ 25
        private const long W_DISK = 10, W_IO = 8, W_PAGE = 7;                 // ④ 25

        private readonly long _rttMaxMicros;
        private readonly long _minRateHeadroomPermille;
        private readonly long _minEffFreqPermille;
        private readonly long _minMarginHeadroomRaw;
        private readonly long _minDiskFreeMB;

        private long _score;        // 0~100(최근 인증)
        private long _forensic;     // 실패 서브체크 비트마스크
        private long _certifyCount;
        private int  _certified;    // 1 = Score==100(전 항목 적격)

        /// <param name="rttMaxMicros">브로커 IPC RTT 허용 상한(µs·기본 5000=5ms).</param>
        /// <param name="minRateHeadroomPermille">rate-limit 최소 여유 ‰(기본 100=10%).</param>
        /// <param name="minEffFreqPermille">유효주파수 최소 ‰(기본 850).</param>
        /// <param name="minMarginHeadroomRaw">최소 증거금 여유(raw 10^8·기본 1=양수).</param>
        /// <param name="minDiskFreeMB">로그 디스크 최소 잔여(MB·기본 500).</param>
        public GenesisPreFlightCertifier(long rttMaxMicros = 5000, long minRateHeadroomPermille = 100,
                                         long minEffFreqPermille = 850, long minMarginHeadroomRaw = 1,
                                         long minDiskFreeMB = 500)
        {
            _rttMaxMicros = rttMaxMicros;
            _minRateHeadroomPermille = minRateHeadroomPermille;
            _minEffFreqPermille = minEffFreqPermille;
            _minMarginHeadroomRaw = minMarginHeadroomRaw;
            _minDiskFreeMB = minDiskFreeMB;
            // 미인증 기본값 = fail-closed(Veto 활성·Score 0·전 항목 실패 마스크).
            _forensic = F_IPC_DOWN | F_RTT_HIGH | F_RATE_LOW | F_CPU_UNSUP | F_PIN_LOST | F_THERMAL_HOLD | F_FREQ_LOW
                      | F_MARGIN_LOW | F_VAR_BREACH | F_COV_FUSE | F_DISK_LOW | F_IO_ABNORMAL | F_PAGE_HOLD;
        }

        public long Score        => Interlocked.Read(ref _score);
        public long ForensicCode => Interlocked.Read(ref _forensic);
        public long CertifyCount => Interlocked.Read(ref _certifyCount);
        /// <summary>전 항목 적격(Score==100). blockWhile 은 이 부정(!IsCertified)이 아니라 VetoActive 를 읽는다.</summary>
        public bool IsCertified  => Volatile.Read(ref _certified) == 1;
        /// <summary>★fail-closed 무장 거부 — Score<100(1개라도 부적격) 또는 미인증. blockWhile 이 tighten-only 로 읽어 무장 거부.</summary>
        public bool VetoActive   => Volatile.Read(ref _certified) == 0;

        /// <summary>1회 종합 인증(δ=0·순수 함수). 입력만으로 결정론 채점 → Score/Forensic/Certified 원자 갱신. 콜드 샘플러가 주기 호출.</summary>
        public long Certify(in PreFlightInputs x)
        {
            long score = 0, forensic = 0;

            // ① 브로커 소켓
            if (x.IpcWired) score += W_IPC; else forensic |= F_IPC_DOWN;
            if (x.IpcWired && x.RttMicros >= 0 && x.RttMicros <= _rttMaxMicros) score += W_RTT; else forensic |= F_RTT_HIGH;
            if (x.RateHeadroomPermille >= _minRateHeadroomPermille) score += W_RATE; else forensic |= F_RATE_LOW;
            // ② CPU/서멀
            if (x.CpuSupported) score += W_CPU; else forensic |= F_CPU_UNSUP;
            if (x.PCorePinned) score += W_PIN; else forensic |= F_PIN_LOST;
            if (!x.ThermalSafeHold) score += W_THERM; else forensic |= F_THERMAL_HOLD;
            if (x.EffFreqPermille >= _minEffFreqPermille) score += W_FREQ; else forensic |= F_FREQ_LOW;
            // ③ 계좌/VaR
            if (x.MarginHeadroomRaw >= _minMarginHeadroomRaw) score += W_MARGIN; else forensic |= F_MARGIN_LOW;
            if (!x.VarLimitBreached) score += W_VAR; else forensic |= F_VAR_BREACH;
            if (x.CovGuardState != 2) score += W_COV; else forensic |= F_COV_FUSE;
            // ④ 디스크/MMF
            if (x.DiskFreeMB >= _minDiskFreeMB) score += W_DISK; else forensic |= F_DISK_LOW;
            if (x.IoStatusCode == 0) score += W_IO; else forensic |= F_IO_ABNORMAL;
            if (!x.PageFaultHold) score += W_PAGE; else forensic |= F_PAGE_HOLD;

            Volatile.Write(ref _score, score);
            Volatile.Write(ref _forensic, forensic);
            Volatile.Write(ref _certified, score >= 100 ? 1 : 0);   // 전 서브체크 통과만 인증(결함 0)
            Interlocked.Increment(ref _certifyCount);
            return score;
        }

        /// <summary>강제 미인증화(즉시 fail-closed·정지/비상 시). 다음 Certify 에서 재평가.</summary>
        public void Invalidate(long forensicCode = 0)
        {
            Volatile.Write(ref _score, 0L);
            Volatile.Write(ref _forensic, forensicCode);
            Volatile.Write(ref _certified, 0);
        }
    }
}
