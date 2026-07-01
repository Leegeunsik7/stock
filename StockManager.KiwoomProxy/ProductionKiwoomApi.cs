// ============================================================================
// ProductionKiwoomApi — 과제 2 §2.1: 실 키움 OpenAPI+ COM(axKHOpenAPI) ↔ IKiwoomApi 결속
//
//  ★★ SDK 게이트: 본 파일은 기본적으로 `#if KIWOOM_COM` 로 '전량 제외'된다. 따라서 표준 0/0
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
//   · COM 이벤트(OnReceiveRealData/OnReceiveChejanData)는 STA 스레드에서 발생 → IKiwoomApi 이벤트로
//     매핑. 수신 파싱은 값타입만 사용(문자열→숫자 변환은 콜드, 이후 KiwoomProxyHost 가 v4.82.0
//     재사용 버퍼+_sendLock 으로 δ=0 직렬화 송신).
//   · 계좌번호·화면번호는 배포별 값 → KiwoomComConfig 로 주입(코드에 박제하지 않음·No Guessing).
//   · FID 는 키움 OpenAPI+ 표준 코드(현재가 10·누적거래량 13·등락율 12; 체결 9203/9001/910/911/907).
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
        /// <summary>실시간 구독 FID 목록(기본: 현재가·등락율·누적거래량).</summary>
        public string RealFidList { get; init; } = "10;12;13";
    }
}

#if KIWOOM_COM
namespace StockManager.KiwoomProxy
{
    // ★using 은 네임스페이스 블록 내부 최상단(KiwoomComConfig 블록이 파일 앞에 있으므로 파일-레벨 불가).
    using System;
    using System.Threading;
    using System.Windows.Forms;
    using AxKHOpenAPILib;   // COM Interop — 키움 OpenAPI+ 설치·참조 시에만 존재(SDK 게이트)

    /// <summary>
    /// 실 키움 OpenAPI+ COM 어댑터. STA 스레드에서 axKHOpenAPI(ActiveX)를 구동하고, COM 이벤트를
    /// IKiwoomApi 이벤트로 매핑한다. KiwoomProxyHost 가 이 인스턴스를 구독해 파이프로 송신한다.
    /// </summary>
    public sealed class ProductionKiwoomApi : IKiwoomApi, IDisposable
    {
        private readonly KiwoomComConfig _cfg;
        private Thread?      _staThread;
        private Form?        _host;
        private AxKHOpenAPI? _ax;
        private volatile bool _ready;

        public event EventHandler<TickEventArgs>?    TickReceived;
        public event EventHandler<FillEventArgs>?    FillReceived;
        public event EventHandler<AccountEventArgs>? AccountReceived;

        public ProductionKiwoomApi(KiwoomComConfig config)
            => _cfg = config ?? throw new ArgumentNullException(nameof(config));

        // ── STA 부팅: 숨김 Form + ActiveX + 메시지 펌프 ─────────────────────────
        public void Connect()
        {
            if (_staThread is not null) return;
            var started = new ManualResetEventSlim(false);
            _staThread = new Thread(() =>
            {
                _host = new Form { WindowState = FormWindowState.Minimized, ShowInTaskbar = false };
                _ax   = new AxKHOpenAPI();
                ((System.ComponentModel.ISupportInitialize)_ax).BeginInit();
                _host.Controls.Add(_ax);
                ((System.ComponentModel.ISupportInitialize)_ax).EndInit();

                _ax.OnEventConnect      += OnEventConnect;
                _ax.OnReceiveRealData   += OnReceiveRealData;
                _ax.OnReceiveChejanData += OnReceiveChejanData;

                _host.HandleCreated += (_, _) => { _ax.CommConnect(); started.Set(); };
                Application.Run(_host);   // STA 메시지 펌프(ActiveX 필수)
            })
            { IsBackground = true, Name = "KiwoomComSTA" };
            _staThread.SetApartmentState(ApartmentState.STA);
            _staThread.Start();
            started.Wait(TimeSpan.FromSeconds(10));
        }

        private void OnEventConnect(object? sender, _DKHOpenAPIEvents_OnEventConnectEvent e)
            => _ready = e.nErrCode == 0;   // 0 = 로그인 성공

        // ── 실시간 시세 → TickReceived ─────────────────────────────────────────
        private void OnReceiveRealData(object? sender, _DKHOpenAPIEvents_OnReceiveRealDataEvent e)
        {
            if (_ax is null) return;
            if (e.sRealType != "주식체결") return;   // 체결 실시간만
            long code   = ParseCode(e.sRealKey);
            double price = Math.Abs(ParseDouble(_ax.GetCommRealData(e.sRealKey, 10)));   // FID 10 현재가(부호 제거)
            double rate  = ParseDouble(_ax.GetCommRealData(e.sRealKey, 12));             // FID 12 등락율
            long vol     = (long)ParseDouble(_ax.GetCommRealData(e.sRealKey, 13));       // FID 13 누적거래량
            TickReceived?.Invoke(this, new TickEventArgs
            { SymbolCode = code, Price = price, Volume = vol, ChangeRate = rate });
        }

        // ── 체결/거부 → FillReceived ───────────────────────────────────────────
        private void OnReceiveChejanData(object? sender, _DKHOpenAPIEvents_OnReceiveChejanDataEvent e)
        {
            if (_ax is null || e.sGubun != "0") return;   // "0" = 주문체결
            long orderId = (long)ParseDouble(_ax.GetChejanData(9203));   // 주문번호
            long code    = ParseCode(_ax.GetChejanData(9001));           // 종목코드(A 접두 가능)
            double fillPx = Math.Abs(ParseDouble(_ax.GetChejanData(910)));// 체결가
            long fillQty  = (long)ParseDouble(_ax.GetChejanData(911));    // 체결량(0 = 거부/미체결 → ProxyHost 가 OrderRejected 로 표기)
            int side      = _ax.GetChejanData(907).Trim() == "2" ? 1 : -1;// 907 매도수구분(2=매수)
            FillReceived?.Invoke(this, new FillEventArgs
            { OrderId = orderId, SymbolCode = code, FillPrice = fillPx, FillQty = fillQty, Side = side });
        }

        // ── 명령(Terminal → COM) ───────────────────────────────────────────────
        public void SendOrder(long symbolCode, int side, double price, long qty, long orderId)
        {
            if (_ax is null || !_ready) return;
            if (string.IsNullOrWhiteSpace(_cfg.AccountNo)) return;   // ★fail-closed: 계좌 미설정 → 송출 안 함
            int orderType = side > 0 ? 1 : 2;                        // 1=신규매수, 2=신규매도
            string hoga   = price <= 0.0 ? "03" : "00";              // 03=시장가, 00=지정가
            _ax.SendOrder($"RQ{orderId}", _cfg.OrderScreenNo, _cfg.AccountNo, orderType,
                          symbolCode.ToString("D6"), (int)qty, (int)price, hoga, "");
        }

        public void CancelOrder(long orderId) { /* 취소는 원주문번호 필요 — LiveAuditService 원장과 결속(운영자 배선) */ }

        public void SubscribeTick(long symbolCode)
        {
            if (_ax is null) return;
            _ax.SetRealReg(_cfg.RealScreenNo, symbolCode.ToString("D6"), _cfg.RealFidList, "1");
        }

        public void UnsubscribeTick(long symbolCode)
            => _ax?.SetRealRemove(_cfg.RealScreenNo, symbolCode.ToString("D6"));

        public void QueryAccount() { /* opw00001/opw00018 TR 요청 — OnReceiveTrData → AccountReceived(운영자 배선) */ }

        // ── 파싱 헬퍼 ───────────────────────────────────────────────────────────
        private static long ParseCode(string s)
            => long.TryParse(new string(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(s ?? "", char.IsDigit))), out var v) ? v : 0L;
        private static double ParseDouble(string s)
            => double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.0;

        public void Dispose()
        {
            try { _host?.BeginInvoke(new Action(() => _host.Close())); } catch { }
            _staThread = null; _ax = null; _host = null;
        }
    }
}
#endif
