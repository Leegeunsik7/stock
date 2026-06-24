using System;
using System.Collections.Generic;
using System.Threading;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    // S29 (Path C) — SPMC 주문 디스패치 링: 고정소수점 제안 라운드트립·다중소비자 분배·동시성 torn-safety·
    //   추월 드롭 계측·무장 게이트(liveArmed:false → 송출 0). 안전성(torn=0)은 부하 무관 결정론.
    public sealed class S29OrderDispatchRingTests
    {
        [Fact]
        public void OrderProposal_FixedPoint_RoundTrips()
        {
            var ring = new OrderDispatchRing(8);
            long id = ring.Publish(genomeId: 7, symbolCode: 5930, OrderSide.Buy, kind: 1,
                                   QPrice.Of(85_000m), QQty.Of(10m), timestampTicks: 12_345L);
            Assert.Equal(1L, id);

            var c = ring.CreateConsumer();
            Assert.True(c.TryRead(out var p));
            Assert.Equal(1L,   p.OrderId);
            Assert.Equal(7L,   p.GenomeId);
            Assert.Equal(5930L, p.SymbolCode);
            Assert.Equal(OrderSide.Buy, p.Side);
            Assert.Equal((byte)1, p.KindByte);
            Assert.Equal(85_000m, p.Price.ToDecimal());
            Assert.Equal(10m,     p.Qty.ToDecimal());
            Assert.Equal(12_345L, p.TimestampTicks);
            Assert.False(c.TryRead(out _));   // 소진
        }

        [Fact]
        public void Spmc_FanOut_AllConsumersSeeAllInOrder()
        {
            const int N = 100;
            var ring = new OrderDispatchRing(1024);   // 용량 ≥ N → 무드롭
            var consumers = new[] { ring.CreateConsumer(), ring.CreateConsumer(), ring.CreateConsumer() };

            for (int i = 1; i <= N; i++)
                ring.Publish(i, i, OrderSide.Buy, 1, QPrice.Of(i), QQty.Of(i), i);

            foreach (var c in consumers)
            {
                for (int i = 1; i <= N; i++)
                {
                    Assert.True(c.TryRead(out var p), $"소비자가 {i}번째 제안을 못 읽음");
                    Assert.Equal((long)i, p.OrderId);
                    Assert.Equal((long)i, p.SymbolCode);   // 순서·정합 보존
                }
                Assert.False(c.TryRead(out _));   // 정확히 N개 후 소진
                Assert.Equal(0L, c.Dropped);      // 무드롭
            }
        }

        [Fact]
        public void Overrun_FastForwards_AccountsDrops()
        {
            var ring = new OrderDispatchRing(4);      // 극소 용량
            var c    = ring.CreateConsumer();
            for (int i = 1; i <= 100; i++)            // 100 ≫ 4 → 추월
                ring.Publish(i, i, OrderSide.Buy, 1, QPrice.Of(i), QQty.Of(i), i);

            int read = 0;
            while (c.TryRead(out var p)) { read++; Assert.Equal(p.OrderId, p.SymbolCode); }   // 읽은 건 전부 정합(무torn)
            Assert.Equal(4, read);          // 최신 용량만큼만 읽힘
            Assert.Equal(96L, c.Dropped);   // 96건 추월 유실 계측
        }

        // ★동시성 torn-safety(부하 무관 결정론) — 단일 생산자 + 4 소비자. 모든 필드를 OrderId 로부터 파생시켜
        //   읽은 제안의 내부 정합(GenomeId==OrderId 등)을 검사 → seqlock 이 깨지면 torn 으로 포착. 작업량 경계.
        [Fact]
        public void Concurrent_SingleProducer_ManyConsumers_NoTornProposal()
        {
            const long N = 50_000;
            var ring = new OrderDispatchRing(1024);
            int producerDone = 0, torn = 0;

            var producer = new Thread(() =>
            {
                for (long i = 1; i <= N; i++)
                    ring.PublishRaw(new OrderProposal(i, i, i, OrderSide.Buy, 1, new QPrice(i * 100), new QQty(i), i * 1000));
                Volatile.Write(ref producerDone, 1);
            }) { IsBackground = true };

            var consumers = new Thread[4];
            for (int k = 0; k < consumers.Length; k++)
                consumers[k] = new Thread(() =>
                {
                    var c = ring.CreateConsumer();
                    bool more = true;
                    while (more)
                    {
                        more = Volatile.Read(ref producerDone) == 0;
                        while (c.TryRead(out var p))
                        {
                            bool ok = p.GenomeId == p.OrderId && p.SymbolCode == p.OrderId
                                      && p.PriceRaw == p.OrderId * 100 && p.QtyRaw == p.OrderId
                                      && p.TimestampTicks == p.OrderId * 1000;
                            if (!ok) Interlocked.Increment(ref torn);
                        }
                    }
                }) { IsBackground = true };

            producer.Start(); foreach (var t in consumers) t.Start();
            Assert.True(producer.Join(60000), "producer join 타임아웃(데드락?)");
            foreach (var t in consumers) Assert.True(t.Join(60000), "consumer join 타임아웃(데드락?)");

            Assert.Equal(0, torn);                 // 단 한 건의 찢긴 제안도 없어야 함(seqlock)
            Assert.Equal(N - 1, ring.Published);   // 생산자 전량 발행(진행 보장)
        }

        [Fact]
        public void GatedDispatcher_Disarmed_ForwardsNothing()
        {
            var ring = new OrderDispatchRing(1024);
            var disp = new GatedOrderDispatcher(ring, () => false);   // 미무장(liveArmed:false 자세)
            for (int i = 1; i <= 10; i++) ring.Publish(i, i, OrderSide.Buy, 1, QPrice.Of(i), QQty.Of(i), i);

            var sent = new List<long>();
            int n = disp.Drain(p => sent.Add(p.OrderId));

            Assert.Equal(10, n);
            Assert.Empty(sent);              // 송출 0 — fail-closed
            Assert.Equal(0L, disp.Forwarded);
            Assert.Equal(10L, disp.GatedOut);
        }

        [Fact]
        public void GatedDispatcher_Armed_ForwardsAll_ThenRespectsRuntimeRetreat()
        {
            var ring = new OrderDispatchRing(1024);
            bool armed = true;
            var disp = new GatedOrderDispatcher(ring, () => armed);
            for (int i = 1; i <= 10; i++) ring.Publish(i, i, OrderSide.Buy, 1, QPrice.Of(i), QQty.Of(i), i);

            var sent = new List<long>();
            disp.Drain(p => sent.Add(p.OrderId));
            Assert.Equal(10, sent.Count);
            Assert.Equal(10L, disp.Forwarded);
            Assert.Equal(0L,  disp.GatedOut);

            armed = false;   // 런타임 후퇴 — 즉시 반영(매 제안 재평가)
            ring.Publish(99, 99, OrderSide.Sell, 0, QPrice.Of(1m), QQty.Of(1m), 1);
            disp.Drain(p => sent.Add(p.OrderId));
            Assert.Equal(10, sent.Count);    // 추가 송출 없음
            Assert.Equal(1L, disp.GatedOut);
        }

        [Fact]
        public void GatedDispatcher_PredicateThrows_FailClosed_NoSend_DrainContinues()
        {
            // ★적대적 리뷰(minor) — 무장 술어가 throw(예: BrokerHub.IsArmed 의 db I/O 예외) 하면 fail-closed:
            //   해당 제안은 송출 안 함 + GatedOut 계상(증발 아님), 예외는 Drain 밖으로 전파 안 됨(드레인 완주).
            var ring = new OrderDispatchRing(1024);
            for (int i = 1; i <= 5; i++) ring.Publish(i, i, OrderSide.Buy, 1, QPrice.Of(i), QQty.Of(i), i);

            int calls = 0;
            var disp = new GatedOrderDispatcher(ring, () =>
            {
                calls++;
                if (calls == 3) throw new InvalidOperationException("db i/o 모사");   // 3번째 평가만 throw
                return true;
            });

            var sent = new List<long>();
            int n = disp.Drain(p => sent.Add(p.OrderId));

            Assert.Equal(5, n);                       // 5건 전부 처리(예외 전파 없이 완주)
            Assert.Equal(new long[] { 1, 2, 4, 5 }, sent.ToArray());   // 3번만 미송출(술어 throw → 미무장)
            Assert.Equal(4L, disp.Forwarded);
            Assert.Equal(1L, disp.GatedOut);          // throw 한 1건 = GatedOut(어느 카운터에도 없는 '증발' 아님)
        }

        [Fact]
        public void Publish_WriterGate_ReleasesBetweenCalls_SequentialSucceeds()
        {
            // ★단일 생산자 게이트는 정당 순차 발행을 막지 않는다(finally 로 매 발행 후 해제). 동시 생산자 throw 경로는
            //   결정론 단위테스트로 강제 불가(실 경합 필요)하나, 게이트는 CAS 로 correct-by-construction — 해제 정상성만 박제.
            var ring = new OrderDispatchRing(8);
            for (int i = 1; i <= 6; i++)
                Assert.Equal((long)i, ring.Publish(i, i, OrderSide.Buy, 1, QPrice.Of(i), QQty.Of(i), i));   // 게이트 막힘 없이 연속 성공
            Assert.Equal(5L, ring.Published);   // 6건 발행(seq 0..5)
        }

        [Fact]
        public void NullArgs_And_BadCapacity_Throw()
        {
            var ring = new OrderDispatchRing(8);
            Assert.Throws<ArgumentNullException>(() => new GatedOrderDispatcher(null!, () => true));
            Assert.Throws<ArgumentNullException>(() => new GatedOrderDispatcher(ring, null!));
            Assert.Throws<ArgumentNullException>(() => new GatedOrderDispatcher(ring, () => true).Drain(null!));
            Assert.Throws<ArgumentException>(() => new OrderDispatchRing(7));   // 2의 거듭제곱 아님
        }
    }
}
