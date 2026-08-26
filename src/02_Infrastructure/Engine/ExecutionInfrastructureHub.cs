// ============================================================================
// ExecutionInfrastructureHub — Blazor DI ↔ WinForms Launcher 싱글톤 브릿지
//
//  문제: BlazorHomeForm.ConfigureServices()에서 생성된 ShadowCutoverGate / OrderAttestationService
//  인스턴스는 Blazor DI 컨테이너에 등록되지만, WinForms Launcher에서는 접근 불가 — "절연 공백".
//  결과: Launcher.PaperQuick()의 BrokerHub.Create(_db)가 게이트 없이 호출되어 ShadowClearingBroker
//  미배선 — QFixedPrimary 권위 전환이 실주문 경로에 전혀 반영되지 않음.
//
//  해결: 정적 브릿지(최초 1회 쓰기·다회 읽기)
//   · BlazorHomeForm이 인스턴스 생성 직후 Register(gate, att) 호출 → 공유 슬롯에 저장.
//   · Launcher(및 여타 WinForms 모듈)이 BrokerHub.Create 직전에 TryGet() 호출 → 슬롯 추출.
//   · TryGet()이 null을 반환하면 BrokerHub.Create(db) 기본(게이트 없음)으로 폴백 — 후방호환 유지.
//
//  스레드 안전:
//   · 쓰기: Interlocked.CompareExchange — 최초 등록만 반영(first-write wins).
//   · 읽기: Volatile.Read — CS0420 없는 단일 volatile 읽기 의미론.
//   · CS0420 회피: 필드를 volatile 선언하지 않고 Volatile.Read / Interlocked 로 직접 관리.
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    public static class ExecutionInfrastructureHub
    {
        private sealed class InfraState
        {
            public readonly ShadowCutoverGate       Gate;
            public readonly OrderAttestationService  Att;
            public InfraState(ShadowCutoverGate gate, OrderAttestationService att)
            {
                Gate = gate;
                Att  = att;
            }
        }

        // CS0420 회피: volatile 선언 없이 Interlocked/Volatile.Read 로 관리.
        private static InfraState? _state;

        /// <summary>
        /// BlazorHomeForm이 호출 — DI 컨테이너 구성 후 gate/att 를 공유 슬롯에 등록.
        /// 최초 등록만 반영(first-write wins). 이미 등록됐으면 무시.
        /// </summary>
        public static void Register(ShadowCutoverGate gate, OrderAttestationService attestation)
        {
            if (gate        is null) throw new ArgumentNullException(nameof(gate));
            if (attestation is null) throw new ArgumentNullException(nameof(attestation));
            Interlocked.CompareExchange(ref _state, new InfraState(gate, attestation), null);
        }

        /// <summary>
        /// Launcher / WinForms 모듈이 BrokerHub.Create 직전에 호출.
        /// 등록되지 않았으면 (null, null) — 호출부는 그대로 BrokerHub.Create(_db) 기본 경로로 폴백.
        /// </summary>
        public static (ShadowCutoverGate? gate, OrderAttestationService? attestation) TryGet()
        {
            var s = Volatile.Read(ref _state);
            return s is null ? (null, null) : (s.Gate, s.Att);
        }
    }
}
