using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Outil LLM <c>mcc_lookup</c> — consultation EXPLICITE du Répertoire des
    /// films classés du Québec (MCC) par le chat. Décision usager 2026-11-20 :
    /// « le chat doit faire une validation sur MCC si nécessaire » — les deux
    /// cas fondateurs (prod « Coup de coeur » item 329735 : la v.o. manquante
    /// comme CLÉ de recherche ; banc « Sur la route » item 160848 : les
    /// fiches MCC comme DÉPARTAGEUR contre l'homonyme exact-et-faux Big Drive
    /// 2011 proposé par le resolver sur l'année contaminée).
    /// <para>Le tool rend TOUTES les fiches à titre exact (titre QC, v.o.,
    /// année, genre, synopsis FR) — l'équivoque est le BUT : c'est le lecteur
    /// (le LLM, puis l'admin sur la carte) qui tranche, exactement le geste
    /// manuel de l'usager. Le PONT silencieux v2.22.1
    /// (<see cref="MccClient.TryGetOriginalTitleAsync"/>) reste le mécanisme
    /// automatique ; ce tool est la validation sur demande.</para>
    /// <para>Enregistrement GATED (<see cref="LlmRunner.BuildTools"/>) :
    /// seulement si <see cref="MccClient.IsEnabledForInstall"/> (rang fr-CA
    /// de <see cref="PluginConfiguration.TmdbLangPriority"/>) — un install
    /// hors Québec ne paie ni le tool ni son token de prompt.</para>
    /// <para>Doctrine : ne lève jamais (erreur = JSON <c>{"error":…}</c>) ;
    /// une absence au répertoire n'est PAS une preuve (répertoire de films
    /// DISTRIBUTÉS AU QUÉBEC — les téléfilms récents et séries télé n'y
    /// figurent pas forcément) ; le tool n'écrit rien — il fournit du
    /// matériel de décision, la preuve affichée sur la carte reste calculée
    /// côté serveur.</para>
    /// </summary>
    internal sealed class MccLookupTool : ILlmTool
    {
        private readonly ILogger _logger;

        public MccLookupTool(ILogger logger)
        {
            _logger = logger;
        }

        public string Name => "mcc_lookup";

        public string Description =>
            "Consulte le Répertoire des films classés du Québec (MCC — répertoire gouvernemental des films distribués au Québec) " +
            "par TITRE tel que sorti au Québec. Rend TOUTES les fiches à TITRE EXACT : titre québécois, titre original (v.o.), année, genre, synopsis FR. " +
            "Usages : (1) un titre québécois est introuvable sur TMDB → prends la v.o. de la fiche et relance tmdb_lookup avec (sans année) ; " +
            "(2) des fiches homonymes se disputent → départage par année, synopsis et durée du fichier ; " +
            "(3) avant identify_propose, vérifie que la fiche candidate concorde (v.o. + année) avec la fiche MCC. " +
            "NB : répertoire de FILMS seulement — les séries télé n'y figurent pas. Une absence au répertoire n'est PAS une preuve.";

        public string ArgumentsSchema =>
            "{\"type\":\"object\",\"properties\":{" +
            "\"query\":{\"type\":\"string\",\"description\":\"Titre à chercher (titre de distribution québécois)\"}," +
            "\"year\":{\"type\":\"integer\",\"description\":\"Année attendue (optionnel — sert uniquement à ordonner les fiches, jamais à en masquer)\"}}," +
            "\"required\":[\"query\"]}";

        public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            string query = ChatActions.ArgString(args, "query");
            if (string.IsNullOrWhiteSpace(query))
                return ChatActions.Json(new { error = "paramètre 'query' requis." });

            int? year = null;
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("year", out var yEl))
            {
                int y = 0;
                if ((yEl.ValueKind == JsonValueKind.Number && yEl.TryGetInt32(out y))
                    || (yEl.ValueKind == JsonValueKind.String && int.TryParse(yEl.GetString(), out y)))
                {
                    if (y > 1900 && y < 2100) year = y;
                }
            }

            var fiches = await MccClient.LookupFichesAsync(query, 4, _logger, ct).ConfigureAwait(false);
            if (fiches == null)
                return ChatActions.Json(new { error = "Répertoire MCC indisponible (réseau ou délai) — réessaie plus tard." });

            if (fiches.Count == 0)
                return ChatActions.Json(new
                {
                    source = "mcc",
                    total = 0,
                    note = "Aucun titre exact au répertoire. Les œuvres francophones québécoises récentes et les séries télé peuvent y être absentes — ce n'est PAS une preuve que la fiche cherchée n'existe pas."
                });

            // Ordre : année demandée d'abord (annotation), le reste derrière —
            // l'année n'élimine JAMAIS (doctrine v2.4.4 : une année de surface
            // instable ne doit pas masquer).
            var ordered = fiches
                .OrderBy(f => year.HasValue && f.Year.HasValue ? Math.Abs(f.Year.Value - year.Value) : int.MaxValue)
                .ThenBy(f => f.Year ?? int.MaxValue)
                .ToList();

            string note = ordered.Count > 1
                ? "Plusieurs fiches homonymes à ce TITRE EXACT — départage par année/synopsis/durée du fichier (la carte identify affiche aussi la concordance MCC calculée côté serveur)."
                : "Fiche unique à ce titre — la v.o. relance tmdb_lookup si le titre québécois échoue.";

            return ChatActions.Json(new
            {
                source = "mcc",
                total = ordered.Count,
                fiches = ordered.Select(f => new
                {
                    titre = f.Titre,
                    vo = f.Vo,
                    annee = f.Year,
                    annee_recherchee = year.HasValue && f.Year.HasValue && OrphanResolver.YearCompatible(year.Value, f.Year.Value),
                    genre = f.Genre,
                    synopsis = f.Synopsis,
                    url = f.Url
                }),
                note
            });
        }
    }
}
