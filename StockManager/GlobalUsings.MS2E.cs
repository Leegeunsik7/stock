// v11.2.0 Stage 2 — 레이어 네임스페이스 전역 결속(per-file using 수술 회피).
//   Core/Infra 소스가 StockManager.Common → MS2E.Core / MS2E.Infrastructure 로 정렬되면서, 이 프로젝트 전 파일이
//   두 레이어 타입을 짧은 이름으로 참조하도록 전역 using 을 선언한다.
global using MS2E.Core;
global using MS2E.Infrastructure;
global using StockManager;
// 이동 파일(MS2E.*)이 '부모-워크'로 참조하던 형제 모듈 네임스페이스를 별칭으로 복원(using N; 은 중첩 NS 짧은이름을 안 줌).
//   compound 이름이라 MS2E.* 타입명과 충돌 위험 없음(Portfolio 등 단일어는 충돌 위험 → 별칭 대신 정규화).
global using CryptoWatcher = StockManager.CryptoWatcher;
global using RealtimeWatcher = StockManager.RealtimeWatcher;
global using PaperTrading = StockManager.PaperTrading;
global using SignalEngine = StockManager.SignalEngine;
global using CompositeAlerts = StockManager.CompositeAlerts;
global using AIAnalysis = StockManager.AIAnalysis;
global using NewsRadar = StockManager.NewsRadar;
global using TotalAsset = StockManager.TotalAsset;
