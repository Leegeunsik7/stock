// ============================================================================
// ForensicAuditViewPort — v12.12.0 [Step 6/7] 포렌식 감사 & 비행 기록 장치 (순수 UI 경계)
//
//   MmfFlightRecorder(v4.95.0·이미 실배선·100,000 슬롯 링 — 그러나 UI 는 최근 100건 고정 헥스덤프만 노출했음,
//   실측 확인)와 ZeroAllocForensicAuditStream(v12.2.0·엔진·테스트만 완성되고 DI 등록 자체가 없었음, 실측 확인)
//   을 대용량 Virtualize 뷰어에 결속한다. 원시 값(string/long/bool)만 다뤄 엔진 타입 참조 0(RCL 순수성) 유지.
// ============================================================================
using System.Collections.Generic;

namespace MS2E.UI.Portable.Ports.Shell
{
    /// <summary>블랙박스 레코드 1행(Virtualize 렌더용 값타입 뷰).</summary>
    public interface IForensicLogRowView
    {
        long Sequence { get; }
        long TimestampTicks { get; }
        string EventKind { get; }
        long Detail0 { get; }
        long Detail1 { get; }
    }

    public interface IForensicAuditViewPort
    {
        int RingCapacity { get; }
        long TotalRecorded { get; }
        bool RingSealed { get; }

        /// <summary>최근 최대 maxRows 건을 최신순으로 스냅샷(콜드 경로 — 수동 조회 전용, 핫패스 아님).
        /// Blazor &lt;Virtualize Items="..."&gt; 가 실제 DOM 렌더는 화면에 보이는 행만 수행(가상화).</summary>
        IReadOnlyList<IForensicLogRowView> SnapshotRecent(int maxRows);

        // ── ZeroAllocForensicAuditStream(SHA-256 해시 체인, Think Beyond B) ──
        bool ChainAvailable { get; }
        long ChainWriteHead { get; }
        /// <summary>체인 무결성 검증(PrevHash 연속성) — false 면 위변조 감지(적색 경고 대상).</summary>
        bool VerifyChainIntegrity();
    }
}
