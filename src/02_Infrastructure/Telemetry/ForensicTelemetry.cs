// ============================================================================
// ForensicTelemetry — S26-c(Horizon B): 락-프리 단방향 텔레메트리 채널
//
//  복합 저널 플러시 펌프(백그라운드 단일 라이터)가 발행한 포렌식 계기(영속화 주기·CRC 무결성·
//  seqlock 커서·신선도 lag)를, UI 렌더링 스레드가 가변 주기로 '무할당·락-프리'로 읽어 /command
//  관제탑에 비춘다. 메인 금융 핫패스와 UI 스레드를 공간적으로 격리 — UI 는 코어를 절대 블록하지 않고,
//  경합 시 직전 프레임 값을 유지한다(비대칭 안전: 코어 우선).
//
//  ── seqlock(단일 라이터) ───────────────────────────────────────────────────
//  멀티바이트 값 타입은 원자 쓰기가 불가하므로 짝/홀 버전 커서로 보호한다. 라이터는 홀(쓰기 시작)→
//  payload→짝(완료) 순서로 발행하고, 리더는 v1(짝) 독출→payload 복사→v2 재독출→v1==v2 검증으로
//  torn read 를 격리한다(불일치 시 재시도). x64 전용 — TSO(store-store 무재배열) + Volatile 컴파일러
//  배리어로 충분(RejectionForensicAuditor 와 동일 검증된 idiom). volatile long 불법 → Volatile.Read/Write.
//
//  ── 거짓 공유(False Sharing) 격리 ──────────────────────────────────────────
//  라이터가 고빈도로 두드리는 Version 커서와 리더가 읽는 Data 영역을 '서로 다른 64B 캐시라인'에 배치하고,
//  앞뒤를 패딩해 이웃 핫 필드와의 캐시 무효화 바운싱을 물리적으로 차단한다(LayoutKind.Explicit·FieldOffset).
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>관제탑이 비추는 포렌식 계기 스냅샷(블리터블 값 타입 · 참조 0 · 힙 0).</summary>
    public readonly struct ForensicTelemetry
    {
        public readonly long PersistSeq;              // 누적 복합 저널 영속화 횟수
        public readonly long LastPersistMono;         // 마지막 영속화 단조 시각 — Stopwatch.GetTimestamp 단위(≠100ns)
        public readonly long ForensicSeqlockVersion;  // 감사기 ring seqlock 버전(쓰기 진행 커서)
        public readonly long FreshnessLagTicks;       // 챔피언 As-Of 신선도 lag — DateTime 100ns ticks(≠Stopwatch)
        public readonly int  ForensicCount;           // 현재 포렌식 레코드 수
        public readonly int  IntegrityScore;          // 0~100 (마지막 영속화 무결성)
        public readonly uint GenomeFp;                // ★FNV-1a 해시 저32비트 지문 — CRC32 아님(라벨 정직)
        public readonly uint ForensicFp;              // 〃 (포렌식 최신 레코드 지문)

        public ForensicTelemetry(long persistSeq, long lastPersistMono, long forensicSeqlockVersion,
                                 long freshnessLagTicks, int forensicCount, int integrityScore,
                                 uint genomeFp, uint forensicFp)
        {
            PersistSeq            = persistSeq;
            LastPersistMono       = lastPersistMono;
            ForensicSeqlockVersion = forensicSeqlockVersion;
            FreshnessLagTicks     = freshnessLagTicks;
            ForensicCount         = forensicCount;
            IntegrityScore        = integrityScore;
            GenomeFp              = genomeFp;
            ForensicFp            = forensicFp;
        }

        public bool   HasValue       => PersistSeq > 0 || ForensicSeqlockVersion > 0;
        public double FreshnessLagMs => FreshnessLagTicks / (double)TimeSpan.TicksPerMillisecond;
    }

    /// <summary>
    /// 단일 라이터 seqlock 채널 — 캐시라인 격리. Publish(코어 단일 라이터) / TryRead(UI 락-프리·무할당).
    /// </summary>
    public sealed class ForensicTelemetryChannel
    {
        private const int CacheLine = 64;

        // ★거짓 공유 차단 메커니즘은 '64B 정렬'이 아니라 '필드당 전용 패딩'이다(적대적 리뷰 정정).
        //   .NET 힙 정렬은 객체 베이스에만 보장되고 객체헤더(MT 8B)+오프셋만큼 밀려 _cell.Version 의 절대주소는
        //   캐시라인 내 비정렬(실측 오프셋 40/56 등)에 착지한다 — 즉 Version 은 라인 시작에 오지 않는다.
        //   그럼에도 (1) 선행 64B 패딩(0~63)이 Version(64~71)을 자기 라인에 고립시키고 (2) Version↔Data 사이
        //   56B 갭(72~127)이 둘을 별도 라인에 두므로, 비정렬이어도 어떤 Cell '내부' 필드도 Version 라인을
        //   공유하지 않는다. ⚠️ Cell '외부' 이웃 힙 객체와의 격리는 베이스 비정렬로 best-effort(보장 아님).
        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct Cell
        {
            [FieldOffset(CacheLine)]     public long             Version;
            [FieldOffset(CacheLine * 2)] public ForensicTelemetry Data;
        }

        // ★적대적 리뷰(회귀 방어) — 단일 라이터 전제는 현재 코드로 충족(펌프 Loop 스레드 + Join 후 최종 플러시만
        //   Publish, happens-before 분리)되나, 미래에 둘째 Publish 경로가 생기면 ver 경쟁으로 침묵 torn write 가
        //   난다(컴파일러 불검출). DEBUG 빌드에서 라이터 스레드 동일성을 박제해 회귀를 즉시 터뜨린다(릴리스 비용 0).
        private int _writerTid;

        private Cell _cell;

        /// <summary>현재 seqlock 커서(진단). 짝=안정, 홀=발행 중.</summary>
        public long Version => Volatile.Read(ref _cell.Version);

        /// <summary>
        /// 텔레메트리 발행 — '단일 라이터'(플러시 펌프 스레드)에서만 호출. 홀→payload→짝. 무할당(구조체 복사).
        /// </summary>
        public void Publish(in ForensicTelemetry t)
        {
            AssertSingleWriter();
            long ver = _cell.Version;                       // 단일 라이터 → 자기 마지막 값
            Volatile.Write(ref _cell.Version, ver + 1);     // 홀 — 발행 시작(리더 무효화)
            _cell.Data = t;                                  // payload 구조체 복사(x64 store 순서 보존)
            Volatile.Write(ref _cell.Version, ver + 2);     // 짝 — 발행 완료
        }

        /// <summary>
        /// 락-프리 무할당 독출 — UI 스레드. torn read 면 재시도(최대 8회), 끝내 경합이면 false 반환 +
        /// out=default 로 UI 가 직전 프레임을 유지(코어 무블록). out 한정자 → 박싱 0·힙 0.
        /// </summary>
        public bool TryRead(out ForensicTelemetry t)
        {
            for (int i = 0; i < 8; i++)
            {
                long v1 = Volatile.Read(ref _cell.Version);
                if ((v1 & 1L) == 0L)                         // 짝수 = 안정 스냅샷
                {
                    ForensicTelemetry local = _cell.Data;    // 구조체 복사(스택)
                    long v2 = Volatile.Read(ref _cell.Version);
                    if (v1 == v2) { t = local; return true; } // 두 독출 사이 발행 없음 → 일관
                }
                // 홀수(발행 중) 또는 v1≠v2(도중 발행) → 재시도
            }
            t = default;                                     // 경합 지속 → 직전 프레임 유지 신호(비블록)
            return false;
        }

        // DEBUG 전용 단일 라이터 회귀 가드 — 첫 Publish 스레드를 박제하고 이후 불일치 시 즉시 throw.
        // 릴리스에선 [Conditional("DEBUG")]로 호출 자체가 제거(핫패스 비용 0).
        [Conditional("DEBUG")]
        private void AssertSingleWriter()
        {
            int tid  = Environment.CurrentManagedThreadId;
            int prev = Interlocked.CompareExchange(ref _writerTid, tid, 0);
            if (prev != 0 && prev != tid)
                throw new InvalidOperationException(
                    "ForensicTelemetryChannel: 단일 라이터 위반 — 둘째 Publish 스레드 감지(seqlock torn write 위험).");
        }
    }
}
