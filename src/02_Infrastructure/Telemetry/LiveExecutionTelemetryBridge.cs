// ============================================================================
// LiveExecutionTelemetryBridge — v11.0.0: 실체결 텔레메트리 브리지 (δ=0)
//
//   실제 증권사로부터 수신된 체결 통보(Execution Report)의 응답 지연(RTT)과 실 슬리피지(체결가 vs 주문가)를 δ=0 파싱해
//   RingTelemetry 카나리 슬롯으로 직결한다. Utf8JsonReader + TossJsonParser.TryReadLong(토큰 타입 안전) 재사용.
//
//   ★기대 스키마(실 스펙에 맞춰 조정): {"clOrdId":L,"fillPrice":L,"fillQty":L,"ts":µs,"status":"filled"}
//
//   ★불변: δ=0(IngestExecutionReport 핫패스 힙 0 — Utf8JsonReader ref struct·Interlocked) · fail-closed(파싱 실패/미체결
//     → 무시·계수) · liveArmed:false(순수 체결 통보 파싱 — 실 발주 유발 0).
// ============================================================================
using System;
using System.Text.Json;
using System.Threading;

namespace MS2E.Infrastructure
{
    public sealed class LiveExecutionTelemetryBridge
    {
        private readonly InFlightOrderReconciliationLedger? _ledger;   // ★정산 결선(옵션) — 체결 통보로 pending 해제(브릭 방지)
        private long _lastRttMicros, _lastSlippageBps, _execReports, _timeouts, _parseErrors;

        public LiveExecutionTelemetryBridge(InFlightOrderReconciliationLedger? ledger = null) => _ledger = ledger;

        public long LastRttMicros    => Interlocked.Read(ref _lastRttMicros);
        public long LastSlippageBps  => Interlocked.Read(ref _lastSlippageBps);
        public long ExecReports      => Interlocked.Read(ref _execReports);
        public long Timeouts         => Interlocked.Read(ref _timeouts);
        public long ParseErrors      => Interlocked.Read(ref _parseErrors);

        /// <summary>
        /// ★체결 통보 δ=0 파싱 → RTT(now-거래소ts)·슬리피지 bps(|체결가-주문가|/주문가) 산출. 미체결/파싱 실패 → false(fail-closed).
        /// </summary>
        public bool IngestExecutionReport(ReadOnlySpan<byte> json, long orderPriceRaw, long nowMicros)
        {
            long clOrdId = -1L, fillPrice = 0L, fillQty = 0L, ts = 0L;
            bool terminal = false;
            try
            {
                var reader = new Utf8JsonReader(json);
                while (reader.Read())
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    if      (reader.ValueTextEquals("clOrdId"u8))   { reader.Read(); TossJsonParser.TryReadLong(ref reader, out clOrdId); }
                    else if (reader.ValueTextEquals("fillPrice"u8)) { reader.Read(); TossJsonParser.TryReadLong(ref reader, out fillPrice); }
                    else if (reader.ValueTextEquals("fillQty"u8))   { reader.Read(); TossJsonParser.TryReadLong(ref reader, out fillQty); }
                    else if (reader.ValueTextEquals("ts"u8))        { reader.Read(); TossJsonParser.TryReadLong(ref reader, out ts); }
                    else if (reader.ValueTextEquals("status"u8))    { reader.Read(); terminal = IsTerminalStatus(ref reader); }
                    else reader.Skip();
                }
            }
            catch (JsonException)
            {
                Interlocked.Increment(ref _parseErrors);
                return false;
            }
            if (fillQty <= 0L && !terminal) return false;   // 미체결·비종결 → 무시(fail-closed). 종결(거부/취소)은 정산 진행.

            if (ts > 0L) Interlocked.Exchange(ref _lastRttMicros, Math.Max(0L, nowMicros - ts));
            if (orderPriceRaw > 0L && fillPrice > 0L)
            {
                // ★Int128 곱(fillPrice 언바운드 오버플로 → fail-open 봉쇄). bps 결과는 소수 → long 절단 안전.
                long slipBps = (long)((Int128)(fillPrice - orderPriceRaw) * 10000L / orderPriceRaw);
                Interlocked.Exchange(ref _lastSlippageBps, slipBps < 0L ? -slipBps : slipBps);   // 절대 슬리피지 bps
            }
            // ★정산 결선: 체결 통보로 원장 갱신(부분 체결 누적·종결 시 pending 해제 → 브릭 방지).
            if (clOrdId >= 0L && _ledger is { } led)
            {
                if (fillQty > 0L) led.RecordFill(clOrdId, fillQty);
                if (terminal) led.Reconcile(clOrdId);
            }
            Interlocked.Increment(ref _execReports);
            return true;
        }

        // 종결 상태(체결완료/거부/취소) 판정 — pending 해제 대상.
        private static bool IsTerminalStatus(ref Utf8JsonReader reader)
        {
            if (reader.TokenType != JsonTokenType.String) return false;
            ReadOnlySpan<byte> v = reader.ValueSpan;
            return v.SequenceEqual("filled"u8) || v.SequenceEqual("rejected"u8)
                || v.SequenceEqual("cancelled"u8) || v.SequenceEqual("canceled"u8) || v.SequenceEqual("done"u8);
        }

        /// <summary>증권사 API 발주 타임아웃 통지(δ=0).</summary>
        public void NoteTimeout() => Interlocked.Increment(ref _timeouts);
    }
}
