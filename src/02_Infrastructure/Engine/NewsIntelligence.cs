// ============================================================================
// NewsIntelligence — v4.93.0: 실시간 뉴스 인텔리전스 코어(δ=0 인입·세맨틱 쇼크·감성 속도 브레이커)
//
//  실시간 자연어 뉴스 스트림을 힙 오염 없이 포획하고, 거시/공급망 쇼크를 포트폴리오에 전파하며, 감성 폭주를
//  발주 무장 강등으로 물리 차단하는 정보 인텔리전스 레이어.
//
//  ── 부품 ────────────────────────────────────────────────────────────────────
//   · NewsPayload         — blittable 값 타입(문자열 0·숫자 인코딩). δ=0 링 슬롯. SentimentRaw==0 = 미분석 격리.
//   · NewsIntelParser     — ReadOnlySpan<byte> JSON → NewsPayload(Utf8JsonReader·ToString 없음).
//   · NewsDispatchRing    — 무블록 라우팅 큐(bounded·DropOldest). 핫패스 발주 드레인과 스레드 격리.
//   · SemanticShockMatrix — LLM 은 [ShockClass,Severity] ACL DTO 만 추출(수학 금지). 코어가 고정소수점 β 행렬로
//                           관련 자산군 Stressed 가치 하향(floor·QChaos 스트레스 모델 재사용). 환각 유입 차단.
//   · SentimentVelocityBreaker — 128표본 롤링 카운트 창(결정론·벽시계 미사용) 감성 하향 드리프트 δ=0 3σ. 폭주 시 IgnitionLock.Downgrade(발주 강등).
//
//  ── 불변식 ──────────────────────────────────────────────────────────────────
//   liveArmed:false : 쇼크 클램프·브레이커 강등은 '방어 방향'(가치 하향·무장 하강)만 — 실주문 송출 경로 무접촉.
//   fail-closed     : 파싱 실패·오염·워밍업 미달 → 미분석/무충격/무트립(모호하면 통과 금지). δ=0 정수 산술.
//   결정론          : 벽시계/실난수 미사용(시각은 주입). 한국색상(호재 빨강/악재 파랑)은 UI.
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;

namespace MS2E.Infrastructure
{
    /// <summary>거시/섹터 쇼크 분류(LLM ACL 추출 대상). None=무충격.</summary>
    public enum NewsShockClass : byte { None = 0, MacroInterest = 1, SupplyChainLithium = 2, Geopolitical = 3 }

    /// <summary>감성 3분류(±0.3 임계, 0=미분석). 한국 색상: Bullish/호재=빨강, Bearish/악재=파랑.</summary>
    public enum SentimentState : byte { Unanalyzed = 0, Bullish = 1, Neutral = 2, Bearish = 3 }

    /// <summary>δ=0 뉴스 인입 페이로드(blittable·문자열 0·숫자 인코딩). 링 슬롯에 값 복사.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct NewsPayload
    {
        public readonly long SymbolCode;      // KSE 6자리(0=시장 전반)
        public readonly long SentimentRaw;    // QFixed(-1..+1 × 10^8). ★0 = 미분석 센티널(다운스트림 격리)
        public readonly long PublishedTicks;  // UTC ticks(가상 타임스탬프 추적용)
        public readonly long SeverityRaw;     // 쇼크 심각도(0..1 × 10^8)
        public readonly long SourceCode;      // 출처 식별(선택·해시)
        public readonly byte ShockClassByte;  // NewsShockClass

        public NewsPayload(long symbolCode, long sentimentRaw, long publishedTicks, long severityRaw, long sourceCode, byte shockClassByte)
        { SymbolCode = symbolCode; SentimentRaw = sentimentRaw; PublishedTicks = publishedTicks; SeverityRaw = severityRaw; SourceCode = sourceCode; ShockClassByte = shockClassByte; }

        /// <summary>decimal 편의 팩토리(테스트/조립부용 — 내부 QFixed raw 변환).</summary>
        public static NewsPayload Of(long symbolCode, decimal sentiment, long publishedTicks,
                                     NewsShockClass shock = NewsShockClass.None, decimal severity = 0m, long sourceCode = 0)
            => new(symbolCode, QMath.FromDecimal(sentiment), publishedTicks, QMath.FromDecimal(severity < 0 ? 0 : severity > 1 ? 1 : severity), sourceCode, (byte)shock);

        public NewsShockClass ShockClass => (NewsShockClass)ShockClassByte;
        public bool           IsUnanalyzed => SentimentRaw == 0;           // ★미분석 격리
        public decimal        Sentiment    => SentimentRaw / (decimal)QMath.Scale;
        public decimal        Severity     => SeverityRaw / (decimal)QMath.Scale;
        /// <summary>감성 3분류(±0.3). 0=미분석. 한국색상: Bullish 빨강/Bearish 파랑.</summary>
        public SentimentState State => SentimentRaw == 0 ? SentimentState.Unanalyzed
            : SentimentRaw >= 30_000_000L ? SentimentState.Bullish     // +0.3 이상 호재
            : SentimentRaw <= -30_000_000L ? SentimentState.Bearish    // -0.3 이하 악재
            : SentimentState.Neutral;
    }

    /// <summary>δ=0 뉴스 JSON 파서 — ReadOnlySpan&lt;byte&gt; 위 Utf8JsonReader(ToString 없이 숫자 필드 독출).</summary>
    public static class NewsIntelParser
    {
        // 스키마: {"code":5930,"sent":0.42,"ts":638...,"shock":1,"sev":0.8,"src":12}. 숫자만 — 문자열 필드 없음(δ=0).
        public static bool TryParse(ReadOnlySpan<byte> json, out NewsPayload news)
        {
            news = default;
            try
            {
                var r = new Utf8JsonReader(json);
                long code = 0, ts = 0, src = 0; decimal sent = 0m, sev = 0m; int shock = 0;
                bool anyNumber = false;
                while (r.Read())
                {
                    if (r.TokenType != JsonTokenType.PropertyName) continue;
                    // ★ TryGet*(비throw): 유효 JSON 숫자이나 CLR 타입 범위 밖(소수·오버플로·1e400) → 예외 힙 할당 없이 fail-closed 거부(δ=0 유지).
                    // ★ 매칭 키 값이 컨테이너면 Skip: 미Skip 시 다음 Read()가 중첩 프로퍼티명을 최상위 필드로 오독(누수) → 방지.
                    if (r.ValueTextEquals("code"u8))
                    { r.Read(); if (r.TokenType == JsonTokenType.Number) { if (!r.TryGetInt64(out code)) return false; anyNumber = true; } else if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) r.Skip(); }
                    else if (r.ValueTextEquals("sent"u8))
                    { r.Read(); if (r.TokenType == JsonTokenType.Number) { if (!r.TryGetDecimal(out sent)) return false; } else if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) r.Skip(); }
                    else if (r.ValueTextEquals("ts"u8))
                    { r.Read(); if (r.TokenType == JsonTokenType.Number) { if (!r.TryGetInt64(out ts)) return false; } else if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) r.Skip(); }
                    else if (r.ValueTextEquals("shock"u8))
                    { r.Read(); if (r.TokenType == JsonTokenType.Number) { if (!r.TryGetInt32(out shock)) return false; } else if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) r.Skip(); }
                    else if (r.ValueTextEquals("sev"u8))
                    { r.Read(); if (r.TokenType == JsonTokenType.Number) { if (!r.TryGetDecimal(out sev)) return false; } else if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) r.Skip(); }
                    else if (r.ValueTextEquals("src"u8))
                    { r.Read(); if (r.TokenType == JsonTokenType.Number) { if (!r.TryGetInt64(out src)) return false; } else if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) r.Skip(); }
                    else r.Skip();
                }
                if (!anyNumber) return false;                                  // code 없음 → 오염/비뉴스 프레임 거부
                if (sent < -1m) sent = -1m; else if (sent > 1m) sent = 1m;     // 감성 [-1,1] 클램프(fail-closed)
                if (shock < 0 || shock > 3) shock = 0;                          // 알 수 없는 쇼크 → None
                news = NewsPayload.Of(code, sent, ts, (NewsShockClass)shock, sev, src);
                return true;
            }
            catch { return false; }                                            // 파싱 예외 → fail-closed(수용 안 함)
        }
    }

    /// <summary>무블록 뉴스 라우팅 링(bounded·DropOldest). 핫패스 발주 드레인과 스레드 격리(백그라운드 오프로드).</summary>
    public sealed class NewsDispatchRing
    {
        private readonly Channel<NewsPayload> _ch;
        private long _published, _dropped, _read;

        public NewsDispatchRing(int capacity = 2048)
        {
            _ch = Channel.CreateBounded<NewsPayload>(new BoundedChannelOptions(capacity < 16 ? 16 : capacity)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        }
        public long Published => Interlocked.Read(ref _published);
        public long Dropped   => Interlocked.Read(ref _dropped);
        public long Read      => Interlocked.Read(ref _read);
        public int  Depth     => _ch.Reader.CanCount ? _ch.Reader.Count : 0;

        /// <summary>δ=0 논블로킹 발행(값 복사). 가득 차면 DropOldest — 핫패스 무블록.</summary>
        public void Publish(in NewsPayload p)
        {
            if (_ch.Writer.TryWrite(p)) Interlocked.Increment(ref _published);
            else Interlocked.Increment(ref _dropped);   // DropOldest 라 통상 도달 안 함(방어)
        }
        public bool TryRead(out NewsPayload p)
        {
            if (_ch.Reader.TryRead(out p)) { Interlocked.Increment(ref _read); return true; }
            return false;
        }
    }

    /// <summary>LLM 이 기사 본문에서 뽑아내는 유일한 ACL 경계 DTO — [ShockClass, Severity]. 수학·자산비중 계산 금지(환각 차단).</summary>
    public readonly record struct ShockDto(NewsShockClass Class, decimal Severity);

    /// <summary>
    /// ★세맨틱 쇼크 전파 행렬 — ShockClass×AssetClass 고정 β 로 관련 자산군 Stressed 가치를 하향(floor). QChaos 스트레스
    /// 모델(stressed = present × max(0,1−sev·β)) 재사용·δ=0 QMath.Notional. LLM 은 DTO 만 제공, 수학은 전부 코어 정수 연산.
    /// </summary>
    public static class SemanticShockMatrix
    {
        /// <summary>쇼크 분류별 자산군 시스템 β(민감도). 높을수록 같은 쇼크에 더 깊이 하향.</summary>
        public static decimal Beta(NewsShockClass s, AssetClass c) => s switch
        {
            // 미국 기준금리 폭등: 성장·위험자산 타격 큼(크립토 최대).
            NewsShockClass.MacroInterest      => c switch { AssetClass.DomesticStock => 0.9m, AssetClass.ForeignStock => 1.1m, AssetClass.Crypto => 1.8m, _ => 1.0m },
            // 리튬 공급망 차단: 배터리·2차전지 집중된 국내주식 큰 타격.
            NewsShockClass.SupplyChainLithium => c switch { AssetClass.DomesticStock => 1.2m, AssetClass.ForeignStock => 0.8m, AssetClass.Crypto => 0.5m, _ => 0.7m },
            // 지정학 리스크: 전반적·해외주식 민감.
            NewsShockClass.Geopolitical       => c switch { AssetClass.DomesticStock => 1.0m, AssetClass.ForeignStock => 1.2m, AssetClass.Crypto => 1.4m, _ => 1.0m },
            _                                 => 0.0m,   // None → 무충격
        };

        private static long SurvivalRaw(NewsShockClass s, AssetClass c, decimal severity)
        {
            decimal sev = severity < 0m ? 0m : severity > 1m ? 1m : severity;   // [0,1] 클램프
            decimal f = 1m - sev * Beta(s, c);
            if (f < 0m) f = 0m;                                                 // 전손 이하(음수) 0 클램프 fail-closed
            return QMath.FromDecimal(f);
        }

        /// <summary>
        /// 자산별 present 가치에 쇼크 전파 → stressedOut 기입(δ=0·floor·하향 편향). 길이 불일치/음수 poison → false(fail-closed).
        /// ★방어 방향만: 가치를 낮출 뿐 실주문 경로 무접촉(Stressed 는 그림자 진단값 — 권위 청산은 별도).
        /// </summary>
        public static bool Propagate(ShockDto dto, ReadOnlySpan<QMoney> present, ReadOnlySpan<AssetClass> classes,
                                     Span<QMoney> stressedOut, OrderAttestationService? attestation = null)
        {
            int n = present.Length;
            if (classes.Length != n || stressedOut.Length != n) return false;
            for (int i = 0; i < n; i++)
            {
                if (present[i].Raw < 0)
                { try { attestation?.ForceFreeze("세맨틱 쇼크: 음수 present 가치 오염 — fail-closed"); } catch { } return false; }
                stressedOut[i] = new QMoney(QMath.Notional(present[i].Raw, SurvivalRaw(dto.Class, classes[i], dto.Severity), roundUp: false));
            }
            return true;
        }
    }

    /// <summary>
    /// ★감성 속도 서킷 브레이커 — 128표본 롤링 카운트 창(결정론·벽시계 미사용)에서 감성 하향 드리프트를 δ=0 3σ 로 추적. 패닉(3σ 하향 폭주) 감지 시
    /// IgnitionLock.Downgrade 로 발주 무장을 한 단 강등(MasterIgnited→ProbeReady). SlippageSentinel δ=0 정수 3σ 원형 미러.
    /// </summary>
    public sealed class SentimentVelocityBreaker
    {
        private const int  Cap = 128;
        private const int  MinSamples = 10;
        private readonly long[] _drift = new long[Cap];   // 사전할당 링(연속 감성 델타 raw) — δ=0
        private int  _idx, _count;
        private long _sum;            // 델타 합
        private Int128 _sumSq;        // 델타 제곱합(Int128 정확)
        private long _lastSentiment;  // 직전 감성 raw
        private bool _hasLast;

        private readonly IgnitionLock? _ignition;
        private long _lastDriftRaw, _lastSigmaRaw, _tripCount;
        private int  _tripped;

        public SentimentVelocityBreaker(IgnitionLock? ignition = null) => _ignition = ignition;

        // ── 관제 독출(락-프리) ─────────────────────────────────────────────────
        public long LastDriftRaw   => Interlocked.Read(ref _lastDriftRaw);
        public long LastSigmaRaw   => Interlocked.Read(ref _lastSigmaRaw);
        public long TripCount      => Interlocked.Read(ref _tripCount);
        public bool IsTripped      => Volatile.Read(ref _tripped) != 0;
        public int  SampleCount    => _count;

        /// <summary>
        /// 새 감성 관측(raw)을 기록하고 하향 드리프트가 3σ 를 초과하는지 판정(단일 라이터=뉴스 드레인 스레드 전제).
        /// 반환 true=패닉 감지(강등 트리거됨). ★fail-closed: 워밍업(&lt;10)·퇴화 분산(≤0)·상향 드리프트는 무트립.
        /// </summary>
        public bool RecordAndCheck(long sentimentRaw)
        {
            if (!_hasLast) { _lastSentiment = sentimentRaw; _hasLast = true; return false; }
            long delta = sentimentRaw - _lastSentiment;   // 감성 변화(음수=하향)
            _lastSentiment = sentimentRaw;

            // 롤링 링 갱신(오래된 값 축출).
            if (_count == Cap) { long old = _drift[_idx]; _sum -= old; _sumSq -= (Int128)old * old; }
            else _count++;
            _drift[_idx] = delta; _sum += delta; _sumSq += (Int128)delta * delta;
            _idx = (_idx + 1) % Cap;
            Interlocked.Exchange(ref _lastDriftRaw, delta);

            if (_count < MinSamples) return false;                          // 워밍업 — fail-closed 무트립

            long mean = _sum / _count;
            // 분산 = E[x²] − mean² (Int128 정확). 표본 분산 근사(모분산).
            Int128 meanSq = (Int128)mean * mean;
            Int128 varScaled = _sumSq / _count - meanSq;
            if (varScaled <= 0) { Interlocked.Exchange(ref _lastSigmaRaw, 0); return false; }   // 퇴화 분산 — 무트립

            // σ raw 노출(Int128 Newton isqrt).
            long sigma = ISqrt(varScaled);
            Interlocked.Exchange(ref _lastSigmaRaw, sigma);

            // ★하향 드리프트만: dev = delta − mean; dev<0(하향) ∧ dev² > 9·var → 3σ 초과 패닉.
            long dev = delta - mean;
            if (dev >= 0) return false;                                     // 상향/보합 — 무트립
            Int128 devSq = (Int128)dev * dev;
            if (devSq <= (Int128)9 * varScaled) return false;               // 3σ 미만 — 무트립

            // 패닉 — 발주 무장 강등(정보 서킷 브레이커).
            Interlocked.Increment(ref _tripCount);
            Volatile.Write(ref _tripped, 1);
            try { _ignition?.Downgrade("감성 속도 브레이커 — 128표본 롤링 창 감성 3σ 하향 드리프트 폭주 감지"); } catch { }
            return true;
        }

        /// <summary>운영자 수동 리셋(패닉 해소 후).</summary>
        public void ResetTrip() => Volatile.Write(ref _tripped, 0);

        // Int128 정수 제곱근(Newton). varScaled≥0 전제.
        private static long ISqrt(Int128 v)
        {
            if (v <= 0) return 0;
            Int128 x = v, y = (x + 1) / 2;
            while (y < x) { x = y; y = (x + v / x) / 2; }
            return (long)x;
        }
    }
}
