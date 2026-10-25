// ============================================================================
// PlutoHttpMacroSource — v12.18.77-DEV: 플루토 글로벌 인제스천 허브 HTTP 수집원(설정 `Pluto.MacroFeedUrl` · 기본 빈 값 = 미결선 · 네트워크 호출 0)
//   GET 1회 = JSON 관측 1건(PlutoGlobalDataIngestor.TryParse 스키마). https 만 허용(그 외 = 생성 거부). 응답 상한 64KB 초과 = 실패(직전 안정값 유지).
//   인증 · 쿠키 · 송출 본문 0(조회 전용). ★QuantumVault · 피어 · 브로커 경로 0.
// ============================================================================
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Engines.Cognitive.Learning;

namespace StockManager.Adapters
{
    public sealed class PlutoHttpMacroSource : IPlutoMacroSource, IDisposable
    {
        public const int MaxBodyBytes = 64 * 1024;

        private readonly HttpClient _http;
        private readonly Uri _uri;

        public PlutoHttpMacroSource(Uri uri, HttpMessageHandler? handler = null)
        {
            ArgumentNullException.ThrowIfNull(uri);
            if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("https 절대 URI 만 허용", nameof(uri));
            _uri = uri;
            _http = handler is null ? new HttpClient() : new HttpClient(handler);
            _http.MaxResponseContentBufferSize = MaxBodyBytes;
        }

        public string Name => _uri.Host;

        public async Task<byte[]> FetchAsync(CancellationToken ct)
        {
            using var resp = await _http.GetAsync(_uri, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }

        public void Dispose() => _http.Dispose();
    }
}
