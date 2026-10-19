// ============================================================================
// ArenaReplayPort — [v12.18.12-DEV] 아레나 고속 틱 리플레이 포트(순수 UI 경계 · 엔진 타입 참조 0)
//
//   호스트 ArenaTickReplayEngine 이 녹화/합성 틱 테이프를 배속(1 · 5 · 10 · 50 · 100 · 최대)으로 재생해 전용 듀얼 북(실시간 아레나와 별개 원장)에 주입한다.
//   스코어보드 · 휴먼 퀵 티켓은 Arena(IArenaPort — 리플레이 원장)를 그대로 재사용한다. 재생 제어 = 명령 링 적재만(재생 스레드가 순서대로 반영).
//   ★Fail-Closed: 브로커 · 무장 · 실주문 경로 없음 — 리플레이 주문의 목적지는 가상 원장뿐이다.
// ============================================================================
using System;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>재생 상태(0 대기 · 1 재생 · 2 일시정지 · 3 완료).</summary>
    public static class ArenaReplayState
    {
        public const int Idle = 0, Playing = 1, Paused = 2, Finished = 3;
        public static string Label(int s) => s switch { Idle => "대기", Playing => "재생 중", Paused => "일시정지", Finished => "재생 완료", _ => "?" };
    }

    /// <summary>배속(0 = 최대 · 지연 없음).</summary>
    public static class ArenaReplaySpeed
    {
        public const int Max = 0;
        private static readonly int[] _options = { 1, 5, 10, 50, 100, Max };
        public static ReadOnlySpan<int> Options => _options;
        public static bool IsValid(int speed) => speed is 1 or 5 or 10 or 50 or 100 or Max;
        public static string Label(int speed) => speed == Max ? "최대" : speed + "x";
    }

    /// <summary>[v12.18.26-DEV] 극한 시나리오 모드(0 끔 · 1 블라인드 — 종류 · 발동 시각 비공개 · 재생 시각 3 ~ 5분 사이 무작위 1회).</summary>
    public static class ArenaScenario
    {
        public const int Off = 0, Blind = 1;
        public static bool IsValid(int mode) => mode is Off or Blind;
        public static string Label(int mode) => mode switch { Off => "끔", Blind => "블라인드", _ => "?" };
    }

    /// <summary>[v12.18.26-DEV] 극한 시나리오 종류(0 없음 · 1 플래시 크래시 급락 · 2 숏 스퀴즈 급등).</summary>
    public static class ArenaScenarioProfile
    {
        public const int None = 0, FlashCrash = 1, ShortSqueeze = 2;
        public static string Label(int p) => p switch { FlashCrash => "플래시 크래시(급락)", ShortSqueeze => "숏 스퀴즈(급등)", _ => "-" };
    }

    /// <summary>[v12.18.26-DEV] 극한 시나리오 국면(0 없음 · 1 잠복 — 미발동 · 2 발동 — 패닉 버스트 재생 중 · 3 여진 — 버스트 종료 후 회복 구간).</summary>
    public static class ArenaScenarioPhase
    {
        public const int None = 0, Armed = 1, Active = 2, Aftermath = 3;
        public static string Label(int p) => p switch { Armed => "잠복", Active => "발동", Aftermath => "여진", _ => "-" };
    }

    /// <summary>재생 진행 상태(50ms 게시 주기에 갱신 · 값 복사).</summary>
    public readonly struct ArenaReplayStatus
    {
        public readonly int State, Speed;
        public readonly long Cursor, Total, SimTicks, TicksPerSecond, Version;
        /// <summary>
        /// [v12.18.26-DEV] 극한 시나리오: 모드(ArenaScenario) · 국면(ArenaScenarioPhase) · 종류(ArenaScenarioProfile) · 대상 종목 · 버스트 틱/초.
        /// ★블라인드: 잠복 국면에서는 종류 · 종목 · 틱/초가 0 — 발동 시점에 처음 공개된다(발동 시각은 끝까지 비공개).
        /// </summary>
        public readonly int ScenarioMode, ScenarioPhase, ScenarioProfile;
        public readonly long ScenarioSymbol, ScenarioTicksPerSecond;

        public ArenaReplayStatus(int state, int speed, long cursor, long total, long simTicks, long ticksPerSecond, long version,
                                 int scenarioMode = ArenaScenario.Off, int scenarioPhase = ArenaScenarioPhase.None, int scenarioProfile = ArenaScenarioProfile.None,
                                 long scenarioSymbol = 0, long scenarioTicksPerSecond = 0)
        {
            State = state; Speed = speed; Cursor = cursor; Total = total; SimTicks = simTicks; TicksPerSecond = ticksPerSecond; Version = version;
            ScenarioMode = scenarioMode; ScenarioPhase = scenarioPhase; ScenarioProfile = scenarioProfile; ScenarioSymbol = scenarioSymbol; ScenarioTicksPerSecond = scenarioTicksPerSecond;
        }

        /// <summary>진행률(0 ~ 10000 bp).</summary>
        public int ProgressBp => Total <= 0 ? 0 : (int)Math.Min(10_000, Cursor * 10_000 / Total);
    }

    public interface IArenaReplayPort
    {
        /// <summary>리플레이 전용 원장(스코어보드 · 휴먼 퀵 티켓 · 자산곡선) — 실시간 아레나와 별개 인스턴스.</summary>
        IArenaPort Arena { get; }
        ArenaReplayStatus Status { get; }
        /// <summary>대기/완료 = 처음부터 새 매치(양측 동일 자본금) · 일시정지 = 재개. 범위 밖 자본금 = false.</summary>
        bool Play(long capitalWon);
        /// <summary>일시정지 — 양측 포지션 · 손익 동결. 일시정지 중 휴먼 주문은 큐에 대기 → 재개 첫 틱에 체결.</summary>
        void Pause();
        /// <summary>처음(틱 0)으로 되감고 원장 초기화 · 대기.</summary>
        void Reset();
        /// <summary>배속 변경(재생 중이면 현재 위치에서 재기준). 허용 밖 값 = false.</summary>
        bool SetSpeed(int speed);
        /// <summary>[v12.18.13-DEV] 현재 재생 원천 라벨(합성 테이프 · .tape 파일명).</summary>
        string SourceLabel { get; }
        /// <summary>[v12.18.13-DEV] 재생 원천 교체(null = 합성 테이프 · 경로 = 녹화 .tape). 열기/검증 실패 = false(원천 유지). 교체 후 처음 · 대기.</summary>
        bool LoadTape(string? path);
        /// <summary>
        /// [v12.18.26-DEV] 극한 시나리오 모드(ArenaScenario) — 다음 '처음부터' 재생(대기/완료 → 시작)에 반영. 재생/일시정지 중 = false(진행 중 매치 불변).
        /// 블라인드 = 매치마다 종류(급락/급등) · 발동 시각(재생 시각 3 ~ 5분)을 무작위로 정해 원천 테이프에 패닉 버스트(≥ 2,000틱/초 · 유동성 진공)를 삽입.
        /// </summary>
        bool SetScenario(int mode);
    }
}
