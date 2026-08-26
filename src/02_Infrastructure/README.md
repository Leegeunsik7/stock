# 02_Infrastructure — 인프라 어댑터

Core Port 의 **구현체**. 외부 세계(브로커 Open API·DB·소켓·하드웨어)와 접촉. **Core(+동일 레이어)만 참조.**

- `Adapters/Brokers/` — `TossPortfolioSyncAdapter`, `OpenApiPortfolioAdapter`(KIS), `UniversalBrokerGatewayMesh`, `UniversalOrderExecutionAdapter` …
- `Hardware/` — AVX-512 SIMD, Affinity, P-Core 바인딩, Thermal/NUMA
- `Security/` — `SecurityAssetVault`(DPAPI), `QuantumVaultSentinel`, `SignoffValidator`, `AntiHooking`, `HoneyKeyDecoyMesh`, `VerifiedSecureZeroizer`
- `Telemetry/` — `RingTelemetry`, `MmfFlightRecorder`, `AuditTrail`, `CrashResilientDumper`
- `Persistence/` · `Networking/` — LocalDatabase, WebSocket/소켓/LAN/IPC

**★불변**: liveArmed:false(실 발주는 `GenesisLiveIgnitionGateway` 이중 물리 키 인터락 경유), δ=0 핫패스, fail-closed.
**현재 물리 위치**: `StockManager/Common/`(약 250+ 파일).
