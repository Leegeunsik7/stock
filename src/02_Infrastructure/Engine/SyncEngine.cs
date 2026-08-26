// ============================================================================
// SyncEngine - P2P/다기기 동기화 · 순수 머지 코어 (v3.43.0)
//  "위험하지 않은 범위"의 동기화: 결정론적·가산적 머지만 수행(절대 삭제 안 함).
//   · 거래(Transaction)  = UnionDistinct — 내용 해시로 중복 제거 후 합집합(append-only 원장)
//   · 보유(Holding)      = AddMissingOnly — 로컬에 없는 키만 추가, 기존은 절대 덮어쓰지 않음
//   · 설정(Setting)      = PreferIncoming — 다르면 '충돌'로 표시(적용은 사용자 확정 시에만)
//  Plan()은 dry-run 미리보기(무엇이 추가/충돌/동일인지)만 만든다 — 실제 쓰기는 어댑터(SyncService).
//  per-record 타임스탬프가 없는 스키마라 LWW 대신 내용해시 기반 가산 머지를 택함(데이터 손실 불가).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MS2E.Infrastructure
{
    public enum SyncKind { Transaction, Holding, Setting }

    public enum MergePolicy { UnionDistinct, AddMissingOnly, PreferIncoming }

    public enum MergeOp { Insert, ConflictDiff, Identical }

    /// <summary>동기화 레코드(불투명 Json 페이로드 + 식별/해시). 엔진은 Json 내용을 해석하지 않음.</summary>
    public sealed record SyncRecord(SyncKind Kind, string Key, string Hash, string Json);

    public sealed record SyncBundle(int Version, string DeviceId, string ExportedAtIso, List<SyncRecord> Records);

    public sealed record MergeItem(SyncKind Kind, string Key, MergeOp Op, string? LocalHash, string IncomingHash, string IncomingJson);

    public sealed record MergePlan(IReadOnlyList<MergeItem> Items)
    {
        public int Inserts => Items.Count(i => i.Op == MergeOp.Insert);
        public int Conflicts => Items.Count(i => i.Op == MergeOp.ConflictDiff);
        public int Identical => Items.Count(i => i.Op == MergeOp.Identical);
        public IEnumerable<MergeItem> OfKind(SyncKind k) => Items.Where(i => i.Kind == k);
        public int InsertsOf(SyncKind k) => OfKind(k).Count(i => i.Op == MergeOp.Insert);
        public int ConflictsOf(SyncKind k) => OfKind(k).Count(i => i.Op == MergeOp.ConflictDiff);
    }

    public static class SyncEngine
    {
        private const string Sep = "";   // 단위 구분자(데이터에 등장하지 않음)

        public static MergePolicy DefaultPolicy(SyncKind k) => k switch
        {
            SyncKind.Transaction => MergePolicy.UnionDistinct,
            SyncKind.Holding => MergePolicy.AddMissingOnly,
            SyncKind.Setting => MergePolicy.PreferIncoming,
            _ => MergePolicy.AddMissingOnly,
        };

        /// <summary>SHA-256 지문(대문자 hex). 필드를 단위구분자로 직렬화 후 해시 — 결정론.</summary>
        public static string Fingerprint(params string?[] fields)
        {
            var canonical = string.Join(Sep, fields.Select(f => f ?? ""));
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToHexString(hash);
        }

        /// <summary>로컬 vs 수신 레코드의 결정론적 머지 계획(dry-run). 쓰기는 하지 않음.</summary>
        public static MergePlan Plan(
            IReadOnlyList<SyncRecord> local,
            IReadOnlyList<SyncRecord> incoming,
            IReadOnlyDictionary<SyncKind, MergePolicy>? policies = null)
        {
            var items = new List<MergeItem>();
            var localByKind = local.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var grp in incoming.GroupBy(r => r.Kind))
            {
                var kind = grp.Key;
                var policy = policies != null && policies.TryGetValue(kind, out var p) ? p : DefaultPolicy(kind);
                var loc = localByKind.TryGetValue(kind, out var ll) ? ll : new List<SyncRecord>();

                if (policy == MergePolicy.UnionDistinct)
                {
                    var localHashes = new HashSet<string>(loc.Select(r => r.Hash), StringComparer.Ordinal);
                    foreach (var inc in grp)
                    {
                        var op = localHashes.Contains(inc.Hash) ? MergeOp.Identical : MergeOp.Insert;
                        items.Add(new MergeItem(kind, inc.Key, op, op == MergeOp.Identical ? inc.Hash : null, inc.Hash, inc.Json));
                    }
                }
                else // AddMissingOnly / PreferIncoming — 둘 다 키 기준; 차이는 적용 단계(충돌 적용 여부)에서만
                {
                    var localByKey = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var r in loc) localByKey[r.Key] = r.Hash;   // 키 중복 시 마지막 채택
                    foreach (var inc in grp)
                    {
                        if (!localByKey.TryGetValue(inc.Key, out var lh))
                            items.Add(new MergeItem(kind, inc.Key, MergeOp.Insert, null, inc.Hash, inc.Json));
                        else if (!string.Equals(lh, inc.Hash, StringComparison.Ordinal))
                            items.Add(new MergeItem(kind, inc.Key, MergeOp.ConflictDiff, lh, inc.Hash, inc.Json));
                        else
                            items.Add(new MergeItem(kind, inc.Key, MergeOp.Identical, lh, inc.Hash, inc.Json));
                    }
                }
            }
            return new MergePlan(items);
        }
    }
}
