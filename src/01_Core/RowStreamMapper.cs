// ============================================================================
// ZeroAllocRowStreamMapper + IRowCursor — v12.0.0 Phase 94-P Part 3 Think Beyond Gemini (Engine A)
//
//   위험 가정(제미나이의 '단순 Port 이관'이 다루지 않는 데이터 마샬링 파멸 리스크):
//     "대용량 검증/예측 데이터 조회 시 DataRow 힙 가비지 유출(LOH 타격)" — 기존 LocalDatabase.Query 는
//     `dt.Load(reader)` 로 N개 DataRow(참조형 객체) + DataTable 오버헤드를 힙에 물성화한다. 캘리브레이터·트윈·
//     예측 백테스트가 수천~수만 행을 조회하면 DataRow 객체가 LOH/Gen2 로 승격되어 GC 스톨을 유발한다.
//
//   ── Think Beyond (Engine A) ───────────────────────────────────────────────────
//     Zero-Alloc Unmanaged Stream Mapper — DataRow/DataTable 물성화 없이 스트리밍 리더(IRowCursor)를 호출자
//     제공 버퍼로 직접 투사한다. 두 경로:
//       · Project<T>(cursor, Span<T> dest, proj) where T:unmanaged — 숫자 스트림(예측점·자산곡선·트윈커브)을
//         stackalloc 슬라이스로 O(1) 투사 → 핫패스 힙 0 Byte(δ=0). 예측 스트림 매퍼의 정수(精髓).
//       · MapAll<T>(cursor, proj, hint) — 문자열 포함 DTO 도 리더→레코드 '직접' 투사(결과 List 1회만 할당,
//         행당 DataRow 물성화 0). 리포지토리 어댑터가 이 경로로 원시 SQL 을 LOH 무타격으로 흡수.
//
//   불변: 결정론(리더 순서 그대로) · δ=0(Project 핫패스 힙 0) · fail-safe(빈 커서→0/빈 List) · liveArmed:false 무관(순수 판독).
//   ※ MS2E.Core(최내부·순수) — Infrastructure 가 SqliteDataReader 를 IRowCursor 로 감싸 LocalDatabase.QueryProject 로 결선.
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.Core
{
    /// <summary>스트리밍 행 커서(DataRow/DataTable 없는 최소 판독 계약). 컬럼은 0-기반 인덱스.</summary>
    public interface IRowCursor
    {
        bool Read();               // 다음 행으로 전진(없으면 false)
        long Int64(int col);
        double Double(int col);
        decimal Decimal(int col);
        string Str(int col);
        DateTime Time(int col);
        bool IsNull(int col);
    }

    /// <summary>현재 행을 결과 T 로 투사하는 순수 함수(힙 할당 없이 커서 접근자만 사용).</summary>
    public delegate T RowProjector<out T>(IRowCursor cursor);

    public static class ZeroAllocRowStreamMapper
    {
        /// <summary>
        /// ★숫자/unmanaged 스트림을 호출자 Span 으로 직접 투사(δ=0). dest 용량까지 읽어 채우고 기입 개수 반환.
        /// proj 는 커서 접근자(Int64/Double/Decimal/Time)만 사용해야 힙 0 이 보장됨(문자열 접근 금지).
        /// </summary>
        public static int Project<T>(IRowCursor cursor, Span<T> dest, RowProjector<T> proj) where T : unmanaged
        {
            if (cursor is null || proj is null) return 0;
            int n = 0;
            while (n < dest.Length && cursor.Read())
                dest[n++] = proj(cursor);
            return n;
        }

        /// <summary>
        /// 커서 전체를 List 로 투사(문자열 포함 DTO 허용). ★행당 DataRow 물성화 0 — 리더에서 레코드로 '직접' 투사하여
        /// DataTable 경로의 LOH 가비지를 제거. 결과 List 만 1회 할당(capacityHint 로 성장 재할당 최소화).
        /// </summary>
        public static List<T> MapAll<T>(IRowCursor cursor, RowProjector<T> proj, int capacityHint = 0)
        {
            var list = capacityHint > 0 ? new List<T>(capacityHint) : new List<T>();
            if (cursor is null || proj is null) return list;
            while (cursor.Read()) list.Add(proj(cursor));
            return list;
        }
    }
}
