using System.IO;
using System.Text;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class ScrubbingTextWriterTests
    {
        private const string Secret = "SK-LIVE-abcdef123456";

        // 비밀 1건이 등록된 redactor + 캡처용 StringWriter 를 감싼 스크러버
        private static (ScrubbingTextWriter w, StringWriter sink) Rig()
        {
            var redactor = new LogRedactor();
            redactor.Register(Secret);
            var sink = new StringWriter();
            return (new ScrubbingTextWriter(sink, redactor), sink);
        }

        [Fact]
        public void WriteLine_WithSecret_IsMasked()
        {
            var (w, sink) = Rig();
            w.WriteLine($"3rd-party dump: key={Secret} status=401");
            var outp = sink.ToString();
            Assert.DoesNotContain(Secret, outp);
            Assert.Contains("****", outp);
            Assert.EndsWith("\n", outp.Replace("\r", ""));   // 개행 보존
        }

        [Fact]
        public void PartialLine_IsBuffered_UntilNewline()
        {
            var (w, sink) = Rig();
            w.Write("계좌 ");
            w.Write(Secret);          // 아직 개행 없음 → 버퍼링(내부로 안 흐름)
            Assert.Equal("", sink.ToString());   // 미완성 라인은 보류
            w.Write(" 끝\n");          // 개행 도달 → 라인 완성·스크럽
            var outp = sink.ToString();
            Assert.DoesNotContain(Secret, outp);
            Assert.Contains("계좌", outp);
        }

        [Fact]
        public void Flush_EmitsRemainingPartial_Scrubbed()
        {
            var (w, sink) = Rig();
            w.Write($"trailing {Secret}");   // 개행 없는 잔여
            w.Flush();
            var outp = sink.ToString();
            Assert.DoesNotContain(Secret, outp);   // Flush 시 잔여도 차폐
            Assert.Contains("trailing", outp);
        }

        [Fact]
        public void MultipleLinesInOneWrite_EachScrubbed()
        {
            var (w, sink) = Rig();
            w.Write($"a={Secret}\nb={Secret}\n");
            var outp = sink.ToString();
            Assert.DoesNotContain(Secret, outp);
            Assert.Equal(2, outp.Split('\n').Length - 1);   // 라인 2개 + 말미
        }

        [Fact]
        public void NonSecretOutput_PassesThroughUnchanged()
        {
            var (w, sink) = Rig();
            w.WriteLine("정상 콘솔 출력 — 비밀 없음");
            Assert.Contains("정상 콘솔 출력 — 비밀 없음", sink.ToString());
        }

        [Fact]
        public void CharByChar_AccumulatesAndScrubsOnNewline()
        {
            var (w, sink) = Rig();
            foreach (char c in $"x={Secret}\n") w.Write(c);
            var outp = sink.ToString();
            Assert.DoesNotContain(Secret, outp);
            Assert.Contains("x=", outp);
        }

        [Fact]
        public void Encoding_DelegatesToInner()
        {
            var sink = new StringWriter();   // StringWriter.Encoding == Unicode
            var w = new ScrubbingTextWriter(sink, new LogRedactor());
            Assert.Equal(sink.Encoding, w.Encoding);
        }

        [Fact]
        public void NullArgs_Throw()
        {
            Assert.Throws<System.ArgumentNullException>(() => new ScrubbingTextWriter(null!, new LogRedactor()));
            Assert.Throws<System.ArgumentNullException>(() => new ScrubbingTextWriter(new StringWriter(), null!));
        }

        [Fact]
        public void CrLf_NewlinePreserved_BodyScrubbed()
        {
            var (w, sink) = Rig();
            w.Write($"win={Secret}\r\n");
            var outp = sink.ToString();
            Assert.DoesNotContain(Secret, outp);
            Assert.Contains("\r\n", outp);   // CRLF 보존
        }
    }
}
