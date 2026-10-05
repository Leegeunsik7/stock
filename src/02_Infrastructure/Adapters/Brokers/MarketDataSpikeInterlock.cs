// ============================================================================
// MarketDataSpikeInterlock — [시세 하드닝 Think Beyond 방향 B] Market Data Anomaly & Spike Interlock
//
//   위협: 거래소/브로커 피드가 한 틱에 상식을 벗어난 가격(체결가 0 근처·자릿수 오류·상하한가 밖)을 흘리면, 그 값이 곧바로 리스크
//   평가(VaR·손절·낙폭)와 텔레메트리로 번져 "가짜 급락"에 의한 연쇄 판정 또는 "가짜 안정"에 의한 방어 무력화를 일으킨다.
//   판정(전부 δ=0 · 정수 산술 · Int128 곱):
//     ① 틱 점프 — 같은 종목 직전 수용가 대비 |Δ| > JumpBps(기본 1,000bps = 10%). KRX 개별 종목은 VI(변동성완화장치)로 수 % 단위에서
//        단일가로 전환되므로 한 틱 10% 도약은 실데이터로는 사실상 발생하지 않는다(오탐 최소화 기준).
//     ② 가격제한폭 — 기준가(전일 종가)가 SetReference 로 주입된 종목만: 기준가 ±BandBps(기본 3,000bps = ±30%) 밖이면 이상.
//        기준가 미주입 종목은 ①만 적용(첫 관측가를 기준가로 삼으면 장중 ±30% 이동을 오탐하므로 추정하지 않음 — 정직 한계).
//   이상 시: 해당 시세 라인(종목) 격리(sticky — 이후 틱 무시) + 인터락 Trip(sticky) → ArmingBlocked=true(BlazorHomeForm blockWhile)
//   + onTrip(킬스위치). 재무장은 물리 이중키(TryRearm) — 격리 종목도 그때 해제.
//   저장소: 종목 해시 → (기준가, 직전가, 격리) 선형 탐사 고정 테이블(생성 시 1회 할당, 이후 힙 0) · 단일 라이터(수신 루프) + 경합 없는 lock.
//   불변: liveArmed:false(차단만 — 무장 부여 불가) · 결정론 · fail-closed(테이블 포화 → 새 종목은 판정 불가 = 격리 취급).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    public enum SpikeVerdict : byte { Accepted = 0, InvalidPrice = 1, TickJump = 2, OutOfPriceBand = 3, LineQuarantined = 4, TableFull = 5 }

    public sealed class MarketDataSpikeInterlock : MS2E.Core.IArmingBlockSource
    {
        public const long DefaultJumpBps = 1_000;
        public const long DefaultBandBps = 3_000;

        private readonly long[] _sym;       // 0 = 빈 슬롯(해시 0 은 1 로 치환)
        private readonly long[] _refPx;     // 0 = 기준가 미주입
        private readonly long[] _lastPx;    // 0 = 수용 이력 없음
        private readonly bool[] _quarantined;
        private readonly int _mask;
        private readonly long _jumpBps, _bandBps;
        private readonly Action<SpikeVerdict>? _onTrip;
        private readonly object _gate = new();
        private int _count, _tripped;
        private long _spikes;

        public MarketDataSpikeInterlock(int capacityPow2 = 4096, long jumpBps = DefaultJumpBps, long bandBps = DefaultBandBps, Action<SpikeVerdict>? onTrip = null)
        {
            if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0) throw new ArgumentException("용량은 2의 거듭제곱", nameof(capacityPow2));
            if (jumpBps <= 0 || bandBps <= 0) throw new ArgumentOutOfRangeException(nameof(jumpBps));
            _sym = new long[capacityPow2]; _refPx = new long[capacityPow2]; _lastPx = new long[capacityPow2]; _quarantined = new bool[capacityPow2];
            _mask = capacityPow2 - 1; _jumpBps = jumpBps; _bandBps = bandBps; _onTrip = onTrip;
        }

        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        /// <summary>★blockWhile — 이상 시세 유입 이후 무장 차단(sticky).</summary>
        public bool ArmingBlocked => Tripped;
        public long Spikes => Interlocked.Read(ref _spikes);

        // [실배선 · 방향 B] 원자 인터락 집계기 푸시 결속(ArmingInterlockBitmask) — 상태 전이 직후 Resync(δ=0).
        private MS2E.Core.ArmingInterlockLink? _interlockLink;
        /// <summary>집계기에 이 엔진을 고정 비트로 결속 — 결속 즉시 현재 상태 동기화(이미 차단이면 비트 유지).</summary>
        public void BindInterlock(MS2E.Core.ArmingInterlockBitmask aggregator, MS2E.Core.ArmingInterlockSource bit)
        {
            ArgumentNullException.ThrowIfNull(aggregator);
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _interlockLink, link);
            link.Resync();   // Bind ~ 링크 가시화 사이의 전이 흡수
        }
        private void SignalInterlock() => Volatile.Read(ref _interlockLink)?.Resync();

        /// <summary>종목 기준가(전일 종가) 주입(콜드) — 가격제한폭 판정 활성화.</summary>
        /// <summary>[보호 지정가] 주입된 기준가(가격제한폭 산정용) 판독. 미주입 = false.</summary>
        public bool TryGetReference(long symbolHash, out long referencePriceRaw)
        {
            lock (_gate)
            {
                int i = Slot(symbolHash, create: false);
                referencePriceRaw = i >= 0 ? _refPx[i] : 0;
                return referencePriceRaw > 0;
            }
        }

        public bool SetReference(long symbolHash, long referencePriceRaw)
        {
            if (referencePriceRaw <= 0) return false;
            lock (_gate)
            {
                int i = Slot(symbolHash, create: true);
                if (i < 0) return false;
                _refPx[i] = referencePriceRaw;
                return true;
            }
        }

        public bool IsQuarantined(long symbolHash)
        {
            lock (_gate) { int i = Slot(symbolHash, create: false); return i >= 0 && _quarantined[i]; }
        }

        /// <summary>틱 1건 판정(δ=0). Accepted 만 하류(스냅샷·리스크)로 흘려야 한다.</summary>
        public SpikeVerdict Observe(long symbolHash, long priceRaw)
        {
            SpikeVerdict v;
            lock (_gate)
            {
                int i = Slot(symbolHash, create: true);
                if (i < 0) v = SpikeVerdict.TableFull;
                else if (_quarantined[i]) return SpikeVerdict.LineQuarantined;   // 이미 격리 — 재Trip 없이 무시
                else if (priceRaw <= 0) v = SpikeVerdict.InvalidPrice;
                else if (_lastPx[i] > 0 && Bps(priceRaw, _lastPx[i]) > _jumpBps) v = SpikeVerdict.TickJump;
                else if (_refPx[i] > 0 && Bps(priceRaw, _refPx[i]) > _bandBps) v = SpikeVerdict.OutOfPriceBand;
                else { _lastPx[i] = priceRaw; return SpikeVerdict.Accepted; }
                if (i >= 0) _quarantined[i] = true;
            }
            Interlocked.Increment(ref _spikes);
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) == 0)
            {
                SignalInterlock();   // 플래그 선기록 → 집계 비트 Raise(송출 직전 veto 즉시 반영)
                try { _onTrip?.Invoke(v); } catch { /* 콜백 실패가 차단을 되돌리지 않음 */ }
            }
            return v;
        }

        /// <summary>★물리 이중키 재무장 — 격리·Trip 해제(원인 규명 후).</summary>
        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            lock (_gate) { Array.Clear(_quarantined); Array.Clear(_lastPx); }
            Volatile.Write(ref _tripped, 0);
            SignalInterlock();
            return true;
        }

        private static long Bps(long px, long basis)
        {
            Int128 d = (Int128)px - basis;
            if (d < 0) d = -d;
            Int128 bps = d * 10_000 / basis;
            return bps > long.MaxValue ? long.MaxValue : (long)bps;
        }

        private int Slot(long symbolHash, bool create)
        {
            long key = symbolHash == 0 ? 1 : symbolHash;
            int i = (int)((ulong)key * 0x9E3779B97F4A7C15UL >> 40) & _mask;
            for (int n = 0; n <= _mask; n++)
            {
                if (_sym[i] == key) return i;
                if (_sym[i] == 0)
                {
                    if (!create || _count >= _mask - (_mask >> 3)) return -1;
                    _sym[i] = key; _count++;
                    return i;
                }
                i = (i + 1) & _mask;
            }
            return -1;
        }
    }
}
