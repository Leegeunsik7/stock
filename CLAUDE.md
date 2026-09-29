# MS2E Stock Suite (Paper-Trading Terminal & High-Frequency Engine)

## 1. liveArmed:false Fail-Closed
- 실주문 경로 기본 차단. ValveWired∧LiveArmingWired 이중 키 + 카나리(<0.1%) 미결속 시 무장 불가.
- 지연>50ms / HTTP 429 / 슬리피지 예외 시 0ms 즉시 Disarm(Sticky Lockout). 재무장은 물리 이중키 재확인 필수.
- Disarm 즉시 PoisonedSecretBuffer로 자격증명 ZeroMemory 소거 — 재참조 시 ObjectDisposedException.
- .NET 8 In-Process Native COM AccessViolationException try-catch 절대 금지. 64비트 StockManager ↔ 32비트 x86 KiwoomProxy는 NamedPipe IPC로 Out-of-Process 분리, ProxyLivenessProbe 하트비트로만 크래시 방어.

## 2. API 커넥터 가드레일 (Kiwoom / Toss / KIS / LS)
- Kiwoom OpenAPI 호출 제한: 조회 TR 초당 5회 / 1분당 100회 초과 금지. 주문 TR도 동일 레이트리밋 대상 — 초과 시 서버 측 강제 연결 해제 위험.
- CrossBrokerAdaptiveRateCoalescer(Drop-Oldest 링버퍼 + Seqlock)로 초과 요청은 폐기. 큐잉·자동 재시도 금지(주문 중복 실행 위험 — Fail-Closed 원칙 위반).
- 실계좌 연결은 App Key/Secret(OAuth2) 방식 — 로그인 ID/PW 아님. 스텁 트랜스포트(미배선 어댑터) 상태에서 SyncNow는 항상 fail-closed 반환 — 가짜 잔고/체결 데이터 날조 금지, UI는 미연결 상태를 정직하게 렌더.
- LockFreeBrokerSwitchboard(단일 CAS)로 <10ms 페일오버, 락 없이 브로커 전환. 미분류(unclassified) COM HRESULT/응답은 전부 fail-closed로 분류.

## 3. Hot-Path Zero-Alloc (δ=0B) & BCL 우선
- 대상: 틱수집 / 드레인 / 킬스위치 / 페일오버 / 텔레메트리. Release 빌드에서 힙 할당 0B 검증 필수(Debug 결과 무효).
- 허용 패턴: Span<T> 직접 투사(ZeroAllocRowStreamMapper) · Pinned Object Heap(NativeBacktestRingCache) · Seqlock/CAS(락 없는 단일 라이터).
- 금지: 핫패스 내 LINQ, 박싱, 클로저 캡처, 문자열 보간.
- BCL 우선: 신규 NuGet 의존성 도입 전 System.* BCL(System.Text.Json, System.Buffers, System.Threading.Channels 등)로 해결 가능한지 우선 검토. 핫패스 JSON 파싱은 Utf8JsonReader 타입드 게터만 사용(예외는 JsonException만 캐치) — Newtonsoft.Json 등 서드파티 직렬화 라이브러리 도입 금지.

## 4. 그 외 핵심 규칙
- Pure Shell & Ports/Adapters: MS2E.UI.Portable은 구상 엔진 참조 0건. 통신은 Ports/ 인터페이스+어댑터로만. RatchetCeiling(<=40) 단조 감축.
- 4중 빌드 0W/0E: Default/WebOnly/PrimesBench/x86 KiwoomProxy 전부 TreatWarningsAsErrors=true.
- Zero Truncation: 소스 출력 시 생략(`// ...`) 금지. 공유 저장소 타 세션 미커밋 파일 오염 금지(선별 스테이징).
- UI/Razor: 상승 적색(#E53935)/하강 청색(#1E88E5) · `@Member한글`은 `@(Member)한글`로 이스케이프.

## 5. Commands (Repo Root 기준)
```
dotnet build StockManager.sln -c Release -m:1                             # Standard Release Build
dotnet build StockManager\StockManager.csproj -c Release -p:WebOnly=true  # Alt Build (Native Security 제외)
dotnet test StockManager.Tests\StockManager.Tests.csproj -c Release -m:1  # Unit Tests (xUnit)
dotnet run --project StockManager\StockManager.csproj                     # Desktop UI (WinForms + Blazor)
dotnet run --project StockManager\StockManager.csproj -- --headless       # Headless Engine Mode
./infra-scan.ps1                                                          # Infra Coupling Ratchet Check
```
Single Test: `dotnet test StockManager.Tests\StockManager.Tests.csproj --filter "FullyQualifiedName~ClassName.MethodName"`

## 6. STATUS.md 동기화
코드 작성 + Release 빌드/테스트 0W/0E 검증 완료 직후, 리포 루트 `STATUS.md`를 덮어쓰기(누적 금지, 20줄 이내, 코드/로그 원문 금지): Active Milestone / Build Status / Updated At / Completed Deliverables / Next Milestone.
