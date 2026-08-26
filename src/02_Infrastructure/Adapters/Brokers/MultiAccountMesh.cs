// ============================================================================
// MultiAccountMesh — S20 #3(Path B): 가상 서브계좌 할당 순수 코어
//
//  단일 키움 리테일 계좌 내부를 논리적 '가상 서브계좌'로 파티셔닝해, 다중 승격 게놈이
//  서로의 자산을 침범하지 않고 독립 지분으로 집행할 토대를 닦는다. (자산운용사급 분할)
//
//  ── 핵심 연산 (순수 결정론) ────────────────────────────────────────────────
//  · Apportion(totalQty)  : 정수 슬라이스를 지분율대로 무손실 분배 — 최대잉여법(Hamilton)
//                           으로 합이 정확히 totalQty (주식 생성·소실 0, 결정론적 타이브레이크).
//  · PartitionCash(cash)  : 현금을 지분율대로 분배 — 마지막 계좌가 잔여를 흡수해 합 == cash.
//  · WithinShare(...)     : 한 서브계좌 가치가 자기 지분 한도를 넘지 않는지(자산 침범 가드).
//
//  외부 의존성 0 · 사이드 이펙트 0 · 가중치 0 허용(0지분=0배분). 실행/멀티슬롯 결속은 별도.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    /// <summary>가상 서브계좌 정의 — 식별자 + 지분 가중치(≥0).</summary>
    public sealed record SubAccountSpec(string Id, decimal Weight);

    /// <summary>현금 파티션 1건.</summary>
    public sealed record CashSplit(string Id, decimal Weight, decimal Cash);

    public sealed class MultiAccountMesh
    {
        private readonly SubAccountSpec[]              _subs;
        private readonly Dictionary<string, decimal>   _weight;
        private readonly decimal                       _totalWeight;

        public MultiAccountMesh(IReadOnlyList<SubAccountSpec> subs)
        {
            if (subs is null || subs.Count == 0)
                throw new ArgumentException("서브계좌가 1개 이상이어야 합니다.", nameof(subs));

            _subs   = new SubAccountSpec[subs.Count];
            _weight = new Dictionary<string, decimal>(subs.Count, StringComparer.Ordinal);
            decimal total = 0m;

            for (int i = 0; i < subs.Count; i++)
            {
                var s = subs[i];
                if (string.IsNullOrWhiteSpace(s.Id))
                    throw new ArgumentException("서브계좌 Id 는 비어 있을 수 없습니다.", nameof(subs));
                if (s.Weight < 0m)
                    throw new ArgumentException($"가중치는 음수일 수 없습니다: {s.Id}", nameof(subs));
                if (!_weight.TryAdd(s.Id, s.Weight))
                    throw new ArgumentException($"중복 서브계좌 Id: {s.Id}", nameof(subs));
                _subs[i] = s;
                total += s.Weight;
            }

            if (total <= 0m)
                throw new ArgumentException("가중치 합은 0보다 커야 합니다.", nameof(subs));
            _totalWeight = total;
        }

        public int                   Count       => _subs.Length;
        public decimal               TotalWeight => _totalWeight;
        public IReadOnlyList<string> Ids         => Array.ConvertAll(_subs, s => s.Id);

        /// <summary>해당 서브계좌의 정규화 지분율(weight / totalWeight).</summary>
        public decimal ShareOf(string id)
            => _weight.TryGetValue(id, out var w) ? w / _totalWeight : 0m;

        // ── 슬라이스 무손실 분배 (최대잉여법) ──────────────────────────────────

        /// <summary>
        /// 정수 주문 수량을 지분율대로 분배한다. 최대잉여법으로 합이 정확히 totalQty 가 되며
        /// (반올림 손실 0), 잉여는 잔여분이 큰 계좌부터 결정론적으로(동률은 인덱스순) 배정.
        /// </summary>
        public IReadOnlyDictionary<string, long> Apportion(long totalQty)
        {
            if (totalQty < 0L) throw new ArgumentOutOfRangeException(nameof(totalQty));

            var result = new Dictionary<string, long>(_subs.Length, StringComparer.Ordinal);
            if (totalQty == 0L)
            {
                foreach (var s in _subs) result[s.Id] = 0L;
                return result;
            }

            var floors = new long[_subs.Length];
            var rema   = new decimal[_subs.Length];
            long allocated = 0L;

            for (int i = 0; i < _subs.Length; i++)
            {
                decimal exact = totalQty * _subs[i].Weight / _totalWeight;
                long    fl    = (long)Math.Floor(exact);
                floors[i] = fl;
                rema[i]   = exact - fl;
                allocated += fl;
            }

            long leftover = totalQty - allocated;   // 0 ≤ leftover < N (분수부 합 < N)
            int[] order = Enumerable.Range(0, _subs.Length)
                .OrderByDescending(i => rema[i])
                .ThenBy(i => i)                       // 동률 → 인덱스순(결정론)
                .ToArray();
            for (int k = 0; k < leftover; k++) floors[order[k]]++;

            for (int i = 0; i < _subs.Length; i++) result[_subs[i].Id] = floors[i];
            return result;
        }

        // ── 현금 파티션 (합 == realCash) ───────────────────────────────────────

        /// <summary>실계좌 현금을 지분율대로 분배. 마지막 계좌가 잔여를 흡수해 합이 정확히 realCash.</summary>
        public IReadOnlyList<CashSplit> PartitionCash(decimal realCash)
        {
            var list    = new List<CashSplit>(_subs.Length);
            decimal acc = 0m;
            for (int i = 0; i < _subs.Length; i++)
            {
                decimal cash;
                if (i < _subs.Length - 1)
                {
                    cash = realCash * _subs[i].Weight / _totalWeight;
                    acc += cash;
                }
                else
                {
                    cash = realCash - acc;   // 잔여 흡수 → 합 정확
                }
                list.Add(new CashSplit(_subs[i].Id, _subs[i].Weight, cash));
            }
            return list;
        }

        // ── 자산 침범 가드 ─────────────────────────────────────────────────────

        /// <summary>서브계좌의 지분 한도 가치 = realTotal × (weight / totalWeight).</summary>
        public decimal MaxShareValue(string id, decimal realTotal)
            => realTotal * ShareOf(id);

        /// <summary>서브계좌 가치가 자기 지분 한도를 넘지 않는지(자산 침범 방지). tolerance 는 비율 여유.</summary>
        public bool WithinShare(string id, decimal subValue, decimal realTotal, decimal tolerance = 0m)
            => subValue <= MaxShareValue(id, realTotal) * (1m + tolerance) + 1e-9m;
    }
}
