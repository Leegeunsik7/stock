// ============================================================================
// PaperDashboardPort — [대시보드 · 추천 A] AI 모의투자 실시간 시각화 대시보드 포트(순수 UI 경계)
//
//   호스트 DashboardSnapshotHub(무할당 텔레메트리 수집 + 3중 버퍼 스냅샷)가 50ms 주기로 조립한 프레임을 UI 가 '빌려서(Acquire)'
//   렌더하고 '돌려준다(Release)'. 빌린 프레임은 허브가 덮어쓰지 않는다(리스 중 버퍼 재사용 금지) — UI 가 느려도 찢어진 프레임 0,
//   허브는 남은 버퍼로 계속 조립(코어 매매 경로와 무관). 프레임은 원시 구조체 배열만 담는다(엔진 타입 참조 0 · RCL 순수성).
//   ★Zero UI Interference: 이 포트는 '읽기 전용'이다 — 주문/무장/취소 명령 경로가 없다(대시보드가 브로커에 닿을 수단 자체가 없음).
// ============================================================================
using System;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>체결 피드 1건(원 · 주). Side +1 매수 · −1 매도. Broker 0 키움 · 1 KIS · 2 모의(가상).</summary>
    public struct DashFill { public long Ticks, Symbol, Qty, PriceWon; public int Side, Broker; }

    /// <summary>[방향 B] AI 판단 근거 1건 — 문자열 없이 코드·수치만(텍스트는 렌더 시 UI 가 조립). bp = 1/10000.</summary>
    public struct DashDecision { public long Ticks, Symbol, ScaleBp, VolatilityBp, Value; public int Kind, Reason; }

    /// <summary>인터락 비트 전이 1건(raised/cleared 비트 · 전이 후 마스크).</summary>
    public struct DashInterlockEvent { public long Ticks, Raised, Cleared, MaskAfter; }

    /// <summary>[방향 A] 자식 슬롯 1개(부모 · 주문/체결 수량 · 브로커 · 상태 코드).</summary>
    public struct DashChild { public long ParentId, OrderQty, FilledQty, AvgPriceWon; public int Index, Broker, State; }

    /// <summary>브로커 한도 지표(결속 여부 · 매수 여력 원).</summary>
    public struct DashBrokerLimit { public int Broker; public bool Bound; public long HeadroomWon; }

    public struct DashEquityPoint { public long Ticks, EquityWon; }

    public static class DashDecisionKind
    {
        public const int Entry = 1, Exit = 2, Scale = 3, Hold = 4, Veto = 5;
        public static string Label(int k) => k switch { Entry => "진입", Exit => "청산", Scale => "비중", Hold => "관망", Veto => "거부", _ => "?" };
    }

    public static class DashChildState
    {
        // ChildSlotState 와 같은 코드(0 빈 · 1 대기 · 2 송출 · 3 접수 · 4 체결 · 5 취소 · 6 거부 · 7 회수)
        public static string Label(int s) => s switch { 1 => "대기", 2 => "송출", 3 => "접수", 4 => "체결", 5 => "취소", 6 => "거부", 7 => "회수", _ => "-" };
    }

    /// <summary>대시보드 1프레임 — 고정 용량 배열 + 유효 개수. 허브가 재사용(할당 0) · UI 는 리스 동안만 읽는다.</summary>
    public sealed class PaperDashboardFrame
    {
        public const int MaxFills = 32, MaxDecisions = 48, MaxEvents = 32, MaxEquity = 600, MaxChildren = 64, MaxBrokers = 4, MaxBits = 64;

        public long Version, BuiltTicks;
        /// <summary>허브 3중 버퍼 슬롯 번호(리스 반납 식별 · UI 는 쓰지 않는다).</summary>
        public int BufferSlot;
        // 인터락
        public long Mask, RequiredMask, BoundMask;
        public bool Sealed;
        // 성과(원) — 수수료/세금 미반영(정직)
        public long RealizedPnlWon, UnrealizedPnlWon, EquityWon, PeakEquityWon, MaxDrawdownWon;
        public int Wins, Losses, OpenPositions;
        // 처리량 · 유실(링 덮어쓰기 = 렌더보다 빠른 유입 — 표시 전용 데이터 유실이며 매매 경로와 무관)
        public long TicksSeen, FillsSeen, FillsLost, DecisionsLost, EventsLost, BuildMicros;

        public readonly DashFill[] Fills = new DashFill[MaxFills];
        public readonly DashDecision[] Decisions = new DashDecision[MaxDecisions];
        public readonly DashInterlockEvent[] Events = new DashInterlockEvent[MaxEvents];
        public readonly DashEquityPoint[] Equity = new DashEquityPoint[MaxEquity];
        public readonly DashChild[] Children = new DashChild[MaxChildren];
        public readonly DashBrokerLimit[] Limits = new DashBrokerLimit[MaxBrokers];
        public int FillCount, DecisionCount, EventCount, EquityCount, ChildCount, LimitCount;

        /// <summary>비트 이름(허브가 기동 시 1회 채움 · 이후 불변).</summary>
        public string[] BitNames = Array.Empty<string>();

        public double WinRate => Wins + Losses == 0 ? 0 : (double)Wins / (Wins + Losses);
        public bool IsRaised(int bit) => (uint)bit < 64 && (Mask & (1L << bit)) != 0;
        public bool IsRequired(int bit) => (uint)bit < 64 && (RequiredMask & (1L << bit)) != 0;
        public bool IsBound(int bit) => (uint)bit < 64 && (BoundMask & (1L << bit)) != 0;
        public string BitName(int bit) => (uint)bit < (uint)BitNames.Length && BitNames[bit] is { Length: > 0 } n ? n : "bit" + bit;
    }

    public interface IPaperDashboardPort
    {
        /// <summary>최신 게시 프레임 버전(0 = 아직 없음).</summary>
        long Version { get; }
        /// <summary>최신 프레임 리스(없으면 null). 반드시 Release 로 반납 — 리스 중 허브는 그 버퍼를 덮어쓰지 않는다.</summary>
        PaperDashboardFrame? Acquire();
        void Release(PaperDashboardFrame frame);
        /// <summary>새 프레임 게시(허브 펌프 스레드 · 인자 = 버전). 구독자 예외는 허브가 삼켜 계수(펌프 무영향).</summary>
        event Action<long>? FrameReady;
        /// <summary>허브 가동 여부(미가동 = 피드 미결선 · UI 는 정직하게 '미연결' 렌더).</summary>
        bool Running { get; }
    }
}
