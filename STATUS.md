# Current Project Status

- **Active Milestone**: v12.18.23-DEV — 아레나 초현실 2단계(대기열 취소 이탈 · 가상 공매도)
- **Build Status**: 오중 빌드(Default · WebOnly · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0/0 · 전체 스위트 4,065/4,065 · infra-scan 0 · 래칫 ≤40
- **Updated At**: 2026-10-19

## Completed Deliverables
- 대기열 취소 이탈: C = max(0, ΔD − 체결) 비례 감축(내 앞 몫) · δ=0 · 미상 구간 재기준
- 가상 공매도: 숏 개시(담보 150% 잠금) · 숏커버 실현손익 · 0 교차 금지 · 롱/숏 동시 개시 차단 · 숏 시가평가 = 매도호가 · −3% 마진콜 강제 커버
- AI: 평균회귀 상단 숏/회귀 커버 · 포식자 휴먼 숏 손실 스퀴즈 · /arena [LONG]/[SHORT] 배지 · 양방향 핫키/래더 · Space 숏커버
- 신규 테스트 31건(QueueDepletionTests 19 · SyntheticShortingTests 12) · HANDOVER §91

## Next Milestone
- v12.18.23 GM 단독 출하(-NoPeer) 판단
- 대주 수수료 · 대차 가능 수량 모델(선택) · 실 키움 + KIS 동시 피드 보정
