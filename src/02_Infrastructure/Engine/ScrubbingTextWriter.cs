// ============================================================================
// ScrubbingTextWriter — Console/stdout·stderr 봉합선 밀봉 (S22 #3-b)
//
//  앱 코드는 Log.* 단일 초크포인트로 차폐된다(S22 #3). 그러나 서드파티 라이브러리
//  (WebView2·Sqlite·QuestPDF 등)가 System.Console 로 직접 쓰면 그 출력은 차폐를 우회한다.
//  이 래퍼를 Console.SetOut/SetError 에 꽂으면, 프로세스의 모든 콘솔 출력이 라인 단위로
//  LogRedactor 를 통과한다 — 현재·미래의 서드파티 출력까지 자동 차폐.
//
//  ── 라인 버퍼링 ─────────────────────────────────────────────────────────────
//  비밀 탐지는 '완성된 라인' 단위로 해야 한다(부분 청크는 비밀을 토막 낼 수 있음).
//  개행이 올 때까지 버퍼링하고, 완성 라인만 스크럽해 내부 writer 로 흘린다.
//  Flush 시 남은 부분도 스크럽해 비운다(데이터 손실 방지). Scrub 은 예외 안전.
// ============================================================================
using System.IO;
using System.Text;

namespace StockManager.Common
{
    public sealed class ScrubbingTextWriter : TextWriter
    {
        private readonly TextWriter   _inner;
        private readonly LogRedactor  _redactor;
        private readonly StringBuilder _buf = new();
        private readonly object        _gate = new();

        public ScrubbingTextWriter(TextWriter inner, LogRedactor redactor)
        {
            _inner    = inner    ?? throw new System.ArgumentNullException(nameof(inner));
            _redactor = redactor ?? throw new System.ArgumentNullException(nameof(redactor));
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            lock (_gate) { _buf.Append(value); DrainLines(); }
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            lock (_gate) { _buf.Append(value); DrainLines(); }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_gate) { _buf.Append(buffer, index, count); DrainLines(); }
        }

        public override void Flush()
        {
            lock (_gate)
            {
                if (_buf.Length > 0)
                {
                    _inner.Write(_redactor.Scrub(_buf.ToString()));
                    _buf.Clear();
                }
            }
            _inner.Flush();
        }

        // 버퍼에서 완성된 라인(개행 포함)을 모두 스크럽해 내부로 흘린다. 부분 라인은 남긴다.
        private void DrainLines()
        {
            int nl;
            while ((nl = FindNewline()) >= 0)
            {
                string line = _buf.ToString(0, nl);   // 개행 제외 본문
                char   ch   = _buf[nl];               // '\n'
                _buf.Remove(0, nl + 1);
                _inner.Write(_redactor.Scrub(line));   // 본문만 차폐(\r 은 본문에 포함)
                _inner.Write(ch);                      // 개행은 그대로
            }
        }

        private int FindNewline()
        {
            for (int i = 0; i < _buf.Length; i++)
                if (_buf[i] == '\n') return i;
            return -1;
        }
    }
}
