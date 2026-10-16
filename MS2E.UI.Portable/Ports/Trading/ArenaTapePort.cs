// ============================================================================
// ArenaTapePort — [v12.18.13-DEV] 아레나 바이너리 틱 테이프(.tape) 녹화/목록 포트(순수 UI 경계 · 엔진 타입 참조 0)
//
//   정규장 라이브 호가 · 체결을 MMF 고정 레코드 테이프로 녹화(ON/OFF)하고, 리플레이가 불러올 테이프 목록을 제공한다.
//   ★녹화는 수신 피드의 복사본 기록일 뿐 — 주문 · 무장 · 브로커 경로가 없다.
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>테이프 1개(경로 · 표시명 · 레코드 수 · 바이트 · 생성 UTC). Records &lt; 0 = 헤더 손상(불러오기 불가).</summary>
    public readonly record struct ArenaTapeInfo(string Path, string Name, long Records, long Bytes, DateTime CreatedUtc);

    public interface IArenaTapePort
    {
        bool Recording { get; }
        /// <summary>현재 녹화 레코드 수 · 용량 초과 유실 수(녹화 중이 아니면 마지막 녹화 값).</summary>
        long RecordedCount { get; }
        long RecordDropped { get; }
        string? RecordingFile { get; }
        /// <summary>새 .tape 파일로 녹화 시작(이미 녹화 중 = false · 파일 생성 실패 = false).</summary>
        bool StartRecording();
        /// <summary>녹화 종료 — 진행 중 기록 완료 대기 후 헤더 확정 · 파일 크기 절단.</summary>
        void StopRecording();
        /// <summary>테이프 디렉터리의 .tape 목록(최신순 · 녹화 중 파일 제외).</summary>
        IReadOnlyList<ArenaTapeInfo> ListTapes();
    }
}
