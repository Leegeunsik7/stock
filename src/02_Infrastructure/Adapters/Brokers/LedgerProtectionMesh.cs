// ============================================================================
// LedgerProtectionMesh — v4.88.0: 원장 영속화 비동기 분리(발주 핫패스 지터 차단)
//
//  시장 폭발 시 대량 체결 원장 쓰기(SQLite WAL) + 평단 재계산이 50ms 발주 핫패스에 레이턴시 지터를
//  유발하지 않도록, 핫패스는 blittable 페이로드를 무할당·논블로킹으로 큐에 밀어 넣고 즉시 복귀하며,
//  전용 백그라운드 드레인 스레드가 순차 영속화 + 후속 연산(RecomputeAverage)을 격리 실행한다.
//
//  ── 무할당(δ=0) 핫패스 ──────────────────────────────────────────────────────
//   DbTransactionPayload 는 [Sequential,Pack=1] unmanaged 값 타입(참조/문자열 0). Bounded Channel<T>
//   에 TryWrite(값 복사·힙 0). 심볼은 KSE 6자리 long 코드(OrderProposal 관례) — 드레인이 FormatSymbol 복원.
//
//  ── fail-closed(방화벽 2대) ─────────────────────────────────────────────────
//   · 큐 포화: TryWrite 실패(가득) 또는 잔여 용량 90% 초과(디스크 쓰기 기아) → ForceFreeze("Ledger Mesh Overflow").
//   · 영속화 실패: AddTransaction/RecomputeAverage 예외(디스크 풀·DB 락·검증 실패) → 삼키지 않고 ForceFreeze.
//
//  ★HANDOVER §12 연쇄 저장 불변식: 드레인 워커는 AddTransaction '직후' 반드시 RecomputeAverage 를 동일
//   스레드에서 순차 실행 — 장부 가치와 평단 디싱크 원천 차단(비동기여도 순서 보장).
//  ★liveArmed:false: 본 메시는 순수 영속화 오프로드 — 주문 발주/점화 FSM 을 만지지 않는다(무결합).
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;

namespace MS2E.Infrastructure
{
    /// <summary>원장 쓰기 요청 blittable 페이로드(참조 0·힙 0). 심볼=KSE 6자리 long, 수치=QFixed raw(10^8).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct DbTransactionPayload
    {
        public readonly long SymbolCode;      // KSE 6자리(예 5930) — FormatSymbol 로 "005930" 복원
        public readonly byte TypeByte;        // TransactionType (Buy=0/Sell=1/…)
        public readonly long QtyRaw;          // 수량(QQty raw)
        public readonly long PriceRaw;        // 단가(QPrice raw)
        public readonly long CommissionRaw;   // 수수료(raw)
        public readonly long TaxRaw;          // 세금(raw)
        public readonly long ExecutedAtTicks; // 체결 시각(UTC ticks)
        public readonly long GenomeId;        // 감사 계보(선택)

        public DbTransactionPayload(long symbolCode, byte typeByte, long qtyRaw, long priceRaw,
                                    long commissionRaw, long taxRaw, long executedAtTicks, long genomeId = 0)
        {
            SymbolCode = symbolCode; TypeByte = typeByte; QtyRaw = qtyRaw; PriceRaw = priceRaw;
            CommissionRaw = commissionRaw; TaxRaw = taxRaw; ExecutedAtTicks = executedAtTicks; GenomeId = genomeId;
        }
    }

    public sealed class LedgerProtectionMesh : IDisposable
    {
        private readonly LocalDatabase            _db;
        private readonly OrderAttestationService  _att;
        private readonly int                      _capacity;
        private readonly int                      _highWater;      // 90% 포화 임계
        private readonly Channel<DbTransactionPayload> _channel;
        private readonly ChannelWriter<DbTransactionPayload> _writer;
        private readonly ChannelReader<DbTransactionPayload> _reader;

        private Thread?       _drainThread;
        private volatile bool _running;

        private long _enqueued;
        private long _persisted;
        private long _writeFailures;
        private long _avgPersistTicksBits;   // 지수이동평균(persist 소요 Stopwatch ticks) — double bits, Interlocked

        public LedgerProtectionMesh(LocalDatabase db, OrderAttestationService attestation, int capacity = 4096)
        {
            _db        = db          ?? throw new ArgumentNullException(nameof(db));
            _att       = attestation ?? throw new ArgumentNullException(nameof(attestation));
            _capacity  = capacity < 16 ? 16 : capacity;
            _highWater = (int)(_capacity * 0.9);   // 90% 초과 → fail-closed
            // ★Bounded·FullMode.Wait: 가득 차면 TryWrite=false(원장은 절대 드롭 금지 — DropWrite 미사용).
            _channel = Channel.CreateBounded<DbTransactionPayload>(new BoundedChannelOptions(_capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,     // 단일 드레인 스레드
                SingleWriter = false,    // 다중 생산자 방어(핫패스 스레드 다수 대비)
            });
            _writer = _channel.Writer;
            _reader = _channel.Reader;
        }

        // ── 관제 독출(락-프리) ─────────────────────────────────────────────────
        public int    Capacity          => _capacity;
        public int    QueueDepth        => _reader.CanCount ? _reader.Count : 0;
        public long   Enqueued          => Interlocked.Read(ref _enqueued);
        public long   Persisted         => Interlocked.Read(ref _persisted);
        public long   WriteFailures     => Interlocked.Read(ref _writeFailures);
        public double AvgPersistMicros
        {
            get
            {
                double t = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _avgPersistTicksBits));
                return t <= 0 ? 0.0 : t / Stopwatch.Frequency * 1_000_000.0;   // ticks → µs
            }
        }
        public bool Running => _running;

        // ── 핫패스: 무할당·논블로킹 인큐 ────────────────────────────────────────
        /// <summary>
        /// 원장 페이로드를 큐에 밀어 넣고 즉시 복귀(δ=0·논블로킹). 반환: 수용 여부.
        /// ★fail-closed: 큐 포화(TryWrite 실패) 또는 잔여 용량 90% 초과 → ForceFreeze("Ledger Mesh Overflow").
        /// </summary>
        public bool Enqueue(in DbTransactionPayload p)
        {
            // ★동결 인지 fail-closed: 이미 EmergencyFreeze 면 신규 수용 거부(동결 슬롯 재유입·busy-spin 유발 차단).
            if (_att.Mode == GatingMode.EmergencyFreeze)
            { Interlocked.Increment(ref _writeFailures); return false; }
            if (!_writer.TryWrite(p))   // 가득 참 → 원장 드롭 불가 → 즉시 fail-closed
            {
                Interlocked.Increment(ref _writeFailures);
                Freeze("Ledger Mesh Overflow — 큐 포화(write 거부·디스크 쓰기 기아)");
                return false;
            }
            Interlocked.Increment(ref _enqueued);
            if (QueueDepth > _highWater)   // 90% 초과 → 선제 영구 락업(모호한 방치 금지)
                Freeze($"Ledger Mesh Overflow — 큐 깊이 {QueueDepth}/{_capacity}(90% 초과)");
            return true;
        }

        // ── 백그라운드 드레인 스레드 ───────────────────────────────────────────
        public void Start()
        {
            if (_running) return;
            _running = true;
            _drainThread = new Thread(DrainLoop) { IsBackground = true, Name = "LedgerDrain", Priority = ThreadPriority.BelowNormal };
            _drainThread.Start();
        }

        public void Stop()
        {
            _running = false;
            var t = _drainThread;
            if (t is not null && t.IsAlive) t.Join(2000);
            _drainThread = null;
        }

        private void DrainLoop()
        {
            while (_running)
            {
                int n = DrainAvailable();
                if (n == 0) Thread.Sleep(2);   // 콜드 대기(핫패스 무영향) — 큐 비거나 동결 유휴 시 짧게 양보
            }
            DrainAvailable();   // 종료 전 잔여 플러시(동결 상태면 즉시 0 반환)
        }

        /// <summary>
        /// 현재 큐에 있는 페이로드를 순차 영속화(테스트 직접 호출 가능). 반환: 성공 영속화 건수.
        /// ★동결 인지 유휴: EmergencyFreeze 면 아무것도 읽지 않고 0 반환 — 잔여는 큐에 보존(드롭 금지),
        ///   DrainLoop 은 n==0 로 backoff(Sleep) → 재시도/ForceFreeze 폭주 원천 차단. 수동 해제 후 재개하면 배수.
        /// </summary>
        public int DrainAvailable()
        {
            if (_att.Mode == GatingMode.EmergencyFreeze) return 0;   // ★동결 시 소비 중단(busy-spin·감사 범람 방지)
            int n = 0;
            while (_reader.TryRead(out var p))
            {
                if (!Persist(in p)) break;   // 영속화 실패 → ForceFreeze; 다음 사이클부터 상단 게이트가 유휴 전환
                n++;                          // ★성공만 계상 — 실패-only 사이클은 0 반환 → DrainLoop 즉시 backoff
                if (_att.Mode == GatingMode.EmergencyFreeze) break;   // 도중 타 원인 동결 시 즉시 중단(잔여 보존)
            }
            return n;
        }

        /// <summary>드레인 직전 페이로드 검증 펜스(웜스타트 가격정합성). true=통과·false=오염(호출측이 ForceFreeze 책임).</summary>
        public delegate bool RecoveryFence(in DbTransactionPayload payload);

        /// <summary>
        /// ★웜스타트 복구 전용 순차 드레인(단일 백그라운드 스레드). 반드시 Stop() 으로 자체 DrainLoop 를 종료
        /// (SingleReader 인계)한 뒤 호출해야 한다 — _running 이면 InvalidOperationException(레이스 원천 차단).
        /// 각 항목을 fence 로 검증 후 Persist(→AddTransaction→RecomputeAverage 연쇄). fence 위반(오염) 또는 Persist
        /// 실패 시 그 항목은 영속화하지 않고 즉시 중단(잔여 큐 보존·드롭 금지). 반환: 영속화 성공 건수.
        /// 위반 여부는 호출측이 _att.Mode(EmergencyFreeze) 또는 반환 건수로 판정.
        /// </summary>
        public int RecoveryDrain(RecoveryFence fence)
        {
            if (fence is null) throw new ArgumentNullException(nameof(fence));
            if (_running) throw new InvalidOperationException("RecoveryDrain 전에 Stop() 필수 — SingleReader 인계 위반 차단.");
            int n = 0;
            while (_reader.TryRead(out var p))
            {
                if (!fence(in p)) break;      // 오염 — 호출측 펜스가 이미 ForceFreeze. 미영속화·중단(잔여 보존).
                if (!Persist(in p)) break;    // 영속화 실패 — Persist 가 ForceFreeze. 중단.
                n++;
            }
            return n;
        }

        // 단일 페이로드 영속화 + ★연쇄 후속 연산(RecomputeAverage) 순차 보장. 실패 시 fail-closed.
        private bool Persist(in DbTransactionPayload p)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                string symbol = BridgeSerializer.FormatSymbol(p.SymbolCode);
                var t = new Transaction
                {
                    Symbol        = symbol,
                    Type          = (TransactionType)p.TypeByte,
                    Quantity      = QMath.ToDecimal(p.QtyRaw),
                    Price         = QMath.ToDecimal(p.PriceRaw),
                    Commission    = QMath.ToDecimal(p.CommissionRaw),
                    Tax           = QMath.ToDecimal(p.TaxRaw),
                    ExecutedAt    = SafeUtc(p.ExecutedAtTicks),
                    BrokerAccount = "mesh",
                    Memo          = "",
                };
                _db.AddTransaction(t);                          // ① 원장 기록(WAL·트랜잭션)
                PortfolioService.RecomputeAverage(_db, symbol); // ② ★연쇄 순차 — 장부/평단 디싱크 차단(HANDOVER §12)
                Interlocked.Increment(ref _persisted);
                UpdateLatencyEma(Stopwatch.GetTimestamp() - start);
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _writeFailures);
                Freeze("Ledger 영속화 실패(디스크 풀·DB 락·검증) — " + ex.Message);   // ★예외 삼킴 금지 → 에스컬레이션
                return false;
            }
        }

        private void UpdateLatencyEma(long sampleTicks)
        {
            // 단일 드레인 스레드 전용 — EMA(α=0.2). double bits 로 Interlocked 노출(관제 원자 독출).
            double prev = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _avgPersistTicksBits));
            double ema  = prev <= 0 ? sampleTicks : prev * 0.8 + sampleTicks * 0.2;
            Interlocked.Exchange(ref _avgPersistTicksBits, BitConverter.DoubleToInt64Bits(ema));
        }

        private static DateTime SafeUtc(long ticks)
            => ticks > 0 && ticks <= DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.UtcNow;

        private void Freeze(string reason)
        {
            try { _att.ForceFreeze(reason); } catch { /* 동결 실패해도 진행 오염 금지 */ }
        }

        public void Dispose() => Stop();
    }
}
