// ============================================================================
// LanStatusServer - 읽기전용 LAN 상태 서버 (외부 의존 0 · System.Net.HttpListener) (v3.11)
//  "내 PC = 개인 금융 서버" — 같은 와이파이의 폰 브라우저에서 포트폴리오 현황만 조회.
//  · 옵트인(Lan.Enabled), PIN 게이트(Lan.Pin). 주문/설정 변경 엔드포인트 없음(읽기전용).
//  · LAN 노출엔 Windows urlacl(관리자) 필요 → 실패 시 localhost 폴백 + 안내 로그.
//  ⚠️ 네트워크 리스너라 헤드리스 검증 불가 — 실행 검증은 사용자 측. 기본 OFF.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Common
{
    public sealed class LanStatusServer : IDisposable
    {
        public static LanStatusServer Instance { get; } = new();

        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private LocalDatabase? _db;
        private readonly FxConverter _fx = new();

        public bool Running => _listener?.IsListening == true;
        public int Port { get; private set; }
        public string LastError { get; private set; } = "";

        private LanStatusServer() { }

        /// <summary>Lan.Enabled=="true"일 때만 시작. 이미 실행 중이면 무시. (앱 시작 시·설정 변경 시 호출)</summary>
        public void Start(LocalDatabase db)
        {
            _db = db;
            if (db.GetSetting("Lan.Enabled") != "true" || Running) return;
            Port = int.TryParse(db.GetSetting("Lan.Port"), out var p) && p is > 0 and < 65536 ? p : 8787;
            LastError = "";
            try
            {
                _listener = new HttpListener();
                try { _listener.Prefixes.Add($"http://+:{Port}/"); _listener.Start(); }   // LAN(모든 인터페이스)
                catch (HttpListenerException)
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://localhost:{Port}/");                   // 폴백: 같은 PC만
                    _listener.Start();
                    LastError = $"LAN 바인딩 실패 → localhost 전용. LAN 노출하려면 관리자에서 'netsh http add urlacl url=http://+:{Port}/ user=Everyone' 후 재시작.";
                    Log.Warn("[LAN] " + LastError);
                }
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => LoopAsync(_cts.Token));
                Log.Info($"[LAN] 읽기전용 상태 서버 시작 :{Port}");
            }
            catch (Exception ex) { LastError = ex.Message; Log.Warn($"[LAN] 시작 실패: {ex.Message}"); _listener = null; }
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _listener = null;
        }

        /// <summary>설정(Enabled/Port/Pin) 변경 후 재시작.</summary>
        public void Restart(LocalDatabase db) { Stop(); Start(db); }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }
                try { Handle(ctx); } catch { try { ctx.Response.Abort(); } catch { } }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path is "/" or "/index.html") { Write(ctx, 200, "text/html; charset=utf-8", Html()); return; }
            if (path == "/api/status")
            {
                var pin = _db!.GetSetting("Lan.Pin") ?? "";
                var given = ctx.Request.QueryString["pin"] ?? "";
                if (string.IsNullOrEmpty(pin) || !TimeSafeEquals(given, pin)) { Write(ctx, 401, "application/json", "{\"error\":\"unauthorized\"}"); return; }
                Write(ctx, 200, "application/json; charset=utf-8", StatusJson());
                return;
            }
            Write(ctx, 404, "text/plain; charset=utf-8", "not found");
        }

        private string StatusJson()
        {
            decimal usd = 1380m; try { usd = _fx.GetUsdKrwAsync().GetAwaiter().GetResult(); } catch { }
            var holdings = _db!.GetAllHoldings();
            decimal total = 0, cost = 0;
            var items = new List<object>();
            foreach (var h in holdings)
            {
                var fx = h.Currency == Currency.USD ? usd : 1m;
                var val = h.CurrentPrice * h.Quantity * fx;
                total += val; cost += h.AvgPrice * h.Quantity * fx;
                items.Add(new { name = h.Name, symbol = h.Symbol, qty = h.Quantity, price = h.CurrentPrice, value = val });
            }
            var pnl = total - cost;
            return JsonSerializer.Serialize(new
            {
                time = DateTime.Now.ToString("HH:mm:ss"),
                usdKrw = usd, total, pnl, ret = cost > 0 ? pnl / cost * 100m : 0m,
                count = holdings.Count, holdings = items
            });
        }

        // 길이 누출 줄이는 상수시간 비교(과한 보안은 아니나 PIN 비교에 적절)
        private static bool TimeSafeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static void Write(HttpListenerContext ctx, int code, string contentType, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        // 자체 완결 읽기전용 HTML (PIN 입력 → /api/status 15초 폴링)
        private static string Html() =>
            "<!doctype html><html lang=ko><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'>" +
            "<title>MS2-E 현황</title><style>body{font-family:system-ui;background:#10141d;color:#e8edf6;margin:0;padding:16px}" +
            "h1{font-size:18px}h2{font-size:20px}input,button{font-size:16px;padding:9px;border-radius:8px;border:1px solid #2a3450;background:#1c2438;color:#e8edf6}" +
            "table{width:100%;border-collapse:collapse;margin-top:12px}td,th{padding:8px;border-bottom:1px solid #262d3c;text-align:left;font-size:13px}" +
            ".r{text-align:right}.up{color:#ff5a6e}.down{color:#5b8cff}.sub{color:#8b95a8;font-size:12px}</style></head><body>" +
            "<h1>📊 MS2-E 현황 <span class=sub>(읽기전용)</span></h1>" +
            "<div><input id=pin type=password placeholder=PIN><button onclick=load()>조회</button> <span id=t class=sub></span></div>" +
            "<div id=sum></div><table id=tb></table>" +
            "<script>async function load(){var p=document.getElementById('pin').value;if(!p)return;" +
            "try{var r=await fetch('/api/status?pin='+encodeURIComponent(p));if(!r.ok){document.getElementById('t').textContent='PIN 오류';return;}" +
            "var d=await r.json();document.getElementById('t').textContent=d.time+' · 환율 '+Math.round(d.usdKrw);" +
            "var c=d.ret>=0?'up':'down';document.getElementById('sum').innerHTML='<h2 class='+c+'>총 '+Math.round(d.total).toLocaleString()+'원<br><span style=font-size:14px>손익 '+Math.round(d.pnl).toLocaleString()+'원 ('+d.ret.toFixed(1)+'%)</span></h2>';" +
            "var h='<tr><th>종목</th><th class=r>수량</th><th class=r>현재가</th><th class=r>평가액</th></tr>';" +
            "for(var i=0;i<d.holdings.length;i++){var x=d.holdings[i];h+='<tr><td>'+x.name+'<br><span class=sub>'+x.symbol+'</span></td><td class=r>'+x.qty.toLocaleString()+'</td><td class=r>'+Math.round(x.price).toLocaleString()+'</td><td class=r>'+Math.round(x.value).toLocaleString()+'</td></tr>';}" +
            "document.getElementById('tb').innerHTML=h;}catch(e){document.getElementById('t').textContent='연결 오류';}}" +
            "setInterval(function(){if(document.getElementById('pin').value)load();},15000);</script></body></html>";

        public void Dispose() => Stop();
    }
}
