using System.Text.Json;
using Xunit;

namespace LLM_AI.Tests
{
    /// <summary>Béquilles de réparation JSON des petits modèles (LlmAgentService).</summary>
    public class JsonRepairTests
    {
        private static string ValueOf(string json, string prop)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty(prop).GetString();
        }

        [Fact]
        public void Sanitize_controle_brut_dans_une_chaine_est_echappe()
            => Assert.Equal("a\nb", ValueOf(LlmAgentService.SanitizeJsonControlChars("{\"k\":\"a\nb\"}"), "k"));

        [Fact]
        public void Sanitize_antislash_devant_un_controle_brut_donne_un_json_valide()
            // Régression audit : « \ » + saut de ligne réel laissait un JSON invalide.
            => Assert.Equal("a\nb", ValueOf(LlmAgentService.SanitizeJsonControlChars("{\"k\":\"a\\\nb\"}"), "k"));

        [Fact]
        public void Sanitize_echappement_invalide_tombe_l_antislash()
            // \' émis par gemma4 : le caractère seul suffit.
            => Assert.Equal("l'historique", ValueOf(LlmAgentService.SanitizeJsonControlChars("{\"k\":\"l\\'historique\"}"), "k"));

        [Fact]
        public void Sanitize_echappements_valides_conserves()
            => Assert.Equal("a\"b\\c\n", ValueOf(LlmAgentService.SanitizeJsonControlChars("{\"k\":\"a\\\"b\\\\c\\n\"}"), "k"));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Sanitize_vide_inchange(string s) => Assert.Equal(s, LlmAgentService.SanitizeJsonControlChars(s));

        [Fact]
        public void RepairUnescapedQuotes_guillemets_non_echappes_dans_une_valeur()
        {
            var repaired = LlmAgentService.RepairUnescapedQuotes("{\"q\":\"un (kind=\"series\") ici\"}");
            Assert.Equal("un (kind=\"series\") ici", ValueOf(repaired, "q"));
        }
    }
}
