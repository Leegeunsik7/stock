// KiwoomProxy 는 호스트 어셈블리(MS2E.StockSuite)를 참조하지 않는 격리 x86 COM 프록시다.
// 링크된 BridgeProtocol.cs 만이 MS2E.Infrastructure 네임스페이스를 이 컴파일에 제공한다(공유 와이어 타입).
// ★MS2E.Core 는 이 프로젝트에 존재하지 않으므로 전역 using 금지(v11.2.0 Stage-2 잔재 제거).
global using MS2E.Infrastructure;
