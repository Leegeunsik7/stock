// ============================================================================
// DashRateGate — [v12.18.22-DEV] 브로커 레이트 게이트 관제 상태(순수 UI 경계 · 엔진 타입 참조 0)
//
//   0 미결선(이 허브에 게이트/판독자 없음) · 1 수신 대기(키움 프록시 관제 맵 부재 · 게이트 미결선) · 2 정상 · 3 백오프(한도 초과 응답 뒤 송신 보류) ·
//   4 래치(연속 3회 · 물리 이중키 재무장) · 5 노후(프록시 박동 > 2초 정지 · 찢김 — 잔여량 비표시).
//   ★읽기 전용 — 재무장 · 주문 경로 없음. 대기열이 없으므로 '큐 깊이' 대신 거부 누계를 보인다.
// ============================================================================
namespace MS2E.UI.Portable.Ports.Trading
{
    public static class DashRateGate
    {
        public const int Unbound = 0, Waiting = 1, Live = 2, Backoff = 3, Latched = 4, Stale = 5;
        public static string Label(int s) => s switch
        {
            Waiting => "관제 미수신", Live => "정상", Backoff => "백오프", Latched => "래치(연속 초과)", Stale => "관제 노후(프록시 정지?)", _ => "미결선",
        };
        public static string Css(int s) => s switch { Live => "ok", Backoff => "wait", Latched => "bad", Stale => "bad", Waiting => "wait", _ => "off" };
        public static bool ShowsQuota(int s) => s is Live or Backoff or Latched;
    }
}
