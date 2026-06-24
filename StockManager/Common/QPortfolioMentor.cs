// ============================================================================
// QPortfolioMentor — S28 Mission 1: 제로-마찰 대화형 온보딩 엔티티 수입 엔진
//
//  ★경계 규율(Gemini 룰1 "LLM에게 절대 수학을 시키지 말라"): LLM 레이어는 자유 대화·영수증 이미지에서
//  엔티티(Symbol/Amount/AvgPrice)를 '추출'하고 페르소나를 '서술'할 뿐이다. 추출 결과는 EntityDraft 라는
//  ACL 경계 DTO 로만 코어에 들어오며(텍스트·이미지 모달리티 무관·동일 경로), 여기서부터 모든 숫자는
//  결정론적 고정소수점 격자(PortfolioSlot)가 권위를 갖는다. LLM 이 수익률·평가액을 자체 계산하는 순간
//  금융 데이터 오염(Hallucination)이므로, 본 엔진은 LLM 산출 '숫자'를 신뢰하지 않고 '엔티티'만 정준화한다.
//
//  ── 정준화(Canonicalization) fail-closed ────────────────────────────────────
//  심볼 미해석·음수 수량·음수 단가는 슬롯을 만들지 않고 거부(모호함 앞 fail-closed) — 잘못된 격자를 절대
//  포트폴리오에 주입하지 않는다. 심볼만 확정되면 수량·단가는 '미상(Known 플래그 off)'으로 슬롯을 열고,
//  인입 펌프가 누락 필드를 자연스러운 유도 질문으로 받아낸다(제로-마찰).
//
//  ── 인입 펌프(흐름 제어) ─────────────────────────────────────────────────────
//  OnboardingPump 는 사전할당 슬롯 격자에 EntityDraft 를 upsert(동일 심볼은 누락 필드 병합)하고, 다음에
//  무엇을 물을지 결정론 우선순위(첫자산 → 수량 → 평단 → 확정)로 ElicitField 를 산출한다. LLM 은 이 결정을
//  '따뜻하게 표현'만 한다(공감형 톤은 프롬프트 상태 머신의 책임, 무엇을 물을지는 코어의 책임).
//
//  ── 컴플라이언스 락(Gemini 룰3 fail-closed 페르소나 가드) ────────────────────
//  유사투자자문·확정수익 보장 유도·탈옥 시도를 LLM 이 IntentClass 로 '분류'하면, ComplianceGate 가 즉시
//  sticky 락(한 번 잠기면 세션 내 해제 불가)으로 전이해 모호한 응답 없이 컴플라이언스 거부를 발화한다.
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>슬롯에서 '확정된' 필드 비트. 심볼만 확정·수량/평단 미상 상태를 표현(제로-마찰 점진 입력).</summary>
    [Flags]
    public enum SlotField : byte
    {
        None     = 0,
        Symbol   = 1,
        Quantity = 2,
        AvgPrice = 4,
        All      = Symbol | Quantity | AvgPrice,
    }

    /// <summary>정준화 거부 사유(fail-closed). None 이외는 슬롯을 만들지 않는다.</summary>
    public enum CanonReject : byte { None = 0, NoSymbol, UnresolvedSymbol, BadQuantity, BadPrice, Overflow, CapacityExceeded }

    /// <summary>온보딩 대화 상태 머신.</summary>
    public enum OnboardingState : byte { Greeting = 0, AwaitingFirstAsset, FillingFields, Confirming, Complete }

    /// <summary>펌프가 다음에 받아내야 할 필드(LLM 이 공감형으로 '표현'만 한다 — 무엇을 물을지는 코어 결정).</summary>
    public enum ElicitField : byte { None = 0, FirstAsset, Quantity, AvgPrice, Confirm }

    /// <summary>LLM 분류 의도(코어는 이 enum 만 신뢰 — LLM 이 자체 판단으로 거부/허용하지 않는다).</summary>
    public enum IntentClass : byte { Normal = 0, GuaranteedReturnSolicitation, UnlicensedAdviceRequest, Jailbreak }

    /// <summary>
    /// ACL 경계 DTO — LLM(텍스트/비전)이 추출한 '엔티티 초안'. 숫자는 decimal? 로, 미추출은 null 로 온다.
    /// 코어는 이 값을 '검증·정준화'할 뿐 신뢰하지 않는다. 이미지·텍스트 모달리티가 동일 타입으로 수렴.
    /// </summary>
    public readonly record struct EntityDraft(string? RawSymbol, decimal? Qty, decimal? AvgPrice, string? CcyHint);

    /// <summary>정준화된 보유 슬롯(불변 값 타입). 모든 숫자는 고정소수점. Known 으로 부분 입력을 표현.</summary>
    public readonly record struct PortfolioSlot(
        string Symbol, AssetClass Class, Currency Ccy, QQty Qty, QPrice AvgPrice, SlotField Known)
    {
        public bool HasQty      => (Known & SlotField.Quantity) != 0;
        public bool HasAvgPrice => (Known & SlotField.AvgPrice) != 0;
        public bool IsComplete  => (Known & SlotField.All) == SlotField.All;
    }

    /// <summary>심볼 해석기(ACL) — "삼성전자"/"005930"/"비트코인" → 정준 심볼·시장·통화. 라이브는 네이버 검색 어댑터.</summary>
    public interface ISymbolResolver
    {
        bool TryResolve(string raw, out string canonicalSymbol, out Market market, out Currency ccy);
    }

    /// <summary>
    /// Mission 1 — 정준화기. EntityDraft → PortfolioSlot. fail-closed: 심볼 미해석·음수 수량·음수 단가는 거부.
    /// 심볼만 확정되면 수량/단가 미상으로 슬롯을 연다(점진 입력). 순수 함수(네트워크 없음, 해석은 주입된 resolver).
    /// </summary>
    public static class OnboardingCanonicalizer
    {
        public static bool TryCanonicalize(in EntityDraft draft, ISymbolResolver resolver,
                                           out PortfolioSlot slot, out CanonReject reject)
        {
            slot = default; reject = CanonReject.None;
            if (resolver is null) throw new ArgumentNullException(nameof(resolver));

            string? raw = draft.RawSymbol;
            if (string.IsNullOrWhiteSpace(raw)) { reject = CanonReject.NoSymbol; return false; }
            if (!resolver.TryResolve(raw.Trim(), out string canon, out Market market, out Currency ccy))
            { reject = CanonReject.UnresolvedSymbol; return false; }   // 모호함 앞 fail-closed — 추측 금지

            var known = SlotField.Symbol;
            QQty qty = default; QPrice avg = default;
            try
            {
                if (draft.Qty is decimal q)
                {
                    if (q <= 0m) { reject = CanonReject.BadQuantity; return false; }       // 0/음수 수량은 정준 불가
                    qty = QQty.Of(q);                                                      // (long)(v*Scale) — 대형값은 OverflowException
                    // ★적대적 리뷰(major) — 0<q<1e-8 은 q>0 게이트를 통과하나 (long) 절사로 Raw=0 이 된다('소수 0.13→0'
                    //   계열). 절사로 0 이 된 수량은 '수량 0 오염 슬롯'이므로 fail-closed 거부(LLM 숫자 무검증 신뢰 금지).
                    if (qty.Raw <= 0) { reject = CanonReject.BadQuantity; return false; }
                    known |= SlotField.Quantity;
                }
                if (draft.AvgPrice is decimal p)
                {
                    if (p < 0m) { reject = CanonReject.BadPrice; return false; }            // 음수 단가 거부
                    avg = QPrice.Of(p);
                    if (p > 0m && avg.Raw <= 0) { reject = CanonReject.BadPrice; return false; }   // ★양수 단가가 0으로 절사 — 손실 거부(p==0 은 허용)
                    known |= SlotField.AvgPrice;
                }
            }
            catch (OverflowException)
            { reject = CanonReject.Overflow; return false; }   // ★적대적 리뷰(major) — 대형값은 fail-crash 아닌 fail-closed(거부)

            slot = new PortfolioSlot(canon, PortfolioBreakdown.Classify(market), ccy, qty, avg, known);
            return true;
        }
    }

    /// <summary>
    /// Mission 1 — 인입 펌프. 사전할당 슬롯 격자에 EntityDraft 를 upsert(동일 심볼 누락필드 병합)하고,
    /// 다음 유도 필드를 결정론 우선순위로 산출한다. 거부 시 격자·상태 불변(fail-closed, 부분 오염 차단).
    /// </summary>
    public sealed class OnboardingPump
    {
        private readonly PortfolioSlot[] _slots;
        private readonly ISymbolResolver _resolver;
        private int _count;

        public OnboardingPump(ISymbolResolver resolver, int capacity = 64)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _slots = new PortfolioSlot[capacity];
            State   = OnboardingState.Greeting;
        }

        public OnboardingState State { get; private set; }
        public int Count => _count;
        public ReadOnlySpan<PortfolioSlot> Slots => _slots.AsSpan(0, _count);

        /// <summary>드래프트 1건을 정준화해 upsert. 성공 시 상태 진행, 실패 시 reject 사유 반환·격자 불변.</summary>
        public bool Ingest(in EntityDraft draft, out CanonReject reject)
        {
            if (!OnboardingCanonicalizer.TryCanonicalize(in draft, _resolver, out var fresh, out reject))
                return false;   // fail-closed — 부분 입력도 격자에 반영하지 않음

            int idx = IndexOf(fresh.Symbol);
            if (idx >= 0) _slots[idx] = Merge(_slots[idx], fresh);   // 동일 심볼: 누락필드만 채움(덮어쓰기 아님)
            else
            {
                if (_count >= _slots.Length) { reject = CanonReject.CapacityExceeded; return false; }   // 용량 초과(거부·동결 아님, 사유 명시)
                _slots[_count++] = fresh;
            }

            // 상태 진행: 첫 자산이 들어오면 FillingFields, 모두 완성되면 Confirming.
            if (State is OnboardingState.Greeting or OnboardingState.AwaitingFirstAsset)
                State = OnboardingState.FillingFields;
            if (State == OnboardingState.FillingFields && AllComplete())
                State = OnboardingState.Confirming;
            return true;
        }

        /// <summary>다음에 받아낼 필드(공감형 유도의 '대상'). 코어가 무엇을 물을지 결정하고 LLM 이 표현한다.</summary>
        public ElicitField NextElicitation()
        {
            if (_count == 0) return ElicitField.FirstAsset;            // "가장 애착 가는 자산 하나만 먼저"
            for (int i = 0; i < _count; i++)
                if (!_slots[i].HasQty) return ElicitField.Quantity;    // 수량 누락 우선
            for (int i = 0; i < _count; i++)
                if (!_slots[i].HasAvgPrice) return ElicitField.AvgPrice; // 그다음 평단(모르면 현재가 대체 안내)
            return ElicitField.Confirm;                                // 전부 채워짐 → 확정 유도
        }

        /// <summary>사용자가 확정. 모든 슬롯 완성일 때만 Complete 로 전이(미완성이면 Confirming 유지, fail-closed).</summary>
        public bool TryComplete()
        {
            if (_count == 0 || !AllComplete()) return false;
            State = OnboardingState.Complete;
            return true;
        }

        private bool AllComplete()
        {
            for (int i = 0; i < _count; i++) if (!_slots[i].IsComplete) return false;
            return _count > 0;
        }

        private int IndexOf(string symbol)
        {
            for (int i = 0; i < _count; i++)
                if (string.Equals(_slots[i].Symbol, symbol, StringComparison.Ordinal)) return i;
            return -1;
        }

        // 누락 필드만 신규 값으로 채움(이미 확정된 값은 보존 — 사용자 정정은 별도 경로). 분류/통화는 신규 기준 갱신.
        private static PortfolioSlot Merge(PortfolioSlot cur, PortfolioSlot fresh)
        {
            QQty   qty  = cur.HasQty      ? cur.Qty      : fresh.Qty;
            QPrice avg  = cur.HasAvgPrice ? cur.AvgPrice : fresh.AvgPrice;
            SlotField known = cur.Known | fresh.Known;
            return new PortfolioSlot(cur.Symbol, fresh.Class, fresh.Ccy, qty, avg, known);
        }
    }

    /// <summary>
    /// Mission(Gemini 룰3) — fail-closed 페르소나 가드. LLM 이 IntentClass 로 분류하면 코어가 락을 결정한다.
    /// 비정상 의도(확정수익 보장·유사투자자문·탈옥)는 즉시 sticky ComplianceLocked 로 전이 — 한 번 잠기면
    /// 세션 내 어떤 입력(Normal 포함)도 거부(fail-closed). 모호한 회피 응답을 내지 않고 명시적 컴플라이언스 거부.
    /// </summary>
    public sealed class ComplianceGate
    {
        private int _locked;   // 0=Engaged, 1=ComplianceLocked (Interlocked sticky)

        public PersonaState State => Volatile.Read(ref _locked) == 0 ? PersonaState.Engaged : PersonaState.ComplianceLocked;

        /// <summary>의도를 받아 응답 허용 여부 결정. 비정상 의도 또는 이미 잠긴 세션이면 false(거부)+락 고정.</summary>
        public bool Admit(IntentClass intent)
        {
            if (Volatile.Read(ref _locked) != 0) return false;   // 이미 잠김 — sticky fail-closed
            if (intent != IntentClass.Normal)
            {
                Interlocked.Exchange(ref _locked, 1);            // 비정상 의도 → 즉시 잠금(되돌릴 수 없음)
                return false;
            }
            return true;
        }

        /// <summary>잠금 시 발화할 고정 컴플라이언스 고지(LLM 이 윤색하지 않는 결정론 텍스트).</summary>
        public static string RefusalNotice =>
            "본 서비스는 확정 수익을 보장하지 않으며 개별 투자자문을 제공하지 않습니다. " +
            "안전을 위해 해당 요청은 처리하지 않습니다(컴플라이언스 보호).";
    }

    public enum PersonaState : byte { Engaged = 0, ComplianceLocked }
}
