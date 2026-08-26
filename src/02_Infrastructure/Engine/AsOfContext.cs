// ============================================================================
// AsOfContext - 전역 "시점(As-Of)" 경량 상태 (Season 6 #3 글로벌 타임-스크러버)
//  터미널 전체를 특정 과거 시점으로 '되감기' 위한 단일 진실원. null이면 LIVE(현재).
//  변경 시 OnChange로 구독자(상단 스크러버 바·열린 페이지)가 그 시점 기준으로 다시 그린다.
//  싱글톤 등록(앱 단일 사용자). 외부 의존 0. (룩어헤드 차단은 각 페이지가 ExecutedAt/Date<=AsOf로 보장)
// ============================================================================
using System;

namespace StockManager.Common
{
    public class AsOfContext
    {
        /// <summary>현재 보기 시점. null이면 LIVE(현재 시점).</summary>
        public DateTime? AsOf { get; private set; }
        public bool IsLive => AsOf == null;

        public event Action? OnChange;

        public void Set(DateTime? d)
        {
            // 미래 날짜는 LIVE로 간주(룩어헤드 방지)
            if (d.HasValue && d.Value.Date >= DateTime.Today) d = null;
            if (AsOf == d) return;
            AsOf = d;
            OnChange?.Invoke();
        }

        public void Live() => Set(null);
    }
}
