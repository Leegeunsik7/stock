// ============================================================================
// PlutoMultiAssetUniverseCatalog — v12.18.79-DEV: 플루토 3대 자산 감시 유니버스 카탈로그(KRX · US · 업비트 KRW · 종목 메타데이터 · 콜드 구축 · δ=0 조회)
//
//   국내 = 코스피 200 · 코스닥 150 주요 구성 종목(6자리 코드 · 한글명 · 시장) · 미국 = S&P 100 · 나스닥 100 주요 구성(티커 · 영문명 · 지수 계열) ·
//   가상자산 = 업비트 KRW 마켓 주요 상장 코인(마켓 코드 · 코인명).
//   ★메타데이터 전용 — 재무 수치(DART · SEC) · 시세 · 점수는 담지 않는다(날조 0). 점수는 아레나 피드가 실제 시세/브레인 판독을 공급하는 종목만 산출되고,
//    카탈로그 나머지 = '감시 중 · 피드 판독 대기'로 정직 집계(PlutoScreenTally.Monitored − Scanned). 재무 판독 = 기존 PlutoValuationBuffer(시드/실 피드)만.
//   ★정적 스냅샷 — 지수 정기 변경(반기) · 업비트 신규 상장/상장 폐지 반영 = 표 갱신 필요(런타임 네트워크 조회 0 · 스크리너 네트워크 0 유지).
//   종목 키 = 아레나와 동일 인코딩(KRX = 코드 정수 · US = UsTechUniverse.Pack · 코인 = CryptoArenaUniverse.Pack("KRW-XXX")) — 정렬 long[] 이분 탐색(δ=0).
//    업비트 마켓 코드 9자 이상(KRW-XXXXX…) = 아레나 키 인코딩 불가(0) → 감시 수에는 포함 · 키 조회 대상 아님(아레나 피드 대상이 될 수 없음).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Arena.Us;

namespace StockManager.Engines.Cognitive.Screening
{
    /// <summary>카탈로그 구성 계열.</summary>
    public enum PlutoUniverseSegment : byte
    {
        Kospi = 0,       // 코스피 200 주요 구성
        Kosdaq = 1,      // 코스닥 150 주요 구성
        Sp100 = 2,       // S&P 100
        Nasdaq100 = 3,   // 나스닥 100(S&P 100 비중복분)
        UpbitKrw = 4,    // 업비트 KRW 마켓
    }

    /// <summary>카탈로그 종목 1건(불변). Symbol = 아레나 키(인코딩 불가 = 0).</summary>
    public sealed record PlutoUniverseEntry(PlutoScreenMarket Market, PlutoUniverseSegment Segment, long Symbol, string Code, string Name);

    public static class PlutoMultiAssetUniverseCatalog
    {
        // ── 국내: 코스피 200 주요 구성(시가총액 상위 · 업종 대표) ───────────────────────────
        private static readonly string[] s_kospi =
        {
            "005930:삼성전자", "000660:SK하이닉스", "373220:LG에너지솔루션", "207940:삼성바이오로직스", "005935:삼성전자우",
            "005380:현대차", "000270:기아", "068270:셀트리온", "005490:POSCO홀딩스", "035420:NAVER",
            "035720:카카오", "051910:LG화학", "006400:삼성SDI", "105560:KB금융", "055550:신한지주",
            "012330:현대모비스", "028260:삼성물산", "066570:LG전자", "003670:포스코퓨처엠", "086790:하나금융지주",
            "032830:삼성생명", "000810:삼성화재", "015760:한국전력", "017670:SK텔레콤", "030200:KT",
            "034730:SK", "096770:SK이노베이션", "003550:LG", "018260:삼성에스디에스", "009150:삼성전기",
            "011200:HMM", "010130:고려아연", "033780:KT&G", "316140:우리금융지주", "024110:기업은행",
            "138040:메리츠금융지주", "012450:한화에어로스페이스", "329180:HD현대중공업", "009540:HD한국조선해양", "010140:삼성중공업",
            "042660:한화오션", "267250:HD현대", "034020:두산에너빌리티", "000100:유한양행", "128940:한미약품",
            "302440:SK바이오사이언스", "326030:SK바이오팜", "251270:넷마블", "036570:엔씨소프트", "259960:크래프톤",
            "352820:하이브", "323410:카카오뱅크", "377300:카카오페이", "035250:강원랜드", "090430:아모레퍼시픽",
            "051900:LG생활건강", "097950:CJ제일제당", "271560:오리온", "004370:농심", "011170:롯데케미칼",
            "010950:S-Oil", "078930:GS", "006800:미래에셋증권", "005940:NH투자증권", "016360:삼성증권",
            "039490:키움증권", "071050:한국금융지주", "000720:현대건설", "028050:삼성E&A", "047040:대우건설",
            "006360:GS건설", "011780:금호석유", "009830:한화솔루션", "000880:한화", "088350:한화생명",
            "001040:CJ", "000120:CJ대한통운", "003490:대한항공", "180640:한진칼", "086280:현대글로비스",
            "011210:현대위아", "204320:HL만도", "161390:한국타이어앤테크놀로지", "073240:금호타이어", "004020:현대제철",
            "001450:현대해상", "005830:DB손해보험", "032640:LG유플러스", "011070:LG이노텍", "034220:LG디스플레이",
            "010120:LS ELECTRIC", "006260:LS", "001120:LX인터내셔널", "064350:현대로템", "047810:한국항공우주",
            "079550:LIG넥스원", "272210:한화시스템", "112610:씨에스윈드", "298040:효성중공업", "267260:HD현대일렉트릭",
            "042700:한미반도체", "000990:DB하이텍", "004990:롯데지주", "023530:롯데쇼핑", "139480:이마트",
            "069960:현대백화점", "282330:BGF리테일", "007070:GS리테일", "008770:호텔신라", "021240:코웨이",
            "036460:한국가스공사", "052690:한전기술", "051600:한전KPS", "003410:쌍용C&E", "000080:하이트진로",
            "005300:롯데칠성", "280360:롯데웰푸드", "007310:오뚜기", "004170:신세계", "185750:종근당",
            "069620:대웅제약", "006280:녹십자", "000150:두산", "241560:두산밥캣", "454910:두산로보틱스",
            "450080:에코프로머티", "003230:삼양식품", "017800:현대엘리베이터", "002380:KCC", "120110:코오롱인더",
            "298020:효성티앤씨", "004800:효성", "011790:SKC", "285130:SK케미칼", "402340:SK스퀘어",
            "361610:SK아이이테크놀로지", "018880:한온시스템", "009240:한샘", "010060:OCI홀딩스", "383220:F&F",
        };

        // ── 국내: 코스닥 150 주요 구성 ───────────────────────────────────────────────
        private static readonly string[] s_kosdaq =
        {
            "247540:에코프로비엠", "086520:에코프로", "196170:알테오젠", "028300:HLB", "263750:펄어비스",
            "293490:카카오게임즈", "035760:CJ ENM", "041510:에스엠", "035900:JYP Ent.", "122870:와이지엔터테인먼트",
            "068760:셀트리온제약", "145020:휴젤", "214150:클래시스", "214450:파마리서치", "141080:리가켐바이오",
            "058470:리노공업", "240810:원익IPS", "039030:이오테크닉스", "357780:솔브레인", "005290:동진쎄미켐",
            "036930:주성엔지니어링", "403870:HPSP", "095340:ISC", "278280:천보", "112040:위메이드",
            "078340:컴투스", "194480:데브시스터즈", "253450:스튜디오드래곤", "237690:에스티팜", "085660:차바이오텍",
            "328130:루닛", "338220:뷰노", "277810:레인보우로보틱스", "348370:엔켐", "393890:더블유씨피",
            "121600:나노신소재", "064760:티씨케이", "222800:심텍", "067310:하나마이크론", "140860:파크시스템스",
            "319660:피에스케이", "039200:오스코텍", "298380:에이비엘바이오", "096530:씨젠", "086900:메디톡스",
            "215600:신라젠", "060280:큐렉소", "032500:케이엠더블유", "036830:솔브레인홀딩스", "042000:카페24",
            "053800:안랩", "950160:코오롱티슈진", "365340:성일하이텍", "383310:에코프로에이치엔", "214370:케어젠",
            "000250:삼천당제약", "225570:넥슨게임즈", "101490:에스앤에스텍", "080220:제주반도체", "089030:테크윙",
        };

        // ── 미국: S&P 100 주요 구성 ────────────────────────────────────────────────
        private static readonly string[] s_sp100 =
        {
            "AAPL:Apple", "MSFT:Microsoft", "NVDA:NVIDIA", "AMZN:Amazon", "GOOGL:Alphabet A",
            "GOOG:Alphabet C", "META:Meta Platforms", "AVGO:Broadcom", "TSLA:Tesla", "BRK.B:Berkshire Hathaway B",
            "JPM:JPMorgan Chase", "LLY:Eli Lilly", "V:Visa", "UNH:UnitedHealth", "XOM:Exxon Mobil",
            "MA:Mastercard", "JNJ:Johnson & Johnson", "PG:Procter & Gamble", "HD:Home Depot", "COST:Costco",
            "ABBV:AbbVie", "WMT:Walmart", "NFLX:Netflix", "BAC:Bank of America", "CRM:Salesforce",
            "ORCL:Oracle", "CVX:Chevron", "KO:Coca-Cola", "MRK:Merck", "AMD:AMD",
            "PEP:PepsiCo", "ADBE:Adobe", "TMO:Thermo Fisher", "LIN:Linde", "ACN:Accenture",
            "MCD:McDonald's", "CSCO:Cisco", "ABT:Abbott", "WFC:Wells Fargo", "DIS:Walt Disney",
            "INTU:Intuit", "QCOM:Qualcomm", "TXN:Texas Instruments", "DHR:Danaher", "CAT:Caterpillar",
            "VZ:Verizon", "AMGN:Amgen", "IBM:IBM", "PM:Philip Morris", "GE:GE Aerospace",
            "NOW:ServiceNow", "ISRG:Intuitive Surgical", "CMCSA:Comcast", "PFE:Pfizer", "UNP:Union Pacific",
            "SPGI:S&P Global", "GS:Goldman Sachs", "NEE:NextEra Energy", "RTX:RTX", "LOW:Lowe's",
            "HON:Honeywell", "AMAT:Applied Materials", "T:AT&T", "BKNG:Booking", "UBER:Uber",
            "AXP:American Express", "BLK:BlackRock", "MS:Morgan Stanley", "PLTR:Palantir", "LMT:Lockheed Martin",
            "SBUX:Starbucks", "DE:Deere", "MDT:Medtronic", "GILD:Gilead", "MU:Micron",
            "BMY:Bristol-Myers Squibb", "C:Citigroup", "SCHW:Charles Schwab", "MO:Altria", "INTC:Intel",
            "PYPL:PayPal", "ADP:ADP",
        };

        // ── 미국: 나스닥 100 주요 구성(S&P 100 비중복분) ──────────────────────────────────
        private static readonly string[] s_ndx =
        {
            "ADI:Analog Devices", "LRCX:Lam Research", "MDLZ:Mondelez", "KLAC:KLA", "PANW:Palo Alto Networks",
            "SNPS:Synopsys", "CDNS:Cadence", "MRVL:Marvell", "CRWD:CrowdStrike", "ABNB:Airbnb",
            "MELI:MercadoLibre", "ASML:ASML", "FTNT:Fortinet", "ORLY:O'Reilly", "CSX:CSX",
            "MAR:Marriott", "CTAS:Cintas", "REGN:Regeneron",
        };

        // ── 가상자산: 업비트 KRW 마켓 주요 상장 코인 ─────────────────────────────────────
        private static readonly string[] s_upbit =
        {
            "BTC:비트코인", "ETH:이더리움", "XRP:리플", "SOL:솔라나", "DOGE:도지코인",
            "ADA:에이다", "TRX:트론", "AVAX:아발란체", "LINK:체인링크", "DOT:폴카닷",
            "BCH:비트코인캐시", "ETC:이더리움클래식", "XLM:스텔라루멘", "SHIB:시바이누", "NEAR:니어프로토콜",
            "APT:앱토스", "SUI:수이", "HBAR:헤데라", "SEI:세이", "ARB:아비트럼",
            "POL:폴리곤에코시스템토큰", "ATOM:코스모스", "ALGO:알고랜드", "AAVE:에이브", "UNI:유니스왑",
            "SAND:샌드박스", "MANA:디센트럴랜드", "AXS:엑시인피니티", "ENS:이더리움네임서비스", "STX:스택스",
            "IMX:이뮤터블엑스", "GRT:더그래프", "CHZ:칠리즈", "FLOW:플로우", "EOS:이오스",
            "NEO:네오", "QTUM:퀀텀", "ICX:아이콘", "IOST:아이오에스티", "IOTA:아이오타",
            "ONT:온톨로지", "ZIL:질리카", "THETA:쎄타토큰", "TFUEL:쎄타퓨엘", "VET:비체인",
            "XTZ:테조스", "KAVA:카바", "HIVE:하이브", "STEEM:스팀", "BTT:비트토렌트",
            "JST:저스트", "SXP:솔라", "ANKR:앵커", "CVC:시빅", "GAS:가스",
            "GLM:골렘", "MED:메디블록", "MLK:밀크", "META:메타디움", "ORBS:오브스",
            "POWR:파워렛저", "SC:시아코인", "SNT:스테이터스네트워크토큰", "STORJ:스토리지", "T:쓰레스홀드",
            "1INCH:1인치네트워크", "AERGO:아르고", "ARDR:아더", "ARK:아크", "ASTR:아스타",
            "BAT:베이직어텐션토큰", "BLUR:블러", "BORA:보라", "CELO:셀로", "EGLD:멀티버스엑스",
            "ELF:엘프", "GMT:스테픈", "GRS:그로스톨코인", "HUNT:헌트", "ID:스페이스아이디",
            "IQ:아이큐", "KNC:카이버네트워크", "LSK:리스크", "MASK:마스크네트워크", "MINA:미나",
            "MTL:메탈", "MVL:엠블", "ONG:온톨로지가스", "PEPE:페페", "PUNDIX:펀디엑스",
            "PYTH:피스네트워크", "QKC:쿼크체인", "SBD:스팀달러", "STG:스타게이트파이낸스", "STPT:에스티피",
            "TT:썬더코어", "UPP:센티넬프로토콜", "USDT:테더", "W:웜홀", "WAXP:왁스",
            "XEC:이캐시", "XEM:넴", "ZETA:제타체인", "ZRO:레이어제로", "ZRX:제로엑스",
            "ONDO:온도파이낸스", "RENDER:렌더토큰", "BONK:봉크", "ENA:에테나", "JUP:주피터",
            "AKT:아카시네트워크", "CKB:너보스", "USDC:유에스디코인", "TAIKO:타이코", "MOVE:무브먼트",
            "LAYER:솔레이어", "VIRTUAL:버추얼프로토콜", "BERA:베라체인", "TRUMP:오피셜트럼프", "WLD:월드코인",
        };

        private static readonly PlutoUniverseEntry[][] s_byMarket;
        private static readonly long[][] s_sortedKeys;
        private static readonly ReadOnlyCollection<PlutoUniverseEntry> s_all;

        static PlutoMultiAssetUniverseCatalog()
        {
            var krx = new List<PlutoUniverseEntry>(s_kospi.Length + s_kosdaq.Length);
            Add(krx, PlutoScreenMarket.Krx, PlutoUniverseSegment.Kospi, s_kospi);
            Add(krx, PlutoScreenMarket.Krx, PlutoUniverseSegment.Kosdaq, s_kosdaq);
            var us = new List<PlutoUniverseEntry>(s_sp100.Length + s_ndx.Length);
            Add(us, PlutoScreenMarket.Us, PlutoUniverseSegment.Sp100, s_sp100);
            Add(us, PlutoScreenMarket.Us, PlutoUniverseSegment.Nasdaq100, s_ndx);
            var crypto = new List<PlutoUniverseEntry>(s_upbit.Length);
            Add(crypto, PlutoScreenMarket.Crypto, PlutoUniverseSegment.UpbitKrw, s_upbit);
            s_byMarket = new[] { krx.ToArray(), us.ToArray(), crypto.ToArray() };
            s_sortedKeys = new long[s_byMarket.Length][];
            var all = new List<PlutoUniverseEntry>(krx.Count + us.Count + crypto.Count);
            for (int m = 0; m < s_byMarket.Length; m++)
            {
                var keys = new List<long>(s_byMarket[m].Length);
                foreach (var e in s_byMarket[m]) { if (e.Symbol > 0) keys.Add(e.Symbol); all.Add(e); }
                keys.Sort();
                s_sortedKeys[m] = keys.ToArray();
            }
            s_all = all.AsReadOnly();
        }

        private static void Add(List<PlutoUniverseEntry> dst, PlutoScreenMarket market, PlutoUniverseSegment seg, string[] rows)
        {
            foreach (var row in rows)
            {
                int sep = row.IndexOf(':');
                string code = market == PlutoScreenMarket.Crypto ? "KRW-" + row[..sep] : row[..sep];
                dst.Add(new PlutoUniverseEntry(market, seg, KeyOf(market, code), code, row[(sep + 1)..]));
            }
        }

        /// <summary>코드 → 아레나 키(KRX 6자리 숫자 · US 티커 · 업비트 "KRW-XXX" · 인코딩 불가 = 0).</summary>
        public static long KeyOf(PlutoScreenMarket market, string code) => market switch
        {
            PlutoScreenMarket.Krx => code.Length == 6 && long.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out long k) ? k : 0,
            PlutoScreenMarket.Us => UsTechUniverse.Pack(code),
            PlutoScreenMarket.Crypto => CryptoArenaUniverse.Pack(code),
            _ => 0,
        };

        public static int KrxCount => s_byMarket[(int)PlutoScreenMarket.Krx].Length;
        public static int UsCount => s_byMarket[(int)PlutoScreenMarket.Us].Length;
        public static int CryptoCount => s_byMarket[(int)PlutoScreenMarket.Crypto].Length;
        public static int TotalCount => s_all.Count;

        /// <summary>시장별 감시 종목 수(범위 밖 = 0).</summary>
        public static int CountOf(PlutoScreenMarket m) => (uint)m < (uint)s_byMarket.Length ? s_byMarket[(int)m].Length : 0;

        /// <summary>시장별 카탈로그(콜드 · 표 순서).</summary>
        public static IReadOnlyList<PlutoUniverseEntry> Of(PlutoScreenMarket m)
            => (uint)m < (uint)s_byMarket.Length ? Array.AsReadOnly(s_byMarket[(int)m]) : Array.Empty<PlutoUniverseEntry>();

        public static IReadOnlyList<PlutoUniverseEntry> All => s_all;

        /// <summary>카탈로그 포함 여부(정렬 키 이분 탐색 · δ=0 · 키 0 이하/범위 밖 = false).</summary>
        public static bool Contains(PlutoScreenMarket m, long symbol)
            => symbol > 0 && (uint)m < (uint)s_sortedKeys.Length && Array.BinarySearch(s_sortedKeys[(int)m], symbol) >= 0;

        /// <summary>종목명(콜드 · 미등록 = null — 날조 0).</summary>
        public static string? NameOf(PlutoScreenMarket m, long symbol)
        {
            if (!Contains(m, symbol)) return null;
            foreach (var e in s_byMarket[(int)m]) if (e.Symbol == symbol) return e.Name;
            return null;
        }

        public static string SegmentLabel(PlutoUniverseSegment s) => s switch
        {
            PlutoUniverseSegment.Kospi => "코스피 200",
            PlutoUniverseSegment.Kosdaq => "코스닥 150",
            PlutoUniverseSegment.Sp100 => "S&P 100",
            PlutoUniverseSegment.Nasdaq100 => "나스닥 100",
            _ => "업비트 KRW",
        };
    }
}
