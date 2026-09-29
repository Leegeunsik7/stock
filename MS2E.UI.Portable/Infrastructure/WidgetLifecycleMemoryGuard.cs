// ============================================================================
// WidgetLifecycleMemoryGuard — v12.8.0 Think Beyond Gemini Idea B: Dashboard Widget Hot-Reload & Memory Isolation Guard
//
//   위젯의 동적 추가/드래그 재배치/제거(핫스왑) 시 폐기된 컴포넌트 인스턴스가 실제로 GC 회수되는지 검증하는
//   진단 가드. ★바이트-델타 추정이 아니라 텍스트북 표준 기법인 WeakReference 기반 판정을 쓴다 — 컴포넌트
//   폐기(Dispose) 직후 Track() 으로 약한 참조만 보유하고, 호출측이 GC 사이클을 유도한 뒤 VerifyCollected() 로
//   그 인스턴스가 여전히 살아있는지(=무언가 계속 루팅 중=누수 의심) 확인한다.
//
//   ★설계 결정(정직 명기): 가드 자신은 GC.Collect() 를 호출하지 않는다 — 라이브러리가 스스로 풀 GC 를 강제하는
//   것은 프로덕션 렌더 경로에 부적절한 부수효과이므로, 언제 강제 수집할지는 항상 호출측(테스트·진단 도구)의
//   책임으로 남긴다(liveArmed:false — 관측 전용, 개입 0).
//
//   불변: 결정론(관측만) · liveArmed:false(순수 진단 — 발주/무장 경로 무접촉) · Track/VerifyCollected 는 저빈도
//        진단 호출(위젯 추가/제거는 사용자 조작 단위 — 렌더 핫패스 아님, δ=0 요구 대상 아님).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.UI.Portable.Infrastructure
{
    /// <summary>위젯 인스턴스 폐기 후 실제 GC 회수 여부를 WeakReference 로 검증하는 진단 가드.</summary>
    public sealed class WidgetLifecycleMemoryGuard
    {
        private long _tracked;
        private long _verified;
        private long _leaksDetected;

        /// <summary>폐기(Dispose) 직후 위젯 인스턴스를 약한 참조로 등록(강한 참조 보유 금지 — 그 자체가 누수가 되지
        /// 않도록). 반환된 핸들을 보관했다가 GC 사이클 이후 VerifyCollected 에 전달한다.</summary>
        public WeakReference Track(object disposedWidgetInstance)
        {
            if (disposedWidgetInstance is null) throw new ArgumentNullException(nameof(disposedWidgetInstance));
            Interlocked.Increment(ref _tracked);
            return new WeakReference(disposedWidgetInstance);
        }

        /// <summary>
        /// GC 사이클 이후 호출 — 인스턴스가 여전히 살아있으면(IsAlive) 누수 의심으로 판정하고 sticky 카운터 증가,
        /// false(수거 안 됨) 반환. 이미 회수됐으면 true.
        /// </summary>
        public bool VerifyCollected(WeakReference handle)
        {
            if (handle is null) throw new ArgumentNullException(nameof(handle));
            Interlocked.Increment(ref _verified);
            if (handle.IsAlive)
            {
                Interlocked.Increment(ref _leaksDetected);
                return false;
            }
            return true;
        }

        public long Tracked => Interlocked.Read(ref _tracked);
        public long Verified => Interlocked.Read(ref _verified);
        public long LeaksDetected => Interlocked.Read(ref _leaksDetected);

        public void Reset()
        {
            Interlocked.Exchange(ref _tracked, 0);
            Interlocked.Exchange(ref _verified, 0);
            Interlocked.Exchange(ref _leaksDetected, 0);
        }
    }
}
