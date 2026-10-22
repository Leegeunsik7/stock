// ============================================================================
// PlutoCatalystAnalyzer — v12.18.63-DEV: 플루토 브레인 1단계 — 실시간 뉴스/공시 촉매 분석기(비동기 지각 계층 · 콜드 경로)
//
//   파이프라인(항목 1건): 검증(빈 헤드라인 · 무효/미래(>5분)/과거(>24h) 시각 = 기각) → 노이즈 필터 → 촉매 분류 → 엔티티(종목) 매핑 →
//     종목별 PlutoSentimentFactor → PlutoSentimentBuffer.Publish(seqlock). 아레나 핫패스는 버퍼만 판독(텍스트 처리 0).
//   ① 노이즈(0점 · 즉시 기각): 부인/사실무근 · 비공시 출처의 루머/찌라시 · 비공시 출처의 낚시성 헤드라인 · 촉매 없는 단순 시황(급등/급락/특징주).
//     공시(DART · SEC) = 1차 출처 → 루머/낚시 필터 면제.
//   ② 분류: 키워드 규칙 표(한/영) 전체 평가 → |점수| 최대 규칙 채택(동률 = 음수 우선 · 보수적). 대규모/조원/billion 강조 = 구조적 |점수| +10.
//     반전 표지(해지 · 철회 · 무산 · 취소 · terminated …): 구조적 호재 → 구조적 악재 −60(계약 해지) · 모멘텀 호재 → 단기 악재 −30 ·
//     구조적 악재 → 모멘텀 호재 +30(유상증자 철회) · 단기 악재 → 노이즈.
//   ③ 확신도: 출처 기본(DART/SEC 90 · 통신사 70 · 미상 45) + 같은 방향 추가 규칙 1건당 +5(최대 +10) − 반대 방향 규칙 혼재 20.
//   ④ 엔티티: 별칭 표(한글 사명 · 영문 사명 · 티커 · 6자리 코드) — ASCII 별칭은 단어 경계 검사(pineapple ≠ Apple) ·
//     겹치는 매칭은 긴 별칭 우선(카카오뱅크 ≠ 카카오) · 출처 힌트(DART 종목코드 6자리 · 티커) 우선 · 항목당 최대 4종목.
//   ⑤ 비동기: TryEnqueue(임의 스레드) → 유계 채널(DropOldest · 폐기 계수 · 재시도 0) → Start() 워커가 Ingest. 항목 예외 = 계수 후 다음 항목.
//   ★실시간 뉴스 소스(DART OpenAPI · SEC EDGAR · 통신사) 미결선 — 주입 API(Ingest · TryEnqueue)만 제공.
//   ★liveArmed:false — 이 분석기의 출력은 가상 아레나 시그널뿐(주문 · 브로커 경로 0).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using StockManager.Engines.Arena.Us;

namespace StockManager.Engines.Cognitive.News
{
    /// <summary>뉴스/공시 1건(UTC 발생 시각 · SymbolHint = DART 종목코드 6자리 또는 티커).</summary>
    public sealed record PlutoNewsItem(PlutoSource Source, string Headline, long PublishedUtcTicks, string? Body = null, string? SymbolHint = null);

    public enum PlutoNoiseReason : byte
    {
        None = 0,
        Empty = 1,
        Denial = 2,
        Rumor = 3,
        Clickbait = 4,
        MarketChatter = 5,
        NoCatalyst = 6,
        Reversed = 7,
    }

    /// <summary>분류 결과(종목 무관). Rule = 채택 규칙 이름(노이즈 = 사유).</summary>
    public readonly record struct PlutoVerdict(CatalystCategory Category, int Score, int Confidence, string Rule, PlutoNoiseReason Noise)
    {
        public bool IsNoise => Category == CatalystCategory.Noise;
    }

    public enum PlutoIngestStatus : byte
    {
        Published = 0,
        Noise = 1,
        NoEntity = 2,
        InvalidItem = 3,
        FutureTimestamp = 4,
        Stale = 5,
        Superseded = 6,
        BufferFull = 7,
    }

    public readonly record struct PlutoIngestResult(PlutoIngestStatus Status, PlutoVerdict Verdict, int Symbols, int Published);

    public sealed class PlutoCatalystAnalyzer : IDisposable
    {
        public const int MaxEntitiesPerItem = 4, MaxHeadlineChars = 1024, MaxBodyChars = 8192, DefaultQueueCapacity = 1024;
        public const int DisclosureConfidence = 90, WireConfidence = 70, UnknownConfidence = 45;
        public const int ExtraRuleBonus = 5, MaxExtraBonus = 10, MixedPenalty = 20, IntensifierBonus = 10;
        public static readonly long MaxAgeTicks = TimeSpan.FromHours(24).Ticks;

        private readonly record struct Rule(string Name, CatalystCategory Category, int Score, string[] Keys);
        private readonly record struct Alias(string Key, long Symbol, bool Ascii);

        // 규칙 표(키 = 소문자 · 영문 키는 단어 경계 검사).
        private static readonly Rule[] s_rules =
        {
            new("횡령 · 배임", CatalystCategory.StructuralNegative, -100, new[] { "횡령", "배임", "embezzlement" }),
            new("분식회계", CatalystCategory.StructuralNegative, -100, new[] { "분식회계", "회계부정", "accounting fraud" }),
            new("감사의견 거절 · 부적정", CatalystCategory.StructuralNegative, -100, new[] { "감사의견 거절", "의견거절", "감사의견 부적정", "부적정 의견", "disclaimer of opinion", "adverse opinion" }),
            new("감사의견 한정", CatalystCategory.StructuralNegative, -80, new[] { "감사의견 한정", "한정 의견" }),
            new("상장폐지 · 회생 · 부도", CatalystCategory.StructuralNegative, -100, new[] { "상장폐지", "회생절차", "파산", "부도", "delisting", "bankruptcy", "chapter 11" }),
            new("SEC 제재 · 계속기업 의문", CatalystCategory.StructuralNegative, -90, new[] { "going concern", "sec charges", "restatement", "auditor resigns", "auditor resignation" }),
            new("유상증자", CatalystCategory.StructuralNegative, -80, new[] { "유상증자" }),
            new("전환사채 · 신주인수권부사채", CatalystCategory.StructuralNegative, -70, new[] { "전환사채", "신주인수권부사채", "교환사채", "convertible notes", "convertible bonds" }),
            new("지분 희석 공모", CatalystCategory.StructuralNegative, -70, new[] { "secondary offering", "share offering", "stock offering", "dilution" }),
            new("실적 쇼크", CatalystCategory.MomentumNegative, -50, new[] { "어닝쇼크", "어닝 쇼크", "실적 부진", "적자전환", "misses estimates", "missed estimates" }),
            new("리콜 · 제품 결함", CatalystCategory.MomentumNegative, -45, new[] { "리콜", "결함", "recall", "recalls", "defect" }),
            new("소송 · 당국 조사", CatalystCategory.MomentumNegative, -40, new[] { "소송", "피소", "압수수색", "lawsuit", "probe", "investigation" }),
            new("투자의견 하향", CatalystCategory.MomentumNegative, -40, new[] { "투자의견 하향", "downgrade", "downgrades", "downgraded" }),
            new("목표주가 하향", CatalystCategory.MomentumNegative, -35, new[] { "목표주가 하향", "목표가 하향", "price target cut", "cuts price target", "lowers price target" }),
            new("경영진 구설", CatalystCategory.MomentumNegative, -25, new[] { "구설", "논란", "갑질", "scandal" }),
            new("인허가 승인", CatalystCategory.StructuralPositive, 85, new[] { "품목허가", "판매 승인", "허가 승인", "fda 승인", "fda approval", "regulatory approval" }),
            new("사상 최대 실적", CatalystCategory.StructuralPositive, 80, new[] { "사상 최대", "역대 최대 실적", "최대 실적", "record revenue", "record profit", "record earnings" }),
            new("대규모 수주 · 공급계약", CatalystCategory.StructuralPositive, 75, new[] { "수주", "공급계약", "단일판매", "awarded contract", "wins contract", "contract award" }),
            new("어닝 서프라이즈", CatalystCategory.StructuralPositive, 70, new[] { "어닝서프라이즈", "어닝 서프라이즈", "earnings surprise" }),
            new("핵심 특허 취득", CatalystCategory.StructuralPositive, 65, new[] { "특허 취득", "특허권 취득", "patent granted", "secures patent" }),
            new("실적 개선", CatalystCategory.MomentumPositive, 50, new[] { "흑자전환", "beats estimates", "tops estimates" }),
            new("자사주 매입", CatalystCategory.MomentumPositive, 45, new[] { "자사주 매입", "자기주식 취득", "buyback", "share repurchase" }),
            new("투자의견 상향", CatalystCategory.MomentumPositive, 40, new[] { "투자의견 상향", "upgrade", "upgrades", "upgraded" }),
            new("목표주가 상향", CatalystCategory.MomentumPositive, 35, new[] { "목표주가 상향", "목표가 상향", "price target raised", "raises price target", "lifts price target" }),
            new("MOU · 업무협약", CatalystCategory.MomentumPositive, 30, new[] { "양해각서", "업무협약", "mou", "partnership" }),
            new("테마 수급", CatalystCategory.MomentumPositive, 20, new[] { "테마", "수혜" }),
        };

        private static readonly string[] s_denial = { "사실무근", "사실 무근", "denies", "denied" };
        private static readonly string[] s_rumor = { "루머", "찌라시", "카더라", "풍문", "rumor", "rumors", "rumour", "unconfirmed", "speculation" };
        private static readonly string[] s_clickbait = { "충격", "경악", "헉", "대박", "결국", "you won't believe", "shocking", "!!", "?!" };
        private static readonly string[] s_chatter = { "특징주", "급등", "급락", "상승 마감", "하락 마감", "강세", "약세", "shares rose", "shares fell", "stock jumps", "stock falls", "surges", "plunges", "rallies" };
        private static readonly string[] s_reversal = { "해지", "철회", "무산", "취소", "파기", "terminated", "terminates", "withdrawn", "withdraws", "canceled", "cancelled", "cancels", "scrapped" };
        private static readonly string[] s_intensifier = { "대규모", "조원", "역대", "billion" };

        private static readonly Alias[] s_aliases = BuildAliases();

        private readonly PlutoSentimentBuffer _buffer;
        private readonly Func<long> _utcNow;
        private readonly Channel<PlutoNewsItem> _queue;
        private readonly CancellationTokenSource _cts = new();
        private Task? _worker;
        private int _started;
        private long _enqueued, _droppedOldest, _processed, _published, _noise, _rejected, _faulted;

        public PlutoCatalystAnalyzer(PlutoSentimentBuffer buffer, Func<long>? utcNowTicks = null, int queueCapacity = DefaultQueueCapacity)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            if (queueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
            _utcNow = utcNowTicks ?? (static () => DateTime.UtcNow.Ticks);
            _queue = Channel.CreateBounded<PlutoNewsItem>(
                new BoundedChannelOptions(queueCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false },
                _ => Interlocked.Increment(ref _droppedOldest));
        }

        public PlutoSentimentBuffer Buffer => _buffer;
        public long Enqueued => Interlocked.Read(ref _enqueued);
        public long DroppedOldest => Interlocked.Read(ref _droppedOldest);
        public long Processed => Interlocked.Read(ref _processed);
        public long PublishedItems => Interlocked.Read(ref _published);
        public long NoiseItems => Interlocked.Read(ref _noise);
        public long RejectedItems => Interlocked.Read(ref _rejected);
        public long Faulted => Interlocked.Read(ref _faulted);

        // ── 비동기 지각 계층 ─────────────────────────────────────────────────────

        /// <summary>항목 비동기 투입(임의 스레드). 큐 포화 = 가장 오래된 항목 폐기(DroppedOldest). 처분 후 = false.</summary>
        public bool TryEnqueue(PlutoNewsItem item)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!_queue.Writer.TryWrite(item)) return false;
            Interlocked.Increment(ref _enqueued);
            return true;
        }

        /// <summary>백그라운드 워커 기동(1회 · 재호출 무시).</summary>
        public void Start()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
            _worker = Task.Run(() => RunAsync(_cts.Token));
        }

        /// <summary>큐 소비 루프(단일 소비자). 취소 · 큐 완료 = 정상 종료.</summary>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var reader = _queue.Reader;
            try
            {
                while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var item))
                    {
                        try { Ingest(item); }
                        catch (Exception) { Interlocked.Increment(ref _faulted); }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }

        public void Dispose()
        {
            _queue.Writer.TryComplete();
            _cts.Cancel();
            try { _worker?.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
            _cts.Dispose();
        }

        // ── 동기 파이프라인 ───────────────────────────────────────────────────────

        /// <summary>항목 1건 분석 · 종목별 게시(호출 스레드). 결과 = 상태 · 판정 · 매핑 종목 수 · 게시 수.</summary>
        public PlutoIngestResult Ingest(PlutoNewsItem item)
        {
            ArgumentNullException.ThrowIfNull(item);
            Interlocked.Increment(ref _processed);
            long now = _utcNow();
            if (string.IsNullOrWhiteSpace(item.Headline) || item.PublishedUtcTicks <= 0 || item.PublishedUtcTicks > DateTime.MaxValue.Ticks)
                return Reject(PlutoIngestStatus.InvalidItem, default);
            if (item.PublishedUtcTicks - now > PlutoClock.MaxSkewTicks) return Reject(PlutoIngestStatus.FutureTimestamp, default);
            if (now - item.PublishedUtcTicks > MaxAgeTicks) return Reject(PlutoIngestStatus.Stale, default);

            string headline = item.Headline.Length > MaxHeadlineChars ? item.Headline[..MaxHeadlineChars] : item.Headline;
            string? body = item.Body is { Length: > MaxBodyChars } b ? b[..MaxBodyChars] : item.Body;
            var verdict = Classify(item.Source, headline, body);
            if (verdict.IsNoise)
            {
                Interlocked.Increment(ref _noise);
                return new PlutoIngestResult(PlutoIngestStatus.Noise, verdict, 0, 0);
            }

            Span<long> symbols = stackalloc long[MaxEntitiesPerItem];
            int n = ResolveSymbols(headline + "\n" + body, item.SymbolHint, symbols);
            if (n == 0) return Reject(PlutoIngestStatus.NoEntity, verdict);

            Span<byte> digest = stackalloc byte[PlutoDigest.Capacity];
            int len = PlutoDigest.Encode(headline, digest);
            int published = 0, superseded = 0;
            for (int i = 0; i < n; i++)
            {
                var f = new PlutoSentimentFactor(symbols[i], item.PublishedUtcTicks, verdict.Score, verdict.Confidence, verdict.Category, item.Source, digest[..len]);
                switch (_buffer.Publish(in f, now))
                {
                    case PlutoPublishResult.Published: published++; break;
                    case PlutoPublishResult.Superseded: superseded++; break;
                }
            }
            if (published > 0) Interlocked.Increment(ref _published);
            var status = published > 0 ? PlutoIngestStatus.Published : superseded > 0 ? PlutoIngestStatus.Superseded : PlutoIngestStatus.BufferFull;
            return new PlutoIngestResult(status, verdict, n, published);
        }

        private PlutoIngestResult Reject(PlutoIngestStatus status, PlutoVerdict verdict)
        {
            Interlocked.Increment(ref _rejected);
            return new PlutoIngestResult(status, verdict, 0, 0);
        }

        /// <summary>촉매 분류(순수 함수 · 종목 무관).</summary>
        public static PlutoVerdict Classify(PlutoSource source, string headline, string? body = null)
        {
            if (string.IsNullOrWhiteSpace(headline)) return Noise(PlutoNoiseReason.Empty);
            string text = (headline + "\n" + body).ToLowerInvariant();
            string head = headline.ToLowerInvariant();
            bool official = source is PlutoSource.Dart or PlutoSource.Sec;

            if (ContainsAny(text, s_denial)) return Noise(PlutoNoiseReason.Denial);
            if (!official && ContainsAny(text, s_rumor)) return Noise(PlutoNoiseReason.Rumor);
            if (!official && ContainsAny(head, s_clickbait)) return Noise(PlutoNoiseReason.Clickbait);

            int best = -1, pos = 0, neg = 0;
            for (int i = 0; i < s_rules.Length; i++)
            {
                if (!ContainsAny(text, s_rules[i].Keys)) continue;
                if (s_rules[i].Score > 0) pos++; else neg++;
                if (best < 0) { best = i; continue; }
                int a = Math.Abs(s_rules[i].Score), c = Math.Abs(s_rules[best].Score);
                if (a > c || (a == c && s_rules[i].Score < 0)) best = i;
            }
            if (best < 0) return Noise(ContainsAny(text, s_chatter) ? PlutoNoiseReason.MarketChatter : PlutoNoiseReason.NoCatalyst);

            var rule = s_rules[best];
            var cat = rule.Category;
            int score = rule.Score;
            string name = rule.Name;
            if (ContainsAny(text, s_reversal))
            {
                switch (cat)
                {
                    case CatalystCategory.StructuralPositive: cat = CatalystCategory.StructuralNegative; score = -CatalystText.StructuralMin; name += " 해지 · 무산"; break;
                    case CatalystCategory.MomentumPositive: cat = CatalystCategory.MomentumNegative; score = -30; name += " 취소"; break;
                    case CatalystCategory.StructuralNegative: cat = CatalystCategory.MomentumPositive; score = 30; name += " 철회"; break;
                    default: return Noise(PlutoNoiseReason.Reversed);
                }
            }
            else if ((cat is CatalystCategory.StructuralPositive or CatalystCategory.StructuralNegative) && ContainsAny(text, s_intensifier))
                score += Math.Sign(score) * IntensifierBonus;
            var (min, max) = CatalystText.Band(cat);
            score = Math.Clamp(score, min, max);

            int confidence = source switch
            {
                PlutoSource.Dart or PlutoSource.Sec => DisclosureConfidence,
                PlutoSource.Wire => WireConfidence,
                _ => UnknownConfidence,
            };
            int same = rule.Score > 0 ? pos : neg, opposite = rule.Score > 0 ? neg : pos;
            confidence += Math.Min(MaxExtraBonus, (same - 1) * ExtraRuleBonus);
            if (opposite > 0) confidence -= MixedPenalty;
            return new PlutoVerdict(cat, score, Math.Clamp(confidence, 0, 100), name, PlutoNoiseReason.None);
        }

        private static PlutoVerdict Noise(PlutoNoiseReason r) => new(CatalystCategory.Noise, 0, 0, r.ToString(), r);

        /// <summary>
        /// 종목 매핑(콜드 경로). 힌트(6자리 코드 · 1~8자 영문 티커) 우선 → 본문 별칭(긴 별칭 우선 · ASCII 단어 경계) 첫 등장 순.
        /// 반환 = dest 에 기록한 종목 수(중복 0 · 최대 dest.Length).
        /// </summary>
        public static int ResolveSymbols(string text, string? hint, Span<long> destination)
        {
            int n = 0;
            if (TryParseHint(hint, out long h) && n < destination.Length) destination[n++] = h;
            if (string.IsNullOrEmpty(text)) return n;
            string lower = text.ToLowerInvariant();
            var hits = new List<(int Start, int Len, long Symbol)>();
            foreach (var a in s_aliases)
            {
                int from = 0;
                while (from < lower.Length)
                {
                    int at = lower.IndexOf(a.Key, from, StringComparison.Ordinal);
                    if (at < 0) break;
                    if (!a.Ascii || WordBoundary(lower, at, a.Key.Length)) hits.Add((at, a.Key.Length, a.Symbol));
                    from = at + 1;
                }
            }
            hits.Sort(static (x, y) => x.Start != y.Start ? x.Start.CompareTo(y.Start) : y.Len.CompareTo(x.Len));
            int coveredEnd = -1;
            foreach (var (start, len, sym) in hits)
            {
                if (start < coveredEnd) continue;                 // 더 긴(먼저 정렬된) 별칭 구간 안 = 무시
                coveredEnd = start + len;
                if (n >= destination.Length) break;
                if (destination[..n].Contains(sym)) continue;
                destination[n++] = sym;
            }
            return n;
        }

        private static bool TryParseHint(string? hint, out long symbol)
        {
            symbol = 0;
            if (string.IsNullOrWhiteSpace(hint)) return false;
            var s = hint.AsSpan().Trim();
            if (s.Length == 6 && IsAllDigits(s)) return long.TryParse(s, out symbol) && symbol > 0;
            if (s.Length is >= 1 and <= 8 && IsAllAsciiLetters(s))
            {
                Span<char> up = stackalloc char[s.Length];
                s.ToUpperInvariant(up);
                symbol = UsTechUniverse.Pack(up);
                return symbol > 0;
            }
            return false;
        }

        private static bool IsAllDigits(ReadOnlySpan<char> s)
        {
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        private static bool IsAllAsciiLetters(ReadOnlySpan<char> s)
        {
            foreach (char c in s) if (!char.IsAsciiLetter(c)) return false;
            return true;
        }

        private static bool ContainsAny(string lowerText, string[] keys)
        {
            foreach (var k in keys)
            {
                int from = 0;
                while (from < lowerText.Length)
                {
                    int at = lowerText.IndexOf(k, from, StringComparison.Ordinal);
                    if (at < 0) break;
                    if (!IsAsciiWordKey(k) || WordBoundary(lowerText, at, k.Length)) return true;
                    from = at + 1;
                }
            }
            return false;
        }

        // 영문/숫자로 시작 · 끝나는 키만 단어 경계 검사(한글 키 = 조사 결합 허용 · 기호 키 "!!" = 경계 무관).
        private static bool IsAsciiWordKey(string k) => k.Length > 0 && char.IsAsciiLetterOrDigit(k[0]) && char.IsAsciiLetterOrDigit(k[^1]);

        private static bool WordBoundary(string s, int at, int len)
        {
            bool left = at == 0 || !char.IsAsciiLetterOrDigit(s[at - 1]);
            int end = at + len;
            bool right = end >= s.Length || !char.IsAsciiLetterOrDigit(s[end]);
            return left && right;
        }

        private static Alias[] BuildAliases()
        {
            var list = new List<Alias>();
            void Krx(long code, params string[] names)
            {
                list.Add(new Alias(code.ToString("D6", System.Globalization.CultureInfo.InvariantCulture), code, true));
                foreach (var n in names) list.Add(new Alias(n.ToLowerInvariant(), code, IsAsciiWordKey(n.ToLowerInvariant())));
            }
            Krx(5930, "삼성전자", "Samsung Electronics");
            Krx(660, "SK하이닉스", "하이닉스", "SK hynix");
            Krx(373220, "LG에너지솔루션");
            Krx(207940, "삼성바이오로직스");
            Krx(5380, "현대차", "현대자동차", "Hyundai Motor");
            Krx(270, "기아");
            Krx(35420, "NAVER", "네이버");
            Krx(35720, "카카오");
            Krx(323410, "카카오뱅크");
            Krx(68270, "셀트리온");
            Krx(5490, "POSCO홀딩스", "포스코홀딩스");
            Krx(6400, "삼성SDI");
            Krx(51910, "LG화학");
            Krx(12330, "현대모비스");
            Krx(105560, "KB금융");

            void Us(string ticker, params string[] names)
            {
                long sym = UsTechUniverse.Pack(ticker);
                list.Add(new Alias(ticker.ToLowerInvariant(), sym, true));
                foreach (var n in names) list.Add(new Alias(n.ToLowerInvariant(), sym, IsAsciiWordKey(n.ToLowerInvariant())));
            }
            Us("AAPL", "Apple", "애플");
            Us("NVDA", "Nvidia", "엔비디아");
            Us("MSFT", "Microsoft", "마이크로소프트");
            Us("AMZN", "Amazon", "아마존");
            Us("TSLA", "Tesla", "테슬라");
            return list.ToArray();
        }
    }
}
