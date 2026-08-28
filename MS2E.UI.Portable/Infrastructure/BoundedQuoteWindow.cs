// ============================================================================
// BoundedQuoteWindow — Phase 94-E Think Beyond Gemini
//   ★대용량 틱(수백만 개) 시세를 UI 로 가져올 때의 두 파멸적 맹점을 영구 격리:
//     ① Port 경계를 넘는 대용량 Array 할당(GC LOH 타격) — UI 는 절대 백만 원소 배열을 수신하지 않는다.
//     ② 화면 밖(Off-Screen) 데이터 렌더링에 의한 UI 스레드 패닉 — UI 작업집합을 항상 ≤2N 로 상한.
//
//   ── 제미나이 A(MMF 브릿지) + B(청크 스트리밍)를 융합·초월 ──
//     A(MMF): 네이티브 파일 매핑의 복잡·플랫폼 결합 없이, 순수 관리형 '경계 고정 링'으로 LOH 자체를 회피.
//     B(청크 스트리밍): 단순 tail 청킹이 아니라, 스트리밍 중 '전 구간을 보존'하는 제자리 데시메이션(2배 조대화)로
//       수백만 틱을 무손실 span 이 아닌 '경계 안에서 전 기간을 대표'하는 ≤2N OHLC 버킷으로 압축.
//
//   ── ★독창적 초월점(제미나이 미제시) ──
//     · 스트리밍 중 무할당 제자리 병합(Compact): 버퍼가 2N 도달 시 인접쌍을 1 OHLC 버킷으로 병합(High=max·Low=min·
//       Close=last·Vol=sum) → N 으로 반감·해상도 절반·전 구간 유지. 총 이력이 얼마든 작업집합은 [N,2N] 에 고정.
//     · Push 는 amortized O(1)·힙 0(고정 버퍼·제자리). UI 는 Snapshot(Span) 로 ≤2N 만 읽어 가상 스크롤에 직결.
//
//   불변: δ=0(Push/Snapshot 힙 0·고정 버퍼·in QuotePoint) · 결정론(병합 규칙 고정·stride 2의 거듭제곱) ·
//        fail-closed(용량 상한·범위 밖 무시) · liveArmed:false(순수 UI 데이터 창 — 발주/소켓 무접촉).
//   ※ 스레드 모델: 단일 생산자(스트리밍 어댑터) Push ↔ 단일 소비자(렌더) Snapshot. 동시 Push 는 계약상 금지.
// ============================================================================
using System;

namespace MS2E.UI.Portable.Infrastructure
{
    /// <summary>OHLC 시세 1점(값타입·비관리형 — stackalloc/Span 안전).</summary>
    public readonly record struct QuotePoint(long TimeTicks, double Open, double High, double Low, double Close, double Volume);

    public sealed class BoundedQuoteWindow
    {
        private readonly QuotePoint[] _buf;   // 물리 용량 = 2N (병합 헤드룸)
        private readonly int _cap;            // 목표 렌더 용량 N
        private int _count;                   // 현재 보관 점수(≤ 2N)
        private long _totalIngested;          // 누적 유입(데시메이션 압축비 산출)
        private int _stride;                  // 현재 버킷당 원본 점수(1=무손실, 2^k=데시메이션)

        public BoundedQuoteWindow(int capacity = 2048)
        {
            if (capacity < 8) capacity = 8;
            if (capacity > (1 << 20)) capacity = 1 << 20;   // 상한(고정 용량)
            _cap = capacity;
            _buf = new QuotePoint[capacity * 2];
            _stride = 1;
        }

        public int  Capacity      => _cap;
        public int  Count         => _count;
        public long TotalIngested => _totalIngested;
        /// <summary>현재 데시메이션 배율(1=무손실). 총 이력이 2N 초과 시 2의 거듭제곱으로 증가.</summary>
        public int  Stride        => _stride;

        // ── 핫패스: 스트리밍 어댑터가 청크의 각 점을 밀어넣음(δ=0·amortized O(1)) ──
        /// <summary>한 점 유입. 버퍼가 2N 도달 시 제자리 병합으로 N 확보(전 구간 보존). 힙 0.</summary>
        public void Push(in QuotePoint p)
        {
            if (_count >= _buf.Length) Compact();
            _buf[_count++] = p;
            _totalIngested++;
        }

        /// <summary>인접 2점을 1 OHLC 버킷으로 제자리 병합 → 점수 반감·해상도 절반·전 구간 유지(무할당).</summary>
        private void Compact()
        {
            int n = _count, w = 0;
            for (int i = 0; i + 1 < n; i += 2) _buf[w++] = Merge(in _buf[i], in _buf[i + 1]);
            if ((n & 1) == 1) _buf[w++] = _buf[n - 1];   // 홀수 잔여 1점 보존
            _count = w;
            if (_stride < (1 << 30)) _stride <<= 1;      // ★포화: 지속 유입(31회+ 압축)에도 int 오버플로→0 방지

        }

        private static QuotePoint Merge(in QuotePoint a, in QuotePoint b) => new(
            a.TimeTicks,                       // 버킷 시작 시각(구간 좌표 보존)
            a.Open,                            // 시가 = 앞점 시가
            a.High >= b.High ? a.High : b.High,
            a.Low  <= b.Low  ? a.Low  : b.Low,
            b.Close,                           // 종가 = 뒷점 종가
            a.Volume + b.Volume);

        // ── 소비자: 렌더가 경계 안의 전 구간(≤2N)을 스팬으로 읽음(δ=0) ──
        /// <summary>현재 창을 시간순(오래된→최신)으로 dest 에 복사. 반환=기입 개수(min(Count, dest.Length)).</summary>
        public int Snapshot(Span<QuotePoint> dest)
        {
            int n = _count < dest.Length ? _count : dest.Length;
            for (int i = 0; i < n; i++) dest[i] = _buf[i];
            return n;
        }

        /// <summary>재사용 초기화(새 종목 로딩 시).</summary>
        public void Reset()
        {
            _count = 0;
            _totalIngested = 0;
            _stride = 1;
        }
    }
}
