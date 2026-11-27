using Xunit;

namespace LLM_AI.Tests
{
    public class OpenAiCompatTests
    {
        [Theory]
        [InlineData("http://h:1234", "http://h:1234/v1/chat/completions")]
        [InlineData("http://h:1234/", "http://h:1234/v1/chat/completions")]
        [InlineData("http://h:1234/v1", "http://h:1234/v1/chat/completions")]
        [InlineData("http://h:1234/v1/", "http://h:1234/v1/chat/completions")]
        [InlineData("http://h/v1/chat/completions", "http://h/v1/chat/completions")]
        public void Endpoint_sans_doubler_v1(string url, string attendu)
            => Assert.Equal(attendu, LlmClient.OpenAiCompatEndpoint(url));

        [Fact]
        public void Extrait_le_contenu_du_premier_choix()
            => Assert.Equal("OK", LlmClient.ExtractOpenAiContent(
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\",\"reasoning_content\":\"x\"}}]}", null));

        [Fact]
        public void Contenu_absent_ou_vide_donne_la_sentinelle()
        {
            Assert.Equal("(réponse vide du LLM)", LlmClient.ExtractOpenAiContent("{\"choices\":[]}", null));
            Assert.Equal("(réponse vide du LLM)", LlmClient.ExtractOpenAiContent("{\"choices\":[{\"message\":{\"content\":null}}]}", null));
        }

        [Fact]
        public void Objet_error_leve()
            => Assert.Throws<System.InvalidOperationException>(() =>
                LlmClient.ExtractOpenAiContent("{\"error\":{\"message\":\"model not found\"}}", null));

        [Fact]
        public void Corps_echappe_correctement()
        {
            var body = LlmClient.BuildOpenAiCompatBody("m", new[]
            {
                new LlmClient.ChatMessage { Role = "user", Content = "a \"b\"\nc" }
            });
            Assert.Equal("{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\"a \\\"b\\\"\\nc\"}],\"stream\":false}", body);
        }

        [Theory]
        [InlineData("openai_compat", LlmProvider.OpenAiCompat)]
        [InlineData("OpenAI", LlmProvider.OpenAiCompat)]
        [InlineData("gemini", LlmProvider.Gemini)]
        [InlineData("", LlmProvider.OllamaLocal)]
        public void Parse_provider(string s, LlmProvider attendu)
            => Assert.Equal(attendu, LlmProviderHelper.Parse(s));
    }
}
