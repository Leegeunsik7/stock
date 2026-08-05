// ============================================================================
// UniversalUiPortRegistry — v10.1.0 Phase 94-A: 범용 UI Port 레지스트리 (Gemini 보강 과제)
//
//   UI 이관으로 신설된 모든 Port 어댑터를 타입 키로 등록/주입하는 UI 서비스 레지스트리. 핫패스 조회(Get/TryGet)는
//   무할당(δ=0)·O(1). ★계약: 등록은 startup(콜드·단일 라이터), 이후 Seal() 로 봉인하면 읽기 전용 → 락 없는 δ=0
//   읽기가 안전. 미등록 Port 조회는 fail-closed(null/false·throw 아님).
//
//   ★불변: δ=0(Get/TryGet/Contains 핫패스 힙 0 — typeof(T) 캐시 토큰·Dictionary ref 조회 무할당·참조 캐스트 무할당) ·
//     결정론 · liveArmed:false(순수 UI 서비스 조회 — IBroker·소켓·실 발주 유발 0).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;

namespace MS2E.UI.Portable.Infrastructure
{
    /// <summary>δ=0 O(1) UI Port 레지스트리. 타입 키 등록/주입. Seal 후 읽기 전용(락프리 δ=0 읽기).</summary>
    public sealed class UniversalUiPortRegistry
    {
        private readonly Dictionary<Type, object> _ports = new();
        private long _sealed;
        private int _count;

        public int Count => Volatile.Read(ref _count);
        public bool IsSealed => Volatile.Read(ref _sealed) == 1L;

        /// <summary>Port 등록(콜드·startup). Seal 후 호출 → 예외(fail-closed·읽기 전용 계약 사수). 같은 타입 재등록 시 교체.</summary>
        public void Register<T>(T port) where T : class
        {
            ArgumentNullException.ThrowIfNull(port);
            if (Volatile.Read(ref _sealed) == 1L)
                throw new InvalidOperationException($"UniversalUiPortRegistry 는 봉인됨 — {typeof(T).Name} 등록 거부(읽기 전용).");
            if (!_ports.ContainsKey(typeof(T))) Interlocked.Increment(ref _count);
            _ports[typeof(T)] = port;
        }

        /// <summary>봉인 — 이후 등록 차단(읽기 전용). 락프리 δ=0 읽기 안전 보장. 멱등.</summary>
        public void Seal() => Interlocked.Exchange(ref _sealed, 1L);

        /// <summary>★O(1) δ=0 조회. 미등록 → null(fail-closed).</summary>
        public T? Get<T>() where T : class
            => _ports.TryGetValue(typeof(T), out var v) ? (T)v : null;

        /// <summary>★O(1) δ=0 조회(try). 미등록 → false(fail-closed).</summary>
        public bool TryGet<T>(out T port) where T : class
        {
            if (_ports.TryGetValue(typeof(T), out var v)) { port = (T)v; return true; }
            port = null!;
            return false;
        }

        /// <summary>등록 여부(δ=0).</summary>
        public bool Contains<T>() where T : class => _ports.ContainsKey(typeof(T));
    }
}
