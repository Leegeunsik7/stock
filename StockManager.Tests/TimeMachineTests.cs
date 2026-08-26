// ============================================================================
// TimeMachineTests - 시점 고정 슬라이스/리플레이/채점 (순수·결정적)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    public class TimeMachineTests
    {
        private static List<Candle> Wave(int n)
        {
            var list = new List<Candle>();
            for (int i = 0; i < n; i++)
            {
                decimal c = (decimal)(100 + 15 * Math.Sin(i / 6.0) + 0.1 * i);
                list.Add(new Candle { Date = new DateTime(2023, 1, 1).AddDays(i), Open = c, High = c * 1.01m, Low = c * 0.99m, Close = c, Volume = 1000 });
            }
            return list;
        }

        [Fact]
        public void AsOfSlice_NoLookAhead()
        {
            var c = Wave(100);
            var asOf = new DateTime(2023, 2, 1);   // ~32일째
            var slice = TimeMachine.AsOfSlice(c, asOf);
            Assert.All(slice, x => Assert.True(x.Date.Date <= asOf.Date));
            Assert.True(slice.Count is > 25 and < 40);
        }

        [Fact]
        public void Replay_Valid_GradesWhenFutureExists()
        {
            var c = Wave(200);
            var asOf = new DateTime(2023, 4, 1);   // 충분한 과거 + 미래
            var r = TimeMachine.Replay(c, asOf, Market.KRX_KOSPI, 10, "X");
            Assert.True(r.Valid);
            Assert.True(r.AsOfBars >= 30);
            Assert.True(r.Graded);                  // 미래 10봉 존재 → 채점
            Assert.True(r.AsOfClose > 0);
            Assert.InRange(r.RsiAsOf, 0, 100);
        }

        [Fact]
        public void Replay_NoLookAhead_AsOfCloseMatchesSliceEnd()
        {
            var c = Wave(200);
            var asOf = new DateTime(2023, 5, 1);
            var slice = TimeMachine.AsOfSlice(c, asOf);
            var r = TimeMachine.Replay(c, asOf, Market.KRX_KOSPI, 10);
            Assert.Equal(slice[^1].Close, r.AsOfClose);   // as-of 종가 = 슬라이스 마지막(미래 안 봄)
        }

        [Fact]
        public void Replay_AtSeriesEnd_NotGraded()
        {
            var c = Wave(100);
            var r = TimeMachine.Replay(c, c[^1].Date, Market.KRX_KOSPI, 10);   // 마지막 날 → 미래 없음
            Assert.True(r.Valid);
            Assert.False(r.Graded);
        }

        [Fact]
        public void Replay_TooEarly_Invalid()
        {
            var c = Wave(100);
            var r = TimeMachine.Replay(c, new DateTime(2023, 1, 10), Market.KRX_KOSPI, 10);  // 10일째 < 30봉
            Assert.False(r.Valid);
        }

        [Fact]
        public void Replay_Deterministic()
        {
            var c = Wave(200);
            var asOf = new DateTime(2023, 4, 15);
            var a = TimeMachine.Replay(c, asOf, Market.KRX_KOSPI, 10, "X");
            var b = TimeMachine.Replay(c, asOf, Market.KRX_KOSPI, 10, "X");
            Assert.Equal(a.RealizedPct, b.RealizedPct, 9);
            Assert.Equal(a.Stance, b.Stance);
            Assert.Equal(a.RsiAsOf, b.RsiAsOf, 9);
        }
    }
}
