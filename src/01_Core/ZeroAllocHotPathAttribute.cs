// ============================================================================
// ZeroAllocHotPathAttribute — [Phase 1] Think Beyond 방향 B: Zero-Alloc Hot-Path Static Allocator Guard 의 계약 표지
//
//   이 어트리뷰트가 붙은 메서드는 "정상 경로에서 관리 힙 할당 0"을 약속한다. 약속의 검증은 두 겹이다:
//     ① 정적(IL) — StockManager.Tests 의 ZeroAllocIlInspector 가 표지된 모든 메서드의 IL 을 해독해 box / newarr /
//        참조형 newobj(클로저 디스플레이 클래스·델리게이트 포함) / 문자열 생성 API / LINQ 호출 지점을 찾아내면 테스트 실패.
//        (예외 객체 생성은 throw 전용 실패 경로라 허용.)
//     ② 동적 — GC.GetAllocatedBytesForCurrentThread 실측(Release). IL 검사는 "직접 본문"만 보므로 전이 호출의 할당은 ②가 잡는다.
//   ★정직 한계: Roslyn 분석기가 아니라 테스트 시점 검사다(빌드 자체를 막지 않음 — 전역 테스트 게이트로 차단).
//   런타임 동작 영향 0(메타데이터 표지일 뿐).
// ============================================================================
using System;

namespace MS2E.Core
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class ZeroAllocHotPathAttribute : Attribute
    {
    }
}
