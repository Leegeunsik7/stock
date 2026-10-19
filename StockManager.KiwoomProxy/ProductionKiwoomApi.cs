// ============================================================================
// ProductionKiwoomApi — 과제 2 §2.1: 실 키움 OpenAPI+ COM(axKHOpenAPI) ↔ IKiwoomApi 결속
//
//  ★★ SDK 게이트: 본 파일의 COM 어댑터는 기본적으로 KIWOOM_COM 조건부 컴파일로 '전량 제외'된다. 따라서 표준 0/0
//     빌드(키움 SDK 미설치 환경·CI)에는 영향이 0 이다. axKHOpenAPI COM 타입은 이 저장소에
//     없으며(키움증권 OpenAPI+ 설치본에만 존재), 아래 코드는 '운영자가 실 배선 시' 활성화한다.
//
//  ── 활성화 절차(운영자) ─────────────────────────────────────────────────────
//   1) 키움 OpenAPI+ 설치 + OCX(KHOPENAPI.OCX) regsvr32 등록(32비트).
//   2) StockManager.KiwoomProxy.csproj 에 COM 참조 추가: "KHOpenAPI Control"(AxKHOpenAPILib +
//      Interop.KHOpenAPILib). x86 타깃은 이미 강제됨(격리 프로세스).
//   3) 빌드 상수 정의: <DefineConstants>KIWOOM_COM</DefineConstants> (또는 -p:DefineConstants=KIWOOM_COM).
//   4) Program.cs 에서 NullKiwoomApi 대신 new ProductionKiwoomApi(config) 주입.
//
//  ── 설계(격리·δ=0·fail-closed 계승) ─────────────────────────────────────────
//   · axKHOpenAPI 는 ActiveX 컨트롤 → STA 스레드 + 메시지 펌프 필요. 숨김 Form(_host) 위에 얹고
//     Application.Run 으로 펌프를 돌린다(전용 STA 스레드).
//   · [키움 COM · 추천 A] 이벤트 처리 로직(FID 사상 · 주문번호 양방향 사상 · 보류 체결 통보 · 서버 거부 분류 · 송신 거부)은
//     전부 공용 KiwoomComEventCore(src/02_Infrastructure/Engine/KiwoomComBridgeCore.cs · COM 무의존 · 테스트 검증)로 이관.
//     이 파일의 조건부 블록은 STA 호스팅 · AxKHOpenAPI → IKiwoomOcx 위임 · 이벤트 구독 · STA 펌프 Beat 타이머만 남는다.
//   · 계좌번호·화면번호는 배포별 값 → KiwoomComConfig 로 주입(코드에 박제하지 않음·No Guessing).
// ============================================================================

namespace StockManager.KiwoomProxy
{
    /// <summary>키움 배포별 구성(계좌·화면번호·구독 FID). 실 값은 운영자 주입 — 코드 박제 금지.</summary>
    public sealed class KiwoomComConfig
    {
        /// <summary>실 계좌번호(예: "8012345611"). 미설정 시 SendOrder 는 거부(fail-closed).</summary>
        public string AccountNo { get; init; } = "";
        /// <summary>실시간 시세 구독 화면번호(예: "5000").</summary>
        public string RealScreenNo { get; init; } = "5000";
        /// <summary>주문 화면번호(예: "5001").</summary>
        public string OrderScreenNo { get; init; } = "5001";
        /// <summary>[L2] 체결(10~13 · 11=전일대비 → 기준가) + 10호가 가격·잔량(41~80 → 주식호가잔량).</summary>
        public string RealFidList { get; init; } = KiwoomRealFids.DefaultRegistrationList();
    }
}

#if KIWOOM_COM
namespace StockManager.KiwoomProxy
{
    // ★using 은 네임스페이스 블록 내부 최상단(KiwoomComConfig 블록이 파일 앞에 있으므로 파일-레벨 불가).
    using System;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Windows.Forms;
    using AxKHOpenAPILib;   // COM Interop — 키움 OpenAPI+ 설치·참조 시에만 존재(SDK 게이트)

    /// <summary>
    /// 실 키움 OpenAPI+ COM 어댑터 — [키움 COM · 추천 A] 얇은 위임만 남김.
    ///   COM 이벤트 → KiwoomComEventCore(공용 · COM 무의존 · 테스트 검증) → 와이어 페이로드 → IKiwoomApi 이벤트.
    ///   이 #if 블록의 책임은 ① STA 스레드 + ActiveX 호스팅 ② AxKHOpenAPI → IKiwoomOcx 위임 ③ 이벤트 구독 ④ STA 펌프 Beat 타이머뿐.
    ///   ★SDK 미설치 환경에선 컴파일되지 않는다 — 로직은 전부 코어로 옮겨 이 블록의 미검증 표면을 최소화했다.
    /// </summary>
    public sealed class ProductionKiwoomApi : IKiwoomApi, IDisposable
    {
        private readonly KiwoomComConfig _cfg;
        private readonly StaPumpWatchdog _pump = new(stallMillis: 100);
        private Thread?      _staThread;
        private Form?        _host;
        private AxKHOpenAPI? _ax;
        private System.Windows.Forms.Timer? _beat;
        private KiwoomRateTelemetryWriter? _rateTelemetry;
        private KiwoomComEventCore? _core;
        private volatile bool _ready;

        public event EventHandler<TickEventArgs>?    TickReceived;
        public event EventHandler<FillEventArgs>?    FillReceived;
        // [키움 COM · 실결함] 계좌 TR(opw00001/opw00018) 은 미결선이라 AccountReceived 는 발생하지 않는다 — 수정 전 이 블록은
        //   TreatWarningsAsErrors 하에서 CS0067(미사용 이벤트)로 KIWOOM_COM 빌드 자체가 실패했다(인터롭 형상 쉼 컴파일로 실측).
#pragma warning disable CS0067
        public event EventHandler<AccountEventArgs>? AccountReceived;
#pragma warning restore CS0067
        public event EventHandler<OrderBookEventArgs>? OrderBookReceived;
        public event EventHandler<ReferencePriceEventArgs>? ReferencePriceReceived;
        public event EventHandler<ServerErrorEventArgs>? ServerErrorReceived;
        public event EventHandler<AccountCashEventArgs>? AccountCashReceived;
        public event EventHandler<PositionEventArgs>? PositionReceived;
        public int ApiKind => ProxyApiKind.ProductionCom;
        public event EventHandler<OpenOrderEventArgs>? OpenOrderReceived;
        public event EventHandler<OrderMappedEventArgs>? OrderMapped;

        /// <summary>[방향 A] STA 펌프 정상(최초 Beat 전 = 정상 간주 · 동결 sticky).</summary>
        public bool PumpHealthy => !_pump.IsStalled();

        public ProductionKiwoomApi(KiwoomComConfig config)
            => _cfg = config ?? throw new ArgumentNullException(nameof(config));

        /// <summary>AxKHOpenAPI → IKiwoomOcx 위임(시그니처 동일).</summary>
        private sealed class AxOcx : IKiwoomOcx
        {
            private readonly AxKHOpenAPI _ax;
            public AxOcx(AxKHOpenAPI ax) => _ax = ax;
            public string GetCommRealData(string realKey, int fid) => _ax.GetCommRealData(realKey, fid);
            public string GetChejanData(int fid) => _ax.GetChejanData(fid);
            public string GetCommData(string trCode, string recordName, int index, string itemName) => _ax.GetCommData(trCode, recordName, index, itemName);
            public int SendOrder(string rqName, string screenNo, string accountNo, int orderType, string code, int qty, int price, string hoga, string orgOrderNo)
                => _ax.SendOrder(rqName, screenNo, accountNo, orderType, code, qty, price, hoga, orgOrderNo);
            public void SetInputValue(string id, string value) => _ax.SetInputValue(id, value);
            public int CommRqData(string rqName, string trCode, int prevNext, string screenNo) => _ax.CommRqData(rqName, trCode, prevNext, screenNo);
            public int GetRepeatCnt(string trCode, string recordName) => _ax.GetRepeatCnt(trCode, recordName);
        }

        // ── STA 부팅: 숨김 Form + ActiveX + 메시지 펌프 ─────────────────────────
        public void Connect()
        {
            if (_staThread is not null) return;
            var started = new ManualResetEventSlim(false);
            _staThread = new Thread(() =>
            {
                try
                {
                    _host = new Form { WindowState = FormWindowState.Minimized, ShowInTaskbar = false };
                    _ax   = new AxKHOpenAPI();
                    ((System.ComponentModel.ISupportInitialize)_ax).BeginInit();
                    _host.Controls.Add(_ax);
                    ((System.ComponentModel.ISupportInitialize)_ax).EndInit();
                }
                catch (Exception)
                {
                    // [키움 실배선 · 방향 B] OCX 미등록/32비트 불일치/ActiveX 호스팅 실패 — 수정 전: STA 스레드 미처리 예외 = 프록시 즉사(원인 불명 하트비트 단절).
                    //   이제 중대 서버 오류(ComCreateFailed)로 호스트에 통지(KiwoomServerError Disarm) 후 STA 종료 — 이후 주문은 코어 부재로 거부 통보.
                    try { ServerErrorReceived?.Invoke(this, new ServerErrorEventArgs { Code = KiwoomServerErrorCodes.ComCreateFailed }); } catch { }
                    started.Set();
                    return;
                }

                _core = new KiwoomComEventCore(new AxOcx(_ax),
                    onTick: t => TickReceived?.Invoke(this, new TickEventArgs { SymbolCode = t.SymbolCode, Price = t.Price, Volume = t.Volume, ChangeRate = t.ChangeRate }),
                    onFill: f =>
                    {
                        var st = BridgeFillStatusCodec.Decode(in f, out long rem);
                        FillReceived?.Invoke(this, new FillEventArgs { OrderId = f.OrderId, SymbolCode = f.SymbolCode, FillPrice = f.FillPrice,
                                                                       FillQty = f.FillQty, Side = f.Side, Status = st, RemainingQty = rem });
                    },
                    onBook: b => OrderBookReceived?.Invoke(this, new OrderBookEventArgs { Book = b }),
                    onBase: rp => ReferencePriceReceived?.Invoke(this, new ReferencePriceEventArgs { SymbolCode = rp.SymbolCode, BasePrice = rp.BasePrice }),
                    onServerError: code => ServerErrorReceived?.Invoke(this, new ServerErrorEventArgs { Code = code }),
                    onCash: c => AccountCashReceived?.Invoke(this, new AccountCashEventArgs { Cash = c }),
                    onPosition: row => PositionReceived?.Invoke(this, new PositionEventArgs { Row = row }),
                    onOpenOrder: row => OpenOrderReceived?.Invoke(this, new OpenOrderEventArgs { Row = row }),
                    onMapped: m => OrderMapped?.Invoke(this, new OrderMappedEventArgs { Map = m }));
                // [v12.18.21-DEV] 키움 TR 한도 게이트(주문 · 조회 각 임의 1초 ≤ 5 · 60초 ≤ 100) + −308/−200 지수 백오프 · 연속 3회 sticky 래치.
                _core.AttachRateGate(KiwoomComEventCore.CreateKiwoomLimiter(), KiwoomComEventCore.CreateKiwoomLimiter(), new BrokerBackoffController());
                // [v12.18.22-DEV] 게이트 관제 → 호스트 MMF(관측 전용 · 생성 실패 = 관제 없이 게이트만 동작 · 호스트는 '미수신' 정직 렌더).
                try { _rateTelemetry = new KiwoomRateTelemetryWriter(); _core.AttachRateTelemetry(_rateTelemetry); } catch (Exception) { _rateTelemetry = null; }

                _ax.OnEventConnect      += (_, e) => _ready = _core.OnEventConnect(e.nErrCode);   // 0 = 로그인 성공 · [방향 B] 실패 = 서버 오류 통지(호스트 Disarm)
                _ax.OnReceiveRealData   += (_, e) => _core.OnRealData(e.sRealType, e.sRealKey);
                _ax.OnReceiveChejanData += (_, e) => _core.OnChejan(e.sGubun);
                _ax.OnReceiveTrData     += (_, e) => _core.OnTrData(e.sRQName, e.sTrCode);
                _ax.OnReceiveMsg        += (_, e) => _core.OnReceiveMsg(e.sRQName, e.sMsg);   // [키움 COM] 서버 거부 메시지

                // [방향 A] STA 펌프 Beat(50ms) — 펌프가 얼면 Beat 가 멈추고 프록시 Ping 응답이 억제된다. 보류 체결 통보 만료도 여기서.
                _beat = new System.Windows.Forms.Timer { Interval = 50 };
                _beat.Tick += (_, _) => { _pump.Beat(); _core.FlushExpired(); _core.PublishRateTelemetry(); };
                _beat.Start();

                _host.HandleCreated += (_, _) => { _ax.CommConnect(); started.Set(); };
                Application.Run(_host);   // STA 메시지 펌프(ActiveX 필수)
            })
            { IsBackground = true, Name = "KiwoomComSTA" };
            _staThread.SetApartmentState(ApartmentState.STA);
            _staThread.Start();
            started.Wait(TimeSpan.FromSeconds(10));
        }

        // ── 명령(Terminal → COM) ───────────────────────────────────────────────
        public void SendOrder(long symbolCode, int side, double price, long qty, long orderId)
        {
            var core = _core;
            if (core is null || !_ready)
            {
                // 미연결/미로그인 — 송신 불가를 거부로 즉시 통지(호스트 슬롯 0ms 반납)
                FillReceived?.Invoke(this, new FillEventArgs { OrderId = orderId, SymbolCode = symbolCode, Side = side > 0 ? 1 : -1,
                                                               Status = BridgeFillStatus.Rejected, RemainingQty = 0 });
                return;
            }
            core.SendOrder(orderId, _cfg.OrderScreenNo, _cfg.AccountNo, symbolCode, side, price, qty);   // 계좌 미설정 · rc≠0 → 코어가 Rejected 통지
        }

        // [정정/취소 · 실결함] 수정 전 빈 스텁 — 호스트 취소 요청이 조용히 소실됐다. 이제 코어가 원주문번호·최신 미체결로 키움 3/4(취소)·5/6(정정) 송신.
        public void CancelOrder(long orderId) => _core?.CancelOrder(orderId);

        public void ModifyOrder(long orderId, double price, long qty) => _core?.ModifyOrder(orderId, (long)price, qty);

        public void SubscribeTick(long symbolCode)
        {
            if (_ax is null) return;
            _ax.SetRealReg(_cfg.RealScreenNo, symbolCode.ToString("D6"), _cfg.RealFidList, "1");
        }

        public void UnsubscribeTick(long symbolCode)
            => _ax?.SetRealRemove(_cfg.RealScreenNo, symbolCode.ToString("D6"));

        // [계좌] OPW00018(보유) · OPW00001(예수금) — 응답은 OnReceiveTrData → 코어 → AccountCash/Position 이벤트
        public void QueryAccount() => _core?.QueryAccount(_cfg.AccountNo, _cfg.OrderScreenNo);

        // [미체결 대사] OPT10075 · 사상 재등록 · 키움 번호 직접 취소(유령 주문)
        public void QueryOpenOrders() => _core?.QueryOpenOrders(_cfg.AccountNo, _cfg.OrderScreenNo);
        public void AdoptMapping(long clientId, long venueOrderNo, long symbolCode, int side, long remaining)
            => _core?.AdoptMapping(clientId, venueOrderNo, symbolCode, side, remaining);
        public void CancelByVenueNo(long venueOrderNo, long symbolCode, int side, long qty)
            => _core?.CancelByVenueNo(venueOrderNo, symbolCode, side, qty);

        // ── 드라이버 해제(Phase 95-A §1) — COM RCW 결정적 해제 ──────────────────────────────
        public void Dispose()
        {
            var ax = _ax; var host = _host; var beat = _beat; var rate = _rateTelemetry;
            try
            {
                host?.BeginInvoke(new Action(() =>
                {
                    try { beat?.Stop(); beat?.Dispose(); } catch { }
                    try { ax?.Dispose(); } catch { }                                            // (1) AxHost → OCX 언로드
                    if (ax is not null && Marshal.IsComObject(ax))                               // (2) RCW refcount 0 까지 강제 해제
                        try { while (Marshal.FinalReleaseComObject(ax) > 0) { } } catch { }
                    try { host!.Dispose(); } catch { }                                          // (3) 숨김 Form 해제
                    try { rate?.Dispose(); } catch { }                                          // (4) 관제 맵 해제(호스트 판독 = 노후 전이)
                }));
            }
            catch { }
            _staThread = null; _ax = null; _host = null; _beat = null; _core = null; _rateTelemetry = null;
        }
    }
}
#endif

namespace StockManager.KiwoomProxy
{
    /// <summary>
    /// [키움 실배선] 프록시 API 선택(진입점 Program 이 호출). KIWOOM_COM 빌드 = ProductionKiwoomApi(계좌·화면번호는 환경변수 주입 — 코드 박제 금지) ·
    /// 비 COM 빌드 = NullKiwoomApi(ProxyHello 로 Simulation 선언 → 호스트 실 송출 불인정). requireProduction 인데 비 COM 빌드 = null(진입점 즉시 종료).
    /// ★이 팩토리는 쉼(KiwoomComShim · KIWOOM_COM)이 COM 분기를 컴파일 수검한다 — 진입점에 #if 를 두면 어느 빌드도 그 분기를 검증하지 않는다.
    /// </summary>
    public static class KiwoomApiFactory
    {
        public const string EnvAccount = "MS2E_KIWOOM_ACCOUNT", EnvRealScreen = "MS2E_KIWOOM_REAL_SCREEN", EnvOrderScreen = "MS2E_KIWOOM_ORDER_SCREEN";

        public static IKiwoomApi? Create(bool requireProduction, System.Func<string, string?> env, out string reason)
        {
#if KIWOOM_COM
            _ = requireProduction;
            reason = "ProductionCom(axKHOpenAPI)";
            return new ProductionKiwoomApi(new KiwoomComConfig
            {
                AccountNo = env(EnvAccount) ?? "",
                RealScreenNo = env(EnvRealScreen) ?? "5000",
                OrderScreenNo = env(EnvOrderScreen) ?? "5001",
            });
#else
            _ = env;
            if (requireProduction) { reason = "실 COM 미포함 빌드(KIWOOM_COM 미정의) — 실 API 요구 거부"; return null; }
            reason = "Simulation(NullKiwoomApi) — 호스트가 실 송출 대상으로 인정하지 않음";
            return new NullKiwoomApi();
#endif
        }
    }
}
