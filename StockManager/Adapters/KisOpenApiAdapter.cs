// ============================================================================
// KisOpenApiAdapter — [KIS · 추천 A] 한국투자증권 실시간 WebSocket/REST 수신 어댑터 + OAuth 토큰 수명주기 + 세션 단절 인터락(방향 B)
//
//  구성:
//   · KisOrderIdSpace — 다중 브로커 주문번호 충돌 방지. OrderExecutionFeedAdapter 는 '브로커 주문번호' 하나로 슬롯을 찾는데,
//     키움(호스트 발급 클라이언트 ID)과 KIS(ODNO 10자리)가 같은 숫자를 낼 수 있다 → KIS 번호는 비트 48 네임스페이스로 사상.
//   · KisTokenLifecycle — 접근 토큰을 문자열이 아닌 고정(pinned) 바이트 버퍼에 보관(교체 시 이전 값 0x00) · 서버 만료(expires_in ·
//     access_token_token_expired)를 그대로 사용 · 만료 5분 전부터 갱신 대상 · 그 구간에서 갱신 실패 또는 만료 = KisAuth 비트(Disarm).
//     새 토큰은 FinancialPiiMasker/RawCredentialLeakInterlock 에 등록(로그 마스킹 · 유출 감지). 송신 헤더는 TryWriteBearer(δ=0).
//   · KisSessionInterlock(방향 B) — 실시간 세션의 마지막 수신(PINGPONG 포함) 이후 침묵이 임계를 넘거나 연결 끊김이 보고되면
//     KisSession 비트(Disarm) + 스위치보드에서 KIS 브로커 블랙리스트(신규 주문은 다른 브로커로만 · 없으면 차단).
//     ★지시서의 'KIS 전용 추적 슬롯 회수'는 하지 않는다: 세션이 끊겨도 KIS 서버의 주문은 살아 있을 수 있다 — 슬롯을 지우면 그 노출을
//       영영 못 보고, 잔량을 다른 브로커로 재송출하면 이중 체결 위험(CLAUDE.md §2 큐잉·자동 재시도 금지). 슬롯은 유지(장기 TTL 대사)하고
//       '미확인 노출 건수'만 증거로 남긴다.
//   · KisOpenApiAdapter — 프레임 1건 처리: 제어(PINGPONG → 받은 바이트 그대로 반송) · H0STCNT0 → TickPayload ·
//     H0STCNI0(암호문이면 AES 복호) → FillPayload(KIS 네임스페이스 주문번호 · 브로커 잔량 미상 = 대조 생략). 형식 오류 = 폐기 + 계수.
//  δ=0: 프레임 처리·토큰 판정·헤더 작성·세션 판정은 사전 할당 버퍼만 사용(테스트 실측). 토큰 '갱신'(HTTP · 마스커 등록)은 콜드.
//  ★네트워크 미결선(정직): 실제 WebSocket/HTTP 송수신 루프는 이 파일에 없다 — 전송 계층은 델리게이트로 주입(liveArmed:false ·
//    이 호스트에 KIS 실 연결 경로 0). 실 연동은 모의(VTS) 도메인 검증 후 운용자 결선.
// ============================================================================
using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Adapters
{
    public sealed class KisTokenLifecycle : IArmingBlockSource, IDisposable
    {
        public static readonly TimeSpan DefaultRefreshLead = TimeSpan.FromMinutes(5);
        private static ReadOnlySpan<byte> BearerPrefix => "Bearer "u8;

        private readonly Func<Memory<byte>, CancellationToken, Task<int>> _fetchTokenJson;
        private readonly Func<long> _nowUtcTicks;
        private readonly long _leadTicks;
        private readonly byte[] _token, _response, _stage;   // 전부 pinned · 교체/실패 시 0x00
        private readonly object _gate = new();
        private readonly Action<string>? _onNewTokenText;
        private ArmingInterlockLink? _link;
        private int _len, _lastRefreshFailed = 1, _blocked = 1, _refreshing;
        private long _expiresUtcTicks, _refreshes, _failures, _evaluations;

        /// <param name="fetchTokenJson">토큰 엔드포인트 응답 JSON 을 버퍼에 쓰고 길이 반환(실패 = ≤0 또는 예외). 전송 계층 주입.</param>
        /// <param name="onNewTokenText">새 토큰 문자열(콜드 1회) — 마스커·유출 감지 등록용. 미지정 시 기본 등록.</param>
        public KisTokenLifecycle(Func<Memory<byte>, CancellationToken, Task<int>> fetchTokenJson, Func<long>? nowUtcTicks = null,
                                 TimeSpan? refreshLead = null, int maxTokenBytes = 2048, Action<string>? onNewTokenText = null)
        {
            _fetchTokenJson = fetchTokenJson ?? throw new ArgumentNullException(nameof(fetchTokenJson));
            _nowUtcTicks = nowUtcTicks ?? (static () => DateTime.UtcNow.Ticks);
            var lead = refreshLead ?? DefaultRefreshLead;
            if (lead <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(refreshLead));
            _leadTicks = lead.Ticks;
            _token = GC.AllocateArray<byte>(Math.Max(64, maxTokenBytes), pinned: true);
            _response = GC.AllocateArray<byte>(Math.Max(1024, maxTokenBytes + 512), pinned: true);
            _stage = GC.AllocateArray<byte>(_token.Length, pinned: true);
            _onNewTokenText = onNewTokenText;
        }

        public bool ArmingBlocked => Volatile.Read(ref _blocked) == 1;
        public long Refreshes => Interlocked.Read(ref _refreshes);
        public long Failures => Interlocked.Read(ref _failures);
        public long ExpiresUtcTicks { get { lock (_gate) return _expiresUtcTicks; } }
        public bool HasToken { get { lock (_gate) return _len > 0; } }

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        /// <summary>갱신 필요(토큰 없음 · 만료 5분 전 구간 진입) — δ=0.</summary>
        public bool NeedsRefresh()
        {
            long now = _nowUtcTicks();
            lock (_gate) return _len == 0 || now >= _expiresUtcTicks - _leadTicks;
        }

        /// <summary>
        /// 무장 판정 갱신(δ=0 · 주기 호출). 차단 = 토큰 없음 ∨ 만료 ∨ (만료 5분 전 구간 ∧ 최근 갱신 실패).
        /// 갱신 성공이 확인되기 전까지 '임박 구간의 미갱신'을 허용하지 않는다(만료 순간 송출 중단보다 앞선 fail-closed).
        /// </summary>
        public void Evaluate()
        {
            Interlocked.Increment(ref _evaluations);
            long now = _nowUtcTicks();
            bool blocked;
            lock (_gate)
                blocked = _len == 0 || now >= _expiresUtcTicks
                          || (now >= _expiresUtcTicks - _leadTicks && Volatile.Read(ref _lastRefreshFailed) == 1);
            SetBlocked(blocked);
        }

        /// <summary>토큰 갱신(콜드 · 비동기 · 중복 호출은 1회만 진행). 반환 = 성공.</summary>
        public async Task<bool> RefreshAsync(CancellationToken ct = default)
        {
            if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return false;
            try
            {
                int n;
                try { n = await _fetchTokenJson(_response, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { n = -1; }
                if (n <= 0 || n > _response.Length || !Commit(n)) return Fail();
                CryptographicOperations.ZeroMemory(_response);
                Volatile.Write(ref _lastRefreshFailed, 0);
                Interlocked.Increment(ref _refreshes);
                Evaluate();
                return true;
            }
            finally { Volatile.Write(ref _refreshing, 0); }
        }

        // 응답 판독 → 검증 통과 시에만 기존 토큰 교체(실패 시 기존 토큰 유지 · 스테이징 0x00)
        private bool Commit(int n)
        {
            long now = _nowUtcTicks();
            var stage = _stage.AsSpan();
            try
            {
                if (!KisProtocol.TryParseToken(_response.AsSpan(0, n), stage, out int len, out long expiresIn, out long expiredAtKst)) return false;
                long expiry = now + expiresIn * TimeSpan.TicksPerSecond;
                if (expiredAtKst > 0)
                {
                    long kstAsUtc = expiredAtKst - TimeSpan.FromHours(9).Ticks;    // KST = UTC+9(일광절약 없음)
                    if (kstAsUtc < expiry) expiry = kstAsUtc;                      // 둘 중 이른 쪽(보수)
                }
                if (expiry <= now) return false;
                string text;
                lock (_gate)
                {
                    CryptographicOperations.ZeroMemory(_token);
                    stage[..len].CopyTo(_token);
                    _len = len;
                    _expiresUtcTicks = expiry;
                    text = System.Text.Encoding.ASCII.GetString(stage[..len]);   // 콜드 1회 — 마스커/유출 감지 등록용
                }
                try
                {
                    if (_onNewTokenText is { } cb) cb(text);
                    else { FinancialPiiMasker.RegisterSecret(text); RawCredentialLeakInterlock.Shared.RegisterCredential(text); }
                }
                catch (Exception) { /* 등록 실패는 갱신 성공에 무영향 — 마스커 JWT 패턴이 2차 방어 */ }
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(stage); }
        }

        /// <summary>"Bearer &lt;token&gt;" 를 dst 에 기록(δ=0). 토큰 없음/만료/버퍼 부족 = −1(송신 금지).</summary>
        public int TryWriteBearer(Span<byte> dst)
        {
            long now = _nowUtcTicks();
            lock (_gate)
            {
                if (_len == 0 || now >= _expiresUtcTicks || dst.Length < BearerPrefix.Length + _len) return -1;
                BearerPrefix.CopyTo(dst);
                _token.AsSpan(0, _len).CopyTo(dst[BearerPrefix.Length..]);
                return BearerPrefix.Length + _len;
            }
        }

        public void Zeroize()
        {
            lock (_gate) { CryptographicOperations.ZeroMemory(_token); _len = 0; _expiresUtcTicks = 0; }
            CryptographicOperations.ZeroMemory(_response);
            SetBlocked(true);
        }

        public void Dispose() => Zeroize();

        private bool Fail()
        {
            Volatile.Write(ref _lastRefreshFailed, 1);
            Interlocked.Increment(ref _failures);
            CryptographicOperations.ZeroMemory(_response);
            Evaluate();
            return false;
        }

        private void SetBlocked(bool blocked)
        {
            int v = blocked ? 1 : 0;
            if (Interlocked.Exchange(ref _blocked, v) != v) Volatile.Read(ref _link)?.Resync();
        }
    }

    /// <summary>[방향 B] KIS 실시간 세션 단절 인터락 — 침묵 타임아웃/끊김 보고 → Disarm 비트 + BrokerFailoverSentinel.ReportFault(Disconnect)
    /// (기존 다중 브로커 설계 그대로: 라우터 sticky Disarm · KIS 블랙리스트 · 활성 브로커 CAS 전환 · KIS 추적 슬롯 캐스케이드 취소 상태 전환).</summary>
    public sealed class KisSessionInterlock : IArmingBlockSource
    {
        public const long DefaultSilenceMicros = 60_000_000;   // PINGPONG 주기보다 충분히 긴 침묵(공개 문서상 서버 주도 핑 — 실측 후 조정)

        private readonly BrokerFailoverSentinel? _failover;
        private readonly int _kisBroker;
        private readonly long _silenceMicros;
        private readonly Func<long> _nowMicros;
        private readonly Func<int>? _openKisOrders;
        private ArmingInterlockLink? _link;
        private long _lastActivity, _trips, _orphanAtTrip, _pings;
        private int _connected, _tripped;

        /// <param name="openKisOrders">트립 순간 KIS 미체결(추적 유지) 건수 — 증거용(회수·재송출 없음).</param>
        /// <param name="marketHours">[방향 B] 장중 판정(예: KST 09:00~15:20 · 주말 제외) — 장중엔 marketSilenceMicros(기본 3초)로 단축.
        ///   ★전제: 체결통보(H0STCNI0)만 구독하면 장중에도 대부분 침묵이다 — 심박용 체결가(H0STCNT0) 유동 종목 구독이 함께 있어야 의미가 있다.</param>
        public KisSessionInterlock(BrokerFailoverSentinel? failover, int kisBrokerIndex, Func<long>? nowMicros = null,
                                   long silenceMicros = DefaultSilenceMicros, Func<int>? openKisOrders = null,
                                   Func<bool>? marketHours = null, long marketSilenceMicros = DefaultMarketSilenceMicros)
        {
            if (silenceMicros <= 0) throw new ArgumentOutOfRangeException(nameof(silenceMicros));
            if (marketSilenceMicros <= 0) throw new ArgumentOutOfRangeException(nameof(marketSilenceMicros));
            _failover = failover; _kisBroker = kisBrokerIndex; _silenceMicros = silenceMicros;
            _nowMicros = nowMicros ?? ReferencePriceFeedAdapter.MonotonicMicros;
            _openKisOrders = openKisOrders;
            _marketHours = marketHours; _marketSilenceMicros = Math.Min(marketSilenceMicros, silenceMicros);
        }

        public const long DefaultMarketSilenceMicros = 3_000_000;
        private readonly Func<bool>? _marketHours;
        private readonly long _marketSilenceMicros;

        /// <summary>KST 정규장(09:00~15:20 · 월~금) 판정 — 공휴일 미반영(정직: 휴장일엔 접속 자체가 없어야 정상).</summary>
        public static bool IsKrxRegularSession(DateTime utcNow)
        {
            var kst = utcNow.AddHours(9);
            if (kst.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
            int m = kst.Hour * 60 + kst.Minute;
            return m >= 9 * 60 && m < 15 * 60 + 20;
        }

        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        public bool Connected => Volatile.Read(ref _connected) == 1;
        /// <summary>미연결(최초 포함)도 차단 — 세션이 수립되고 활동이 확인돼야 KIS 경로 무장 가능.</summary>
        public bool ArmingBlocked => Tripped || !Connected;
        public long Trips => Interlocked.Read(ref _trips);
        public long OrphanOrdersAtTrip => Interlocked.Read(ref _orphanAtTrip);
        public long PingPongs => Interlocked.Read(ref _pings);

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        public void MarkConnected()
        {
            Volatile.Write(ref _lastActivity, _nowMicros());
            if (Interlocked.Exchange(ref _connected, 1) == 0) Volatile.Read(ref _link)?.Resync();
        }

        public void MarkDisconnected()
        {
            Volatile.Write(ref _connected, 0);
            Trip();
        }

        /// <summary>수신 활동(모든 프레임 · δ=0).</summary>
        public void OnActivity(bool pingPong)
        {
            Volatile.Write(ref _lastActivity, _nowMicros());
            if (pingPong) Interlocked.Increment(ref _pings);
        }

        /// <summary>주기 판정(δ=0) — 연결 상태에서 침묵 임계 초과 = 트립.</summary>
        public void Evaluate()
        {
            if (Volatile.Read(ref _connected) != 1) return;
            bool market;
            try { market = _marketHours?.Invoke() ?? false; } catch { market = true; }   // 판정 불가 = 장중 취급(엄격 측)
            long limit = market ? _marketSilenceMicros : _silenceMicros;
            if (_nowMicros() - Volatile.Read(ref _lastActivity) > limit) Trip();
        }

        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired) || Volatile.Read(ref _connected) == 0) return false;
            Volatile.Write(ref _tripped, 0);
            Volatile.Write(ref _lastActivity, _nowMicros());
            Volatile.Read(ref _link)?.Resync();
            return true;
        }

        private void Trip()
        {
            Interlocked.Increment(ref _trips);
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) != 0) { Volatile.Read(ref _link)?.Resync(); return; }
            Volatile.Read(ref _link)?.Resync();                                  // ① 차단 선반영
            try { _failover?.ReportFault(_kisBroker, BrokerFaultKind.Disconnect, 0, _nowMicros() * 10); } catch { }   // ② 라우터 Disarm · KIS 블랙리스트 · 페일오버
            try { Interlocked.Exchange(ref _orphanAtTrip, _openKisOrders?.Invoke() ?? 0); } catch { }   // ③ 증거(회수 없음)
        }
    }

    public sealed class KisOpenApiAdapter : IDisposable
    {
        private readonly KisSessionInterlock _session;
        private readonly Func<ReadOnlyMemory<byte>, bool>? _send;
        private readonly Action<TickPayload>? _onTick;
        private readonly Action<FillPayload>? _onFill;
        private readonly Func<long> _nowTicks;
        private readonly KisTick[] _ticks = new KisTick[KisProtocol.MaxRecordsPerFrame];
        private readonly byte[] _cipher = GC.AllocateArray<byte>(8192, pinned: true), _plain = GC.AllocateArray<byte>(8192, pinned: true);
        private KisNoticeCipher? _cipherCtx;
        private long _frames, _ticksEmitted, _fillsEmitted, _malformed, _echoes, _noKey, _subscribeAcks, _subscribeRejects;
        private readonly byte[] _ackKey = new byte[32], _ackIv = new byte[16];
        public long SubscribeAcks => Interlocked.Read(ref _subscribeAcks);
        public long SubscribeRejects => Interlocked.Read(ref _subscribeRejects);
        public bool HasNoticeKeys => Volatile.Read(ref _cipherCtx) is not null;

        /// <param name="send">PINGPONG 반송(받은 바이트 그대로 · 전송 계층 주입). 반환 = 송신 성공.</param>
        public KisOpenApiAdapter(KisSessionInterlock session, Func<ReadOnlyMemory<byte>, bool>? send = null,
                                 Action<TickPayload>? onTick = null, Action<FillPayload>? onFill = null, Func<long>? nowTicks = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _send = send; _onTick = onTick; _onFill = onFill;
            _nowTicks = nowTicks ?? (static () => DateTime.UtcNow.Ticks);
        }

        public long Frames => Interlocked.Read(ref _frames);
        public long TicksEmitted => Interlocked.Read(ref _ticksEmitted);
        public long FillsEmitted => Interlocked.Read(ref _fillsEmitted);
        public long Malformed => Interlocked.Read(ref _malformed);
        public long Echoes => Interlocked.Read(ref _echoes);
        public long NoticeWithoutKey => Interlocked.Read(ref _noKey);

        /// <summary>체결통보 복호 키(구독 응답 output.key 32B · output.iv 16B · 콜드 1회).</summary>
        public void SetNoticeKeys(ReadOnlySpan<byte> key32, ReadOnlySpan<byte> iv16)
        {
            Interlocked.Exchange(ref _cipherCtx, new KisNoticeCipher(key32, iv16))?.Dispose();
        }

        /// <summary>WebSocket 텍스트 프레임 1건(δ=0). 반환 = 인식·처리 여부.</summary>
        public bool OnFrame(ReadOnlyMemory<byte> frame)
        {
            Interlocked.Increment(ref _frames);
            var span = frame.Span;
            if (KisProtocol.IsControlFrame(span))
            {
                bool ping = KisProtocol.IsPingPong(span);
                _session.OnActivity(ping);
                if (ping && _send is { } send)
                {
                    try { if (send(frame)) Interlocked.Increment(ref _echoes); } catch (Exception) { }
                    return true;
                }
                // [KIS 실배선 · 실결함 교정] 수정 전: 구독 응답(체결통보 복호 key/iv)을 버렸다 — 운영에 SetNoticeKeys 호출부가 없어
                //   암호화 체결통보가 전량 '키 없음' 폐기(NoticeWithoutKey)됐다. 이제 체결통보 TR 구독 성공 응답이면 키를 자동 장착(콜드 1회).
                if (!ping && KisProtocol.TryParseSubscribeAck(span, _ackKey, _ackIv, out bool ok, out bool isNotice, out bool hasKeys))
                {
                    if (!ok) { Interlocked.Increment(ref _subscribeRejects); return false; }
                    Interlocked.Increment(ref _subscribeAcks);
                    if (isNotice && hasKeys) SetNoticeKeys(_ackKey, _ackIv);
                    return true;
                }
                return ping;
            }
            _session.OnActivity(false);
            if (!KisProtocol.TryParseHeader(span, out bool encrypted, out var trId, out int count, out var body)) return Bad();
            if (KisProtocol.IsTickTr(trId))
            {
                if (encrypted) return Bad();
                int n = KisProtocol.ParseTicks(body, count, _ticks);
                if (n == 0) return Bad();
                long ts = _nowTicks();
                for (int i = 0; i < n; i++)
                {
                    ref var t = ref _ticks[i];
                    var p = new TickPayload { SymbolCode = t.SymbolCode, Price = t.Price, Volume = t.AccVolume, ChangeRate = 0, TimestampTicks = ts };
                    try { _onTick?.Invoke(p); } catch (Exception) { }
                }
                Interlocked.Add(ref _ticksEmitted, n);
                return true;
            }
            if (KisProtocol.IsNoticeTr(trId))
            {
                ReadOnlySpan<byte> plain = body;
                if (encrypted)
                {
                    var cipher = Volatile.Read(ref _cipherCtx);
                    if (cipher is null) { Interlocked.Increment(ref _noKey); return Bad(); }
                    int len = cipher.TryDecrypt(body, _cipher, _plain);
                    if (len <= 0) return Bad();
                    plain = _plain.AsSpan(0, len);
                }
                if (!KisProtocol.TryParseNotice(plain, out var n)) return Bad();
                // 취소/정정 확인은 새 주문번호로 오고 원주문번호가 실려 온다 — 추적 슬롯은 원주문 기준
                long odno = n.Kind is KisNoticeKind.CancelConfirmed or KisNoticeKind.Amended && n.OrigOrderNo > 0 ? n.OrigOrderNo : n.OrderNo;
                var status = n.Kind switch
                {
                    KisNoticeKind.Filled => BridgeFillStatus.Filled,
                    KisNoticeKind.Rejected => BridgeFillStatus.Rejected,
                    KisNoticeKind.CancelConfirmed => BridgeFillStatus.CancelConfirmed,
                    _ => BridgeFillStatus.Accepted,
                };
                var f = new FillPayload
                {
                    OrderId = KisOrderIdSpace.Encode(odno), SymbolCode = n.SymbolCode,
                    FillPrice = n.Kind == KisNoticeKind.Filled ? n.FillPrice : 0, FillQty = n.Kind == KisNoticeKind.Filled ? n.FillQty : 0,
                    Side = n.Side, TimestampTicks = _nowTicks(),
                    Status = BridgeFillStatusCodec.Encode(status, false), RemainingQty = 0,   // KIS 통보엔 미체결 잔량 없음 → 대조 생략
                };
                if (f.OrderId == 0) return Bad();
                try { _onFill?.Invoke(f); } catch (Exception) { }
                Interlocked.Increment(ref _fillsEmitted);
                return true;
            }
            return false;                                                          // 미구독 TR — 무시(오류 아님)
        }

        private bool Bad() { Interlocked.Increment(ref _malformed); return false; }

        public void Dispose()
        {
            Interlocked.Exchange(ref _cipherCtx, null)?.Dispose();
            CryptographicOperations.ZeroMemory(_plain);
        }
    }
}
