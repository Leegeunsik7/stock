// ============================================================================
// CascadingStormFreezeGate — [카오스 스톰 · 방향 B] 연쇄 격발(캣타스트로픽 스톰) 비상 동결 게이트
//
//  서로 다른 인터락 비트가 짧은 창(기본 50ms) 안에 threshold(기본 3)개 이상 새로 격발하면 = 단일 고장이 아니라 시스템 전반 붕괴 징후.
//  감지 즉시(격발 스레드 · δ=0 · 1회):
//    ① 블랙박스(MMF 비행 기록계)에 StormFreeze 1건(격발 비트 합 · 마스크) 기록 후 Seal → 이후 기록은 드롭(DroppedWhileSealed).
//       ★'최초 원인 증거 보존': 스톰 여파의 대량 사건이 순환 링을 덮어 첫 격발 원인을 지우는 것을 막는다. 덤프는 QuiesceAfterSeal 후 스냅샷.
//    ② onStorm 콜백(호스트: attestation.ForceFreeze — 파이프라인 영구 락업 · 재개는 수동 정합).
//  ★지시서의 'MMF 공유 메모리를 OS 페이지 보호로 읽기 전용 물리 고정'은 하지 않는다(정직): 페이지를 PAGE_READONLY 로 바꾸면 그 뒤
//    기록하는 모든 스레드가 AccessViolation 으로 죽고, .NET 8 은 AV 를 catch 할 수 없다(프로세스 즉사 = 증거 덤프 · 동결 처리 자체 소실).
//    같은 목적(덮어쓰기 0)을 기록계 자체의 원자 봉인으로 달성한다.
//  기동 각인(결속 시점 이미 선 비트의 1회 raised 통지)으로 오격발하지 않도록 Arm() 이후의 전이만 센다(호스트: 집계어 Seal 성공 뒤 Arm).
//  δ=0: 비트별 마지막 격발 시각(64 long) · 창 내 개수 O(64) 스캔 · CAS 1회 트립 — 할당 · 락 0.
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Telemetry
{
    public sealed class CascadingStormFreezeGate : IInterlockTransitionSink
    {
        public const int DefaultThreshold = 3;
        public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(50);

        private readonly long[] _lastRaise = new long[64];
        private readonly int _threshold;
        private readonly long _windowTicks;
        private readonly Func<long> _now;
        private readonly MmfFlightRecorder? _blackbox;
        private readonly Action<string>? _onStorm;
        private int _armed, _tripped;
        private long _stormBits, _stormMask, _evaluations, _ignoredUnarmed;

        /// <param name="threshold">창 안 서로 다른 격발 비트 수 임계(≥ 2).</param>
        /// <param name="now">단조 시계(Stopwatch 틱 · 테스트 주입).</param>
        public CascadingStormFreezeGate(MmfFlightRecorder? blackbox = null, Action<string>? onStorm = null, int threshold = DefaultThreshold,
                                        TimeSpan? window = null, Func<long>? now = null)
        {
            if (threshold < 2 || threshold > 64) throw new ArgumentOutOfRangeException(nameof(threshold));
            var w = window ?? DefaultWindow;
            if (w <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
            _threshold = threshold;
            _windowTicks = (long)(w.TotalSeconds * Stopwatch.Frequency);
            _now = now ?? Stopwatch.GetTimestamp;
            _blackbox = blackbox; _onStorm = onStorm;
        }

        public bool Armed => Volatile.Read(ref _armed) == 1;
        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        /// <summary>트립 시점 창 안 격발 비트 합.</summary>
        public long StormBits => Interlocked.Read(ref _stormBits);
        public long StormMask => Interlocked.Read(ref _stormMask);
        public long Evaluations => Interlocked.Read(ref _evaluations);
        public long IgnoredUnarmed => Interlocked.Read(ref _ignoredUnarmed);

        /// <summary>감시 개시(호스트: 집계어 봉인 성공 뒤 1회). 이전 전이(기동 각인)는 세지 않는다.</summary>
        public void Arm() => Volatile.Write(ref _armed, 1);

        public void OnTransition(long raisedBits, long clearedBits, long maskAfter)
        {
            if (raisedBits == 0) return;
            if (!Armed) { Interlocked.Increment(ref _ignoredUnarmed); return; }
            Interlocked.Increment(ref _evaluations);
            long now = _now();
            if (now == 0) now = 1;
            for (int b = 0; b < 64; b++) if ((raisedBits & (1L << b)) != 0) Volatile.Write(ref _lastRaise[b], now);
            long inWindow = 0; int n = 0;
            for (int b = 0; b < 64; b++)
            {
                long t = Volatile.Read(ref _lastRaise[b]);
                if (t != 0 && now - t <= _windowTicks) { inWindow |= 1L << b; n++; }
            }
            if (n < _threshold || Interlocked.CompareExchange(ref _tripped, 1, 0) != 0) return;
            Interlocked.Exchange(ref _stormBits, inWindow);
            Interlocked.Exchange(ref _stormMask, maskAfter);
            try { _blackbox?.Record(FlightEventKind.StormFreeze, n, inWindow, maskAfter); } catch (Exception) { }
            try { _blackbox?.Seal(); } catch (Exception) { }                         // ★최초 원인 증거 보존(덮어쓰기 0)
            try { _onStorm?.Invoke("연쇄 격발 스톰 — 인터락 " + n + "개 동시 격발(창 내) · 블랙박스 봉인 + 비상 동결"); } catch (Exception) { }
        }
    }
}
