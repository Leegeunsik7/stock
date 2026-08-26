// ============================================================================
// LlmProvidersTests - LLM 라우터 결정 + Ollama 요청/응답 직렬화 (순수)
// ============================================================================
using Xunit;

namespace StockManager.Tests
{
    public class LlmProvidersTests
    {
        [Theory]
        [InlineData("auto", true, false, "gemini")]    // 키 있으면 Gemini
        [InlineData("auto", false, true, "ollama")]    // 키 없고 로컬 켜짐 → 로컬
        [InlineData("auto", false, false, "none")]     // 둘 다 없음
        [InlineData("auto", true, true, "gemini")]     // auto는 Gemini 우선
        [InlineData("ollama", true, true, "ollama")]   // 명시 로컬
        [InlineData("ollama", true, false, "gemini")]  // 로컬 꺼짐 → Gemini 폴백
        [InlineData("gemini", false, true, "ollama")]  // 키 없음 → 로컬 폴백
        [InlineData("gemini", true, false, "gemini")]
        public void Resolve_PicksExpectedBackend(string setting, bool gem, bool oll, string expected)
            => Assert.Equal(expected, LlmRouter.Resolve(setting, gem, oll));

        [Fact]
        public void Resolve_NullSetting_TreatedAsAuto()
            => Assert.Equal("gemini", LlmRouter.Resolve(null, true, false));

        [Fact]
        public void Ollama_BuildRequest_HasModelPromptStreamFalse()
        {
            var json = OllamaProvider.BuildRequest("llama3.1", "hello");
            Assert.Contains("\"model\":\"llama3.1\"", json);
            Assert.Contains("\"stream\":false", json);
            Assert.Contains("\"prompt\":\"hello\"", json);   // (STJ는 비ASCII를 \uXXXX로 이스케이프 — ASCII로 검증)
        }

        [Fact]
        public void Ollama_ParseResponse_ExtractsText()
        {
            Assert.Equal("결과", OllamaProvider.ParseResponse("{\"model\":\"x\",\"response\":\"결과\",\"done\":true}"));
            Assert.Equal("", OllamaProvider.ParseResponse("{\"done\":true}"));   // response 없음
            Assert.Equal("", OllamaProvider.ParseResponse("not json"));          // 깨진 JSON → 빈 문자열
        }
    }
}
