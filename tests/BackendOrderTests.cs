using System.Linq;
using Xunit;

namespace LLM_AI.Tests
{
    /// <summary>Surcharge d'ordre des backends pour un tour de chat.</summary>
    public class BackendOrderTests
    {
        private static readonly LlmBackend[] Sorted =
        {
            new LlmBackend { Model = "X", Priority = 1 },
            new LlmBackend { Model = "Y", Priority = 2 },
            new LlmBackend { Model = "Z", Priority = 3 },
        };

        private static string Order(string model, out bool found)
            => string.Concat(BackendOrder.MoveToFront(Sorted, model, out found).Select(b => b.Model));

        [Theory]
        [InlineData(null, "XYZ")]
        [InlineData("", "XYZ")]
        [InlineData("   ", "XYZ")]
        [InlineData("X", "XYZ")]
        [InlineData("Y", "YXZ")]
        [InlineData("Z", "ZXY")]
        [InlineData("z", "ZXY")]      // casse ignorée
        [InlineData(" Y ", "YXZ")]    // espaces tolérés
        public void Le_choisi_passe_en_tete_les_autres_gardent_leur_ordre(string model, string attendu)
        {
            Assert.Equal(attendu, Order(model, out var found));
            Assert.True(found);
        }

        [Fact]
        public void Modele_introuvable_laisse_la_liste_inchangee()
        {
            Assert.Equal("XYZ", Order("W", out var found));
            Assert.False(found);
        }

        [Fact]
        public void Ne_modifie_pas_la_liste_source()
        {
            BackendOrder.MoveToFront(Sorted, "Z", out _);
            Assert.Equal("XYZ", string.Concat(Sorted.Select(b => b.Model)));
        }

        [Fact]
        public void Deux_backends_meme_modele_le_mieux_classe_gagne()
        {
            var list = new[]
            {
                new LlmBackend { Model = "A", Provider = "ollama_local" },
                new LlmBackend { Model = "B", Provider = "ollama_local" },
                new LlmBackend { Model = "B", Provider = "ollama_cloud" },
            };
            var r = BackendOrder.MoveToFront(list, "B", out _);
            Assert.Equal("ollama_local", r[0].Provider);
            Assert.Equal("A", r[1].Model);
            Assert.Equal("ollama_cloud", r[2].Provider);
        }
    }
}
