// ============================================================================
// ForensicAuditViewAdapter — v12.12.0 IForensicAuditViewPort 구현(Ports/Adapters 결속)
//
//   MmfFlightRecorder.SnapshotRecent(Span&lt;FlightRecordEvent&gt;)는 이미 δ=0(호출자 버퍼)이다. 이 어댑터에서
//   IForensicLogRowView[] 로 변환하는 단계만 콜드 경로 할당(사용자가 뷰어를 열 때 1회 — 핫패스 아님, 정직 명기).
//   ★direction_a 의 "Blazor Virtualize 에 0 Byte 매핑"은 Blazor 렌더 트리가 ref struct(Span)를 컴포넌트 파라미터로
//   받을 수 없어 문자 그대로는 불가능하다(정직 명기) — 달성 가능한 최선은: (1) 엔진 스냅샷 자체는 δ=0 유지,
//   (2) Virtualize 컴포넌트가 스크롤 시 '보이는 행만' DOM 생성(가상화 본연의 목적)으로 DOM 노드 폭발을 차단.
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.Infrastructure
{
    public sealed class ForensicAuditViewAdapter : MS2E.UI.Portable.Ports.Shell.IForensicAuditViewPort
    {
        private sealed record RowView(long Sequence, long TimestampTicks, string EventKind, long Detail0, long Detail1)
            : MS2E.UI.Portable.Ports.Shell.IForensicLogRowView;

        private readonly MmfFlightRecorder _recorder;
        private readonly ZeroAllocForensicAuditStream? _chain;

        public ForensicAuditViewAdapter(MmfFlightRecorder recorder, ZeroAllocForensicAuditStream? chain = null)
        {
            _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _chain = chain;
        }

        public int RingCapacity => _recorder.Capacity;
        public long TotalRecorded => _recorder.TotalRecorded;
        public bool RingSealed => _recorder.IsSealed;

        public IReadOnlyList<MS2E.UI.Portable.Ports.Shell.IForensicLogRowView> SnapshotRecent(int maxRows)
        {
            int cap = Math.Min(Math.Max(maxRows, 0), _recorder.Capacity);
            if (cap == 0) return Array.Empty<MS2E.UI.Portable.Ports.Shell.IForensicLogRowView>();

            var buf = new FlightRecordEvent[cap];   // ★콜드 경로 할당(뷰어 조회 1회) — 엔진 스냅샷 자체는 δ=0
            int n = _recorder.SnapshotRecent(buf);
            var rows = new List<MS2E.UI.Portable.Ports.Shell.IForensicLogRowView>(n);
            for (int i = 0; i < n; i++)
            {
                var e = buf[i];
                rows.Add(new RowView(e.Sequence, e.TimestampTicks, e.Event.ToString(), e.Arg0, e.Arg1));
            }
            return rows;
        }

        public bool ChainAvailable => _chain is not null;
        public long ChainWriteHead => _chain?.WriteHead ?? 0;
        public bool VerifyChainIntegrity() => _chain?.VerifyIntegrity() ?? true;   // 체인 미결속 시 '위변조 없음'을 주장 않고 중립(true=검사 대상 없음)
    }
}
