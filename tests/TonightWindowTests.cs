using System;
using Xunit;

namespace LLM_AI.Tests
{
    /// <summary>Parseur de la « période de grande écoute » (HH:mm-HH:mm).</summary>
    public class TonightWindowTests
    {
        private static bool Parse(string w, out TimeSpan start, out TimeSpan end)
            => TonightService.TryParseProductionWindow(
                new PluginConfiguration { TonightProductionWindow = w }, out start, out end);

        [Theory]
        [InlineData("17:00-02:00", 17, 2)]     // franchit minuit
        [InlineData("09:00-17:00", 9, 17)]
        [InlineData("9:00-17:00", 9, 17)]      // heure sur 1 chiffre (refusée avant v2.23.3)
        [InlineData(" 17:00 - 02:00 ", 17, 2)] // espaces tolérés
        [InlineData("17:00–02:00", 17, 2)]     // demi-cadratin
        [InlineData("17:00—02:00", 17, 2)]     // cadratin
        [InlineData("17:00-24:00", 17, 24)]    // fin de journée
        public void Valide(string w, int h1, int h2)
        {
            Assert.True(Parse(w, out var s, out var e));
            Assert.Equal(TimeSpan.FromHours(h1), s);
            Assert.Equal(TimeSpan.FromHours(h2), e);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("17:00")]          // pas de borne de fin
        [InlineData("17:00-")]
        [InlineData("-02:00")]
        [InlineData("17:00-17:00")]    // bornes égales
        [InlineData("25:00-02:00")]
        [InlineData("17:60-02:00")]
        [InlineData("abc-def")]
        public void Invalide(string w) => Assert.False(Parse(w, out _, out _));
    }
}
