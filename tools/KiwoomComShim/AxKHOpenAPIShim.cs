// ============================================================================
// AxKHOpenAPIShim — [CI 게이트] 키움 KHOpenAPI 인터롭(AxKHOpenAPILib) 형상 시뮬레이션
//
//  키움 SDK(OCX + aximp 생성 AxKHOpenAPILib)는 CI/개발 호스트에 설치돼 있지 않다. 그래서 ProductionKiwoomApi 의
//  `#if KIWOOM_COM` 블록은 평소 빌드에서 한 줄도 컴파일되지 않아, 리팩터링으로 깨져도 아무도 모른다
//  (실증: v12.14.1 직전 HEAD 는 KIWOOM_COM 정의 시 CS0067 로 빌드 불가였다).
//  이 파일은 공개 문서 시그니처 그대로 이벤트·메서드 형상만 선언한다 — 실행 불가(전부 기본값 반환), 출하 금지.
//  KiwoomComShim.csproj 가 KIWOOM_COM 을 정의하고 실제 프록시 소스를 링크해 경고=오류로 컴파일한다.
//  ★형상이 실제 SDK 와 다를 수 있다 — SDK 설치 환경 1회 실빌드로 최종 확인 필요(이 게이트는 '우리 코드의 형상 정합'만 증명).
// ============================================================================
namespace AxKHOpenAPILib
{
    public class _DKHOpenAPIEvents_OnEventConnectEvent { public int nErrCode; }
    public class _DKHOpenAPIEvents_OnReceiveRealDataEvent { public string sRealKey = "", sRealType = "", sRealData = ""; }
    public class _DKHOpenAPIEvents_OnReceiveChejanDataEvent { public string sGubun = "", sFIdList = ""; public int nItemCnt; }
    public class _DKHOpenAPIEvents_OnReceiveTrDataEvent { public string sScrNo = "", sRQName = "", sTrCode = "", sRecordName = "", sPrevNext = ""; }
    public class _DKHOpenAPIEvents_OnReceiveMsgEvent { public string sScrNo = "", sRQName = "", sTrCode = "", sMsg = ""; }
    public delegate void _DKHOpenAPIEvents_OnEventConnectEventHandler(object sender, _DKHOpenAPIEvents_OnEventConnectEvent e);
    public delegate void _DKHOpenAPIEvents_OnReceiveRealDataEventHandler(object sender, _DKHOpenAPIEvents_OnReceiveRealDataEvent e);
    public delegate void _DKHOpenAPIEvents_OnReceiveChejanDataEventHandler(object sender, _DKHOpenAPIEvents_OnReceiveChejanDataEvent e);
    public delegate void _DKHOpenAPIEvents_OnReceiveTrDataEventHandler(object sender, _DKHOpenAPIEvents_OnReceiveTrDataEvent e);
    public delegate void _DKHOpenAPIEvents_OnReceiveMsgEventHandler(object sender, _DKHOpenAPIEvents_OnReceiveMsgEvent e);
    public class AxKHOpenAPI : System.Windows.Forms.Control, System.ComponentModel.ISupportInitialize
    {
        public event _DKHOpenAPIEvents_OnEventConnectEventHandler? OnEventConnect;
        public event _DKHOpenAPIEvents_OnReceiveRealDataEventHandler? OnReceiveRealData;
        public event _DKHOpenAPIEvents_OnReceiveChejanDataEventHandler? OnReceiveChejanData;
        public event _DKHOpenAPIEvents_OnReceiveTrDataEventHandler? OnReceiveTrData;
        public event _DKHOpenAPIEvents_OnReceiveMsgEventHandler? OnReceiveMsg;
        public void BeginInit() { } public void EndInit() { }
        public int CommConnect() => 0;
        public string GetCommRealData(string sCode, int nFid) => "";
        public string GetChejanData(int nFid) => "";
        public string GetCommData(string strTrCode, string strRecordName, int nIndex, string strItemName) => "";
        public int SendOrder(string sRQName, string sScreenNo, string sAccNo, int nOrderType, string sCode, int nQty, int nPrice, string sHogaGb, string sOrgOrderNo) => 0;
        public int SetRealReg(string strScreenNo, string strCodeList, string strFidList, string strOptType) => 0;
        public void SetRealRemove(string strScrNo, string strDelCode) { }
        public void SetInputValue(string sID, string sValue) { }
        public int CommRqData(string sRQName, string sTrCode, int nPrevNext, string sScreenNo) => 0;
        public int GetRepeatCnt(string sTrCode, string sRecordName) => 0;
        internal void Touch() { OnEventConnect?.Invoke(this, new()); OnReceiveRealData?.Invoke(this, new()); OnReceiveChejanData?.Invoke(this, new()); OnReceiveTrData?.Invoke(this, new()); OnReceiveMsg?.Invoke(this, new()); }
    }
}
