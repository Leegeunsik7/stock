// ============================================================================
// KisVtsProtocolValidator — [KIS VTS · 추천 A] 실/모의 TR 도메인 핀 + REST 응답 정합 검증 + 프로토콜 이탈 인터락(비트 26)
//
//  KIS 는 실전/모의(VTS)를 TR ID 접두로 가른다 — 계좌 TR 'TTTC…'(실) ↔ 'VTTC…'(모의: VTTC0802U 매수 · VTTC0801U 매도 · VTTC0803U 정정취소 ·
//    VTTC8434R 잔고 · VTTC8908R 매수가능), 체결통보 H0STCNI0(실) ↔ H0STCNI9(모의). 시세(H0STCNT0) · PINGPONG 은 중립.
//  ★도메인 핀: 세션 도메인(모의/실)과 다른 도메인 TR 이 하나라도 보이면 = 프로토콜 이탈(설정 교차 · 라우팅 오류 · 위조) →
//    sticky Disarm(비트 26) + 격리 콜백 1회(호스트: 자식 매트릭스 KIS 슬롯 격리 = 미송출 회수 + 송출분 즉시 취소 · 세션 단절 보고).
//  REST: 요청 TR 도메인 핀 · 응답 tr_id 헤더 ≠ 요청 TR · 주문 응답 본문 형식 이탈(rt_cd 부재/깨진 JSON) = 이탈. rt_cd≠"0"(업무 거부)은 이탈 아님.
//  δ=0: 판정 경로는 ReadOnlySpan 스캔 + Interlocked 만(정상 경로 할당 0). 보안 로그는 '최초 이탈 1회'만 콜백 — 인자는 열거형 +
//    상수 라벨뿐(원문 프레임 · 토큰 · 계좌 · 본문을 절대 싣지 않는다 = Zero Protocol Leakage).
//  재무장 = 물리 이중키(Valve ∧ LiveArming) — 그 뒤 격리 콜백도 다시 1회 무장.
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Adapters
{
    public enum KisDivergenceKind : byte
    {
        None = 0,
        RealtimeDomain = 1,   // 실시간 프레임 TR 도메인 교차(VTS 세션에 H0STCNI0 등)
        SubscribeDomain = 2,  // 구독 응답 header.tr_id 도메인 교차(교차 도메인 복호 키 장착 거부)
        RestTrHeader = 3,     // REST 응답 tr_id 헤더 ≠ 요청 TR
        RestTrDomain = 4,     // 요청 TR 자체가 세션 도메인과 다름(실 TR 을 모의 도메인으로 · 그 역)
        RestMalformed = 5,    // REST 주문 응답 본문 형식 이탈(rt_cd 부재 · 깨진 JSON)
    }

    public sealed class KisVtsProtocolValidator : IArmingBlockSource
    {
        private readonly KisTrDomain _expected;
        private readonly Action<KisDivergenceKind, string>? _securityLog;
        private readonly Action? _onIsolate;
        private int _tripped, _lastKind;
        private long _divergences, _checks, _trips;
        private ArmingInterlockLink? _link;

        /// <param name="vts">true = 모의(VTS) 세션(H0STCNI9 · VTTC…) · false = 실전.</param>
        /// <param name="securityLog">최초 이탈 1회 보안 로그(종류 + 상수 라벨 — 원문 금지).</param>
        /// <param name="onIsolate">최초 이탈 1회 KIS 슬롯 격리(호스트 결선).</param>
        public KisVtsProtocolValidator(bool vts, Action<KisDivergenceKind, string>? securityLog = null, Action? onIsolate = null)
        {
            _expected = vts ? KisTrDomain.Vts : KisTrDomain.Real;
            _securityLog = securityLog; _onIsolate = onIsolate;
        }

        public KisTrDomain ExpectedDomain => _expected;
        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        public bool ArmingBlocked => Tripped;
        public long Divergences => Interlocked.Read(ref _divergences);
        public long Checks => Interlocked.Read(ref _checks);
        public long Trips => Interlocked.Read(ref _trips);
        public KisDivergenceKind LastKind => (KisDivergenceKind)Volatile.Read(ref _lastKind);

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Volatile.Write(ref _tripped, 0);
            Volatile.Write(ref _lastKind, 0);
            Volatile.Read(ref _link)?.Resync();
            return true;
        }

        // ── 실시간(WebSocket 수신 스레드 · δ=0) ──

        /// <summary>실시간 프레임 TR 도메인 판정. 중립/일치/미상(미구독 TR — 어댑터가 무시) = true · 교차 도메인 = 이탈(false).</summary>
        public bool CheckRealtimeTr(ReadOnlySpan<byte> trId)
        {
            Interlocked.Increment(ref _checks);
            var d = KisProtocol.ClassifyTr(trId);
            if (d is KisTrDomain.Real or KisTrDomain.Vts && d != _expected) { Trip(KisDivergenceKind.RealtimeDomain); return false; }
            return true;
        }

        /// <summary>구독 응답 TR 도메인 판정 — 교차 도메인 응답의 복호 키는 장착 금지(false).</summary>
        public bool CheckSubscribeDomain(KisTrDomain d)
        {
            Interlocked.Increment(ref _checks);
            if (d is KisTrDomain.Real or KisTrDomain.Vts && d != _expected) { Trip(KisDivergenceKind.SubscribeDomain); return false; }
            return true;
        }

        // ── REST(콜드 · 판독 자체는 δ=0) ──

        /// <summary>
        /// 주문 응답(VTTC0802U/0801U/0803U · 실전 TTTC…) 검증 + 판독. 반환 = 프로토콜 정합(이탈 = false + Disarm).
        /// 정합이어도 업무 거부(rt_cd≠"0")는 ack.Ok=false 로 돌려준다(이탈 아님).
        /// </summary>
        public bool ValidateOrderResponse(ReadOnlySpan<char> requestTr, ReadOnlySpan<char> responseTrHeader, ReadOnlySpan<byte> body, out KisOrderAck ack)
        {
            ack = default;
            if (!CheckRestEnvelope(requestTr, responseTrHeader)) return false;
            if (!KisProtocol.TryParseOrderAck(body, out ack)) { ack = default; Trip(KisDivergenceKind.RestMalformed); return false; }
            return true;
        }

        /// <summary>잔고 응답(VTTC8434R/TTTC8434R) 봉투 검증 + 보유 판독(δ=0). 반환 = 행 수 · −1 봉투 이탈(Disarm) 또는 판독 실패 · −2 버퍼 부족.
        /// ★본문 판독 실패는 이탈로 올리지 않는다 — 잔고 판독기는 rt_cd≠"0"(모의 도메인 초당 호출 제한 EGW00201 등 업무 거부)도 −1 로 돌려주므로
        ///   그 구분 없이 Disarm 하면 정상 운용 중 오탐. 판독 실패는 호출측 연속 실패 계수(비트 24)가 담당.</summary>
        public int ValidateBalanceResponse(ReadOnlySpan<char> requestTr, ReadOnlySpan<char> responseTrHeader, ReadOnlySpan<byte> body, Span<KisHolding> rows)
        {
            if (!CheckRestEnvelope(requestTr, responseTrHeader)) return -1;
            return KisProtocol.ParseHoldings(body, rows);
        }

        private bool CheckRestEnvelope(ReadOnlySpan<char> requestTr, ReadOnlySpan<char> responseTrHeader)
        {
            Interlocked.Increment(ref _checks);
            Span<byte> tr = stackalloc byte[16];
            if (requestTr.Length > tr.Length) { Trip(KisDivergenceKind.RestTrDomain); return false; }
            for (int i = 0; i < requestTr.Length; i++)
            {
                char c = requestTr[i];
                if (c > 0x7F) { Trip(KisDivergenceKind.RestTrDomain); return false; }
                tr[i] = (byte)c;
            }
            if (KisProtocol.ClassifyTr(tr[..requestTr.Length]) != _expected) { Trip(KisDivergenceKind.RestTrDomain); return false; }
            if (!KisProtocol.ResponseTrIdConsistent(requestTr, responseTrHeader)) { Trip(KisDivergenceKind.RestTrHeader); return false; }
            return true;
        }

        // ── 이탈 ──

        /// <summary>외부 판정 이탈(예: KisLiveBroker.ResponseDivergence) — sticky Disarm + 최초 1회 보안 로그 · 격리.</summary>
        public void Trip(KisDivergenceKind kind)
        {
            Interlocked.Increment(ref _divergences);
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) != 0) return;
            Volatile.Write(ref _lastKind, (int)kind);
            Interlocked.Increment(ref _trips);
            Volatile.Read(ref _link)?.Resync();                                          // ★원자 Disarm(비트 26)
            try { _securityLog?.Invoke(kind, _expected == KisTrDomain.Vts ? "expected=VTS" : "expected=REAL"); } catch (Exception) { }
            try { _onIsolate?.Invoke(); } catch (Exception) { }                           // ★KIS 슬롯 격리(미체결 고착 0)
        }
    }
}
