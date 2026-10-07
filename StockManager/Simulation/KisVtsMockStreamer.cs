// ============================================================================
// KisVtsMockStreamer — [KIS VTS · 추천 A] KIS 모의투자(VTS) WebSocket 가상 송출기 + 무할당 패킷 리플레이 · 지연 프로파일러(방향 A)
//
//  ① 프레임 조립(콜드 · 등록 시 1회 할당): VTS 구독 응답(header.tr_id=H0STCNI9 · output.key/iv) · 체결가(H0STCNT0 · 46필드) ·
//     체결통보(H0STCNI9 · 공개 문서 필드 순서 0..16 + 이름 2필드 · AES-256-CBC/PKCS7 → Base64 · 암호 플래그 "1") · PINGPONG · 원문.
//     전부 Pinned Object Heap 단일 버퍼(GC.AllocateArray pinned)에 연속 적재 — 오프셋/길이 표만 보관(GC 이동 0 · 캐시 지역성).
//  ② 송출: Frame(i) = 고정 버퍼 위 ReadOnlyMemory 슬라이스(할당 0) → 수신측(KisOpenApiAdapter.OnFrame)에 그대로 투입.
//  ③ 리플레이(방향 A): [from, to) 프레임을 rounds 회 연속 투입 · 프레임마다 Stopwatch 타임스탬프 차 → log2(ns) 64버킷 히스토그램
//     (고정 배열 · δ=0) → p50/p99/max(버킷 상한 · 보수적) · 초당 처리량. ★처리량은 이 호스트 실측값일 뿐 — 50,000건/초는 목표이지
//     보증이 아니다(테스트는 하한만 단언 · 실측은 보고).
//  ★실 KIS 패킷 캡처가 이 저장소엔 없다 — 프레임은 공개 문서 형식으로 합성한다(실 VTS 프레임 1건씩 대조는 운용 과제).
//  계좌/HTS ID 는 합성 값(실 자격증명 아님) · 복호 키도 테스트 전용 임의 값.
// ============================================================================
using System;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MS2E.Infrastructure;

namespace StockManager.Simulation
{
    public readonly record struct KisReplayReport(long Frames, long Accepted, long ElapsedTicks, long P50Nanos, long P99Nanos, long MaxNanos)
    {
        public double FramesPerSecond => ElapsedTicks <= 0 ? 0 : Frames * (double)Stopwatch.Frequency / ElapsedTicks;
    }

    public sealed class KisVtsMockStreamer : IDisposable
    {
        public const string VtsNoticeTr = "H0STCNI9", RealNoticeTr = "H0STCNI0", TickTr = "H0STCNT0";

        private readonly byte[] _buf;
        private readonly int[] _off, _len;
        private readonly byte[] _key, _iv;
        private readonly string _htsId, _account;
        private readonly long[] _hist = new long[64];
        private int _used, _count;

        /// <param name="key32">체결통보 AES-256 키(32 ASCII — 구독 응답 output.key 로도 실린다).</param>
        /// <param name="iv16">CBC IV(16 ASCII).</param>
        /// <param name="account">체결통보 계좌 필드(합성 · 예: 모의 계좌 "50000000" + 상품 "01").</param>
        public KisVtsMockStreamer(string key32, string iv16, string htsId = "vtsuser", string account = "5000000001", int capacityBytes = 1 << 20, int maxFrames = 4096)
        {
            if (key32 is null || key32.Length != 32) throw new ArgumentException("키는 32자", nameof(key32));
            if (iv16 is null || iv16.Length != 16) throw new ArgumentException("IV 는 16자", nameof(iv16));
            if (capacityBytes <= 0 || maxFrames <= 0) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
            _key = Encoding.ASCII.GetBytes(key32); _iv = Encoding.ASCII.GetBytes(iv16);
            _htsId = htsId; _account = account;
            _buf = GC.AllocateArray<byte>(capacityBytes, pinned: true);
            _off = new int[maxFrames]; _len = new int[maxFrames];
        }

        public int Count => _count;
        public string KeyText => Encoding.ASCII.GetString(_key);
        public string IvText => Encoding.ASCII.GetString(_iv);

        /// <summary>i 번째 프레임(고정 버퍼 슬라이스 · 할당 0).</summary>
        public ReadOnlyMemory<byte> Frame(int i)
        {
            if ((uint)i >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(i));
            return new ReadOnlyMemory<byte>(_buf, _off[i], _len[i]);
        }

        // ── ① 프레임 조립(콜드) ──

        public int AddRaw(string text) => Append(Encoding.UTF8.GetBytes(text));

        /// <summary>구독 응답(성공) — trId 기본 = 모의 체결통보. 체결통보 TR 이면 output.key/iv 동봉.</summary>
        public int AddSubscribeAck(string trId = VtsNoticeTr, bool success = true)
        {
            bool notice = trId is VtsNoticeTr or RealNoticeTr;
            string output = notice ? ",\"output\":{\"iv\":\"" + IvText + "\",\"key\":\"" + KeyText + "\"}" : "";
            return AddRaw("{\"header\":{\"tr_id\":\"" + trId + "\",\"tr_key\":\"" + _htsId + "\",\"encrypt\":\"N\"},\"body\":{\"rt_cd\":\"" + (success ? "0" : "1")
                        + "\",\"msg_cd\":\"OPSP0000\",\"msg1\":\"SUBSCRIBE SUCCESS\"" + output + "}}");
        }

        public int AddPingPong() => AddRaw("{\"header\":{\"tr_id\":\"PINGPONG\",\"datetime\":\"20261007093000\"}}");

        /// <summary>체결가 1건(H0STCNT0 · 0 코드 · 1 시간 · 2 현재가 · 12 체결량 · 13 누적량 · 나머지 "0").</summary>
        public int AddTick(string symbol6, long price, long tradeVolume, long accVolume, long timeHhmmss = 93001)
        {
            var f = new string[KisProtocol.H0STCNT0FieldCount];
            for (int i = 0; i < f.Length; i++) f[i] = "0";
            f[0] = symbol6; f[1] = timeHhmmss.ToString("D6", CultureInfo.InvariantCulture);
            f[2] = price.ToString(CultureInfo.InvariantCulture); f[12] = tradeVolume.ToString(CultureInfo.InvariantCulture); f[13] = accVolume.ToString(CultureInfo.InvariantCulture);
            return AddRaw("0|" + TickTr + "|001|" + string.Join('^', f));
        }

        /// <summary>
        /// 체결통보 1건. side +1 매수(02) · −1 매도(01). filled = 체결(체결여부 2) · 아니면 접수(1). cancelConfirmed = 정정구분 2.
        /// encrypt = AES 암호문(실 KIS 형식) · false = 평문(플래그 "0"). trId 기본 = 모의(H0STCNI9) · account null = 생성자 계좌.
        /// </summary>
        public int AddNotice(long odno, int side, string symbol6, long fillQty, long fillPrice, long orderQty, bool filled = true,
                             bool rejected = false, bool cancelConfirmed = false, long origOdno = 0, string trId = VtsNoticeTr, bool encrypt = true, string? account = null)
        {
            var inv = CultureInfo.InvariantCulture;
            string plain = string.Join('^', new[]
            {
                _htsId, account ?? _account, odno.ToString("D10", inv), origOdno > 0 ? origOdno.ToString("D10", inv) : "",
                side > 0 ? "02" : "01", cancelConfirmed ? "2" : "0", "00", "0", symbol6,
                filled ? fillQty.ToString(inv) : "0", filled ? fillPrice.ToString(inv) : "0", "093001",
                rejected ? "1" : "0", filled ? "2" : "1", "1", "91252", orderQty.ToString(inv), "VTSUSER", "SYMBOL",
            });
            string body;
            if (encrypt)
            {
                using var aes = Aes.Create();
                aes.Key = _key;
                body = Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(plain), _iv, PaddingMode.PKCS7));
            }
            else body = plain;
            return AddRaw((encrypt ? "1|" : "0|") + trId + "|001|" + body);
        }

        private int Append(ReadOnlySpan<byte> bytes)
        {
            if (_count == _off.Length) throw new InvalidOperationException("프레임 표 용량 초과");
            if (_used + bytes.Length > _buf.Length) throw new InvalidOperationException("고정 버퍼 용량 초과");
            bytes.CopyTo(_buf.AsSpan(_used));
            _off[_count] = _used; _len[_count] = bytes.Length;
            _used += bytes.Length;
            return _count++;
        }

        // ── ② · ③ 송출 · 리플레이(δ=0) ──

        /// <summary>프레임 1건 송출(수신측 반환 = 인식 여부).</summary>
        public bool Emit(int i, Func<ReadOnlyMemory<byte>, bool> sink) => sink(Frame(i));

        /// <summary>
        /// [방향 A] [from, to) 프레임 × rounds 연속 리플레이 — 프레임당 지연(ns) log2 히스토그램 · 처리량. 할당 0(고정 히스토그램 ·
        /// 슬라이스 구조체 · Stopwatch 정적 타임스탬프). to = −1 → 끝까지.
        /// </summary>
        public KisReplayReport Replay(Func<ReadOnlyMemory<byte>, bool> sink, int rounds, int from = 0, int to = -1)
        {
            if (sink is null) throw new ArgumentNullException(nameof(sink));
            if (to < 0) to = _count;
            if (from < 0 || from > to || to > _count || rounds <= 0) throw new ArgumentOutOfRangeException(nameof(rounds));
            Array.Clear(_hist);
            long frames = 0, accepted = 0, maxTicks = 0;
            long start = Stopwatch.GetTimestamp();
            for (int r = 0; r < rounds; r++)
                for (int i = from; i < to; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    if (sink(new ReadOnlyMemory<byte>(_buf, _off[i], _len[i]))) accepted++;
                    long dt = Stopwatch.GetTimestamp() - t0;
                    if (dt > maxTicks) maxTicks = dt;
                    _hist[Bucket(TicksToNanos(dt))]++;
                    frames++;
                }
            long elapsed = Stopwatch.GetTimestamp() - start;
            return new KisReplayReport(frames, accepted, elapsed, Percentile(frames, 0.50), Percentile(frames, 0.99), TicksToNanos(maxTicks));
        }

        private static long TicksToNanos(long ticks) => (long)(ticks * (1_000_000_000.0 / Stopwatch.Frequency));
        private static int Bucket(long ns) => ns <= 0 ? 0 : Math.Min(63, 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)ns));

        // 버킷 상한(2^b ns) — 보수적(실제 이하가 아님을 보장하지 않으므로 '상한' 으로 보고)
        private long Percentile(long total, double q)
        {
            if (total == 0) return 0;
            long target = (long)Math.Ceiling(total * q), acc = 0;
            for (int b = 0; b < _hist.Length; b++)
            {
                acc += _hist[b];
                if (acc >= target) return b == 0 ? 0 : 1L << Math.Min(62, b);
            }
            return long.MaxValue;
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(_key);
            CryptographicOperations.ZeroMemory(_iv);
        }
    }
}
