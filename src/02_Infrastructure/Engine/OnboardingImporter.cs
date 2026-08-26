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
using System.Globalization;
using System.Text.Json;

namespace MS2E.Infrastructure
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

        // ── 가상 잔고 원자적 시딩 ─────────────────────────────────────────────────
        //
        // PaperBroker의 진실 원천 = Settings['Paper.Account'] JSON.
        // 단순 메모리 변수 시딩은 Reload() 한 번으로 증발하거나 앱 재시작마다 중복 누적되는
        // 이중 결함을 유발한다.
        //
        // 해결책: RunInTransaction으로 3개 쓰기를 단일 원자 커밋
        //   ① Transactions: Type=Deposit, BrokerAccount='paper' (감사 추적; 실 포트폴리오와 격리)
        //   ② Settings['Paper.Account']: PaperAccount JSON (Reload() 내성의 진실 원천)
        //   ③ Settings['Paper.Seeded']: '1' (멱등 플래그)
        //
        // liveArmed 불변식: BrokerAccount='paper' 네임스페이스는 실거래 경로에 오염되지 않는다.
        //   PortfolioService.RecomputeAverage는 Symbol 기준 조회라 빈 Symbol Deposit은 무관.

        /// <summary>
        /// 모의투자 초기 시드머니를 DB에 원자적·멱등 고착화.
        /// 이미 시딩됐으면(Paper.Seeded=1) 즉시 리턴 — 중복 시딩 없음.
        /// </summary>
        public static void SeedVirtualCash(LocalDatabase db)
        {
            if (db is null) throw new ArgumentNullException(nameof(db));
            if (db.GetSetting("Paper.Seeded") == "1") return;   // 멱등 가드
            SeedCore(db, null);
        }

        /// <summary>
        /// 명시적 리셋 — Paper.Seeded 플래그를 무시하고 강제 재시딩.
        /// TradingSettingsPage 모의계좌 초기화 전용. 포지션·주문은 PaperAccount 재생성으로 초기화.
        /// </summary>
        public static void ReseedVirtualCash(LocalDatabase db, decimal? overrideSeed = null)
        {
            if (db is null) throw new ArgumentNullException(nameof(db));
            db.SetSetting("Paper.Seeded", "");   // 플래그 초기화
            SeedCore(db, overrideSeed);
        }

        private static void SeedCore(LocalDatabase db, decimal? overrideSeed)
        {
            decimal seed;
            if (overrideSeed is decimal os && os > 0)
            {
                seed = os;
                db.SetSetting("Onboarding.SeedCashKrw",
                    seed.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                var raw = db.GetSetting("Onboarding.SeedCashKrw");
                seed = decimal.TryParse(raw, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 10_000_000m;
            }

            db.RunInTransaction(() =>
            {
                // ① 감사 Deposit (BrokerAccount='paper' → 실 포트폴리오와 격리)
                db.Execute(
                    @"INSERT INTO Transactions(Symbol,Type,Quantity,Price,Commission,Tax,ExecutedAt,BrokerAccount,Memo)
                      VALUES('',@t,1,@p,0,0,@d,'paper','모의투자 초기 시드머니 입금')",
                    new Dictionary<string, object>
                    {
                        ["@t"] = (int)TransactionType.Deposit,
                        ["@p"] = (double)seed,
                        ["@d"] = DateTime.UtcNow.ToString("o"),
                    });

                // ② PaperAccount JSON (PaperBroker.Reload()가 읽는 진실 원천)
                db.SetSetting("Paper.Account",
                    JsonSerializer.Serialize(new PaperAccount { Seed = seed, Cash = seed }));

                // ③ 멱등 플래그
                db.SetSetting("Paper.Seeded", "1");
            });
        }
    }
}
