// ============================================================================
// DeterministicWarmRestartEngine — v4.90.0: 결정론적 웜 스타트 복구 엔진
//
//  전역 동결(EmergencyFreeze) 해제 직후, 큐에 적체된 원장 백로그를 안전 소화하고 점화 잠금 FSM 을 단계적으로
//  안전 승격시킨다. 5상태 결정론 시퀀스(모호하면 통과 대신 재동결):
//
//    Idle ──BeginWarmRestart(서명)──▶ Validating ──서명 인가 + 동결 전제──▶
//    Draining(단일 리더 백로그 순차 드레인 + 동결창 가격정합성 펜스) ──▶
//    Escalating(SafeIgnitionSequencer: ProbeReady→1주 프로브→3σ+지연→MasterIgnited) ──▶ Stable
//        └─ 서명 미인가 / 펜스 위반 / 프로브 위반·타임아웃 / 예외 ─▶ Frozen (ForceFreeze 단일 소스 + 금고 재봉인)
//
//  ── 정직 회계(이미 존재하는 부품 재사용) ────────────────────────────────────────
//   · 점진 점화 승격(Locked→ProbeReady→1주 프로브→3σ·지연 검증→MasterIgnited·실패 시 ForceFreeze)은
//     SafeIgnitionSequencer.BeginIgnition 이 완비 — 재구현 없이 Escalating 단계에서 호출한다.
//   · 보수정렬(매수↑·매도↓) 명목 산출은 QLedgerGuard.TryClearNotional / QMath.Notional 이 δ=0 로 제공.
//   · 백로그 단일 리더 드레인은 LedgerProtectionMesh.RecoveryDrain(fence) 신설(mesh.Stop 후 리더 인계).
//
//  ── 타협 불가 불변식 ────────────────────────────────────────────────────────────
//   liveArmed:false : 프로브는 SafeIgnitionSequencer 의 격리 브로커로만(실 자본 위험 0). 복구 진행 중에는
//                     IsRecovering==true 로 라이브 라우팅 술어를 차단(주문 폭주 방지 '복구기 잠금 펜스').
//   fail-closed     : 서명/펜스/프로브/예외 어느 실패든 OrderAttestationService.ForceFreeze 단일 소스 + 금고
//                     재봉인(SecurityAssetVault.Seal)으로 수렴. 강등(IgnitionLock.Lock)은 시퀀서가 담당.
//   δ=0             : 가격정합성 펜스는 QFixed raw(10^8) 정수 산술만(Int128 중간). 복호화는 SecretLease(ArrayPool·와이프).
//   결정론          : 세션 판정·펜스는 벽시계/RNG 불사용. 실시간 호가는 주입 스냅샷(Func)으로 1회 정수화.
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>웜 스타트 복구 파이프라인 진행 상태. 부팅 기본 Idle. 실패 종단 = Frozen.</summary>
    public enum RecoveryState { Idle, Validating, Draining, Escalating, Stable, Frozen }

    /// <summary>한 번의 웜 스타트 시도 결과(불변 진단 스냅샷·UI/로그용).</summary>
    public sealed record WarmRestartResult(
        bool          Succeeded,
        RecoveryState FinalState,
        string        Reason,
        long          Drained,
        long          ProbeLatencyMs,
        double        ProbeZ);

    public sealed class DeterministicWarmRestartEngine : IDisposable
    {
        // ── 협력자(주입·공유 싱글톤) ─────────────────────────────────────────────
        private readonly OrderAttestationService _att;
        private readonly SecurityAssetVault      _vault;
        private readonly LedgerProtectionMesh    _mesh;
        private readonly SafeIgnitionSequencer   _sequencer;
        private readonly Func<string, bool>      _verifySignature;   // 프로덕션: ECDsa 검증 주입(기본은 비공백 정책)
        private readonly Func<long, long>        _livePriceRawFor;   // symbolCode → 실시간 호가 raw(10^8), 0=미상
        private readonly string                  _persistenceKeyName;

        // ── 상태·관제 카운터(락-프리) ────────────────────────────────────────────
        private volatile int _stateInt = (int)RecoveryState.Idle;
        private int  _inFlight;
        private long _drained, _fenceViolations, _recoveries, _freezes;
        private long _lastProbeLatencyMs, _lastProbeSigmaRaw;

        public DeterministicWarmRestartEngine(
            OrderAttestationService attestation,
            SecurityAssetVault      vault,
            LedgerProtectionMesh    mesh,
            SafeIgnitionSequencer   sequencer,
            Func<string, bool>?     verifySignature   = null,
            Func<long, long>?       livePriceRawFor   = null,
            string                  persistenceKeyName = "persistence.master")
        {
            _att                = attestation ?? throw new ArgumentNullException(nameof(attestation));
            _vault              = vault       ?? throw new ArgumentNullException(nameof(vault));
            _mesh               = mesh        ?? throw new ArgumentNullException(nameof(mesh));
            _sequencer          = sequencer   ?? throw new ArgumentNullException(nameof(sequencer));
            // 기본 서명 정책은 시스템 기존 ManualRelease 와 동일(비공백). 프로덕션은 ECDsa 검증기를 주입한다.
            _verifySignature    = verifySignature ?? (s => !string.IsNullOrWhiteSpace(s));
            _livePriceRawFor    = livePriceRawFor  ?? (_ => 0L);
            _persistenceKeyName = persistenceKeyName;
        }

        /// <summary>실시간 호가 괴리 오염 판정 배수(저장가가 시세의 이 배수 초과/미만이면 스케일 글리치로 간주·동결).
        /// 정상 시장 변동(상하한 ±30%·마감 후 갭 포함)은 통과하도록 넉넉히(기본 8×). 격자 정합성과 독립한 별개 축.</summary>
        public long GrossDeviationFactor { get; init; } = 8;

        // ── 관제 독출(락-프리) ─────────────────────────────────────────────────
        public RecoveryState State => (RecoveryState)_stateInt;
        /// <summary>★복구기 잠금 펜스 신호 — 검증/드레인/승격 진행 중이면 true. 라이브 라우팅 술어가 이 값을 AND-차단한다.</summary>
        public bool IsRecovering
        { get { var s = (RecoveryState)_stateInt; return s == RecoveryState.Validating || s == RecoveryState.Draining || s == RecoveryState.Escalating; } }
        public long   BacklogRemaining     => _mesh.QueueDepth;
        public long   Drained              => Interlocked.Read(ref _drained);
        public long   FenceViolations      => Interlocked.Read(ref _fenceViolations);
        public long   Recoveries           => Interlocked.Read(ref _recoveries);
        public long   Freezes              => Interlocked.Read(ref _freezes);
        public long   LastProbeLatencyMs   => Interlocked.Read(ref _lastProbeLatencyMs);
        public long   LastProbeSigmaRaw    => Interlocked.Read(ref _lastProbeSigmaRaw);
        public long   RecoveryStateCode    => _stateInt;

        // ── 진입점(단일 진행) ────────────────────────────────────────────────────
        /// <summary>
        /// 웜 스타트 복구 시퀀스(동기·결정론). UI 는 Trigger 로 백그라운드 실행. 재진입은 즉시 거부.
        /// 전제: 현재 EmergencyFreeze. 서명 인가 → mesh 리더 인계 → ManualRelease → 금고 키 로드 → 백로그 드레인
        /// (가격정합성 펜스) → 점화 승격 → Stable. 어느 실패든 ForceFreeze + 금고 재봉인 → Frozen.
        /// </summary>
        public WarmRestartResult BeginWarmRestart(string signature)
        {
            if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
                return new WarmRestartResult(false, State, "이미 웜 스타트 진행 중 — 재진입 거부", 0, 0, 0);

            SecretLease? lease = null;
            try
            {
                // ── Validating: 동결 전제 + 인간 서명 인가 ───────────────────────
                _stateInt = (int)RecoveryState.Validating;

                if (_att.Mode != GatingMode.EmergencyFreeze)
                {
                    _stateInt = (int)RecoveryState.Idle;   // 동결 아님 → 웜 스타트 불필요(상태 오염 없이 반환)
                    return new WarmRestartResult(false, RecoveryState.Idle, "동결 상태 아님 — 웜 스타트 불필요", 0, 0, 0);
                }
                if (!_verifySignature(signature ?? ""))
                {
                    _stateInt = (int)RecoveryState.Frozen;   // 서명 거부 — 동결 유지(해제하지 않음)
                    return new WarmRestartResult(false, RecoveryState.Frozen, "미인가·변조 서명 — 웜 스타트 거부(fail-closed)", 0, 0, 0);
                }

                // ── 단일 리더 인계: mesh 자체 DrainLoop 종료(펜스 우회 레이스 차단) ──
                //   ★반드시 ManualRelease '이전에' Stop — Mode=Normal 되는 즉시 mesh DrainLoop 가 펜스 없이 배수하는 레이스 봉쇄.
                _mesh.Stop();

                // ── 금고 자율 해제 → 영속화 파이프라인 복구 ──────────────────────
                _att.ManualRelease(signature!, "warm-restart");            // Mode → Normal(이 시점부터 금고 개방 가능)
                lease = TryRevealPersistenceKey();                          // requireArmed:false·ArrayPool δ=0·best-effort

                // ── Draining: 백로그 순차 드레인 + 동결창 가격정합성 펜스 ─────────
                _stateInt = (int)RecoveryState.Draining;
                int n = _mesh.RecoveryDrain(PriceCoherenceFence);
                Interlocked.Add(ref _drained, n);

                if (_att.Mode == GatingMode.EmergencyFreeze)   // 펜스 위반/영속화 실패 → 펜스가 이미 ForceFreeze(멱등 재수렴)
                    return FailClosed(lease, "동결창 가격정합성 펜스 위반 — 백로그 오염 감지, 재동결", n, 0, 0);

                // ── Escalating: 점진 점화 승격(격리 1주 프로브 → 3σ·지연 검증 → MasterIgnited) ──
                _stateInt = (int)RecoveryState.Escalating;
                var ign = _sequencer.BeginIgnition(signature!);
                Interlocked.Exchange(ref _lastProbeLatencyMs, ign.PipelineLatencyMs);
                Interlocked.Exchange(ref _lastProbeSigmaRaw, (long)(ign.ObservedZ * QMath.Scale));

                if (!ign.Succeeded)
                    // ★시퀀서 freeze 여부 무관 무조건 재동결: G0 전진거부(이중키 미충족=기본 자세 등)는 att 미동결이므로 필수.
                    return FailClosed(lease, "프로브 승격 실패 — fail-closed 재동결: " + ign.Reason,
                                      n, ign.PipelineLatencyMs, ign.ObservedZ);

                // ── Stable: mesh 오프로드 재개 + 리스 해제 ────────────────────────
                try { lease?.Dispose(); } catch { }
                _mesh.Start();
                _stateInt = (int)RecoveryState.Stable;
                Interlocked.Increment(ref _recoveries);
                return new WarmRestartResult(true, RecoveryState.Stable,
                    "웜 스타트 완료 — 백로그 소진·MasterIgnited·오프로드 재개", n, ign.PipelineLatencyMs, ign.ObservedZ);
            }
            catch (Exception ex)
            {
                // ★fail-closed 최후 벽: 어떤 예외도 ForceFreeze 단일 소스 + 금고 재봉인으로 수렴(재throw 금지).
                return FailClosed(lease, "웜 스타트 예외 — 재동결: " + ex.Message, 0, 0, 0);
            }
            finally { Volatile.Write(ref _inFlight, 0); }
        }

        /// <summary>UI 트리거 — 단일 백그라운드 스레드에서 복구 시퀀스 실행(드레인은 이 스레드가 mesh 단일 리더).</summary>
        public void Trigger(string signature)
        {
            var t = new Thread(() => { try { BeginWarmRestart(signature); } catch { /* fail-closed 는 내부 수렴 */ } })
            { IsBackground = true, Name = "WarmRestart", Priority = ThreadPriority.AboveNormal };
            t.Start();
        }

        // ── 동결창 가격정합성 펜스(δ=0·결정론) ────────────────────────────────────
        /// <summary>
        /// 백로그 항목의 저장 명목가치를 '현재 실시간 호가로 결정한 KRX 호가격자'에 보수 정렬한 명목과 대조한다.
        /// 명목 오차가 1raw 를 초과(=격자 미정렬=동결창 스케일/격자 오염)하면 즉시 ForceFreeze + false(드레인 중단).
        /// KRX 정규 체결가는 항상 격자 정렬이라 정상 항목은 오차 0(오탐 없음). 음수/0·오버플로도 fail-closed.
        /// </summary>
        private bool PriceCoherenceFence(in DbTransactionPayload p)
        {
            try
            {
                if (p.PriceRaw <= 0 || p.QtyRaw <= 0)
                {
                    _att.ForceFreeze($"동결창 오염: 음수/0 가격·수량 (code={p.SymbolCode})");
                    Interlocked.Increment(ref _fenceViolations);
                    return false;
                }

                var  side     = p.TypeByte == (byte)TransactionType.Sell ? OrderSide.Sell : OrderSide.Buy;
                // ★격자 밴드는 '체결 당시 저장가 자기 밴드'로 결정(실시간 호가로 잡지 않음). KRX tick 사슬{1,5,10,50,100,500,1000}은
                //   가분수라, 마감 후 시세가 다른 tick 밴드로 이동했을 때 실시간 밴드로 잡으면 정상 정렬 체결을 오탐 동결하거나(더 굵은
                //   밴드) 미정렬 오염을 미탐한다(더 미세한 밴드). 저장가 자기 밴드로 검사해야 정상=오차0·오염=미정렬 이 성립(적대검증 #3/#4).
                var  spec     = KrxTickLadder.SpecFor(QMath.ToDecimal(p.PriceRaw));     // 저장가 밴드 tick·1주 step

                // ★실시간 호가 대조(격자와 독립한 별개 정합성 축): 시세가 있으면 저장가가 시세와 '괴리 배수(GrossDeviationFactor)'를
                //   초과해 어긋난 오염(스케일/직렬화 글리치 — 통상 10×/100×/0.1×)을 잡는다. 정상 시장 변동(마감 후 갭·상하한 ±30% 포함)은
                //   넉넉한 배수로 통과(오탐 없음). δ=0 정수 비교(long 곱/나눗셈 — 가격 상한에서 오버플로 없음).
                long liveRaw  = _livePriceRawFor(p.SymbolCode);
                if (liveRaw > 0 && (p.PriceRaw > liveRaw * GrossDeviationFactor || p.PriceRaw < liveRaw / GrossDeviationFactor))
                {
                    _att.ForceFreeze($"동결창 시세 괴리 오염: 저장가 {p.PriceRaw}raw vs 실시간 {liveRaw}raw (×{GrossDeviationFactor} 초과, code={p.SymbolCode})");
                    Interlocked.Increment(ref _fenceViolations);
                    return false;
                }

                // 보수정렬(매수↑·매도↓) 후 명목 산출. 입력 오염/오버플로 시 내부에서 ForceFreeze+false.
                if (!QLedgerGuard.TryClearNotional(new QPrice(p.PriceRaw), new QQty(p.QtyRaw),
                        spec.Tick, spec.Step, side, _att, out var aligned, out _, out _))
                {
                    Interlocked.Increment(ref _fenceViolations);
                    return false;
                }

                long storedNotional = QMath.Notional(p.PriceRaw, p.QtyRaw, roundUp: side == OrderSide.Buy);
                long diff           = Math.Abs(aligned.Raw - storedNotional);
                if (diff > 1L)
                {
                    _att.ForceFreeze($"동결창 가격정합성 위반: 명목 오차 {diff}raw > 1raw (code={p.SymbolCode}, 호가격자 미정렬 오염)");
                    Interlocked.Increment(ref _fenceViolations);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                try { _att.ForceFreeze("가격정합성 펜스 예외 — fail-closed: " + ex.Message); } catch { }
                Interlocked.Increment(ref _fenceViolations);
                return false;
            }
        }

        // 금고 복호화 키 로드(best-effort). 로컬 SQLite 영속화는 복호화 불요이므로 키 미설정/봉인 시 null 로 계속.
        //   requireArmed:false — 복구용 키는 '주문 송출 트리거'가 아님(liveArmed:false 무관). SecretLease 는 using/Dispose 로 와이프.
        private SecretLease? TryRevealPersistenceKey()
        {
            try { return _vault.Reveal(_persistenceKeyName, requireArmed: false); }
            catch { return null; }
        }

        // ★실패 단일 수렴점 — att.ForceFreeze 단일 소스(멱등) + 금고 재봉인 + 리스 해제 + Frozen. throw 금지(fail-closed 마지막 벽).
        //   ★시퀀서의 freeze 여부에 '절대 의존하지 않고' 무조건 재동결한다: ManualRelease 로 해제된 Mode=Normal 을 fail-closed 로
        //   반드시 EmergencyFreeze 로 되돌린 뒤에만 Frozen(IsRecovering=false)으로 전환 — 비상 동결이 조용히 사라지는 누수 차단.
        //   (시퀀서 G0 전진거부: 이중키 미충족(기본 자세)·재진입·이미 MasterIgnited·서명거부 는 att 를 동결하지 않으므로 필수.)
        private WarmRestartResult FailClosed(SecretLease? lease, string reason, long drained, long lat, double z)
        {
            try { _att.ForceFreeze(reason); } catch { }   // ★단일 소스 재수렴(멱등 — 이미 동결이어도 안전)
            try { lease?.Dispose(); }        catch { }
            try { _vault.Seal(); }           catch { }
            _stateInt = (int)RecoveryState.Frozen;
            Interlocked.Increment(ref _freezes);
            return new WarmRestartResult(false, RecoveryState.Frozen, reason, drained, lat, z);
        }

        public void Dispose() { /* 소유 지속 스레드 없음 — Trigger 스레드는 IsBackground·단발 */ }
    }
}
