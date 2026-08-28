// ============================================================================
// BoundedVirtualWindow<T> — Phase 94-F Think Beyond Gemini
//   ★DB 에서 수만 건 레코드(신호 등)를 Port 로 가져와 렌더링할 때의 두 파멸적 맹점을 영구 격리:
//     ① SQL Reader/DataTable 변환의 힙 가비지 유출(LOH) — 전체를 한 번에 물성화하지 않고 뷰포트 페이지만 적재.
//     ② 수만 행 렌더링 시 Blazor DOM 폭발·UI 스레드 멈춤 — 화면 가시 영역(뷰포트) N행만 물성화·렌더.
//
//   ── 제미나이 A(무할당 Span 매퍼) + B(뷰포트 링 거버너)를 융합·초월 ──
//     A(Span 매퍼): 어댑터가 페이지 단위로만 물성화(전체 DataTable 금지) → LOH 자체를 회피.
//     B(뷰포트 링): 고정 용량 링에 '가시 영역 ± 마진'만 래치. 스크롤 이탈분은 참조 소거(유령 참조/GC 압력 0).
//
//   ── ★독창적 초월점(제미나이 미제시) ──
//     · page-fault 히트/미스 회계 + 마진 프리페치: 뷰포트가 윈도우(마진 포함) 안이면 δ=0 히트(재조회 0),
//       벗어나면 1회 page-fault 로 링을 재적재(loader 콜백). 스크롤 대부분이 히트 → DB 왕복 최소화.
//     · 제네릭 경계 거버너: 어떤 레코드 타입(T)에도 동작. 링은 1회 할당·재사용 → 총 레코드가 얼마든 물성화 ≤ 용량.
//
//   불변: δ=0(EnsureWindow 히트/TryGet 핫패스 힙 0·정수 산술·배열 참조) · 결정론(범위 산술 고정) ·
//        fail-closed(윈도우 밖/미적재 → TryGet false·default) · liveArmed:false(순수 UI 데이터 창 — 발주/소켓 무접촉).
//   ※ 스레드 모델: 단일 UI 컨텍스트(Blazor Dispatcher)에서 EnsureWindow/TryGet 호출(동시 접근 금지).
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.UI.Portable.Infrastructure
{
    public sealed class BoundedVirtualWindow<T>
    {
        private readonly T[] _ring;      // 고정 용량 링(1회 할당·재사용)
        private readonly int _cap;
        private readonly int _margin;    // 뷰포트 상하 프리페치 여유
        private int _base;               // 링[0] 의 논리 인덱스(-1 = 미적재)
        private int _loaded;             // 유효 적재 개수(≤ cap)
        private int _total;              // 논리 전체 개수(스크롤바용)
        private long _pageFaults;
        private long _hits;

        public BoundedVirtualWindow(int capacity = 256, int margin = 32)
        {
            if (capacity < 16) capacity = 16;
            if (capacity > (1 << 20)) capacity = 1 << 20;
            if (margin < 0) margin = 0;
            if (margin > capacity / 2) margin = capacity / 2;
            _cap = capacity;
            _margin = margin;
            _ring = new T[capacity];
            _base = -1;
        }

        public int  Capacity   => _cap;
        public int  Total      => _total;
        public int  Loaded     => _loaded;
        public int  BaseIndex  => _base;
        public long PageFaults => _pageFaults;
        public long Hits       => _hits;

        /// <summary>
        /// 뷰포트 [firstVisible, firstVisible+visibleCount) 를 보장. 마진 포함 현재 윈도우에 있으면 δ=0 히트(재조회 0),
        /// 아니면 page-fault: need 중심으로 용량 크기 페이지를 loader(start,count)로 1회 적재. loader 는 미스 시에만 호출.
        /// </summary>
        public void EnsureWindow(int firstVisible, int visibleCount, int total,
            Func<int, int, IReadOnlyList<T>> pageLoader)
        {
            _total = total < 0 ? 0 : total;
            if (_total == 0) { ClearRefs(); _base = -1; _loaded = 0; return; }
            if (firstVisible < 0) firstVisible = 0;
            if (visibleCount < 0) visibleCount = 0;

            int needStart = firstVisible;
            int needEnd = Math.Min(_total, firstVisible + visibleCount);   // exclusive

            // 현재 윈도우가 뷰포트를 포함 → δ=0 히트
            if (_base >= 0 && needStart >= _base && needEnd <= _base + _loaded) { _hits++; return; }

            // page-fault: need 중심 ± 마진으로 용량 크기 페이지 적재
            int loadStart = Math.Max(0, needStart - _margin);
            if (needEnd - loadStart > _cap) loadStart = Math.Max(0, needStart);   // 뷰포트가 마진보다 크면 need 기준
            int loadCount = Math.Min(_cap, _total - loadStart);

            var page = pageLoader(loadStart, loadCount) ?? (IReadOnlyList<T>)Array.Empty<T>();
            int n = Math.Min(loadCount, page.Count);
            for (int i = 0; i < n; i++) _ring[i] = page[i];
            for (int i = n; i < _loaded; i++) _ring[i] = default!;   // 잔여 슬롯 참조 소거(유령 참조 방지)
            _base = loadStart;
            _loaded = n;
            _pageFaults++;
        }

        /// <summary>논리 인덱스의 레코드 조회. 윈도우 내면 δ=0(참조 반환) true, 밖/미적재면 false(fail-closed).</summary>
        public bool TryGet(int logicalIndex, out T item)
        {
            int off = logicalIndex - _base;
            if (_base >= 0 && (uint)off < (uint)_loaded) { item = _ring[off]; return true; }
            item = default!;
            return false;
        }

        /// <summary>재초기화(새 조회 시작).</summary>
        public void Reset()
        {
            ClearRefs();
            _base = -1;
            _loaded = 0;
            _total = 0;
            _pageFaults = 0;
            _hits = 0;
        }

        private void ClearRefs()
        {
            for (int i = 0; i < _loaded; i++) _ring[i] = default!;
        }
    }
}
