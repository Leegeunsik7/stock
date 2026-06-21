# S23-b — 라이브 캔들 피드 결속 아키텍처 청사진

> 상태: **안전 코어 구현·CI 검증 완료 (v4.20.0)** · 라이브 활성화(provider 어댑터+드라이버)는 문서화된 후속 단계
> 합성 `BootstrapCandles` 빗장을 풀고 실 캔들 윈도우를 온실에 주기적으로 재주입하기 위한
> 3대 비대칭 안전 규칙의 구현 사양. 모든 시간 의존 로직은 주입 클록으로 결정론·CI 검증된다.

---

## 정직 경계 (먼저)

- 이 마일스톤은 **결속 기계(machinery)를 구현·검증**한다. 실제 라이브 `ICandleSource`(네트워크) 바인딩과
  주기 드라이버는 **의도적으로 호스트에 미배선**이다 — 이 환경에 라이브 provider 가 없고, 검증 불가한
  네트워크 I/O 를 자율 루프 안전 경로에 박는 것은 "테스트된 기계만 안전 경로에 둔다"는 규율 위반이다.
- 따라서 코어(게이트웨이 As-Of, 온실 게이트, LiveCandleFeed)는 **fake 소스·주입 클록으로 전수 CI 검증**되고,
  라이브 활성화는 아래 §활성화 레시피로 문서화한다.
- 세 규칙 모두 **옵트인(기본 OFF = 현행 동작)** — `liveArmed:false` 불변, 실거래 0%.

---

## 규칙 1 — isDirty 숏서킷 + 제로 할당 fetch

**문제**: 정적 스냅샷 위 결정론 Evolve 를 매 사이클 재실행하면 모집단·백테스트 할당이 무한 churn 된다.
재-Prime 할당은 그에 비하면 noise 다 — GC 위험의 주범은 **사이클 내부 재진화**다.

**구현**:
- `GreenhouseService.SkipUnchanged`(opt-in) — 데이터 세대(`_fedGen`) 불변 시 `RunCycleOnce` 숏서킷.
  `Prime` 이 세대를 증가시키고, 진화는 세대가 바뀐 사이클에서만 일어난다(할당·CPU 0).
- `LiveCandleFeed` — 사전 할당된 **단일 재사용 버퍼**에 소스가 필드를 덮어쓴다(fetch 측 할당 0).
  시그니처로 변화를 비트 판정 → 불변이면 `Prime` 자체를 숏서킷.

**더블버퍼는 불필요**(측정 없는 풀링 거부): `GreenhouseService.Prime` 이 `ToArray` 로 **동기 방어 복사**를
하므로, 재사용 버퍼는 `Prime` 반환 시점에 안전하게 재충전된다. 유일한 할당은 '데이터 변화 시 1회'의 스냅샷.
온실 스레드는 자신의 복사본만 읽고 버퍼를 만지지 않는다 → 토른 리드 없음.

## 규칙 2 — 힉컵 흡수 vs 즉시 사살 (캔들은 강등 불가)

**반증**: 매크로 폴백(Calm 강등 후 배양 유지)을 캔들에 적용하면 치명적이다.
- 매크로 = *필터*(거부 게이트 변조). 유실 → 비변조로 배양 지속(2차 효과, 하류가 포착).
- 캔들 = *피사체*(진화의 지반). staleness → 사라진 시장에 적합된 챔피언 제안(1차 오염).

**구현**:
- `GreenhouseService.StalenessWindow`(opt-in) — 마지막 `Prime`(`_lastFreshTicks`) 이후 한도 초과 시
  `RunCycleOnce` 즉시 fail-closed(배양 정지, `StalePauses++`). 1ms 초과해도 사살.
- 힉컵 흡수: 윈도우 *내*의 일시 누락은 마지막 유효 스냅샷으로 배양 지속(가용성 보존).
- `LiveCandleFeed.Validate` — 단조 타임스탬프·유한/양수 OHLC·High≥Close≥Low 검증.
  손상/부족 윈도우는 **거부(드롭)**하고 마지막 유효 유지 → 온실 StalenessWindow 가 시간 경과 후 fail-closed.
- 관제탑 트립와이어: `Greenhouse.FeedStale` → `[⚠️ 데이터 피드 단절(fail-closed)]`.

**책임 분리**: LiveCandleFeed 는 '나쁜 데이터를 막고', GreenhouseService 는 '낡으면 멈춘다'.
**ProxyLost ≠ 캔들 소스 손실** — 캔들이 틱 프록시와 다른 소스면 ProxyLost 가 커버 못 한다(독립 신선도 신호 필수).

## 규칙 3 — As-Of 락아웃 + 승인→활성 레이스 방어

**문제**: 4겹 방화벽은 *실행*을 막지만, 유령 데이터로 만든 제안을 인간이 신선해 보이는 배지를 보고
승인하는 *인지적 결함*은 막지 못한다.

**구현** (`PromotionGateway`):
- `PromotionCandidate.AsOfTicks` — 챔피언이 진화한 캔들 데이터의 마켓 시각. `Submit(spec, f, asOfTicks)`.
- `ProposalFreshnessWindow`(opt-in) — 제안 신선도 한도. null·asOf=0 이면 게이팅 면제(현행).
- **원자 검증 수식**(`_lock` 임계구역 내, TOCTOU 갭 없음):
  ```
  now() - AsOfTicks > ProposalFreshnessWindow  ⟹  Rejected
  ```
- `Approve`: 서명이 올발라도 stale 이면 `Rejected` 로 튕김(조기 거부).
- `Activate`: **비가역 핫스왑 직전 최종 재검문** — 승인→활성 갭에서 마켓 타임이 흘러 열화됐으면
  swap 대신 `Rejected`(레이스 방어의 정답 위치).

## 하드 게이트 작성 규율 (적용됨)

- `_lastFreshTicks`/`_fedGen` 등은 `long` 평이 필드 + `Volatile.Read/Write`·`Interlocked` (volatile long 불법).
- `Now()` 람다는 `static () => DateTime.UtcNow` (캡처 0). 옵트인 props 는 `init` (불변).
- 이벤트 핸들러(라이브 드라이버)는 동기·명명 메서드·`+=`/`-=` 대칭·예외 자체흡수 — async void 금지.
- record positional 기본값(`AsOfTicks = 0`)로 기존 호출부 무파손.

---

## 활성화 레시피 (라이브 provider 결속 시 — 후속 단계)

1. `ICandleSource` 어댑터 구현: `IQuoteProvider.GetHistoryAsync` → `FillLatest(buffer)` 로 필드 덮어쓰기.
   (주의: IQuoteProvider 는 `List<Candle>` 반환 → 어댑터에서 1회 복사 발생. 진정한 제로 할당은
   `CandleFrame`(struct)/RingBuffer 집계 소스에서만 성립.)
2. 온실 게이트 켜기: `new GreenhouseService(...) { SkipUnchanged = true, StalenessWindow = T_stale, Clock = ... }`.
3. 게이트웨이 신선도: `new PromotionGateway(...) { ProposalFreshnessWindow = T_proposal }`.
4. 드라이버: 단일 백그라운드 타이머가 `LiveCandleFeed.Refresh()` 를 주기 호출(명명 핸들러·예외흡수).
5. `Greenhouse.LiveFeed` config 게이트(기본 false)로 운영자 제어.
6. 검증: 라이브 왕복은 CI 미검증 영역 — 사용자 측 실환경 검증 필요(파일 헤더 명시 원칙).

> 핵심 단일 결론: 이 마일스톤의 진짜 위험은 GC 가 아니라 **"신선해 보이는 stale 챔피언"**이다.
> As-Of 박제 + 승인·활성 시점 신선도 검문이 그 인지적 공격면을 닫는다.
