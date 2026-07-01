// ============================================================================
// TossLiveBroker — 과제 4: 토스증권 실거래 어댑터(REST 주문) + WS 인입(체결/호가)
//
//  ⚠️⚠️⚠️ 실제 자금이 집행될 수 있는 어댑터입니다. liveArmed:false 불변 —
//        이 클래스는 '설계 코드'이며 기본 OFF. 실 송출 도달은 3중 옵트인(게이트 주입 +
//        QFixedPrimary + IsLive&IsArmed) 동시 성립 시에만. 실 엔드포인트/인증(OAuth)은
//        ITossTransport 추상 뒤로 분리(openQuestions) — 본 파일은 자동 배선하지 않는다.
//
//  ── 아키텍처(인입 파이프라인) ──────────────────────────────────────────────
//   WS(체결/호가 raw bytes)
//     → ZeroAllocJsonParser.TryParseTick(fragment, continuation)  [δ=0 핫패스: stackalloc/ArrayPool]
//     → OrderProposal 구성(QPrice/QQty raw)                        [δ=0: 값타입·무할당]
//     → OrderDispatchRing.Publish(...)  (SPMC 단일 생산자)          [δ=0: 슬롯 무할당 복사]
//   REST(주문 송출): IBroker.Place → ITossTransport.PostOrder (HttpClient 추상)
//
//  ── fail-closed(동결경로·할당 허용·핫패스 아님) ────────────────────────────
//   · WS 재연결 한도 초과 → ForceFreeze (피드 단절은 권위 오염 위험).
//   · 파싱 실패(경계 오염/미등록 심볼/KSE 범위 외) → 해당 프레임 격리 폐기(핫패스에서
//     동결 아님 = 단일 불량 프레임이 전체를 멈추지 않음) + 누적 카운터, 임계 초과 시 ForceFreeze.
//   · 주문 거부(REST 4xx/5xx·네트워크 예외) → (false, msg) 반환(거부 격리). 단, ★권위 경로
//     (ShadowClearingBroker QFixedPrimary)에서 호출되면 데코레이터가 fail-closed 하드 단절.
//
//  ── δ=0 핫패스 vs 동결경로 할당 분리(명시) ─────────────────────────────────
//   핫패스(IngestFrame→Publish): 문자열·박싱·LINQ·예외throw 0. 동결경로(ForceFreeze·
//   REST 직렬화·재연결 로그): 할당 허용(콜드 패스). 두 경로를 메서드 단위로 격리한다.
// ============================================================================
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace StockManager.Common
{
    // ── 인입 운반 추상(실 Toss API 격리) ──────────────────────────────────────
    /// <summary>
    /// 토스증권 REST/WS 운반 추상. 실 엔드포인트·OAuth 인증·WS 핸드셰이크를 이 인터페이스 뒤로
    /// 격리한다(테스트는 페이크 주입). 본 인터페이스는 '와이어'만 노출 — 청산/정렬/권위는 상위 계층.
    /// </summary>
    public interface ITossTransport
    {
        /// <summary>REST 주문 송출. (성공여부, 원문). 인증 헤더·서명은 구현체가 부착.</summary>
        (bool ok, string body) PostOrder(string pathAndQuery, string jsonBody);

        /// <summary>REST 주문 취소. throw 금지(내부 흡수) — 취소는 best-effort.</summary>
        void DeleteOrder(string orderId);

        /// <summary>심볼 보유수량 조회(REST). 실패 시 0.</summary>
        decimal QueryHeldQty(PaperAsset asset, string symbol);

        /// <summary>WS 1회 수신 시도(블로킹·타임아웃 내). 프레임 바이트를 콜백으로 넘긴다.
        /// 반환: true=프레임 1건 전달, false=타임아웃(정상), throw=소켓 단절(상위가 재연결 판단).</summary>
        bool TryReceive(Action<ReadOnlyMemory<byte>> onFrame, int timeoutMs);

        /// <summary>WS (재)연결 + 구독. 실패 시 throw(상위가 재연결 카운트).</summary>
        void ConnectAndSubscribe();

        /// <summary>WS 종료(idempotent).</summary>
        void Disconnect();
    }

    /// <summary>
    /// 토스증권 실거래 브로커. IBroker(REST 주문) 구현 + WS 인입 펌프(체결/호가 → ring).
    /// ShadowClearingBroker 로 감싸 권위/관측 분기를 받는다(본 클래스는 raw 송출 경계).
    /// </summary>
    public sealed class TossLiveBroker : IBroker, IDisposable
    {
        private readonly ITossTransport       _transport;
        private readonly OrderDispatchRing    _ring;          // SPMC — '시세 신호 전용 링'의 유일 생산자(ChampionEngine 주문제안 링과 물리 분리 필수)
        private readonly OrderAttestationService? _att;       // 동결 채널(피드 단절·임계 초과)
        private readonly long                 _genomeId;      // 감사 계보(주입·결정론)
        private readonly Func<long>           _clock;         // TimestampTicks 주입(δ=0·DateTime.UtcNow 핫패스 배제)
        private readonly int                  _maxReconnects; // 재연결 한도(초과 → fail-closed)
        private readonly long                 _parseFailFreezeThreshold; // 누적 파싱 실패 임계(초과 → 동결)

        // 인입 펌프 스레드
        private Thread?        _pumpThread;
        private volatile bool  _running;

        // 텔레메트리(락프리 카운터)
        private long _published;       // ring 발행 성공
        private long _parseFails;      // 파싱 실패(격리 폐기)
        private long _reconnects;      // WS 재연결 시도
        private long _frozenTrips;     // fail-closed 동결 트립
        private int  _parseFreezeTripped;  // 파싱 임계 동결 1회 래치(>=threshold 재발 방지)

        // ★적대검증 교정(minor): clock 주입 필수. TimestampTicks 는 코드베이스 전반에서 DateTime.Ticks(100ns)
        //   의미이므로, 단위가 다른 Environment.TickCount64(ms-since-boot)를 기본값으로 쓰지 않는다(감사·텔레메트리
        //   타임스탬프 단위 오염 차단). 핫패스에선 DateTime.UtcNow 배제하되 단위는 100ns ticks 로 일관 주입.
        public TossLiveBroker(
            ITossTransport transport,
            OrderDispatchRing ring,
            Func<long> clock,                                // ★주입 필수 — 단위 ticks(100ns) 일관·결정론
            OrderAttestationService? attestation = null,
            long genomeId = 0,
            int maxReconnects = 5,
            long parseFailFreezeThreshold = 1024)
        {
            _transport               = transport ?? throw new ArgumentNullException(nameof(transport));
            _ring                    = ring       ?? throw new ArgumentNullException(nameof(ring));
            _clock                   = clock      ?? throw new ArgumentNullException(nameof(clock));  // 단위 오염 차단(기본값 금지)
            _att                     = attestation;
            _genomeId                = genomeId;
            _maxReconnects           = maxReconnects < 0 ? 0 : maxReconnects;
            _parseFailFreezeThreshold = parseFailFreezeThreshold <= 0 ? long.MaxValue : parseFailFreezeThreshold;
        }

        public string Name   => "토스증권 실거래";
        public bool   IsLive => true;

        // ── 텔레메트리 독출(관제탑) ────────────────────────────────────────────
        public long Published   => Interlocked.Read(ref _published);
        public long ParseFails  => Interlocked.Read(ref _parseFails);
        public long Reconnects  => Interlocked.Read(ref _reconnects);
        public long FrozenTrips => Interlocked.Read(ref _frozenTrips);
        public bool Running     => _running;

        // ====================================================================
        // 1) REST 주문 경계(IBroker) — 거부 격리 fail-closed
        // ====================================================================
        public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name,
            PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
        {
            // 경계 검증(가벼운 거부 — throw 아님). 본 브로커는 raw 송출 경계이며 정렬/권위는 상위 데코레이터.
            if (string.IsNullOrWhiteSpace(symbol)) return (false, "토스: 심볼 누락 — 주문 거부");
            if (qty <= 0m)                         return (false, "토스: 수량 0 이하 — 주문 거부");
            decimal px = kind == PaperKind.Market ? currentPrice : limitPrice;
            if (px < 0m)                           return (false, "토스: 음수 단가 — 주문 거부");

            try
            {
                // ★동결경로(콜드) — JSON 직렬화 할당 허용(핫패스 아님).
                string body = BuildOrderJson(symbol, side, kind, qty, px);
                var (ok, raw) = _transport.PostOrder("/v1/orders", body);
                if (!ok)
                    // 주문 거부 격리 — 예외로 승격하지 않음. (권위 경로면 상위 데코레이터가 별도 판단)
                    return (false, "토스 주문 거부: " + Trunc(raw));
                return (true, $"토스 주문 접수: {symbol} {(side == PaperSide.Buy ? "매수" : "매도")}");
            }
            catch (Exception ex)
            {
                // 네트워크/직렬화 예외 → 거부 격리(false). throw 로 UI 스레드까지 전파시키지 않는다.
                return (false, "토스 주문 오류: " + ex.Message);
            }
        }

        public void Cancel(string orderId)
        {
            try { _transport.DeleteOrder(orderId); }
            catch { /* 취소는 best-effort — 흡수 */ }
        }

        public decimal HeldQty(PaperAsset asset, string symbol)
        {
            try { return _transport.QueryHeldQty(asset, symbol); }
            catch { return 0m; }
        }

        // ── 주문 JSON 구성(콜드 패스·할당 허용) ────────────────────────────────
        private static string BuildOrderJson(string symbol, PaperSide side, PaperKind kind, decimal qty, decimal px)
        {
            // 실 Toss 필드 스키마는 미확정(openQuestions). 결정론 직렬화를 위해 명시 키 순서 사용.
            var sb = new StringBuilder(160);
            sb.Append('{');
            sb.Append("\"symbol\":\"").Append(symbol).Append("\",");
            sb.Append("\"side\":\"").Append(side == PaperSide.Buy ? "buy" : "sell").Append("\",");
            sb.Append("\"ord_type\":\"").Append(kind == PaperKind.Market ? "market" : "limit").Append("\",");
            sb.Append("\"qty\":\"").Append(qty.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture)).Append("\",");
            sb.Append("\"price\":\"").Append(px.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture)).Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        private static string Trunc(string s) => s.Length > 180 ? s[..180] : s;

        // ====================================================================
        // 2) WS 인입 펌프 — δ=0 핫패스(파싱·ring publish) + 재연결 fail-closed
        // ====================================================================

        /// <summary>인입 펌프 시작(백그라운드 스레드). 이미 동작 중이면 무동작.</summary>
        public void StartIngress()
        {
            if (_running) return;
            _running = true;
            _pumpThread = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "TossWsIngress",
            };
            _pumpThread.Start();
        }

        /// <summary>인입 펌프 정지(Join). 핫패스 무접촉.</summary>
        public void StopIngress()
        {
            _running = false;
            try { _transport.Disconnect(); } catch { /* 흡수 */ }
            var t = _pumpThread;
            if (t is not null && t.IsAlive) t.Join(2000);
            _pumpThread = null;
        }

        // ── 펌프 루프(스레드 본체) — 연결/수신/재연결 오케스트레이션 ────────────
        private void PumpLoop()
        {
            int reconnectStreak = 0;
            while (_running)
            {
                try
                {
                    _transport.ConnectAndSubscribe();

                    // 수신 루프 — TryReceive 가 throw 하면 소켓 단절 → catch 로 재연결 판단.
                    while (_running)
                    {
                        // ★δ=0 핫패스 진입 — onFrame 콜백은 IngestFrame(무할당 파싱·ring publish).
                        bool got = _transport.TryReceive(IngestFrame, timeoutMs: 1000);
                        // ★fail-open 차단(적대검증 교정): '연결 성공'이 아니라 '실 데이터 수신(got)' 시에만 스트릭 리셋.
                        //   connect→즉시 close 반복(권한취소·구독거부)이 스트릭을 리셋시켜 한도에 영영 못 닿던
                        //   재연결 livelock 을, 스트릭 누적→_maxReconnects 초과→fail-closed 동결로 수렴시킨다.
                        //   got==false 는 정상 타임아웃(프레임 없음) — 스트릭 불변, 루프 지속.
                        if (got) reconnectStreak = 0;
                    }
                }
                catch (Exception ex)
                {
                    // ── WS 단절(콜드 패스·할당 허용) — 재연결 또는 fail-closed ──
                    if (!_running) break;   // 정상 종료 중이면 조용히 탈출
                    reconnectStreak++;
                    Interlocked.Increment(ref _reconnects);
                    try { _transport.Disconnect(); } catch { /* 흡수 */ }

                    if (reconnectStreak > _maxReconnects)
                    {
                        // ★fail-closed — 재연결 한도 초과: 피드 단절을 통과시키지 않고 동결.
                        //   동결경로는 핫패스 아님(할당 허용·att._lock 직렬화·감사 레코드 ~56B).
                        TripFreeze("TossLiveBroker WS 재연결 한도 초과 — 피드 단절 fail-closed: " + ex.Message);
                        _running = false;
                        break;
                    }

                    // 지수 백오프(상한) — 콜드 패스 sleep 허용(핫패스 아님).
                    int backoffMs = Math.Min(200 * reconnectStreak, 5000);
                    SleepBounded(backoffMs);
                }
            }
        }

        // ── δ=0 핫패스: raw 프레임 → 파싱 → OrderProposal → ring.Publish ────────
        //   ★무할당: ZeroAllocJsonParser 는 stackalloc(≤4096)/ArrayPool 경로, Utf8JsonReader 힙 0.
        //   ★예외 throw 금지: 불량 프레임은 격리 폐기(카운터 증가) — 단일 프레임이 펌프를 멈추지 않음.
        private void IngestFrame(ReadOnlyMemory<byte> frame)
        {
            ReadOnlySpan<byte> span = frame.Span;

            // 분할 프레임 가정 없음(ITossTransport 가 1메시지=1프레임 보장) → continuation 빈 슬라이스.
            if (!ZeroAllocJsonParser.TryParseTick(span, ReadOnlySpan<byte>.Empty, out TickPayload tick))
            {
                OnParseFail();
                return;
            }

            // 경계 검증(미등록/범위 외는 파서가 1차 차단; 여기선 ring 계약 가드).
            //  Price 는 double → QPrice(고정소수점) 변환. 음수/오버플로는 격리 폐기(핫패스 동결 아님).
            if (tick.SymbolCode is <= 0 or > 999_999 || tick.Price <= 0)
            {
                OnParseFail();
                return;
            }

            // double→decimal→QPrice 변환은 할당 0(전부 값타입). 변환 오버플로만 격리.
            QPrice price;
            QQty   qty;
            try
            {
                // 호가 스트림은 '가격'만 권위 — 수량은 0(스트림 틱은 주문 제안의 가격 신호).
                // ★생산자 모델(교정 minor): 실 제안 수량(qty>0)은 ChampionEngine 이 '독립 ring'에 발행한다.
                //   TossLiveBroker 는 '시세 신호 전용 링'의 유일 생산자(qty=0) — 동일 ring 공유 시 CAS 충돌→즉시 동결.
                price = QPrice.Of((decimal)tick.Price);
                qty   = QQty.Of(0m);
            }
            catch
            {
                OnParseFail();
                return;
            }

            // ★SPMC 단일 생산자 발행 — 본 브로커가 '시세 신호 전용 링'의 유일 생산자(CAS 게이트 강제). 무할당 슬롯 복사.
            try
            {
                _ring.Publish(
                    genomeId:       _genomeId,
                    symbolCode:     tick.SymbolCode,
                    side:           OrderSide.Buy,   // 호가 신호 기본 매수측(상위가 재해석)
                    kind:           1,               // Limit(가격 동반)
                    price:          price,
                    qty:            qty,
                    timestampTicks: _clock());
                Interlocked.Increment(ref _published);
            }
            catch (InvalidOperationException ex)
            {
                // ★적대검증 교정(major fail-open): 단일 생산자 CAS 게이트 위반 = 제어흐름 오염(두 생산자가
                //   동일 ring 진입). '불량 프레임 1건' 격리(누적 임계)와 동급 취급 금지 — 즉시 fail-closed 하드 단절.
                //   (메모리: fail-closed-claims-hide-fail-open-edges — 1023회 통과 엣지 제거)
                TripFreeze("TossLiveBroker ring 단일 생산자 계약 위반 — 제어흐름 오염 fail-closed: " + ex.Message);
                _running = false;
            }
        }

        // ── 파싱/변환 오염 누적 — 임계 도달 시 fail-closed(콜드 경로). 생산자 충돌과 카운터 분리 ──────────
        private void OnParseFail()
        {
            long n = Interlocked.Increment(ref _parseFails);
            // 핫패스에서는 카운터만(할당 0). ★교정: '==' 정확 일치 → '>=' + 1회 트립 래치(_parseFreezeTripped).
            //   단일 펌프 스레드라 경합은 없으나, 임계 직전 사유 혼입으로 인한 '영영 미발생' 회귀를 구조적 봉쇄.
            //   생산자 충돌은 이 경로로 흡수되지 않음(IngestFrame 에서 즉시 TripFreeze) → 임계 오염 혼입 제거.
            if (n >= _parseFailFreezeThreshold && Interlocked.Exchange(ref _parseFreezeTripped, 1) == 0)
                TripFreeze("TossLiveBroker 파싱 실패 임계 초과 — 인입 오염 fail-closed");
        }

        // ── fail-closed 동결 트립(콜드 패스·할당 허용) ─────────────────────────
        //   ForceFreeze 는 멱등·감사 append(할당 허용·핫패스 아님). att null 이면 카운터만(테스트 격리).
        private void TripFreeze(string reason)
        {
            Interlocked.Increment(ref _frozenTrips);
            try { _att?.ForceFreeze(reason); }
            catch { /* 동결 실패해도 트립 계상은 정직 유지 */ }
        }

        // 콜드 패스 백오프 sleep — _running 취소에 반응(정지 지연 차단). 핫패스 아님.
        private void SleepBounded(int totalMs)
        {
            int slept = 0;
            while (slept < totalMs && _running)
            {
                int chunk = Math.Min(100, totalMs - slept);
                Thread.Sleep(chunk);
                slept += chunk;
            }
        }

        public void Dispose() => StopIngress();
    }

    // ========================================================================
    // ITossTransport 의 실(實) HttpClient/WebSocket 어댑터 골격 — 인증/엔드포인트 미확정(openQuestions)
    //   ★liveArmed:false 불변: 본 어댑터는 '구성'만 — 자동 배선·실 키 로드는 호출부 옵트인.
    // ========================================================================
    /// <summary>
    /// 실 토스증권 운반체 골격. REST = HttpClient(동기 Send), WS = ITossSocket 추상(미구현 시 throw).
    /// ★실 엔드포인트·OAuth 토큰·WS wss URL·서명 스킴 미확정 → openQuestions. 본 골격은 컴파일·구조만.
    /// </summary>
    public sealed class TossHttpTransport : ITossTransport, IDisposable
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private readonly string  _baseUrl;       // 실 REST 베이스(openQuestions)
        private readonly Func<string>? _bearer;  // OAuth 토큰 공급(openQuestions — 미주입 시 인증 헤더 생략)
        private readonly ITossSocket? _socket;   // WS 추상(미주입 시 ConnectAndSubscribe throw → fail-closed)
        private readonly OrderAttestationService? _att;  // ★인증 실패(401/403) fail-closed 채널

        public TossHttpTransport(string baseUrl, Func<string>? bearerTokenProvider = null, ITossSocket? socket = null,
                                 OrderAttestationService? attestation = null)
        {
            _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.toss-invest.example" : baseUrl;
            _bearer  = bearerTokenProvider;
            _socket  = socket;
            _att     = attestation;
        }

        public (bool ok, string body) PostOrder(string pathAndQuery, string jsonBody)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + pathAndQuery)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
            };
            AttachAuth(req);
            return SendRead(req);
        }

        public void DeleteOrder(string orderId)
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, _baseUrl + "/v1/orders/" + Uri.EscapeDataString(orderId));
            AttachAuth(req);
            _ = SendRead(req);
        }

        public decimal QueryHeldQty(PaperAsset asset, string symbol)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                _baseUrl + "/v1/accounts/holdings?symbol=" + Uri.EscapeDataString(symbol));
            AttachAuth(req);
            var (ok, body) = SendRead(req);
            if (!ok) return 0m;
            try
            {
                using var doc = JsonDocument.Parse(body);
                // 실 응답 스키마 미확정(openQuestions) — 보수적으로 'qty' 키 탐색.
                return doc.RootElement.TryGetProperty("qty", out var q) && q.TryGetDecimal(out var v) ? v : 0m;
            }
            catch { return 0m; }
        }

        public bool TryReceive(Action<ReadOnlyMemory<byte>> onFrame, int timeoutMs)
        {
            if (_socket is null) throw new InvalidOperationException("TossHttpTransport: WS 소켓 미주입 — 인입 불가");
            return _socket.TryReceive(onFrame, timeoutMs);
        }

        public void ConnectAndSubscribe()
        {
            if (_socket is null) throw new InvalidOperationException("TossHttpTransport: WS 소켓 미주입 — 연결 불가(fail-closed)");
            _socket.ConnectAndSubscribe();
        }

        public void Disconnect()
        {
            try { _socket?.Disconnect(); } catch { /* 흡수 */ }
        }

        private void AttachAuth(HttpRequestMessage req)
        {
            var token = _bearer?.Invoke();
            if (!string.IsNullOrEmpty(token))
                req.Headers.TryAddWithoutValidation("authorization", "Bearer " + token);
        }

        private (bool ok, string body) SendRead(HttpRequestMessage req)
        {
            using var res = Http.Send(req);
            var body = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            // ★인증 실패 fail-closed: 401/403 은 재시도로 방치하지 않고 즉시 동결(토큰 만료·서명 실패 표면).
            if (IsAuthFailureStatus(res.StatusCode))
                TripAuthFreeze("Toss Auth Failure — REST " + (int)res.StatusCode);
            return (res.IsSuccessStatusCode, body);
        }

        /// <summary>401 Unauthorized·403 Forbidden 은 인증 실패로 간주(fail-closed 대상).</summary>
        public static bool IsAuthFailureStatus(System.Net.HttpStatusCode code)
            => code == System.Net.HttpStatusCode.Unauthorized || code == System.Net.HttpStatusCode.Forbidden;

        private void TripAuthFreeze(string reason)
        {
            try { _att?.ForceFreeze(reason); } catch { /* 동결 실패해도 진행 흐름 오염 금지 */ }
        }

        public void Dispose() => Disconnect();
    }

    /// <summary>WS 소켓 추상 — 실 wss 핸드셰이크/프레임 조립을 격리(openQuestions). ClientWebSocket 구현체는 별도.</summary>
    public interface ITossSocket
    {
        void ConnectAndSubscribe();
        bool TryReceive(Action<ReadOnlyMemory<byte>> onFrame, int timeoutMs);
        void Disconnect();
    }
}
