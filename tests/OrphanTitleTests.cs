using Xunit;

namespace LLM_AI.Tests
{
    /// <summary>Gardes de titre/année du pipeline orphelins (OrphanResolver).</summary>
    public class OrphanTitleTests
    {
        [Fact]
        public void NormalizeTitle_ignore_casse_accents_ponctuation()
            => Assert.Equal("lemondedegabrielleroy", OrphanResolver.NormalizeTitle("Le Monde de Gabrielle Roy !"));

        [Fact]
        public void NormalizeTitle_plie_la_ligature_oe()
            => Assert.Equal(OrphanResolver.NormalizeTitle("Coup de coeur"), OrphanResolver.NormalizeTitle("Coup de cœur"));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void NormalizeTitle_vide(string s) => Assert.Equal(string.Empty, OrphanResolver.NormalizeTitle(s));

        [Fact]
        public void SameTitleKey_ignore_le_suffixe_annee()
            => Assert.Equal(OrphanResolver.SameTitleKey("Le monde de Gabrielle Roy"),
                            OrphanResolver.SameTitleKey("Le monde de Gabrielle Roy (2021)"));

        [Fact]
        public void SameTitleKey_titres_differents_restent_differents()
            => Assert.NotEqual(OrphanResolver.SameTitleKey("Sissi"), OrphanResolver.SameTitleKey("Sissi et moi"));

        [Theory]
        [InlineData(2020, 2021, true)]   // ±1 an
        [InlineData(2020, 2022, false)]  // écart > 1
        [InlineData(0, 1990, true)]      // « 0 si inconnu » = pas de garde
        [InlineData(1, 1990, true)]      // année aberrante (< 1900) = inconnue
        public void YearCompatible_doctrine(int expected, int actual, bool ok)
            => Assert.Equal(ok, OrphanResolver.YearCompatible(expected, actual));

        [Fact]
        public void YearCompatible_inconnue_nulle()
        {
            Assert.True(OrphanResolver.YearCompatible(null, 2000));
            Assert.True(OrphanResolver.YearCompatible(2000, null));
        }

        [Theory]
        [InlineData(2020, 2011, true)]   // saison diffusée après la 1re : OK (Vera)
        [InlineData(2022, 2021, true)]   // Sissi
        [InlineData(1976, 2021, false)]  // enregistrement ANTÉRIEUR à la fiche : autre œuvre
        [InlineData(0, 2021, true)]
        public void AnchorSeriesYearCompatible_doctrine(int expected, int actual, bool ok)
            => Assert.Equal(ok, OrphanResolver.AnchorSeriesYearCompatible(expected, actual));

        [Fact]
        public void RetryAnchorAccepts_sans_ancre_porte_inactive()
        {
            Assert.True(OrphanResolver.RetryAnchorAccepts(null, null, "N'importe quoi", 1990));
            Assert.True(OrphanResolver.RetryAnchorAccepts("  ", 2000, "", null));
        }

        [Fact]
        public void RetryAnchorAccepts_cle_exacte_et_annee_compatible()
            => Assert.True(OrphanResolver.RetryAnchorAccepts("De souche (2023)", 2023, "De souche", 2021));

        [Fact]
        public void RetryAnchorAccepts_rejette_titre_different()
            => Assert.False(OrphanResolver.RetryAnchorAccepts("De souche", 2023, "De souche : le film", 2023));

        [Fact]
        public void RetryAnchorAccepts_rejette_fiche_posterieure_a_l_ancre()
            => Assert.False(OrphanResolver.RetryAnchorAccepts("Titre", 2010, "Titre", 2021 + 5));

        [Fact]
        public void RetryAnchorAccepts_titre_de_fiche_vide_ne_prouve_rien()
            => Assert.False(OrphanResolver.RetryAnchorAccepts("Titre", 2010, "", 2010));

        [Fact]
        public void TokenSubsequence_sous_titre_insere()
            => Assert.True(OrphanResolver.TokenSubsequence(
                "ADN la face cachee des tests grand public",
                "ADN business la face cachée des tests grand public"));

        [Fact]
        public void TokenSubsequence_exige_deux_tokens()
            => Assert.False(OrphanResolver.TokenSubsequence("Sissi", "Sissi et moi"));

        // CARACTÉRISATION (audit 2026-11) : l'inclusion contiguë est acceptée
        // quel que soit le titre court — « It » ⊂ « Titanic ». Ce test fige le
        // comportement ACTUEL ; si la porte est un jour durcie (longueur
        // minimale / frontière de mot), il doit être modifié volontairement.
        [Fact]
        public void TitleMatches_caracterisation_inclusion_titre_court()
            => Assert.True(OrphanResolver.TitleMatches("It", "Titanic", 1997, 1997));

        [Fact]
        public void TitleMatches_annee_trop_eloignee_refusee()
            => Assert.False(OrphanResolver.TitleMatches("Titanic", "Titanic", 1997, 2005));

        [Fact]
        public void TitleMatches_fiche_sans_titre_refusee()
            => Assert.False(OrphanResolver.TitleMatches("Titanic", "", 1997, 1997));
    }
}
