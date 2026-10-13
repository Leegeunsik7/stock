// ============================================================================
// ManifestSealStreamer — [다중 저장소 봉인 감사 · 방향 A] 고정(POH pinned) 버퍼 무할당 RELEASE_MANIFEST 핸드셰이크 스트리머(δ=0)
//
//  목적: RELEASE_MANIFEST.json(ms2e-multirepo-release/1)을 힙 할당 0B 로 판독해
//    ① 정규화 문자열을 고정 스크래치에 직접 조립 → SHA-256(정적 HashData · 무할당) = manifestHash 재계산(자기 봉인 수검)
//    ② liveArmedDefault=false 확인 · masterSeal/zipSha256/genesisHeadHash 를 32B 로 복호해 외부 앵커와 상수시간 대조.
//  기준 구현(MultiRepoReleaseManifest.CanonicalHash · JsonDocument)과 '독립된 두 번째 파서'다 — 핸드셰이크는 두 판독이 모두
//  통과해야 Ok(파서 차이 공격 차단: 중복 키 · 이스케이프로 같은 값을 다르게 보이게 하는 위조는 여기서 NonCanonical/Malformed).
//
//  정규화 규칙(기준 구현과 1:1): "키=값\n" 줄 · schema→…→liveArmedDefault · interlock(기록 순서) · tighten(기록 순서 · v12.18.1-GM · 있을 때) ·
//    artifact=이름|SHA(대문자 · 이름 서수 정렬) ·
//    peer.*(있을 때) · 해시류 대문자 · 커밋 소문자 · peer.rootHash 원문.
//  엄격(fail-closed) 차이: 문자열 이스케이프 · 비 ASCII 이름 · 중복 키 · 필수 키 누락 · 버퍼/개수 초과 = 거부(Build 산출물에는 없음).
//  δ=0 범위(정직): Verify(판독·정규화·해시·대조) 0B 실측. 생성자(고정 버퍼 1회 확보)와 파일 읽기 진입(FileStream)은 콜드.
// ============================================================================
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace StockManager.Tools
{
    public enum ManifestStreamVerdict : byte
    {
        Ok = 0, Malformed = 1, NonCanonical = 2, TooLarge = 3, SelfHashMismatch = 4, LiveArmedViolation = 5,
        SealMismatch = 6, ZipMismatch = 7, GenesisMismatch = 8,
    }

    public sealed class ManifestSealStreamer
    {
        public const int DefaultCapacity = 64 * 1024;
        public const int MaxArtifacts = 512, MaxInterlocks = 128, MaxTightenRules = 64;

        // 필드 식별(정규화 순서)
        private const int FSchema = 0, FVersion = 1, FTag = 2, FSource = 3, FSig = 4, FSeal = 5, FGenesis = 6, FZipName = 7, FZip = 8, FShim = 9,
                          FHash = 10, FPeerName = 11, FPeerCommit = 12, FPeerPath = 13, FPeerSha = 14, FPeerRoot = 15, FieldCount = 16;
        private const byte Lower = 1, Upper = 2, Raw = 0;

        private readonly byte[] _input, _canon;                 // POH pinned — GC 이동 0 · 재사용
        private readonly int[] _fOff = new int[FieldCount], _fLen = new int[FieldCount];
        private readonly int[] _ilOff = new int[MaxInterlocks], _ilLen = new int[MaxInterlocks];
        private readonly int[] _tlOff = new int[MaxTightenRules], _tlLen = new int[MaxTightenRules];
        private readonly int[] _anOff = new int[MaxArtifacts], _anLen = new int[MaxArtifacts], _ahOff = new int[MaxArtifacts], _ahLen = new int[MaxArtifacts], _order = new int[MaxArtifacts];
        private readonly byte[] _seal = new byte[32], _zip = new byte[32], _genesis = new byte[32], _hash = new byte[32], _calc = new byte[32];
        private int _length, _artifacts, _interlocks, _tighten, _declaredCount;
        private bool _liveArmed, _hasPeer;

        public ManifestSealStreamer(int capacity = DefaultCapacity)
        {
            if (capacity < 1024 || capacity > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(capacity));
            _input = GC.AllocateUninitializedArray<byte>(capacity, pinned: true);
            _canon = GC.AllocateUninitializedArray<byte>(capacity * 2, pinned: true);   // 정규화 문자열 ≤ 입력 × 2(키 접두 · 소/대문자 변환은 길이 불변)
        }

        public int Capacity => _input.Length;
        public int Length => _length;
        public int ArtifactCount => _artifacts;
        public int InterlockCount => _interlocks;
        public int TightenRuleCount => _tighten;
        public ReadOnlySpan<byte> MasterSeal => _seal;
        public ReadOnlySpan<byte> ZipSha256 => _zip;
        public ReadOnlySpan<byte> GenesisHeadHash => _genesis;
        public ReadOnlySpan<byte> ManifestHash => _hash;

        /// <summary>고정 입력 버퍼(호출측이 직접 채움 → Verify(길이)). 복사 없는 경로.</summary>
        public Span<byte> InputBuffer => _input;

        /// <summary>바이트 복사 적재(δ=0). 용량 초과 = false.</summary>
        public bool Load(ReadOnlySpan<byte> json)
        {
            if (json.Length > _input.Length) { _length = 0; return false; }
            json.CopyTo(_input);
            _length = json.Length;
            return true;
        }

        /// <summary>스트림 적재(콜드 진입 · 읽기 루프는 고정 버퍼). 용량 초과 = false.</summary>
        public bool Load(Stream s)
        {
            int n = 0, r;
            while ((r = s.Read(_input, n, _input.Length - n)) > 0) { n += r; if (n == _input.Length && s.ReadByte() >= 0) { _length = 0; return false; } }
            _length = n;
            return true;
        }

        /// <summary>
        /// 적재된 매니페스트 수검(δ=0). anchorSeal/zipSha/genesis 는 32B(빈 = 그 대조 생략).
        /// 판정 순서: 형식 → 정규화 재해시(자기 봉인) → liveArmed → masterSeal → zip → genesis.
        /// </summary>
        public ManifestStreamVerdict Verify(ReadOnlySpan<byte> anchorSeal, ReadOnlySpan<byte> zipSha = default, ReadOnlySpan<byte> genesis = default)
        {
            if (_length <= 0) return ManifestStreamVerdict.Malformed;
            var v = Parse(_input.AsSpan(0, _length));
            if (v != ManifestStreamVerdict.Ok) return v;
            int clen = BuildCanonical();
            if (clen < 0) return ManifestStreamVerdict.TooLarge;
            SHA256.HashData(_canon.AsSpan(0, clen), _calc);
            if (!CryptographicOperations.FixedTimeEquals(_calc, _hash)) return ManifestStreamVerdict.SelfHashMismatch;
            if (_liveArmed) return ManifestStreamVerdict.LiveArmedViolation;
            if (!anchorSeal.IsEmpty && (anchorSeal.Length != 32 || !CryptographicOperations.FixedTimeEquals(anchorSeal, _seal))) return ManifestStreamVerdict.SealMismatch;
            if (!zipSha.IsEmpty && (zipSha.Length != 32 || !CryptographicOperations.FixedTimeEquals(zipSha, _zip))) return ManifestStreamVerdict.ZipMismatch;
            if (!genesis.IsEmpty && (genesis.Length != 32 || !CryptographicOperations.FixedTimeEquals(genesis, _genesis))) return ManifestStreamVerdict.GenesisMismatch;
            return ManifestStreamVerdict.Ok;
        }

        // ── 판독(Utf8JsonReader 타입드 게터 · 예외는 JsonException 만) ──

        private ManifestStreamVerdict Parse(ReadOnlySpan<byte> json)
        {
            Array.Clear(_fLen); Array.Fill(_fOff, -1);
            _artifacts = 0; _interlocks = 0; _tighten = 0; _declaredCount = -1; _liveArmed = true; _hasPeer = false;
            bool seenLive = false, seenIl = false, seenArts = false, seenPeer = false, seenTl = false;
            try
            {
                var r = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return ManifestStreamVerdict.Malformed;
                while (r.Read())
                {
                    if (r.TokenType == JsonTokenType.EndObject) break;
                    if (r.TokenType != JsonTokenType.PropertyName || r.ValueIsEscaped) return ManifestStreamVerdict.NonCanonical;
                    int f = TopField(ref r);
                    if (f >= 0)
                    {
                        if (_fOff[f] >= 0) return ManifestStreamVerdict.NonCanonical;   // 중복 키
                        if (!ReadString(ref r, f)) return ManifestStreamVerdict.NonCanonical;
                    }
                    else if (r.ValueTextEquals("liveArmedDefault"u8))
                    {
                        if (seenLive || !r.Read()) return ManifestStreamVerdict.NonCanonical;
                        if (r.TokenType == JsonTokenType.True) _liveArmed = true;
                        else if (r.TokenType == JsonTokenType.False) _liveArmed = false;
                        else return ManifestStreamVerdict.Malformed;
                        seenLive = true;
                    }
                    else if (r.ValueTextEquals("artifactCount"u8))
                    {
                        if (_declaredCount >= 0 || !r.Read() || r.TokenType != JsonTokenType.Number || !r.TryGetInt32(out _declaredCount) || _declaredCount < 0)
                            return ManifestStreamVerdict.Malformed;
                    }
                    else if (r.ValueTextEquals("interlocks"u8))
                    {
                        if (seenIl || !r.Read() || r.TokenType != JsonTokenType.StartArray) return ManifestStreamVerdict.Malformed;
                        seenIl = true;
                        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                        {
                            if (r.TokenType != JsonTokenType.String || r.ValueIsEscaped) return ManifestStreamVerdict.NonCanonical;
                            if (_interlocks == MaxInterlocks) return ManifestStreamVerdict.TooLarge;
                            _ilOff[_interlocks] = (int)r.TokenStartIndex + 1; _ilLen[_interlocks] = r.ValueSpan.Length; _interlocks++;
                        }
                    }
                    else if (r.ValueTextEquals("tightenRules"u8))
                    {
                        // [v12.18.1-GM] 강화 전용 행렬(선택) — 있으면 1개 이상(빈 배열 = 기준 구현도 거부하는 비정규 형식)
                        if (seenTl || !r.Read() || r.TokenType != JsonTokenType.StartArray) return ManifestStreamVerdict.Malformed;
                        seenTl = true;
                        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                        {
                            if (r.TokenType != JsonTokenType.String || r.ValueIsEscaped) return ManifestStreamVerdict.NonCanonical;
                            if (_tighten == MaxTightenRules) return ManifestStreamVerdict.TooLarge;
                            _tlOff[_tighten] = (int)r.TokenStartIndex + 1; _tlLen[_tighten] = r.ValueSpan.Length; _tighten++;
                        }
                        if (_tighten == 0) return ManifestStreamVerdict.NonCanonical;
                    }
                    else if (r.ValueTextEquals("artifacts"u8))
                    {
                        if (seenArts || !r.Read() || r.TokenType != JsonTokenType.StartArray) return ManifestStreamVerdict.Malformed;
                        seenArts = true;
                        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                        {
                            if (r.TokenType != JsonTokenType.StartObject) return ManifestStreamVerdict.Malformed;
                            if (_artifacts == MaxArtifacts) return ManifestStreamVerdict.TooLarge;
                            int nOff = -1, nLen = 0, hOff = -1, hLen = 0;
                            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                            {
                                if (r.TokenType != JsonTokenType.PropertyName || r.ValueIsEscaped) return ManifestStreamVerdict.NonCanonical;
                                bool isName = r.ValueTextEquals("name"u8), isSha = !isName && r.ValueTextEquals("sha256"u8);
                                if (!r.Read()) return ManifestStreamVerdict.Malformed;
                                if (!isName && !isSha) { r.Skip(); continue; }
                                if (r.TokenType != JsonTokenType.String || r.ValueIsEscaped) return ManifestStreamVerdict.NonCanonical;
                                if (isName) { if (nOff >= 0) return ManifestStreamVerdict.NonCanonical; nOff = (int)r.TokenStartIndex + 1; nLen = r.ValueSpan.Length; }
                                else { if (hOff >= 0) return ManifestStreamVerdict.NonCanonical; hOff = (int)r.TokenStartIndex + 1; hLen = r.ValueSpan.Length; }
                            }
                            if (nOff < 0 || hOff < 0) return ManifestStreamVerdict.Malformed;
                            for (int k = 0; k < nLen; k++) if (json[nOff + k] >= 0x80) return ManifestStreamVerdict.NonCanonical;   // 서수 정렬 = ASCII 한정
                            _anOff[_artifacts] = nOff; _anLen[_artifacts] = nLen; _ahOff[_artifacts] = hOff; _ahLen[_artifacts] = hLen; _artifacts++;
                        }
                    }
                    else if (r.ValueTextEquals("peer"u8))
                    {
                        if (seenPeer || !r.Read()) return ManifestStreamVerdict.NonCanonical;
                        seenPeer = true;
                        if (r.TokenType == JsonTokenType.Null) continue;
                        if (r.TokenType != JsonTokenType.StartObject) return ManifestStreamVerdict.Malformed;
                        _hasPeer = true;
                        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                        {
                            if (r.TokenType != JsonTokenType.PropertyName || r.ValueIsEscaped) return ManifestStreamVerdict.NonCanonical;
                            int pf = r.ValueTextEquals("name"u8) ? FPeerName : r.ValueTextEquals("commit"u8) ? FPeerCommit : r.ValueTextEquals("manifestPath"u8) ? FPeerPath
                                   : r.ValueTextEquals("manifestSha256"u8) ? FPeerSha : r.ValueTextEquals("rootHash"u8) ? FPeerRoot : -1;
                            if (pf < 0) { if (!r.Read()) return ManifestStreamVerdict.Malformed; r.Skip(); continue; }
                            if (_fOff[pf] >= 0 || !ReadString(ref r, pf)) return ManifestStreamVerdict.NonCanonical;
                        }
                        for (int pf = FPeerName; pf <= FPeerRoot; pf++) if (_fOff[pf] < 0) return ManifestStreamVerdict.Malformed;
                    }
                    else
                    {
                        if (!r.Read()) return ManifestStreamVerdict.Malformed;
                        r.Skip();                                                       // 미지 키(기준 구현과 동일하게 무시)
                    }
                }
                if (r.Read()) return ManifestStreamVerdict.Malformed;                  // 루트 뒤 잔여 토큰
            }
            catch (JsonException) { return ManifestStreamVerdict.Malformed; }
            for (int f = FSchema; f <= FHash; f++) if (_fOff[f] < 0) return ManifestStreamVerdict.Malformed;
            if (!seenLive || !seenIl || !seenArts || _declaredCount != _artifacts) return ManifestStreamVerdict.Malformed;
            if (!Matches(json, FSchema, "ms2e-multirepo-release/1"u8)) return ManifestStreamVerdict.Malformed;
            if (!DecodeHex(json, FSeal, _seal) || !DecodeHex(json, FZip, _zip) || !DecodeHex(json, FGenesis, _genesis) || !DecodeHex(json, FHash, _hash))
                return ManifestStreamVerdict.Malformed;
            return ManifestStreamVerdict.Ok;
        }

        private static int TopField(ref Utf8JsonReader r)
            => r.ValueTextEquals("schema"u8) ? FSchema : r.ValueTextEquals("version"u8) ? FVersion : r.ValueTextEquals("tag"u8) ? FTag
             : r.ValueTextEquals("sourceCommit"u8) ? FSource : r.ValueTextEquals("manifestSigSha256"u8) ? FSig : r.ValueTextEquals("masterSeal"u8) ? FSeal
             : r.ValueTextEquals("genesisHeadHash"u8) ? FGenesis : r.ValueTextEquals("zipName"u8) ? FZipName : r.ValueTextEquals("zipSha256"u8) ? FZip
             : r.ValueTextEquals("shimSha256"u8) ? FShim : r.ValueTextEquals("manifestHash"u8) ? FHash : -1;

        private bool ReadString(ref Utf8JsonReader r, int f)
        {
            if (!r.Read() || r.TokenType != JsonTokenType.String || r.ValueIsEscaped || r.HasValueSequence) return false;
            _fOff[f] = (int)r.TokenStartIndex + 1; _fLen[f] = r.ValueSpan.Length;
            return true;
        }

        private bool Matches(ReadOnlySpan<byte> json, int f, ReadOnlySpan<byte> expected) => json.Slice(_fOff[f], _fLen[f]).SequenceEqual(expected);

        // ── 정규화 조립(고정 스크래치 · 기준 구현과 같은 줄 순서) ──

        private int BuildCanonical()
        {
            ReadOnlySpan<byte> src = _input.AsSpan(0, _length);
            Span<byte> dst = _canon;
            int w = 0;
            if (!Line(dst, ref w, "schema="u8, src, FSchema, Raw) || !Line(dst, ref w, "version="u8, src, FVersion, Raw) || !Line(dst, ref w, "tag="u8, src, FTag, Raw)
                || !Line(dst, ref w, "sourceCommit="u8, src, FSource, Lower) || !Line(dst, ref w, "manifestSigSha256="u8, src, FSig, Upper)
                || !Line(dst, ref w, "masterSeal="u8, src, FSeal, Upper) || !Line(dst, ref w, "genesisHeadHash="u8, src, FGenesis, Upper)
                || !Line(dst, ref w, "zipName="u8, src, FZipName, Raw) || !Line(dst, ref w, "zipSha256="u8, src, FZip, Upper)
                || !Line(dst, ref w, "shimSha256="u8, src, FShim, Upper)
                || !Put(dst, ref w, "liveArmedDefault="u8) || !Put(dst, ref w, _liveArmed ? "true\n"u8 : "false\n"u8))
                return -1;
            for (int i = 0; i < _interlocks; i++)
                if (!Put(dst, ref w, "interlock="u8) || !Copy(dst, ref w, src.Slice(_ilOff[i], _ilLen[i]), Raw) || !Put(dst, ref w, "\n"u8)) return -1;
            for (int i = 0; i < _tighten; i++)
                if (!Put(dst, ref w, "tighten="u8) || !Copy(dst, ref w, src.Slice(_tlOff[i], _tlLen[i]), Raw) || !Put(dst, ref w, "\n"u8)) return -1;
            // 아티팩트 이름 서수 정렬(삽입 정렬 · 인덱스만 · 무할당)
            for (int i = 0; i < _artifacts; i++) _order[i] = i;
            for (int i = 1; i < _artifacts; i++)
            {
                int cur = _order[i], j = i - 1;
                var key = src.Slice(_anOff[cur], _anLen[cur]);
                while (j >= 0 && src.Slice(_anOff[_order[j]], _anLen[_order[j]]).SequenceCompareTo(key) > 0) { _order[j + 1] = _order[j]; j--; }
                _order[j + 1] = cur;
            }
            for (int i = 0; i < _artifacts; i++)
            {
                int a = _order[i];
                if (!Put(dst, ref w, "artifact="u8) || !Copy(dst, ref w, src.Slice(_anOff[a], _anLen[a]), Raw) || !Put(dst, ref w, "|"u8)
                    || !Copy(dst, ref w, src.Slice(_ahOff[a], _ahLen[a]), Upper) || !Put(dst, ref w, "\n"u8)) return -1;
            }
            if (_hasPeer
                && (!Line(dst, ref w, "peer.name="u8, src, FPeerName, Raw) || !Line(dst, ref w, "peer.commit="u8, src, FPeerCommit, Lower)
                    || !Line(dst, ref w, "peer.manifestPath="u8, src, FPeerPath, Raw) || !Line(dst, ref w, "peer.manifestSha256="u8, src, FPeerSha, Upper)
                    || !Line(dst, ref w, "peer.rootHash="u8, src, FPeerRoot, Raw)))
                return -1;
            return w;
        }

        private bool Line(Span<byte> dst, ref int w, ReadOnlySpan<byte> key, ReadOnlySpan<byte> src, int f, byte mode)
            => Put(dst, ref w, key) && Copy(dst, ref w, src.Slice(_fOff[f], _fLen[f]), mode) && Put(dst, ref w, "\n"u8);

        private static bool Put(Span<byte> dst, ref int w, ReadOnlySpan<byte> s)
        {
            if (w + s.Length > dst.Length) return false;
            s.CopyTo(dst[w..]); w += s.Length;
            return true;
        }

        // ASCII 대/소문자 정규화(기준 구현 ToUpper/ToLowerInvariant 와 ASCII 범위 동치 — 비 ASCII 바이트는 그대로)
        private static bool Copy(Span<byte> dst, ref int w, ReadOnlySpan<byte> s, byte mode)
        {
            if (w + s.Length > dst.Length) return false;
            for (int i = 0; i < s.Length; i++)
            {
                byte b = s[i];
                if (mode == Upper && b is >= (byte)'a' and <= (byte)'z') b -= 32;
                else if (mode == Lower && b is >= (byte)'A' and <= (byte)'Z') b += 32;
                dst[w + i] = b;
            }
            w += s.Length;
            return true;
        }

        private bool DecodeHex(ReadOnlySpan<byte> json, int f, Span<byte> dst)
        {
            var s = json.Slice(_fOff[f], _fLen[f]);
            if (s.Length != dst.Length * 2) return false;
            for (int i = 0; i < dst.Length; i++)
            {
                int hi = Nib(s[2 * i]), lo = Nib(s[2 * i + 1]);
                if ((hi | lo) < 0) return false;
                dst[i] = (byte)((hi << 4) | lo);
            }
            return true;
        }

        private static int Nib(byte c) => c switch
        {
            >= (byte)'0' and <= (byte)'9' => c - '0',
            >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
            _ => -1,
        };
    }
}
