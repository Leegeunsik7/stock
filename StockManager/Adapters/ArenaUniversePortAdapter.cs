// ============================================================================
// ArenaUniversePortAdapter — v12.18.91-DEV: IArenaUniversePort 구현(PlutoMultiAssetUniverseCatalog → 탭별 뷰 목록)
//
//   구축 = 생성 시 1회(카탈로그 정적 · 불변) → 탭별 배열 캐시. 판독 = 캐시 반환뿐(콜드 · 모달 열림 시).
//   KRX 는 세그먼트(코스피 200 / 코스닥 150)로 탭 분리 · US(S&P 100 + 나스닥 100) = 미국주 탭 · 업비트 KRW = 업비트 탭.
//   ★메타데이터만 투사(코드 · 이름 · 세그먼트 라벨) — 시세 · 점수 0.
// ============================================================================
using System;
using System.Collections.Generic;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Cognitive.Screening;

namespace StockManager.Adapters
{
    public sealed class ArenaUniversePortAdapter : IArenaUniversePort
    {
        private readonly ArenaUniverseEntryView[][] _tabs;
        private readonly int _total;

        public ArenaUniversePortAdapter()
        {
            var tabs = new List<ArenaUniverseEntryView>[ArenaUniverseTab.Count];
            for (int t = 0; t < tabs.Length; t++) tabs[t] = new List<ArenaUniverseEntryView>();
            foreach (var m in new[] { PlutoScreenMarket.Krx, PlutoScreenMarket.Us, PlutoScreenMarket.Crypto })
            {
                foreach (var e in PlutoMultiAssetUniverseCatalog.Of(m))
                {
                    int tab = TabOf(e.Segment);
                    if (tab < 0) continue;
                    tabs[tab].Add(new ArenaUniverseEntryView(tab, e.Code, e.Name, PlutoMultiAssetUniverseCatalog.SegmentLabel(e.Segment)));
                }
            }
            _tabs = new ArenaUniverseEntryView[tabs.Length][];
            for (int t = 0; t < tabs.Length; t++) { _tabs[t] = tabs[t].ToArray(); _total += _tabs[t].Length; }
        }

        public static int TabOf(PlutoUniverseSegment s) => s switch
        {
            PlutoUniverseSegment.Kospi => ArenaUniverseTab.Kospi,
            PlutoUniverseSegment.Kosdaq => ArenaUniverseTab.Kosdaq,
            PlutoUniverseSegment.Sp100 or PlutoUniverseSegment.Nasdaq100 => ArenaUniverseTab.Us,
            PlutoUniverseSegment.UpbitKrw => ArenaUniverseTab.Crypto,
            _ => -1,
        };

        public IReadOnlyList<ArenaUniverseEntryView> Entries(int tab)
            => ArenaUniverseTab.Valid(tab) ? _tabs[tab] : Array.Empty<ArenaUniverseEntryView>();

        public int TotalCount => _total;
    }
}
