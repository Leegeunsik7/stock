// ============================================================================
// HttpFxRateSourceTests — 3단계: 프로덕션 환율 공급자 파싱 + QAcl 경계 펌프 결선
//
//  §1 dot-path 추출(6) — 객체/배열/문자열(콤마)·숫자·미존재/스칼라중단/비숫자 fail.
//  §2 QAcl 경계 펌프(4) — 유효값 SubmitRaw 수용·음수/0 → QAcl 거부 → 브레이커 래치+동결·널.
//
//  ★검증 핵심: 외부 decimal 이 QAcl.TryImportRate 경계를 거쳐 QFixed raw 로 유입되고,
//    경계 거부(≤0/오버플로)는 브레이커 래치 + ForceFreeze 로 fail-closed 됨을 박제.
// ============================================================================
using System;
using System.Text.Json;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class HttpFxRateSourceTests
    {
        private sealed class SeqSource : IFxRateSource
        {
            private readonly decimal?[] _seq;
            private int _i;
            public SeqSource(params decimal?[] seq) { _seq = seq; }
            public bool TryPoll(out decimal rate)
            {
                rate = 0m;
                if (_i >= _seq.Length) return false;
                var v = _seq[_i++];
                if (v is null) return false;
                rate = v.Value; return true;
            }
        }

        private static HttpFxRateSource Src(string path) => new(new Uri("https://fx.example/rate"), path);
        private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

        // ── §1 dot-path 추출 ─────────────────────────────────────────────────

        [Fact]
        public void Extract_Object_StringWithComma()
        {
            Assert.True(Src("USDKRW.deal_bas_r").TryExtractRate(Json("{\"USDKRW\":{\"deal_bas_r\":\"1,330.50\"}}"), out var r));
            Assert.Equal(1330.50m, r);
        }

        [Fact]
        public void Extract_Array_Number()
        {
            Assert.True(Src("0.basePrice").TryExtractRate(Json("[{\"basePrice\":1331.0}]"), out var r));
            Assert.Equal(1331.0m, r);
        }

        [Fact]
        public void Extract_MissingKey_False()
            => Assert.False(Src("USDKRW.nope").TryExtractRate(Json("{\"USDKRW\":{\"deal_bas_r\":1330}}"), out _));

        [Fact]
        public void Extract_ScalarMidPath_False()
            => Assert.False(Src("a.b.c").TryExtractRate(Json("{\"a\":{\"b\":1330}}"), out _));

        [Fact]
        public void Extract_NonNumericString_False()
            => Assert.False(Src("v").TryExtractRate(Json("{\"v\":\"N/A\"}"), out _));

        [Fact]
        public void Extract_ArrayIndexOutOfRange_False()
            => Assert.False(Src("3.basePrice").TryExtractRate(Json("[{\"basePrice\":1}]"), out _));

        // ── §2 QAcl 경계 펌프 결선 ────────────────────────────────────────────

        [Fact]
        public void Pump_ValidRate_RoutesThroughQAcl_ToBreaker()
        {
            var att = new OrderAttestationService();
            var b = new QFxCircuitBreaker(() => 0L, att);
            using var pump = new QFxFeedPump(new SeqSource(1330m), b, pollMs: 20, attestation: att);
            pump.PumpOnce();
            Assert.Equal(1L, b.Accepted);
            Assert.False(b.IsOpen);
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void Pump_NegativeRate_QAclRejects_BreakerLatches_Freezes()
        {
            var att = new OrderAttestationService();
            var b = new QFxCircuitBreaker(() => 0L, att);
            using var pump = new QFxFeedPump(new SeqSource(-5m), b, pollMs: 20, attestation: att);
            pump.PumpOnce();
            Assert.Equal(0L, b.Accepted);
            Assert.True(b.IsOpen);                              // 브레이커 명시 래치
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode); // QAcl 경계 동결
        }

        [Fact]
        public void Pump_ZeroRate_QAclRejects_FailClosed()
        {
            var att = new OrderAttestationService();
            var b = new QFxCircuitBreaker(() => 0L, att);
            using var pump = new QFxFeedPump(new SeqSource(0m), b, pollMs: 20, attestation: att);
            pump.PumpOnce();
            Assert.True(b.IsOpen);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Pump_NoData_NoSubmit_NoFreeze()
        {
            var att = new OrderAttestationService();
            var b = new QFxCircuitBreaker(() => 0L, att);
            using var pump = new QFxFeedPump(new SeqSource((decimal?)null), b, pollMs: 20, attestation: att);
            pump.PumpOnce();                                    // 폴 결과 없음 → 무투입
            Assert.Equal(0L, b.Accepted);
            Assert.False(b.IsOpen);
            Assert.Equal(GatingMode.Normal, att.Mode);
        }
    }
}
