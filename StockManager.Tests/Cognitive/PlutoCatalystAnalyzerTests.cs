// ============================================================================
// PlutoCatalystAnalyzerTests — v12.18.63-DEV: 플루토 브레인 1단계(뉴스/공시 촉매 분석 · 감쇄 · seqlock 버퍼 · 아레나 핫패스 결속)
//
// §1 분류 — 공시 유형별 점수/밴드(수주 · 유상증자 · 감사의견 · 횡령 · CB · 목표가 · MOU · 리콜) · 노이즈 기각(루머 · 낚시 · 시황 · 부인) ·
//          반전(해지 · 철회) · 혼재(음수 우선 · 확신도 감점) · 공시 출처 루머 필터 면제
// §2 엔티티 — 단어 경계 · 긴 별칭 우선 · 코드/힌트 · 중복 제거 · 상한 4
// §3 감쇄 — 반감기 정합(10/30/60분) · 단조 비증가 · 부호 보존 · 미래 시각 fail-closed · 만료
// §4 버퍼 · 분석기 — 우세 규칙 · 악재 고착 · 포화 · 검증 기각 · 비동기 큐(DropOldest)
// §5 아레나 결속 — KRX/US NewsCatalyst 진입 · NewsShock 청산 · 충격 악재 진입 차단 · 근거 요약 결속
// §6 δ=0 · 동시성 — 10만 회 조회 0B · 엔진 정산 10만 회 0B · IL 할당 지점 0 · 4 작성자 × 2 판독자 찢김 0
// ============================================================================
using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.News;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoCatalystAnalyzerTests
    {
        private const long Samsung = 5930, Hynix = 660;
        private static readonly DateTime Kst10 = new(2026, 10, 22, 10, 0, 0);            // 목 10:00 KST(KRX 정규장)
        private static readonly DateTime KstUsReg = new(2026, 10, 22, 23, 0, 0);         // 목 10:00 EDT(미국 정규장)
        private static long Utc(DateTime kst) => kst.Ticks - PlutoClock.KstOffsetTicks;
        private static readonly long Nvda = UsTechUniverse.Pack("NVDA");
        private readonly ITestOutputHelper _out;

        public PlutoCatalystAnalyzerTests(ITestOutputHelper o) => _out = o;

        private static PlutoSentimentFactor F(long symbol, long ts, int score, CatalystCategory cat, int conf = 100, string headline = "h")
            => new(symbol, ts, score, conf, cat, PlutoSource.Dart, Encoding.UTF8.GetBytes(headline));

        // ── §1 분류 ────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(PlutoSource.Dart, "삼성전자 유상증자 결정", CatalystCategory.StructuralNegative, -80)]
        [InlineData(PlutoSource.Dart, "삼성전자 감사의견 거절", CatalystCategory.StructuralNegative, -100)]
        [InlineData(PlutoSource.Dart, "삼성전자 횡령·배임 혐의 발생", CatalystCategory.StructuralNegative, -100)]
        [InlineData(PlutoSource.Dart, "삼성전자 전환사채권 발행결정", CatalystCategory.StructuralNegative, -70)]
        [InlineData(PlutoSource.Dart, "삼성전자 분식회계 혐의", CatalystCategory.StructuralNegative, -100)]
        [InlineData(PlutoSource.Dart, "삼성전자 단일판매ㆍ공급계약 체결", CatalystCategory.StructuralPositive, 75)]
        [InlineData(PlutoSource.Dart, "삼성전자 대규모 공급계약 체결", CatalystCategory.StructuralPositive, 85)]
        [InlineData(PlutoSource.Dart, "셀트리온 신약 품목허가 획득", CatalystCategory.StructuralPositive, 85)]
        [InlineData(PlutoSource.Wire, "삼성전자 목표주가 상향", CatalystCategory.MomentumPositive, 35)]
        [InlineData(PlutoSource.Wire, "삼성전자, 엔비디아와 MOU 체결", CatalystCategory.MomentumPositive, 30)]
        [InlineData(PlutoSource.Wire, "삼성전자 스마트폰 리콜", CatalystCategory.MomentumNegative, -45)]
        [InlineData(PlutoSource.Wire, "삼성전자 목표가 하향", CatalystCategory.MomentumNegative, -35)]
        [InlineData(PlutoSource.Dart, "삼성전자 공급계약 해지", CatalystCategory.StructuralNegative, -60)]
        [InlineData(PlutoSource.Dart, "삼성전자 유상증자 철회", CatalystCategory.MomentumPositive, 30)]
        [InlineData(PlutoSource.Dart, "삼성전자 풍문 조회공시 답변: 유상증자 결정", CatalystCategory.StructuralNegative, -80)]
        [InlineData(PlutoSource.Sec, "Apple reports record revenue", CatalystCategory.StructuralPositive, 80)]
        [InlineData(PlutoSource.Sec, "Auditor resigns at Tesla", CatalystCategory.StructuralNegative, -90)]
        [InlineData(PlutoSource.Wire, "Analyst downgrades Microsoft", CatalystCategory.MomentumNegative, -40)]
        [InlineData(PlutoSource.Unknown, "삼성전자 인수 루머 확산", CatalystCategory.Noise, 0)]
        [InlineData(PlutoSource.Wire, "충격! 삼성전자 결국 이렇게", CatalystCategory.Noise, 0)]
        [InlineData(PlutoSource.Wire, "삼성전자 3% 급등 마감", CatalystCategory.Noise, 0)]
        [InlineData(PlutoSource.Wire, "삼성전자, 인수설 사실무근", CatalystCategory.Noise, 0)]
        [InlineData(PlutoSource.Wire, "Apple rumor: new car unconfirmed", CatalystCategory.Noise, 0)]
        [InlineData(PlutoSource.Wire, "", CatalystCategory.Noise, 0)]
        public void Classify_Assigns_Category_And_Score_Within_Band(PlutoSource source, string headline, CatalystCategory cat, int score)
        {
            var v = PlutoCatalystAnalyzer.Classify(source, headline);
            Assert.Equal(cat, v.Category);
            Assert.Equal(score, v.Score);
            Assert.True(CatalystText.InBand(v.Category, v.Score));
            Assert.InRange(v.Confidence, 0, 100);
            if (v.IsNoise) { Assert.Equal(0, v.Confidence); Assert.NotEqual(PlutoNoiseReason.None, v.Noise); }
            else Assert.True(v.Confidence > 0);
        }

        [Fact]
        public void Noise_Reasons_Confidence_And_Mixed_Headlines()
        {
            Assert.Equal(PlutoNoiseReason.Rumor, PlutoCatalystAnalyzer.Classify(PlutoSource.Wire, "삼성전자 찌라시 유상증자").Noise);
            Assert.Equal(PlutoNoiseReason.Clickbait, PlutoCatalystAnalyzer.Classify(PlutoSource.Unknown, "경악 삼성전자 수주").Noise);
            Assert.Equal(PlutoNoiseReason.MarketChatter, PlutoCatalystAnalyzer.Classify(PlutoSource.Wire, "[특징주] 삼성전자 강세").Noise);
            Assert.Equal(PlutoNoiseReason.NoCatalyst, PlutoCatalystAnalyzer.Classify(PlutoSource.Wire, "삼성전자 주주총회 개최").Noise);
            Assert.Equal(PlutoNoiseReason.Denial, PlutoCatalystAnalyzer.Classify(PlutoSource.Dart, "삼성전자 유상증자설 사실무근").Noise);
            Assert.Equal(PlutoNoiseReason.Reversed, PlutoCatalystAnalyzer.Classify(PlutoSource.Wire, "삼성전자 리콜 취소").Noise);
            // 공시 = 1차 출처 → 낚시 표지 무시 · 영문 단어 경계(mou ≠ amount)
            Assert.Equal(CatalystCategory.StructuralPositive, PlutoCatalystAnalyzer.Classify(PlutoSource.Dart, "충격적 규모 공급계약").Category);
            Assert.Equal(CatalystCategory.Noise, PlutoCatalystAnalyzer.Classify(PlutoSource.Wire, "Apple amount disclosed").Category);

            Assert.Equal(90, PlutoCatalystAnalyzer.Classify(PlutoSource.Dart, "유상증자 결정").Confidence);
            Assert.Equal(70, PlutoCatalystAnalyzer.Classify(PlutoSource.Wire, "유상증자 결정").Confidence);
            Assert.Equal(45, PlutoCatalystAnalyzer.Classify(PlutoSource.Unknown, "유상증자 결정").Confidence);
            Assert.Equal(95, PlutoCatalystAnalyzer.Classify(PlutoSource.Dart, "유상증자 및 전환사채 발행 결정").Confidence);   // 같은 방향 규칙 +5

            // 혼재: 동률 |80| → 음수 우선 · 반대 방향 감점 −20
            var mixed = PlutoCatalystAnalyzer.Classify(PlutoSource.Wire, "삼성전자 사상 최대 실적에도 유상증자 결정");
            Assert.Equal(CatalystCategory.StructuralNegative, mixed.Category);
            Assert.Equal(-80, mixed.Score);
            Assert.Equal(50, mixed.Confidence);
        }

        // ── §2 엔티티 ──────────────────────────────────────────────────────────

        private static long[] Resolve(string text, string? hint = null)
        {
            Span<long> d = stackalloc long[PlutoCatalystAnalyzer.MaxEntitiesPerItem];
            int n = PlutoCatalystAnalyzer.ResolveSymbols(text, hint, d);
            return d[..n].ToArray();
        }

        [Fact]
        public void Entity_Mapping_Respects_Boundaries_Longest_Alias_Hints_And_Cap()
        {
            Assert.Empty(Resolve("Pineapple prices rise"));
            Assert.Equal(new[] { UsTechUniverse.Pack("AAPL"), Nvda }, Resolve("Apple and Nvidia sign deal"));
            Assert.Equal(new[] { 323410L }, Resolve("카카오뱅크 실적 발표"));
            Assert.Equal(new[] { 35720L }, Resolve("카카오가 신규 서비스"));
            Assert.Equal(new[] { Samsung }, Resolve("삼성전자(005930) 수주 · 삼성전자"));
            Assert.Equal(new[] { 6400L }, Resolve("삼성SDI 증설"));
            Assert.Equal(new[] { Hynix, Samsung }, Resolve("삼성전자 수주", "000660"));
            Assert.Equal(new[] { Nvda }, Resolve("chip news", "nvda"));
            Assert.Empty(Resolve("no entity", "12-34"));
            Assert.Equal(4, Resolve("삼성전자 SK하이닉스 현대차 셀트리온 카카오 공동 MOU").Length);
            Assert.Equal(new[] { Samsung, Hynix, 5380L, 68270L }, Resolve("삼성전자 SK하이닉스 현대차 셀트리온 카카오 공동 MOU"));
        }

        // ── §3 감쇄 ────────────────────────────────────────────────────────────

        [Fact]
        public void Decay_Follows_Half_Life_Per_Category_And_Is_Monotone()
        {
            long t0 = Utc(Kst10);
            long M(int minutes) => t0 + minutes * TimeSpan.TicksPerMinute;
            Assert.Equal(10, CatalystText.HalfLifeMinutes(CatalystCategory.MomentumPositive));
            Assert.Equal(10, CatalystText.HalfLifeMinutes(CatalystCategory.MomentumNegative));
            Assert.Equal(30, CatalystText.HalfLifeMinutes(CatalystCategory.StructuralPositive));
            Assert.Equal(60, CatalystText.HalfLifeMinutes(CatalystCategory.StructuralNegative));

            var sp = F(Samsung, t0, 80, CatalystCategory.StructuralPositive);
            Assert.Equal(80, sp.DecayedScore(M(0)));
            Assert.Equal(40, sp.DecayedScore(M(30)));
            Assert.Equal(20, sp.DecayedScore(M(60)));
            Assert.Equal(10, sp.DecayedScore(M(90)));
            Assert.Equal(56, sp.DecayedScore(M(15)));                 // 80/√2 = 56.57 → 0 방향 절단
            var mp = F(Samsung, t0, 40, CatalystCategory.MomentumPositive);
            Assert.Equal(20, mp.DecayedScore(M(10)));
            Assert.Equal(10, mp.DecayedScore(M(20)));
            var sn = F(Samsung, t0, -100, CatalystCategory.StructuralNegative, conf: 90);
            Assert.Equal(-50, sn.DecayedScore(M(60)));
            Assert.Equal(-45, sn.EffectiveScore(M(60)));
            Assert.Equal(-90, sn.EffectiveScore(M(0)));

            foreach (var f in new[] { sp, mp, sn, F(Samsung, t0, -30, CatalystCategory.MomentumNegative) })
            {
                int prev = Math.Abs(f.Score);
                for (int m = 0; m <= 3000; m++)
                {
                    int d = f.DecayedScore(M(m));
                    Assert.True(Math.Abs(d) <= prev, $"{f.Category} m={m}");
                    Assert.True(d == 0 || Math.Sign(d) == Math.Sign(f.Score));
                    prev = Math.Abs(d);
                }
                Assert.Equal(0, f.DecayedScore(M(CatalystText.HalfLifeMinutes(f.Category) * PlutoClock.ExpiryHalfLives)));
            }

            Assert.Equal(80, sp.DecayedScore(M(-4)));                 // 허용 오차 이내 미래 = 경과 0
            Assert.Equal(0, sp.DecayedScore(M(-6)));                  // 오차 초과 미래 = 시계 도메인 불일치 → 0(fail-closed)
            Assert.Equal(0, sp.DecayedScore(0));
            Assert.Equal(0, F(Samsung, t0, 90, CatalystCategory.Noise).DecayedScore(M(0)));
            Assert.Equal(0, F(Samsung, t0, 90, CatalystCategory.Noise).Score);
        }

        // ── §4 버퍼 · 분석기 ───────────────────────────────────────────────────

        [Fact]
        public void Buffer_Precedence_Shock_Stickiness_Capacity_And_Rejection()
        {
            long t0 = Utc(Kst10);
            var b = new PlutoSentimentBuffer();
            Assert.False(b.TryRead(Samsung, out _));
            Assert.Equal(PlutoPublishResult.Published, b.Publish(F(Samsung, t0, 80, CatalystCategory.StructuralPositive), t0));
            Assert.True(b.TryRead(Samsung, out var r));
            Assert.Equal(80, r.Score);
            Assert.Equal(1, r.Seq);

            // 약한 후속 호재는 강한 공시를 덮지 않는다 → 감쇄로 약해지면 교체
            Assert.Equal(PlutoPublishResult.Superseded, b.Publish(F(Samsung, t0, 30, CatalystCategory.MomentumPositive), t0));
            long t90 = t0 + 90 * TimeSpan.TicksPerMinute;
            Assert.Equal(PlutoPublishResult.Published, b.Publish(F(Samsung, t90, 30, CatalystCategory.MomentumPositive), t90));
            Assert.True(b.TryRead(Samsung, out r));
            Assert.Equal(CatalystCategory.MomentumPositive, r.Category);

            // 구조적 악재는 더 강한 호재가 있어도 교체 · 충격 수준 동안 호재로 덮을 수 없음
            Assert.Equal(PlutoPublishResult.Published, b.Publish(F(Hynix, t0, 95, CatalystCategory.StructuralPositive), t0));
            Assert.Equal(PlutoPublishResult.Published, b.Publish(F(Hynix, t0, -70, CatalystCategory.StructuralNegative, conf: 90), t0));
            Assert.Equal(PlutoPublishResult.Superseded, b.Publish(F(Hynix, t0, 100, CatalystCategory.StructuralPositive), t0));
            Assert.True(b.TryRead(Hynix, out r));
            Assert.Equal(-70, r.Score);
            long t60 = t0 + 60 * TimeSpan.TicksPerMinute;                // −70×½×0.9 = −31 → 충격 해제 · 더 강한 호재가 교체
            Assert.Equal(PlutoPublishResult.Published, b.Publish(F(Hynix, t60, 100, CatalystCategory.StructuralPositive), t60));

            // 거부: 노이즈 · 밴드 위반 · 종목 0
            Assert.Equal(PlutoPublishResult.Rejected, b.Publish(F(1, t0, 0, CatalystCategory.Noise), t0));
            Assert.Equal(PlutoPublishResult.Rejected, b.Publish(F(1, t0, 30, CatalystCategory.StructuralPositive), t0));
            Assert.Equal(PlutoPublishResult.Rejected, b.Publish(F(0, t0, 80, CatalystCategory.StructuralPositive), t0));
            Assert.Equal(3, b.RejectedCount);
            Assert.False(b.TryRead(1, out _));

            // 포화: 64 슬롯 · 65번째 종목 = BufferFull(재시도 큐잉 0) · 기존 종목 갱신은 계속 가능
            var full = new PlutoSentimentBuffer();
            for (int i = 1; i <= PlutoSentimentBuffer.Capacity; i++)
                Assert.Equal(PlutoPublishResult.Published, full.Publish(F(100_000 + i, t0, 80, CatalystCategory.StructuralPositive), t0));
            Assert.Equal(PlutoPublishResult.BufferFull, full.Publish(F(999_999, t0, 80, CatalystCategory.StructuralPositive), t0));
            Assert.Equal(PlutoPublishResult.Published, full.Publish(F(100_001, t0, 90, CatalystCategory.StructuralPositive), t0));
            for (int i = 1; i <= PlutoSentimentBuffer.Capacity; i++) Assert.True(full.TryRead(100_000 + i, out _));
            Assert.Equal(PlutoSentimentBuffer.Capacity, full.Count);
        }

        [Fact]
        public void Analyzer_Ingest_Validates_Publishes_Per_Symbol_And_Truncates_Digest()
        {
            long now = Utc(Kst10);
            var b = new PlutoSentimentBuffer();
            var a = new PlutoCatalystAnalyzer(b, () => now);
            Assert.Equal(PlutoIngestStatus.InvalidItem, a.Ingest(new PlutoNewsItem(PlutoSource.Dart, " ", now)).Status);
            Assert.Equal(PlutoIngestStatus.InvalidItem, a.Ingest(new PlutoNewsItem(PlutoSource.Dart, "유상증자 결정", 0)).Status);
            Assert.Equal(PlutoIngestStatus.FutureTimestamp, a.Ingest(new PlutoNewsItem(PlutoSource.Dart, "삼성전자 유상증자 결정", now + TimeSpan.FromMinutes(10).Ticks)).Status);
            Assert.Equal(PlutoIngestStatus.Stale, a.Ingest(new PlutoNewsItem(PlutoSource.Dart, "삼성전자 유상증자 결정", now - TimeSpan.FromHours(25).Ticks)).Status);
            Assert.Equal(PlutoIngestStatus.Noise, a.Ingest(new PlutoNewsItem(PlutoSource.Unknown, "삼성전자 루머 확산", now)).Status);
            Assert.Equal(PlutoIngestStatus.NoEntity, a.Ingest(new PlutoNewsItem(PlutoSource.Dart, "유상증자 결정", now)).Status);
            Assert.Equal(0, b.Count);

            string longHead = "삼성전자·SK하이닉스, 3조원 규모 차세대 메모리 단일판매ㆍ공급계약 체결 — 북미 고객사 장기 물량 확보";
            var res = a.Ingest(new PlutoNewsItem(PlutoSource.Dart, longHead, now));
            Assert.Equal(PlutoIngestStatus.Published, res.Status);
            Assert.Equal(2, res.Symbols);
            Assert.Equal(2, res.Published);
            Assert.Equal(CatalystCategory.StructuralPositive, res.Verdict.Category);
            Assert.Equal(85, res.Verdict.Score);                       // 75 + 조원 강조 10
            foreach (long sym in new[] { Samsung, Hynix })
            {
                Assert.True(b.TryRead(sym, out var f));
                Assert.Equal(85, f.Score);
                Assert.Equal(90, f.Confidence);
                Assert.Equal(PlutoSource.Dart, f.Source);
                Assert.True(f.DigestLength <= PlutoDigest.Capacity);
                Assert.StartsWith(f.Headline, longHead);                  // 코드 포인트 경계 절단(깨진 문자 0)
                Assert.DoesNotContain('�', f.Headline);
            }
            Assert.Equal(1, a.PublishedItems);
            Assert.Equal(6, a.RejectedItems + a.NoiseItems);
        }

        [Fact]
        public void Async_Queue_Drops_Oldest_And_Worker_Drains()
        {
            long now = Utc(Kst10);
            var b = new PlutoSentimentBuffer();
            using var a = new PlutoCatalystAnalyzer(b, () => now, queueCapacity: 4);
            for (int i = 0; i < 10; i++)
                Assert.True(a.TryEnqueue(new PlutoNewsItem(PlutoSource.Dart, i < 6 ? "기아 목표주가 상향" : "삼성전자 유상증자 결정", now)));
            Assert.Equal(10, a.Enqueued);
            Assert.Equal(6, a.DroppedOldest);
            a.Start();
            a.Start();
            Assert.True(SpinWait.SpinUntil(() => a.Processed == 4, TimeSpan.FromSeconds(10)));
            Assert.True(b.TryRead(Samsung, out var f));
            Assert.Equal(CatalystCategory.StructuralNegative, f.Category);
            Assert.False(b.TryRead(270, out _));                         // 폐기된 가장 오래된 항목은 처리 0
            Assert.Equal(0, a.Faulted);
        }

        // ── §5 아레나 결속 ─────────────────────────────────────────────────────

        private static void KrxQ(KrxArenaEngine e, long sym, ref DateTime t, long bid, long ask, long bq, long aq, long vol = 100)
        {
            t = t.AddSeconds(1);
            e.OnQuote(sym, bid, ask, bq, aq, vol, t);
        }

        private static AiTradeRationale[] Rationales(KrxArenaEngine e)
        {
            var buf = new AiTradeRationale[KrxArenaEngine.RationaleCapacity];
            return buf.AsSpan(0, e.CopyRationales(buf)).ToArray();
        }

        private static AiTradeRationale[] Rationales(UsTechArenaEngine e)
        {
            var buf = new AiTradeRationale[UsTechArenaEngine.RationaleCapacity];
            return buf.AsSpan(0, e.CopyRationales(buf)).ToArray();
        }

        [Fact]
        public void Krx_News_Catalyst_Drives_Entry_And_Structural_Shock_Forces_Exit()
        {
            var e = new KrxArenaEngine();
            var b = new PlutoSentimentBuffer();
            e.AttachPluto(b);
            Assert.Throws<ArgumentNullException>(() => e.AttachPluto(null!));
            var t = Kst10;
            for (int i = 0; i < KrxArenaEngine.WarmupTicks; i++) KrxQ(e, Samsung, ref t, 70_000, 70_100, 5000, 5000);
            KrxQ(e, Samsung, ref t, 70_000, 70_100, 6500, 3500);            // 불균형만 = 1050 < 3000 → 진입 0
            Assert.Empty(Rationales(e));

            var a = new PlutoCatalystAnalyzer(b, () => Utc(t));
            Assert.Equal(PlutoIngestStatus.Published, a.Ingest(new PlutoNewsItem(PlutoSource.Dart, "삼성전자, 3조원 규모 반도체 단일판매ㆍ공급계약 체결", Utc(t.AddSeconds(1)))).Status);
            KrxQ(e, Samsung, ref t, 70_000, 70_100, 6500, 3500);            // 1050 + 실효 76×100×30/100 = 3330 → 뉴스 주도 진입
            var entry = Assert.Single(Rationales(e));
            Assert.Equal(ArenaTrigger.NewsCatalyst, entry.TriggerType);
            Assert.True(entry.IsWellFormed, entry.HumanReadableSummary);
            Assert.Equal(CatalystCategory.StructuralPositive, entry.News.Factor.Category);
            Assert.Equal(76, entry.News.EffectiveScore);
            Assert.Equal(3330, entry.SentimentScore);
            string s = entry.HumanReadableSummary;
            _out.WriteLine(s);
            Assert.Contains("주도 근거: 플루토 뉴스/공시 촉매", s);
            Assert.Contains("플루토 뉴스 촉매: 구조적 호재 실효 +76점", s);
            Assert.Contains("DART 공시", s);
            Assert.Contains("삼성전자, 3조원 규모", s);

            // 치명적 구조적 악재 → 다음 정산에 즉시 청산(목표/손절 무관)
            Assert.Equal(PlutoIngestStatus.Published, a.Ingest(new PlutoNewsItem(PlutoSource.Dart, "삼성전자 유상증자 결정", Utc(t.AddSeconds(1)))).Status);
            KrxQ(e, Samsung, ref t, 70_000, 70_100, 6500, 3500);
            var rs = Rationales(e);
            Assert.Equal(2, rs.Length);
            var exit = rs[1];
            Assert.Equal(ArenaTrigger.NewsShock, exit.TriggerType);
            Assert.Equal(-1, exit.Side);
            Assert.Equal(72, exit.ConfidenceScore);                        // −80 × 0.9
            Assert.True(exit.IsWellFormed, exit.HumanReadableSummary);
            Assert.Contains("치명적 구조적 악재", exit.HumanReadableSummary);
            Assert.True(e.TryReadSnapshot(out var snap));
            Assert.Equal(0, snap.Ai.OpenPositions);

            // 충격 동안 강한 수급에도 재진입 0
            for (int i = 0; i < 5; i++) KrxQ(e, Samsung, ref t, 70_000, 70_100, 19_000, 1000, 2000);
            Assert.Equal(2, Rationales(e).Length);
        }

        [Fact]
        public void Krx_Shock_Blocks_Entry_That_Would_Otherwise_Fire()
        {
            long Run(bool shock)
            {
                var e = new KrxArenaEngine();
                var b = new PlutoSentimentBuffer();
                e.AttachPluto(b);
                e.SetOvernightMacro(10000);
                var t = Kst10;
                for (int i = 0; i < KrxArenaEngine.WarmupTicks; i++) KrxQ(e, Hynix, ref t, 180_000, 180_500, 5000, 5000);
                if (shock) b.Publish(F(Hynix, Utc(t), -100, CatalystCategory.StructuralNegative, conf: 90), Utc(t));
                KrxQ(e, Hynix, ref t, 182_000, 182_500, 19_000, 1000, 1000);   // 매크로 + 불균형 + 거래량 급증 + 모멘텀
                return e.TryReadSnapshot(out var s) ? s.RationaleCount : -1;
            }
            Assert.Equal(1, Run(shock: false));
            Assert.Equal(0, Run(shock: true));
        }

        [Fact]
        public void Us_News_Catalyst_Entry_And_Shock_Exit_In_Regular_Session()
        {
            var e = new UsTechArenaEngine();
            var b = new PlutoSentimentBuffer();
            e.AttachPluto(b);
            var t = KstUsReg;
            void Q(long bq, long aq) { t = t.AddSeconds(1); e.OnQuote(Nvda, 13_000, 13_001, bq, aq, t); }
            for (int i = 0; i < UsTechArenaEngine.WarmupTicks; i++) Q(5000, 5000);
            Q(8000, 2000);
            Assert.Empty(Rationales(e));                                   // 갭/수급 900 < 3000

            var a = new PlutoCatalystAnalyzer(b, () => Utc(t));
            var res = a.Ingest(new PlutoNewsItem(PlutoSource.Sec, "Nvidia wins contract worth $2 billion from U.S. government", Utc(t.AddSeconds(1))));
            Assert.Equal(PlutoIngestStatus.Published, res.Status);
            Assert.Equal(85, res.Verdict.Score);
            Q(8000, 2000);
            var entry = Assert.Single(Rationales(e));
            Assert.Equal(ArenaTrigger.NewsCatalyst, entry.TriggerType);
            Assert.Equal(ArenaCurrency.Usd, entry.Currency);
            Assert.True(entry.IsWellFormed, entry.HumanReadableSummary);
            Assert.Contains("SEC 공시", entry.HumanReadableSummary);
            Assert.Contains("Nvidia wins contract", entry.HumanReadableSummary);
            _out.WriteLine(entry.HumanReadableSummary);

            a.Ingest(new PlutoNewsItem(PlutoSource.Sec, "Nvidia auditor resigns, going concern doubt", Utc(t.AddSeconds(1))));
            Q(8000, 2000);
            var rs = Rationales(e);
            Assert.Equal(2, rs.Length);
            Assert.Equal(ArenaTrigger.NewsShock, rs[1].TriggerType);
            Assert.True(rs[1].IsWellFormed, rs[1].HumanReadableSummary);
        }

        [Fact]
        public void Triggers_Are_Classified_And_Default_News_Is_Well_Formed()
        {
            Assert.True(ArenaTriggerText.IsEntry(ArenaTrigger.NewsCatalyst));
            Assert.False(ArenaTriggerText.IsExit(ArenaTrigger.NewsCatalyst));
            Assert.True(ArenaTriggerText.IsExit(ArenaTrigger.NewsShock));
            Assert.False(ArenaTriggerText.IsEntry(ArenaTrigger.NewsShock));
            Assert.NotEqual("미분류", ArenaTriggerText.Label(ArenaTrigger.NewsCatalyst));
            Assert.NotEqual("미분류", ArenaTriggerText.Label(ArenaTrigger.NewsShock));
            PlutoNewsTag none = default;
            Assert.True(none.IsWellFormed);
            Assert.False(none.HasCatalyst);
            Assert.False(none.IsShock);
            Assert.Equal(0, none.NewsBp);
            // 뉴스 0 = 시그널 동일(기존 동작 보존)
            var k0 = KrxArenaEngine.Evaluate(500, 6500, 3500, 100 << 10, 100 << 10, 70_050 << 10, 70_000 << 10);
            var k1 = KrxArenaEngine.Evaluate(500, 6500, 3500, 100 << 10, 100 << 10, 70_050 << 10, 70_000 << 10, 0);
            Assert.Equal((k0.Score, k0.Dominant), (k1.Score, k1.Dominant));
            Assert.Equal(10000, KrxArenaEngine.Evaluate(10000, 10000, 0, 1000 << 10, 100 << 10, 71_000 << 10, 70_000 << 10, 10000).Score);   // ±10000 포화
        }

        // ── §6 δ=0 · 동시성 ────────────────────────────────────────────────────

        [Fact]
        public void Sentiment_Lookup_And_Engine_Settlement_100k_Allocate_Zero_Bytes()
        {
            long t0 = Utc(Kst10);
            var b = new PlutoSentimentBuffer();
            long[] syms = { Samsung, Hynix, 5380, 68270 };
            b.Publish(F(Samsung, t0, 85, CatalystCategory.StructuralPositive, 90, "삼성전자 수주"), t0);
            b.Publish(F(Hynix, t0, -80, CatalystCategory.StructuralNegative, 90, "유상증자"), t0);
            b.Publish(F(5380, t0, 35, CatalystCategory.MomentumPositive, 70, "목표가 상향"), t0);
            long acc = 0;
            void Lookups(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    long now = t0 + (i & 4095) * TimeSpan.TicksPerSecond;
                    if (b.TryRead(syms[i & 3], out var f))
                    {
                        var tag = new PlutoNewsTag(in f, now);
                        acc += tag.NewsBp + (tag.IsShock ? 1 : 0);
                    }
                }
            }
            Lookups(10_000);
            long lookupBytes = MinAllocated(() => Lookups(100_000));

            var e = new KrxArenaEngine();
            e.AttachPluto(b);
            var t = Kst10;
            int k = 0;
            void Settle(int n)
            {
                for (int i = 0; i < n; i++, k++)
                {
                    t = t.AddTicks(TimeSpan.TicksPerMillisecond * 50);
                    if (t >= Kst10.AddMinutes(5)) t = Kst10;                  // 뉴스 신선도 유지(경과 0~5분) — 진입/청산이 측정 구간 안에서 반복
                    long sym = syms[k & 3];
                    long px = 68_000 + ((k >> 6) & 63) * 100;                 // 톱니 68,000~74,300 → 익절 · 손절 · 반전 청산 유발
                    e.OnQuote(sym, px, px + 100, 3000 + (k & 1023) * 10, 3000, 100 + (k & 63), t);
                    if ((k & 15) == 0) e.SubmitHuman(sym, (k & 16) == 0 ? 1 : -1, 1);
                }
            }
            Settle(20_000);
            Assert.True(e.TryReadSnapshot(out var s0));
            long settleBytes = MinAllocated(() => Settle(100_000));
            Assert.True(e.TryReadSnapshot(out var s));
            _out.WriteLine($"lookup δ={lookupBytes}B acc={acc} · settle δ={settleBytes}B ticks={s.Ticks} aiFills={s.Ai.Fills} rationales={s.RationaleCount}");
            Assert.Equal(0, lookupBytes);
            Assert.Equal(0, settleBytes);
            Assert.True(acc != 0);
            Assert.True(s.Ai.Fills - s0.Ai.Fills >= 10, $"측정 구간에서 뉴스 결속 AI 가상 체결이 반복돼야 한다({s.Ai.Fills - s0.Ai.Fills})");
            Assert.Contains(Rationales(e), r => r.News.HasCatalyst);
        }

        [Fact]
        public void HotPath_Methods_Have_No_Il_Allocation_Sites()
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var targets = new (Type t, string[] names)[]
            {
                (typeof(PlutoSentimentBuffer), new[] { "Publish", "TryRead", "TryReadTag", "Dominates", "Lock", "Home", "Claim", "Find" }),
                (typeof(PlutoSentimentFactor), new[] { "DecayedScore", "EffectiveScore", "CopyDigest", "WithSeq", "get_IsWellFormed" }),
                (typeof(PlutoNewsTag), new[] { "get_IsShock", "get_HasCatalyst", "get_NewsBp", "get_IsWellFormed" }),
                (typeof(CatalystText), new[] { "Band", "InBand", "HalfLifeMinutes", "HalfLifeTicks" }),
            };
            foreach (var (t, names) in targets)
                foreach (var name in names)
                {
                    // [v12.18.64-DEV] 가상 시계 오버로드(Publish(in, IVirtualClock)) 추가 → 같은 이름의 전 오버로드를 검사
                    var overloads = t.GetMethods(all).Where(x => x.Name == name).ToArray();
                    Assert.True(overloads.Length > 0, $"{t.Name}.{name}");
                    foreach (var m in overloads)
                    {
                        var sites = ZeroAllocIlInspector.Inspect(m);
                        Assert.True(sites.Count == 0, $"{t.Name}.{name}\n" + string.Join("\n", sites));
                    }
                }
            foreach (var ctor in typeof(PlutoNewsTag).GetConstructors().Concat<MethodBase>(typeof(PlutoSentimentFactor).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)))
                Assert.Empty(ZeroAllocIlInspector.Inspect(ctor));
        }

        [Fact]
        public void Concurrent_Writers_And_Readers_Never_Observe_Torn_Factors()
        {
            long t0 = Utc(Kst10);
            var b = new PlutoSentimentBuffer();
            const int Writers = 4, PerWriter = 50_000, Readers = 2, Symbols = 8;
            int done = 0;
            long torn = 0, reads = 0;
            // 전 게시가 교체 경로를 타도록: |점수| = 100 고정 · 같은 시각(감쇄 동률 → 새 판정 교체) · 악재 확신도 < 60(충격 고착 회피).
            var writers = Enumerable.Range(0, Writers).Select(w => new Thread(() =>
            {
                var rnd = new Random(w + 11);
                for (int i = 0; i < PerWriter; i++)
                {
                    bool neg = rnd.Next(2) == 0;
                    int score = neg ? -100 : 100;
                    int conf = neg ? rnd.Next(0, 60) : rnd.Next(0, 101);
                    var cat = neg ? CatalystCategory.StructuralNegative : CatalystCategory.StructuralPositive;
                    var f = F(1 + rnd.Next(Symbols), t0, score, cat, conf, $"S{score}C{conf}W{w}I{i}");
                    b.Publish(in f, t0);
                }
                Interlocked.Increment(ref done);
            })).ToArray();
            var readers = Enumerable.Range(0, Readers).Select(_ => new Thread(() =>
            {
                Span<byte> d = stackalloc byte[PlutoDigest.Capacity];
                while (Volatile.Read(ref done) < Writers)
                    for (long sym = 1; sym <= Symbols; sym++)
                    {
                        if (!b.TryRead(sym, out var f)) continue;
                        Interlocked.Increment(ref reads);
                        int n = f.CopyDigest(d);
                        string digest = Encoding.UTF8.GetString(d[..n]);
                        if (f.Symbol != sym || !f.IsWellFormed || f.TimestampUtcTicks != t0 || !digest.StartsWith($"S{f.Score}C{f.Confidence}W", StringComparison.Ordinal))
                            Interlocked.Increment(ref torn);
                    }
            })).ToArray();
            foreach (var r in readers) r.Start();
            foreach (var w in writers) w.Start();
            foreach (var w in writers) w.Join();
            foreach (var r in readers) r.Join();
            _out.WriteLine($"reads={reads} published={b.Published} superseded={b.SupersededCount} contended={b.ContendedReads} lastSeq={b.LastSeq}");
            Assert.Equal(0, torn);
            Assert.True(reads > 0);
            Assert.Equal(Writers * PerWriter, b.Published);           // 전량 교체 경로
            Assert.Equal(0, b.SupersededCount);
            Assert.Equal(b.Published, b.LastSeq);
            Assert.Equal(Symbols, b.Count);
        }
    }
}
