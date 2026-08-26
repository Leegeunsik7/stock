// ============================================================================
// SystemVersionProvider — v4.71.0: 런타임 무할당 버전 공급자
//
// 부팅 시 CLR 정적 이니셜라이저(단일 실행 보장)가 Assembly 메타데이터를 1회 추출·캐싱.
// 이후 모든 읽기는 이미 인턴된 문자열 참조만 반환 → 힙 할당 0(δ=0).
//
// 사용처:
//   NavMenu.razor     — 사이드바 "MS2E · vX.Y.Z" 배지
//   DashboardHome     — (필요 시 @SystemVersionProvider.Version)
//   진단 페이지 어디서나  — 환경 정보 표시
//
// 왜 reflection을 컴포넌트 렌더링 안에서 쓰면 안 되는가:
//   Blazor 컴포넌트는 StateHasChanged() 마다 렌더트리를 재방문하므로
//   Assembly.GetExecutingAssembly() per-frame → 매 프레임 힙 압박 발생.
//   정적 readonly 필드는 CLR에 의해 process-lifetime 동안 단 1회만 초기화.
// ============================================================================
using System.Reflection;

namespace MS2E.Infrastructure
{
    public static class SystemVersionProvider
    {
        public static readonly string Version =
            "v" + (typeof(SystemVersionProvider).Assembly
                       .GetName().Version?.ToString(3) ?? "0.0.0");
    }
}
