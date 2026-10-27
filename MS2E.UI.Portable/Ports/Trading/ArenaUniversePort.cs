// ============================================================================
// ArenaUniversePort — v12.18.91-DEV: /dualarena [🔍 종목선택] 모달의 마스터 유니버스 판독 경계(순수 · 엔진 카탈로그 타입 참조 0)
//
//   호스트 어댑터가 플루토 3대 자산 카탈로그(코스피 200 · 코스닥 150 · 미국 S&P 100/나스닥 100 · 업비트 KRW = 420종)를 탭별 목록으로 투사한다.
//   ★메타데이터 전용(코드 · 이름) — 시세 · 점수 · 추천 비중은 담지 않는다(날조 0). 선택 가능 여부는 페이지가 아레나 포트의 '실제 수신 호가'로만 판정한다:
//     호가 미수신 종목 = '시세 미수신 · 선택 불가'로 정직 렌더(가짜 현재가 0 · 거부될 주문 바인딩 0).
//   콜드 경로(모달 열림 시 1회 판독) — 체결/정산 핫패스 무관(δ=0 불변식 무영향).
//   [미결선] UnwiredArenaUniversePort = 카탈로그 없는 셸의 null 객체(빈 목록 · UI 는 '유니버스 미결선' 정직 표시).
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>종목선택 모달 탭(코스피 · 코스닥 · 미국주 · 업비트).</summary>
    public static class ArenaUniverseTab
    {
        public const int Kospi = 0, Kosdaq = 1, Us = 2, Crypto = 3, Count = 4;
        public static bool Valid(int tab) => (uint)tab < Count;
        public static string Label(int tab) => tab switch
        {
            Kospi => "코스피",
            Kosdaq => "코스닥",
            Us => "미국주",
            Crypto => "업비트",
            _ => "?",
        };
    }

    /// <summary>유니버스 1종목(Code = 아레나 표기: KRX 6자리 · US 티커 · 업비트 "KRW-XXX").</summary>
    public readonly record struct ArenaUniverseEntryView(int Tab, string Code, string Name, string Segment);

    public interface IArenaUniversePort
    {
        /// <summary>탭 목록(카탈로그 순서 · 범위 밖 탭 = 빈 목록).</summary>
        IReadOnlyList<ArenaUniverseEntryView> Entries(int tab);
        /// <summary>전 탭 종목 수(420 = 카탈로그 전량).</summary>
        int TotalCount { get; }
        /// <summary>카탈로그 결선 여부(false = 미결선 null 객체). 기본 구현 = true.</summary>
        bool IsWired => true;
    }

    /// <summary>카탈로그 미결선 null 객체(호스트 없는 셸 · 테스트 하네스). 목록 = 항상 빈 목록.</summary>
    public sealed class UnwiredArenaUniversePort : IArenaUniversePort
    {
        public static readonly UnwiredArenaUniversePort Instance = new();
        private UnwiredArenaUniversePort() { }
        public IReadOnlyList<ArenaUniverseEntryView> Entries(int tab) => Array.Empty<ArenaUniverseEntryView>();
        public int TotalCount => 0;
        public bool IsWired => false;
    }
}
