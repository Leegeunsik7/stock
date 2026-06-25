// ============================================================================
// OnboardingImporter — v4.62.0: 온보딩 초기 보유 종목 import (거래 저장 연쇄 불변식 강제)
//
//  ★Pitfall #3(거래 저장 연쇄 불변식): Db.AddTransaction 을 단독 호출하면 Holdings 평균단가가 디싱크된다
//  (AddTransaction 은 Transactions 테이블만 INSERT — Holdings 미갱신). 본 importer 는 각 행을 매수 거래로 저장한 뒤
//  '영향받은 심볼별로 정확히 한 번' PortfolioService.RecomputeAverage 를 호출해 평단 재계산을 구조적으로 강제한다.
//  RecomputeAverage 는 전 거래 이력을 리플레이하므로 멱등 — 배치 후 심볼당 1회면 행별 호출과 동일 결과(O(n²) 회피).
//
//  ── fail-soft 배치 정책 ──────────────────────────────────────────────────────
//  무효 행(빈 심볼·0 이하 수량/단가)은 저장 시도 전 거르고, AddTransaction 검증 throw 는 행 단위 try/catch 로 흡수해
//  한 행 오류가 전체 import 를 중단시키지 않는다(사유 수집). UI(Razor) 밖의 결정론 단위 — 연쇄 강제를 테스트로 박제.
// ============================================================================
using System;
using System.Collections.Generic;

namespace StockManager.Common
{
    /// <summary>온보딩 초기 보유 1행 — 파싱 완료된 결정론 값(InvariantCulture 로 이미 파싱됨).</summary>
    public readonly record struct SeedHolding(string Symbol, decimal Qty, decimal AvgPrice, string? Name = null);

    public static class OnboardingImporter
    {
        public readonly record struct ImportResult(int Imported, int Skipped, IReadOnlyList<string> Errors)
        {
            public bool AllOk => Skipped == 0 && Errors.Count == 0;
        }

        /// <summary>
        /// 초기 보유 종목을 매수 거래로 저장 + 심볼별 1회 평균단가 재계산(연쇄 불변식 강제). 행 단위 fail-soft.
        /// 시장/통화는 RecomputeAverage 의 심볼 추론(KRW-=코인, 6자리=KRX, 그 외 US)에 위임 — name 만 힌트로 전달.
        /// </summary>
        public static ImportResult ImportHoldings(LocalDatabase db, IEnumerable<SeedHolding> rows,
                                                  DateTime executedAtUtc, string brokerAccount = "")
        {
            if (db is null)   throw new ArgumentNullException(nameof(db));
            if (rows is null) throw new ArgumentNullException(nameof(rows));

            var errors  = new List<string>();
            var symbols = new List<string>();                 // 영향 심볼(순서 보존)
            var names   = new Dictionary<string, string?>(StringComparer.Ordinal);
            int imported = 0, skipped = 0;

            foreach (var r in rows)
            {
                string sym = (r.Symbol ?? "").Trim();
                if (sym.Length == 0 || r.Qty <= 0m || r.AvgPrice <= 0m)
                { skipped++; errors.Add($"행 건너뜀(심볼/수량/단가 무효): '{r.Symbol}' {r.Qty}@{r.AvgPrice}"); continue; }
                try
                {
                    db.AddTransaction(new Transaction
                    {
                        Symbol = sym, Type = TransactionType.Buy, Quantity = r.Qty, Price = r.AvgPrice,
                        ExecutedAt = executedAtUtc, BrokerAccount = brokerAccount ?? "",
                        Memo = "온보딩 초기 보유 import",
                    });
                    imported++;
                    if (!symbols.Contains(sym)) { symbols.Add(sym); names[sym] = r.Name; }
                }
                catch (Exception ex) { skipped++; errors.Add($"{sym} 저장 실패: {ex.Message}"); }   // 검증 throw 흡수(fail-soft)
            }

            // ★연쇄 강제 — 심볼별 1회 평균단가 재계산. 누락 시 Holdings 미생성/평단 디싱크. 재계산 실패도 사유만 수집(저장은 유지).
            foreach (var sym in symbols)
            {
                try { PortfolioService.RecomputeAverage(db, sym, names.TryGetValue(sym, out var nm) ? nm : null); }
                catch (Exception ex) { errors.Add($"{sym} 평균단가 재계산 실패: {ex.Message}"); }
            }

            return new ImportResult(imported, skipped, errors);
        }
    }
}
