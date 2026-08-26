// ============================================================================
// TossProduction — 과제 1: 토스증권 실 WSS 수신부(ITossSocket) + 프로덕션 스택 조립기
//
//  ⚠️⚠️ 실 자금 집행 표면. liveArmed:false 불변 — 본 파일은 '구성'만. 자동 배선·자동 무장·자동
//        인그레스 시작을 절대 하지 않는다. 실 엔드포인트(wss/REST)·구독 스키마·서명은 주입(openQuestions).
//
//  ── δ=0 인입(과제 §1.2) ─────────────────────────────────────────────────────
//   ClientWebSocket 수신 바이트를 '재사용 버퍼(_rx)'에 누적하고, 완성 프레임을 ReadOnlyMemory<byte>
//   로 onFrame 에 전달한다. 문자열 변환(ToString) 0 — onFrame(=TossLiveBroker.IngestFrame)이
//   ReadOnlySpan<byte> 를 ZeroAllocJsonParser 에 다이렉트 주입한다. 수신은 pending-receive 패턴으로
//   타임아웃 시 소켓을 abort 하지 않는다(WS ReceiveAsync 취소는 소켓 파괴 → 재사용 유지가 정답).
//
//  ── 인증 실패 fail-closed(과제 §1.3) ────────────────────────────────────────
//   · 연결 전 토큰 공백(만료) → 미인증 연결 자체를 거부(ForceFreeze + throw). 모호한 재시도 금지.
//   · 핸드셰이크 401/403(ClientWebSocket.HttpStatusCode) → ForceFreeze("Toss Auth Failure").
//   · 서버 정책 종료(WebSocketCloseStatus.PolicyViolation) → ForceFreeze(인증/구독 위반 표면).
// ============================================================================
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
{
    /// <summary>실 WSS 시세 수신부. ITossSocket 구현 — TossHttpTransport 에 주입되어 인입 파이프를 완성한다.</summary>
    public sealed class TossProductionSocket : ITossSocket, IDisposable
    {
        private readonly Uri                     _wssUrl;
        private readonly Func<string>            _bearer;        // 토큰 공급(TossTokenService.CurrentAccessToken)
        private readonly Func<string>?           _subscribeJson; // 구독 페이로드 빌더(openQuestions 스키마)
        private readonly OrderAttestationService? _att;
        private readonly byte[]                  _rx = new byte[64 * 1024];  // 재사용 수신 버퍼(δ=0)

        private ClientWebSocket?                 _ws;
        private Task<WebSocketReceiveResult>?    _pending;   // 진행 중 ReceiveAsync(타임아웃 시 유지)
        private int                              _rxLen;     // 진행 중 메시지 누적 바이트

        public TossProductionSocket(Uri wssUrl, Func<string> bearer,
                                    OrderAttestationService? attestation = null, Func<string>? subscribeJson = null)
        {
            _wssUrl        = wssUrl ?? throw new ArgumentNullException(nameof(wssUrl));
            _bearer        = bearer ?? throw new ArgumentNullException(nameof(bearer));
            _att           = attestation;
            _subscribeJson = subscribeJson;
        }

        public void ConnectAndSubscribe()
        {
            // ★fail-closed(§1.3): 토큰 공백(미취득/만료)이면 미인증 연결을 아예 열지 않는다.
            string token = _bearer();
            if (string.IsNullOrEmpty(token))
            {
                TripAuth("Toss Auth Failure — 토큰 없음(미인증 WSS 연결 거부)");
                throw new InvalidOperationException("TossProductionSocket: 유효 토큰 없음 — 연결 거부(fail-closed)");
            }

            var ws = new ClientWebSocket();
            // ★net8.0: HttpStatusCode 는 CollectHttpResponseDetails=true 일 때만 채워진다(기본 false → 0/None).
            //   이걸 켜지 않으면 401/403 핸드셰이크가 IsAuthFailureStatus(0)=false 로 오분류되어 인증 동결이 죽는다.
            ws.Options.CollectHttpResponseDetails = true;
            ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
            try
            {
                ws.ConnectAsync(_wssUrl, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // 인증 실패(401/403) 즉시 동결. ★그 외 예외(타임아웃 등)도 소켓 누수 방지 위해 반드시 Dispose 후 재전파.
                if (TossHttpTransport.IsAuthFailureStatus(ws.HttpStatusCode))
                    TripAuth("Toss Auth Failure — WSS 핸드셰이크 " + (int)ws.HttpStatusCode);
                ws.Dispose();
                throw;   // 상위 펌프가 재연결/한도 판단(인증 실패는 위에서 이미 동결)
            }

            _ws = ws;
            _rxLen = 0; _pending = null;

            if (_subscribeJson is { } build)
            {
                byte[] sub = Encoding.UTF8.GetBytes(build());
                ws.SendAsync(new ArraySegment<byte>(sub), WebSocketMessageType.Text, true, CancellationToken.None)
                  .GetAwaiter().GetResult();
            }
        }

        public bool TryReceive(Action<ReadOnlyMemory<byte>> onFrame, int timeoutMs)
        {
            var ws = _ws;
            if (ws is null || ws.State != WebSocketState.Open)
                throw new InvalidOperationException("TossProductionSocket: WSS 미연결");

            while (true)
            {
                // pending 유지: 타임아웃으로 이전에 미완료였다면 그 ReceiveAsync 를 이어서 대기(소켓 abort 없음).
                // ★로컬로 캡처: Disconnect(다른 스레드)가 _pending 을 null 로 만들어도 check-then-act NRE 가 없도록.
                var pending = _pending ??= ws.ReceiveAsync(new ArraySegment<byte>(_rx, _rxLen, _rx.Length - _rxLen), CancellationToken.None);

                bool completed;
                try { completed = pending.Wait(timeoutMs); }
                catch { _pending = null; throw; }   // 소켓 오류 → 상위 펌프 재연결/한도→동결
                if (!completed) return false;        // 타임아웃(정상) — pending 유지, 다음 호출에 이어감

                WebSocketReceiveResult res = pending.GetAwaiter().GetResult();
                _pending = null;

                if (res.MessageType == WebSocketMessageType.Close)
                {
                    if (res.CloseStatus == WebSocketCloseStatus.PolicyViolation)
                        TripAuth("Toss Auth Failure — 서버 정책 종료(인증/구독 위반)");
                    throw new InvalidOperationException("TossProductionSocket: WSS 종료 " + res.CloseStatus);
                }

                _rxLen += res.Count;
                if (!res.EndOfMessage)
                {
                    if (_rxLen >= _rx.Length) { _rxLen = 0; throw new InvalidOperationException("TossProductionSocket: WSS 프레임 초과"); }
                    continue;   // 같은 메시지의 다음 조각 대기
                }

                int len = _rxLen; _rxLen = 0;
                onFrame(new ReadOnlyMemory<byte>(_rx, 0, len));   // ★δ=0: 재사용 버퍼·문자열 변환 0
                return true;
            }
        }

        public void Disconnect()
        {
            var ws = _ws;
            _ws = null; _pending = null; _rxLen = 0;
            if (ws is null) return;
            try
            {
                if (ws.State == WebSocketState.Open)
                    ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).GetAwaiter().GetResult();
            }
            catch { /* 종료는 best-effort */ }
            finally { ws.Dispose(); }
        }

        private void TripAuth(string reason) { try { _att?.ForceFreeze(reason); } catch { } }

        public void Dispose() => Disconnect();
    }

    /// <summary>
    /// 토스증권 프로덕션 스택 조립 산출물. ★수동 제어: 호출부가 명시적으로 Tokens.Start() 로 토큰 갱신을
    /// 켜고, Raw.StartIngress() 로 시세 인입을 시작한다(자동 없음). Broker 는 ShadowClearingBroker(gate)로
    /// 감싼 결과(옵션). liveArmed:false — 실 송출은 여전히 무장 이중키·점화 FSM 통제 하.
    /// </summary>
    public readonly record struct TossProductionStack(
        IBroker Broker, TossLiveBroker Raw, TossTokenService Tokens, TossProductionSocket Socket);

    /// <summary>토스 프로덕션 스택 순수 조립기(옵트인 전용·자동 배선/무장/시작 없음).</summary>
    public static class TossProductionAssembler
    {
        /// <summary>
        /// 토큰서비스 → HttpTransport(bearer+att) → WSS 소켓 → TossLiveBroker → (옵션)ShadowClearingBroker 조립.
        /// 실 엔드포인트·자격증명·구독 스키마는 인자로 주입(openQuestions). 어떤 것도 자동 시작하지 않는다.
        /// </summary>
        public static TossProductionStack Build(
            string restBaseUrl,
            Uri wssUrl,
            ITossAuthClient authClient,
            OrderDispatchRing signalRing,
            Func<long> clock,
            OrderAttestationService? attestation = null,
            ShadowCutoverGate? shadowGate = null,
            Func<string>? subscribeJson = null,
            long genomeId = 0,
            int maxReconnects = 5,
            CpuAffinityPinningGrid? pinGrid = null)   // v7.3.0 — 주입 시 인입 펌프 스레드 P-Core 자기 핀(라이브 경로 옵트인)
        {
            if (authClient is null) throw new ArgumentNullException(nameof(authClient));
            if (signalRing is null) throw new ArgumentNullException(nameof(signalRing));
            if (clock       is null) throw new ArgumentNullException(nameof(clock));

            var tokens = new TossTokenService(authClient, clock, attestation);
            var socket = new TossProductionSocket(wssUrl, tokens.CurrentAccessToken, attestation, subscribeJson);
            var transport = new TossHttpTransport(restBaseUrl, tokens.CurrentAccessToken, socket, attestation);
            var raw = new TossLiveBroker(transport, signalRing, clock, attestation, genomeId, maxReconnects, pinGrid: pinGrid);
            IBroker broker = shadowGate is null ? raw : new ShadowClearingBroker(raw, shadowGate, attestation);
            return new TossProductionStack(broker, raw, tokens, socket);
        }
    }
}
