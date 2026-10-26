// ============================================================================
// ZeroAllocSlidingTelemetry + HardwarePulseAntiReplayGuard + HardwareMasterKeyHandshake — v12.1.0 Think Beyond Gemini
//
//   위험 가정(제미나이의 '단순 결속 + 패키지 검증'이 다루지 않는 프로덕션 점화 파멸 리스크):
//     ① "고부하 틱 유입 시 슬라이딩 윈도우 텔레메트리 연산 중의 힙 할당 유출" — 지연/슬리피지 이동평균을 순진하게
//        List/LINQ 로 재계산하면 매 틱 힙 폭주 → GC 스톨이 서킷 브레이커 판정 자체를 지연(안전장치가 느려지는 역설).
//     ② "현장 가동 중 외부 물리 신호 리플레이(Replay) 공격" — 공격자가 정당한 하드웨어 펄스를 녹화·재전송하면
//        단조 시퀀스만으로는 '과거의 정당했던 신호'를 구분 못 할 수 있다(HardwarePulseGuard 는 순번만 검사).
//
//   ── Think Beyond (이중 방어 엔진) ─────────────────────────────────────────────
//     ① Zero-Alloc Sliding Telemetry Calculator(Idea A) — 사전 할당 고정 링에 지연/슬리피지 표본을 담고 ★러닝 섬
//        (running-sum) 증분 갱신으로 이동평균을 O(1)·0 Byte 산출. HTTP 429/50x '연속' 카운터는 성공 응답에 리셋.
//        단일 라이터(텔레메트리 집계 스레드) 계약 · Interlocked 단조 커서로 락프리 동시 판독. δ=0·결정론.
//     ② Hardware Pulse Anti-Replay & Ephemeral Key Eraser(Idea B) — 하드웨어 펄스에 ★HMAC-SHA256 인증 태그를 결속.
//        (a) 단조 시퀀스 역행 = 리플레이, (b) HMAC 불일치 = 위조. 둘 중 하나라도 감지 → ★즉시 임시 HMAC 키를
//        CryptographicOperations.ZeroMemory 로 파기(ephemeral key eraser) + sticky 봉인 → 이후 어떤 펄스도 KeyErased
//        로 fail-closed. 리플레이-거부 경로는 HMAC 이전 즉시 반환(δ=0). HardwarePulseGuard(순번+디바운스) 를 암호 인증으로 보강.
//
//   ★조립(Part 2) HardwareMasterKeyHandshake — 물리 이중 키의 '순서 있는' 핸드셰이크(Valve→LiveArming) + 창(window) +
//     단조 nonce 로 out-of-order·stale·리플레이를 거부하는 δ=0 상태기계. ★검증기일 뿐 — 무장 인가는 CanaryHealthSentinel
//     에 위임(liveArmed:false 불변). Armed 판정은 '물리 핸드셰이크가 정당했다'는 사실만 확립한다.
//
//   불변: 결정론(주입 tick·순수 비교) · δ=0(Push/리플레이거부/핸드셰이크 핫패스 힙 0) · fail-closed(위조·역행·미상 → 봉인) ·
//        락프리(Interlocked/Volatile) · liveArmed:false(측정·인증·핸드셰이크 검증일 뿐 — 무장 유발 0).
//   ※ MS2E.Core(최내부·순수) — 상위가 틱/HTTP/체결을 Push 로, 하드웨어 펄스를 Verify 로, 물리 키를 Engage 로 결선.
// ============================================================================
using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Threading;

namespace MS2E.Core
{
    // ════════════════════════════════════════════════════════════════════════
    //  Engine ① (Idea A) — Zero-Alloc Sliding Telemetry Calculator
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// 지연/슬리피지 이동평균 + HTTP 429/50x 연속 카운터를 사전할당 고정 링 + 러닝섬으로 O(1)·0 Byte 산출.
    /// ★단일 라이터(텔레메트리 집계 스레드) 계약 — Push/Reset 은 한 스레드만. 이동평균 판독은 seqlock 으로 (count,sum) 쌍 torn read 0.
    /// </summary>
    public sealed class ZeroAllocSlidingTelemetry
    {
        private readonly long[] _delay;      // 지연 표본(micros) 링
        private readonly long[] _slippage;   // 슬리피지 표본(bps) 링
        private readonly int _cap, _mask;
        private long _writeCursor;           // 단조 커서(락프리 판독용)
        private long _count;                 // 살아있는 표본 수(≤cap)
        private long _sumDelay, _sumSlip;    // 러닝섬(증분)
        private long _lastDelay, _lastSlip;
        private long _consec429, _consec50x; // 연속 오류 카운터(성공에 리셋)
        private long _version;               // ★seqlock 시퀀스(홀수=쓰기 중·짝수=안정) — (count,sum) 쌍 torn read 차단

        public ZeroAllocSlidingTelemetry(int windowPow2)
        {
            if (windowPow2 < 2 || (windowPow2 & (windowPow2 - 1)) != 0)
                throw new ArgumentException("윈도우는 2의 거듭제곱(≥2)이어야 합니다.", nameof(windowPow2));
            _delay = new long[windowPow2];
            _slippage = new long[windowPow2];
            _mask = windowPow2 - 1;
            _cap = windowPow2;
        }

        /// <summary>텔레메트리 표본 1건(★단일 라이터·O(1)·δ=0). httpStatus: 429=레이트리밋·5xx=서버오류·그외=성공(연속 리셋).</summary>
        public void Push(long delayMicros, long slippageBps, int httpStatus)
        {
            long seq = _version;                       // 단일 라이터 — 자기 쓰기 판독
            Interlocked.Exchange(ref _version, seq + 1); // 홀수 = 쓰기 진입(full fence: 이후 데이터 store 선행 금지)
            long w = _writeCursor;
            int idx = (int)(w & _mask);
            if (Volatile.Read(ref _count) == _cap)     // 링 포화 → 덮어쓸 최고참 표본을 러닝섬에서 차감
            {
                _sumDelay -= _delay[idx];
                _sumSlip  -= _slippage[idx];
            }
            else Volatile.Write(ref _count, Volatile.Read(ref _count) + 1);

            _delay[idx] = delayMicros;
            _slippage[idx] = slippageBps;
            _sumDelay += delayMicros;
            _sumSlip  += slippageBps;
            Volatile.Write(ref _lastDelay, delayMicros);
            Volatile.Write(ref _lastSlip, slippageBps);

            if (httpStatus == 429)                     { Volatile.Write(ref _consec429, Volatile.Read(ref _consec429) + 1); Volatile.Write(ref _consec50x, 0); }
            else if (httpStatus >= 500 && httpStatus <= 599) { Volatile.Write(ref _consec50x, Volatile.Read(ref _consec50x) + 1); Volatile.Write(ref _consec429, 0); }
            else                                       { Volatile.Write(ref _consec429, 0); Volatile.Write(ref _consec50x, 0); }

            Volatile.Write(ref _writeCursor, w + 1);   // 발행(release)
            Volatile.Write(ref _version, seq + 2);     // 짝수 = 쓰기 완료(release: 링·러닝섬·count 갱신 선행 보장)
        }

        /// <summary>seqlock 판독 — 짝수 버전 → count·sum 판독 → 버전 불변 확인. 불일치/쓰기 중이면 재시도(δ=0·SpinWait 구조체).</summary>
        private long SeqlockMean(ref long sum)
        {
            SpinWait spin = default;
            while (true)
            {
                long v1 = Volatile.Read(ref _version);
                if ((v1 & 1) == 0)
                {
                    long c = Volatile.Read(ref _count);
                    long s = Volatile.Read(ref sum);       // acquire — 아래 v2 판독이 데이터 판독 앞으로 당겨지지 않음
                    if (Volatile.Read(ref _version) == v1) return c > 0 ? s / c : 0;
                }
                spin.SpinOnce();
            }
        }

        public long Count => Volatile.Read(ref _count);
        public long LastDelayMicros => Volatile.Read(ref _lastDelay);
        public long LastSlippageBps => Volatile.Read(ref _lastSlip);
        public long MeanDelayMicros => SeqlockMean(ref _sumDelay);
        public long MeanSlippageBps => SeqlockMean(ref _sumSlip);
        public long Consecutive429 => Volatile.Read(ref _consec429);
        public long Consecutive50x => Volatile.Read(ref _consec50x);

        /// <summary>직전 틱 지연이 임계(micros) 초과 — "틱 지연 &gt;50ms" 게이트(50_000 micros).</summary>
        public bool DelayBreached(long thresholdMicros) => LastDelayMicros > thresholdMicros;
        /// <summary>이동평균 슬리피지가 임계(bps) 초과.</summary>
        public bool SlippageBreached(long thresholdBps) => MeanSlippageBps > thresholdBps;
        /// <summary>HTTP 429 또는 5xx 연속 발생이 임계 이상(연속 오류 폭풍).</summary>
        public bool HttpErrorStorm(long consecutiveThreshold)
            => Consecutive429 >= consecutiveThreshold || Consecutive50x >= consecutiveThreshold;

        /// <summary>★라이터 스레드에서만 호출(Push 와 동일 단일 라이터 계약) — seqlock 으로 감싸 판독측 반쯤 지워진 상태 차단.</summary>
        public void Reset()
        {
            long seq = _version;
            Interlocked.Exchange(ref _version, seq + 1);
            Array.Clear(_delay); Array.Clear(_slippage);
            Volatile.Write(ref _writeCursor, 0); Volatile.Write(ref _count, 0);
            _sumDelay = _sumSlip = 0;
            Volatile.Write(ref _lastDelay, 0); Volatile.Write(ref _lastSlip, 0);
            Volatile.Write(ref _consec429, 0); Volatile.Write(ref _consec50x, 0);
            Volatile.Write(ref _version, seq + 2);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ② (Idea B) — Hardware Pulse Anti-Replay & Ephemeral Key Eraser
    // ════════════════════════════════════════════════════════════════════════
    public enum AuthPulseVerdict : byte
    {
        Accept    = 0,   // 단조 전진 + HMAC 인증 통과 → 격발
        Replay    = 1,   // 시퀀스 역행(재전송) → 키 파기·봉인
        Forged    = 2,   // HMAC 불일치(위조) → 키 파기·봉인
        KeyErased = 3,   // 이미 임시 키 파기됨(sticky fail-closed)
    }

    /// <summary>
    /// 하드웨어 펄스 HMAC 인증 + 단조 anti-replay + 임시 키 파기. 리플레이/위조 감지 시 ★임시 HMAC 키를 0x00 파기 →
    /// 이후 모든 펄스 KeyErased(fail-closed). 물리 재무장(새 키 시드) 전까지 봉인. 리플레이-거부는 HMAC 이전 δ=0 반환.
    /// </summary>
    public sealed class HardwarePulseAntiReplayGuard : IDisposable
    {
        private readonly byte[] _key;        // 임시 HMAC 키(파기 대상)
        private long _lastSeq;
        private long _initialized;
        private long _keyErased;             // sticky
        private long _accepts, _replays, _forgeries;

        public HardwarePulseAntiReplayGuard(ReadOnlySpan<byte> ephemeralKey)
        {
            if (ephemeralKey.Length == 0) throw new ArgumentException("임시 키는 비어 있을 수 없습니다.", nameof(ephemeralKey));
            _key = ephemeralKey.ToArray();
        }

        /// <summary>정당한 하드웨어가 펄스에 붙일 HMAC 태그 산출(HMAC-SHA256(key, seq_LE64) → 32B). δ=0(호출측 dest 제공).</summary>
        public static void ComputeTag(ReadOnlySpan<byte> key, long seq, Span<byte> dest32)
        {
            Span<byte> src = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(src, seq);
            HMACSHA256.HashData(key, src, dest32);
        }

        /// <summary>
        /// 펄스 인증 판정. ★리플레이(seq≤last)/위조(HMAC 불일치) → 즉시 임시 키 0x00 파기 + 봉인. 파기 후 전량 KeyErased.
        /// 리플레이-거부는 HMAC 계산 이전에 반환(δ=0 fail-closed). 정당 펄스만 단조 전진.
        /// </summary>
        public AuthPulseVerdict Verify(long seq, ReadOnlySpan<byte> tag, long nowTicks)
        {
            if (Volatile.Read(ref _keyErased) == 1) return AuthPulseVerdict.KeyErased;   // ★봉인 후 전량 차단(δ=0)

            if (Volatile.Read(ref _initialized) == 1 && seq <= Volatile.Read(ref _lastSeq))
            {
                Interlocked.Increment(ref _replays);
                EraseKey();                                                              // ★리플레이 → 키 파기(δ=0 경로)
                return AuthPulseVerdict.Replay;
            }

            Span<byte> expect = stackalloc byte[32];
            ComputeTag(_key, seq, expect);
            if (tag.Length != 32 || !CryptographicOperations.FixedTimeEquals(expect, tag))
            {
                Interlocked.Increment(ref _forgeries);
                EraseKey();                                                              // ★위조 → 키 파기
                return AuthPulseVerdict.Forged;
            }

            Volatile.Write(ref _lastSeq, seq);
            Volatile.Write(ref _initialized, 1);
            Interlocked.Increment(ref _accepts);
            return AuthPulseVerdict.Accept;
        }

        private void EraseKey()
        {
            if (Interlocked.Exchange(ref _keyErased, 1) == 0)
                CryptographicOperations.ZeroMemory(_key);   // 임시 키 0x00 파기(1회·멱등)
        }

        public bool IsKeyErased => Volatile.Read(ref _keyErased) == 1;
        public long AcceptCount => Interlocked.Read(ref _accepts);
        public long ReplayCount => Interlocked.Read(ref _replays);
        public long ForgeryCount => Interlocked.Read(ref _forgeries);
        public long LastSeq => Volatile.Read(ref _lastSeq);

        public void Dispose() => EraseKey();   // 폐기 시 키 파기(잔재 0x00)
    }

    // ════════════════════════════════════════════════════════════════════════
    //  조립 (Part 2) — Hardware Master Key Handshake (순서 있는 물리 이중 키 검증)
    // ════════════════════════════════════════════════════════════════════════
    public enum HandshakeState : byte { Idle = 0, ValveEngaged = 1, Armed = 2, Rejected = 3 }

    /// <summary>
    /// 물리 이중 키의 순서 있는 핸드셰이크(Valve→LiveArming) 검증기(δ=0·결정론). out-of-order·stale(창 초과)·리플레이(nonce
    /// 역행)를 거부. ★검증기일 뿐 — 무장 인가는 CanaryHealthSentinel 에 위임(liveArmed:false). Armed=핸드셰이크 정당성만.
    /// </summary>
    public sealed class HardwareMasterKeyHandshake
    {
        private readonly long _windowTicks;
        private long _state;        // (byte)HandshakeState
        private long _valveTicks;
        private long _lastNonce;    // 단조(리플레이 차단·리셋에도 유지)
        private long _initialized;

        public HardwareMasterKeyHandshake(long windowTicks)
            => _windowTicks = windowTicks > 0 ? windowTicks : 1;

        public HandshakeState State => (HandshakeState)(byte)Volatile.Read(ref _state);
        public bool IsArmed => State == HandshakeState.Armed;

        /// <summary>1단계: 밸브 키 결속. nonce 는 단조(역행=리플레이 거부). 성공 시 ValveEngaged.</summary>
        public bool EngageValve(long nonce, long nowTicks)
        {
            if (Volatile.Read(ref _initialized) == 1 && nonce <= Volatile.Read(ref _lastNonce))
            { Volatile.Write(ref _state, (byte)HandshakeState.Rejected); return false; }   // 리플레이
            Volatile.Write(ref _lastNonce, nonce);
            Volatile.Write(ref _initialized, 1);
            Volatile.Write(ref _valveTicks, nowTicks);
            Volatile.Write(ref _state, (byte)HandshakeState.ValveEngaged);
            return true;
        }

        /// <summary>2단계: 라이브 무장 키. ★반드시 ValveEngaged 상태 + 창 이내 + 신선 nonce. 아니면 거부(fail-closed).</summary>
        public bool EngageLiveArming(long nonce, long nowTicks)
        {
            if (State != HandshakeState.ValveEngaged)                              // ★순서 위반(밸브 선행 필수)
            { Volatile.Write(ref _state, (byte)HandshakeState.Rejected); return false; }
            if (nonce <= Volatile.Read(ref _lastNonce))                            // 리플레이/역행 nonce
            { Volatile.Write(ref _state, (byte)HandshakeState.Rejected); return false; }
            if (nowTicks - Volatile.Read(ref _valveTicks) > _windowTicks)          // 창 초과(stale) → Idle 복귀
            { Volatile.Write(ref _state, (byte)HandshakeState.Idle); return false; }
            Volatile.Write(ref _lastNonce, nonce);
            Volatile.Write(ref _state, (byte)HandshakeState.Armed);
            return true;
        }

        /// <summary>세션 리셋 — 상태만 Idle 로. ★_lastNonce 는 유지(리셋 후 리플레이 차단).</summary>
        public void Reset()
        {
            Volatile.Write(ref _state, (byte)HandshakeState.Idle);
            Volatile.Write(ref _valveTicks, 0);
        }
    }
}
