using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;

namespace LLM_AI
{
    /// <summary>
    /// Endpoint HTTP « Traduction des genres (IA) » : expose
    /// <c>GET /Plugins/LLMAI/GenreProposals</c> (détecte les genres EPG non
    /// mappés par GenreCleaner et fait traduire par le LLM chacun en un
    /// équivalent du vocabulaire curaté <c>AllowedGenres</c>) et
    /// <c>POST /Plugins/LLMAI/GenreApply</c> (écrit les mappages acceptés
    /// dans <c>GenreCleaner.xml</c>, les enregistre dans
    /// <see cref="PluginConfiguration.GenreAliasApplied"/> et déclenche
    /// <see cref="IApplicationHost.NotifyPendingRestart"/> — la bannière
    /// « redémarrage requis » d'Emby).
    /// </summary>
    /// <remarks>
    /// Réservé aux administrateurs (page de config admin) : consomme des
    /// tokens LLM et écrit dans le fichier de config d'un AUTRE plugin.
    /// Service ServiceStack découvert par scanning d'assembly : hérite
    /// <see cref="BaseApiService"/> ; la route est portée par le DTO requête
    /// via <see cref="RouteAttribute"/>. L'appel LLM est un one-shot sans
    /// boucle agent via <see cref="LlmRunner.ChatWithFallbackAsync"/>
    /// (mêmes backends/priorités que le reste du plugin).
    /// </remarks>
    public class GenreApiService : BaseApiService
    {
        private readonly IJsonSerializer _json;
        private readonly ILiveTvManager _liveTv;

        /// <summary>Nb max de genres non mappés par section envoyés au LLM.</summary>
        private const int MaxGenresPerSection = 60;

        /// <summary>Longueur max d'un nom de nouveau genre suggéré par le LLM.</summary>
        private const int MaxNewGenreLength = 40;

        public GenreApiService(IJsonSerializer json, ILiveTvManager liveTv)
        {
            _json = json;
            _liveTv = liveTv;
        }

        // ------------------------------------------------------------------
        //  DTO requêtes / réponses
        // ------------------------------------------------------------------

        /// <summary>
        /// Requête GET <c>/Plugins/LLMAI/GenreProposals</c> : analyse + LLM,
        /// renvoie les propositions (aucune écriture à ce stade).
        /// </summary>
        [Route("/Plugins/LLMAI/GenreProposals", "GET")]
        public class GenreProposalsRequest : IReturn<object> { }

        /// <summary>
        /// Requête POST <c>/Plugins/LLMAI/GenreApply</c> : mappages acceptés
        /// par l'usager (déjà validés côté propositions, re-validés ici).
        /// </summary>
        [Route("/Plugins/LLMAI/GenreApply", "POST")]
        public class GenreApplyRequest : IReturn<object>
        {
            public List<GenreMappingDto> Mappings { get; set; }
        }

        /// <summary>Un mappage genre brut → genre curaté, pour une section.</summary>
        public class GenreMappingDto
        {
            /// <summary>Genre brut EPG (ex. « Sitcom »).</summary>
            public string Name { get; set; }
            /// <summary>Genre curaté cible (ex. « Comédie »).</summary>
            public string Value { get; set; }
            /// <summary>Section GenreCleaner : <c>"movie"</c> ou <c>"series"</c>.</summary>
            public string Section { get; set; }
            /// <summary>true = la cible est un NOUVEAU genre suggéré par l'IA,
            /// absent du vocabulaire : elle sera AJOUTÉE aux AllowedGenres
            /// de la section à l'écriture (aucune contrainte de vocabulaire).</summary>
            public bool NewGenre { get; set; }
        }

        /// <summary>Une proposition du LLM (cibles par section, null si aucun équivalent).</summary>
        public class GenreProposalDto
        {
            public string Genre { get; set; }
            /// <summary>Cible pour la section films, ou null.</summary>
            public string Movies { get; set; }
            /// <summary>Cible pour la section séries, ou null.</summary>
            public string Series { get; set; }
            /// <summary>
            /// Nouveau genre suggéré (quand aucune cible n'existe dans le
            /// vocabulaire) : court, général, en français — ex. tous les
            /// sports → « Sport ». Ajouté à AllowedGenres si l'usager
            /// l'accepte. Null sinon.
            /// </summary>
            public string New { get; set; }
            /// <summary>Le genre brut a été détecté côté films.</summary>
            public bool InMovies { get; set; }
            /// <summary>Le genre brut a été détecté côté séries.</summary>
            public bool InSeries { get; set; }
        }

        /// <summary>Réponse GET : propositions + contexte de diagnostic.</summary>
        public class GenreProposalsResponse
        {
            public List<GenreProposalDto> Proposals { get; set; }
            /// <summary>Nb de genres non mappés détectés côté films (avant plafonnement).</summary>
            public int UnmappedMovies { get; set; }
            /// <summary>Nb de genres non mappés détectés côté séries (avant plafonnement).</summary>
            public int UnmappedSeries { get; set; }
            /// <summary>
            /// Genres bruts que le LLM n'a pu placer NI dans le vocabulaire
            /// existant NI en nouveau genre suggéré (aucune action possible —
            /// information seulement).
            /// </summary>
            public List<string> Orphans { get; set; }
            /// <summary>Message informatif (ex. tout est déjà mappé), sinon null.</summary>
            public string Message { get; set; }
            public string Error { get; set; }
        }

        /// <summary>Réponse POST : résultat de l'écriture.</summary>
        public class GenreApplyResponse
        {
            /// <summary>Nb de mappages effectivement ajoutés au XML.</summary>
            public int Applied { get; set; }
            /// <summary>true = bannière « redémarrage requis » déclenchée.</summary>
            public bool RestartRequired { get; set; }
            public string Error { get; set; }
        }

        // ------------------------------------------------------------------
        //  Handler GET : analyse + propositions LLM
        // ------------------------------------------------------------------

        public async Task<object> Get(GenreProposalsRequest req)
        {
            var resp = new GenreProposalsResponse
            {
                Proposals = new List<GenreProposalDto>(),
                Orphans = new List<string>()
            };

            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new GenreProposalsResponse { Error = "Configuration du plugin indisponible." };

            var admin = ResolveAdmin();
            if (!(admin?.Policy?.IsAdministrator ?? false))
                return new GenreProposalsResponse { Error = "Réservé aux administrateurs." };

            // Auto-réparation d'abord : une sauvegarde de la page de config
            // GenreCleaner peut avoir réécrit le XML depuis sa copie mémoire
            // — sans ça, on re-proposerait des mappages déjà appliqués.
            GenreCleanerMap.HealApplied(cfg.GenreAliasApplied, Logger);

            // 1) Genres EPG bruts par section (films / séries), non mappés.
            var movieUnmapped = CollectUnmapped(series: false);
            var seriesUnmapped = CollectUnmapped(series: true);
            resp.UnmappedMovies = movieUnmapped.Count;
            resp.UnmappedSeries = seriesUnmapped.Count;

            var movieAllowed = GenreCleanerMap.Allowed(series: false);
            var seriesAllowed = GenreCleanerMap.Allowed(series: true);

            if (movieUnmapped.Count == 0 && seriesUnmapped.Count == 0)
            {
                resp.Message = "Tous les genres EPG sont déjà couverts par GenreCleaner.";
                // Early-return silencieux = impossible à distinguer d'un clic
                // n'ayant jamais atteint le serveur : trace systématique.
                Logger?.Info("[LLM_AI] [GENRES] Analyse : 0 genre(s) non mappé(s) — tout est déjà couvert.");
                return resp;
            }
            if (movieAllowed.Count == 0 && seriesAllowed.Count == 0)
            {
                resp.Message = "GenreCleaner ne définit aucune liste « AllowedGenres » — aucun vocabulaire cible pour traduire.";
                Logger?.Info("[LLM_AI] [GENRES] Analyse interrompue : {0} film(s) / {1} série(s) non mappé(s), mais AUCUN AllowedGenres défini.",
                    movieUnmapped.Count, seriesUnmapped.Count);
                return resp;
            }

            // Sections sans vocabulaire cible : pas de proposition possible.
            bool wantMovies = movieUnmapped.Count > 0 && movieAllowed.Count > 0;
            bool wantSeries = seriesUnmapped.Count > 0 && seriesAllowed.Count > 0;
            var movieList = wantMovies ? movieUnmapped.Take(MaxGenresPerSection).ToList() : new List<string>();
            var seriesList = wantSeries ? seriesUnmapped.Take(MaxGenresPerSection).ToList() : new List<string>();

            // 2) Un appel LLM one-shot, contraint aux vocabulaires AllowedGenres.
            var runner = new LlmRunner(Logger, _json, LibraryManager, UserManager, _liveTv, ApplicationHost);
            var backends = runner.ResolveBackends(cfg);
            if (backends == null || backends.Count == 0)
                return new GenreProposalsResponse { Error = "Aucun backend LLM activé — configurez un serveur LLM d'abord." };

            string ollamaCloudKey = LlmRunner.ResolveKey(cfg.OllamaApiKey, "OLLAMA_API_KEY");
            string geminiKey = LlmRunner.ResolveKey(cfg.GeminiApiKey, "GEMINI_API_KEY");

            // Langue des genres curatés de l'usager (mêmes nouveaux genres
            // suggérés) : cascade ResponseLanguage → langue d'affichage Emby
            // → legacy TmdbLanguage → anglais — voir I18n.ResolveMetaLangKey.
            var genreLang = ResolveGenreLang(cfg);
            var system =
                "Tu maintiens une bibliothèque Emby dont les genres sont curatés en " + genreLang + ". " +
                "Pour chaque genre TV brut fourni (souvent en anglais, non normalisé), choisis l'équivalent " +
                "le plus proche DANS le vocabulaire autorisé de la section correspondante. " +
                "Réponds UNIQUEMENT par un tableau JSON, sans explication ni texte autour : " +
                "[{\"genre\":\"Sitcom\",\"movies\":\"Comédie\",\"series\":\"Comédie\",\"new\":null}, ...]. " +
                "Règles : (1) les valeurs « movies »/« series » doivent être copiées EXACTEMENT depuis " +
                "le vocabulaire autorisé de leur section — n'invente JAMAIS une valeur ; " +
                "(2) null si aucun équivalent convaincant ; " +
                "(3) un même genre brut peut avoir des cibles différentes selon la section " +
                "(ex. « Children » → « Familial » en films mais « Enfant » en séries) ; " +
                "(4) un « genre » brut présent dans une seule section n'a pas besoin d'entrée pour l'autre " +
                "— mets null ; " +
                "(5) si AUCUN équivalent n'existe dans le vocabulaire autorisé, tu peux proposer dans " +
                "« new » UN nouveau nom de genre court, général, en " + genreLang + " (ex. un cluster " +
                "de genres de sport → un seul « Sport ») — il sera ajouté au vocabulaire si " +
                "l'usager l'accepte ; groupe sous un MÊME nom les genres bruts similaires ; " +
                "(6) « new » : null aussi si le genre est trop spécifique ou intraduisible (ex. météo, " +
                "télé-achat) ; " +
                "(7) « new » est ignoré si une cible du vocabulaire existant a été trouvée.";
            var user = JsonSerializer.Serialize(new
            {
                genres_bruts_films = movieList,
                genres_bruts_series = seriesList,
                vocabulaire_autorise_films = movieAllowed,
                vocabulaire_autorise_series = seriesAllowed
            });

            string reply;
            try
            {
                var ct = Request?.CancellationToken ?? CancellationToken.None;
                reply = await runner.ChatWithFallbackAsync(backends, ollamaCloudKey, geminiKey,
                    system, user, "GENRES", ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger?.Error("[LLM_AI] [GENRES] Appel LLM échoué : {0}", ex.Message);
                return new GenreProposalsResponse { Error = "Appel LLM échoué : " + ex.Message };
            }

            // 3) Parse + validation défensive (le LLM ne peut pas introduire
            //    de valeur hors vocabulaire : tout est re-vérifié ici).
            resp.Proposals = ParseProposals(reply, movieList, seriesList, movieAllowed, seriesAllowed, out var orphans);
            resp.Orphans = orphans;
            int suggested = resp.Proposals.Count(p => p.New != null);
            Logger?.Info("[LLM_AI] [GENRES] {0} genre(s) non mappé(s) films, {1} séries → {2} proposition(s) LLM validée(s) (dont {3} nouveau(x) genre(s) suggéré(s)), {4} orphelin(s).",
                resp.UnmappedMovies, resp.UnmappedSeries, resp.Proposals.Count, suggested, orphans.Count);
            if (resp.Proposals.Count == 0)
                resp.Message = "Le LLM n'a proposé aucun équivalent convaincant (null partout ou réponse ininterprétable).";
            return resp;
        }

        // ------------------------------------------------------------------
        //  Handler POST : écriture + restart requis
        // ------------------------------------------------------------------

        public object Post(GenreApplyRequest req)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new GenreApplyResponse { Error = "Configuration du plugin indisponible." };

            var admin = ResolveAdmin();
            if (!(admin?.Policy?.IsAdministrator ?? false))
                return new GenreApplyResponse { Error = "Réservé aux administrateurs." };

            if (req?.Mappings == null || req.Mappings.Count == 0)
                return new GenreApplyResponse { Error = "Aucun mappage fourni." };

            // Validation : champs non vides + cible ∈ AllowedGenres de sa
            // section (quand la liste existe — sinon GenreCleaner la
            // rejetterait de toute façon au nettoyage de bibliothèque).
            // Les mappages marqués NewGenre (nouveau genre suggéré par
            // l'IA) échappent à la contrainte de vocabulaire : la cible
            // sera AJOUTÉE à la liste à l'écriture (AddMappings).
            var valid = new List<GenreCleanerMap.AppliedMapping>();
            int skipped = 0;
            foreach (var m in req.Mappings)
            {
                if (m == null || string.IsNullOrWhiteSpace(m.Name) || string.IsNullOrWhiteSpace(m.Value)) { skipped++; continue; }
                bool series = string.Equals(m.Section, "series", StringComparison.OrdinalIgnoreCase);
                if (!string.Equals(m.Section, "movie", StringComparison.OrdinalIgnoreCase) &&
                    !series) { skipped++; continue; }

                if (m.NewGenre)
                {
                    // Cible volontairement ABSENTE du vocabulaire : pas de
                    // contrainte AllowedGenres. Garde-fou : longueur
                    // raisonnable. L'identité est permise ici (ex.
                    // « Esports » ajouté tel quel au vocabulaire : mappage
                    // no-op, mais l'entrée AllowedGenres reste utile).
                    if (m.Value.Trim().Length > MaxNewGenreLength) { skipped++; continue; }
                    valid.Add(new GenreCleanerMap.AppliedMapping
                    {
                        Name = m.Name.Trim(),
                        Value = m.Value.Trim(),
                        Section = series ? "series" : "movie",
                        NewGenre = true
                    });
                    continue;
                }

                if (GenreCleanerMap.IsIdentity(m.Name, m.Value)) { skipped++; continue; } // no-op « Action »→« Action »
                var allowed = GenreCleanerMap.Allowed(series);
                if (allowed.Count > 0 && !allowed.Contains(m.Value.Trim(), StringComparer.OrdinalIgnoreCase))
                { skipped++; continue; }
                valid.Add(new GenreCleanerMap.AppliedMapping
                {
                    Name = m.Name.Trim(),
                    Value = m.Value.Trim(),
                    Section = series ? "series" : "movie"
                });
            }
            if (valid.Count == 0)
                return new GenreApplyResponse { Error = "Aucun mappage valide (cible hors AllowedGenres ou champs vides)." };

            // Écriture idempotente dans GenreCleaner.xml.
            int added = GenreCleanerMap.AddMappings(valid, Logger);
            if (added < 0)
                return new GenreApplyResponse
                {
                    Error = "Écriture dans GenreCleaner.xml impossible — vérifiez que le plugin GenreCleaner est installé " +
                            "ET que le fichier est accessible en écriture par l'utilisateur « emby » " +
                            "(un fichier copié en root dans /var/lib/emby/plugins/configurations/ doit être « chown emby:emby »)."
                };

            // Record dans notre config (source de vérité de l'auto-réparation)
            // — on fusionne avec les mappages déjà enregistrés.
            var recorded = GenreCleanerMap.ParseApplied(cfg.GenreAliasApplied);
            recorded.AddRange(valid);
            cfg.GenreAliasApplied = GenreCleanerMap.AppliedToJson(recorded);
            Plugin.Instance.SaveConfiguration();

            // Bannière « redémarrage requis » d'Emby (même mécanisme qu'une
            // mise à jour de plugin). Les recommandations LLM AI utilisent
            // les nouveaux mappages SANS redémarrage (relecture ≤ 30 s) ;
            // le redémarrage ne sert qu'à GenreCleaner lui-même (copie
            // mémoire) — et rend nos entrées pérennes face à ses saves UI.
            bool restart = added > 0;
            if (restart)
            {
                try { ApplicationHost?.NotifyPendingRestart(); }
                catch (Exception ex) { Logger?.Warn("[LLM_AI] [GENRES] NotifyPendingRestart échoué : {0}", ex.Message); }
            }

            Logger?.Info("[LLM_AI] [GENRES] {0} mappage(s) appliqué(s) à GenreCleaner.xml ({1} ignoré(s), redémarrage requis : {2}).",
                added, skipped, restart);
            return new GenreApplyResponse { Applied = added, RestartRequired = restart };
        }

        // ------------------------------------------------------------------
        //  Collecte : genres EPG bruts non mappés, par section
        // ------------------------------------------------------------------

        /// <summary>
        /// Genres des programmes EPG <b>à venir</b> de la classe films
        /// (<paramref name="series"/> = false) ou séries (true) qui ne sont
        /// pas déjà mappés dans la section correspondante de GenreCleaner —
        /// triés alphabétiquement. Requête library calquée sur
        /// <see cref="GetEmbyInfoTool"/>/BuildGenreMap (les DTO de GetPrograms
        /// ne portent pas Genres). Ne lève jamais : erreur de requête =
        /// liste vide.
        /// </summary>
        /// <remarks>
        /// <c>HasAired = false</c> est INDISPENSABLE sur ce build d'Emby :
        /// sans filtre de date, GetItemsResult retourne un sous-ensemble
        /// arbitraire (tri par défaut + Limit) du pool de programmes
        /// (passés + futurs) — vécu 2026-09-01 : l'analyse a annoncé
        /// « 0 genre non mappé » alors que 23 genres non mappés existaient
        /// côté séries à venir. Les programmes à venir sont de toute façon
        /// le bon périmètre : ce sont eux que les recommandations émettent.
        /// </remarks>
        private List<string> CollectUnmapped(bool series)
        {
            var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var q = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Program" },
                    IsSeries = series ? true : (bool?)null,
                    IsMovie = series ? (bool?)null : true,
                    HasAired = false,
                    Limit = 4000
                };
                var items = LibraryManager?.GetItemsResult(q)?.Items ?? Array.Empty<BaseItem>();
                foreach (var it in items)
                    foreach (var g in it.Genres ?? Array.Empty<string>())
                    {
                        if (string.IsNullOrWhiteSpace(g)) continue;
                        if (!GenreCleanerMap.IsCovered(g, series)) result.Add(g.Trim());
                    }
            }
            catch (Exception ex)
            {
                Logger?.Warn("[LLM_AI] [GENRES] Collecte des genres {0} échouée : {1}",
                    series ? "séries" : "films", ex.Message);
            }
            return result.ToList();
        }

        // ------------------------------------------------------------------
        //  Parse + validation des propositions LLM
        // ------------------------------------------------------------------

        /// <summary>
        /// Extrait le tableau JSON de propositions de la réponse LLM
        /// (<see cref="LlmRunner.ExtractJsonPayload"/> isole le premier
        /// <c>[...]</c>) et re-valide chaque entrée : le genre doit être un
        /// de ceux envoyés, la cible ∈ AllowedGenres de sa section
        /// (comparaison insensible à la casse). Un champ <c>new</c> (nouveau
        /// genre suggéré, hors vocabulaire) n'est retenu QUE si aucune cible
        /// du vocabulaire existant n'a été trouvée, et plafonné à
        /// <see cref="MaxNewGenreLength"/> caractères. Les entrées invalides
        /// sont silencieusement écartées — le LLM ne peut pas faire passer
        /// une valeur hors vocabulaire. Via <paramref name="orphans"/> :
        /// les genres envoyés que le LLM n'a pu placer ni dans le
        /// vocabulaire ni en nouveau genre (information pour l'usager).
        /// </summary>
        private static List<GenreProposalDto> ParseProposals(string reply,
            List<string> movieSent, List<string> seriesSent,
            List<string> movieAllowed, List<string> seriesAllowed,
            out List<string> orphans)
        {
            var proposals = new List<GenreProposalDto>();
            orphans = new List<string>();
            var payload = LlmRunner.ExtractJsonPayload(reply ?? string.Empty);
            if (string.IsNullOrEmpty(payload))
            {
                FillOrphans(orphans, movieSent, seriesSent, null);
                return proposals;
            }

            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    FillOrphans(orphans, movieSent, seriesSent, null);
                    return proposals;
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var genre = el.TryGetProperty("genre", out var jg) ? jg.GetString() : null;
                    if (string.IsNullOrWhiteSpace(genre) || !seen.Add(genre.Trim())) continue;
                    genre = genre.Trim();
                    bool inMovies = movieSent.Contains(genre, StringComparer.OrdinalIgnoreCase);
                    bool inSeries = seriesSent.Contains(genre, StringComparer.OrdinalIgnoreCase);

                    string movies = null, series = null;
                    if (inMovies)
                    {
                        var v = el.TryGetProperty("movies", out var jm) ? jm.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(v) &&
                            !GenreCleanerMap.IsIdentity(genre, v) && // self-mapping = no-op
                            movieAllowed.Contains(v.Trim(), StringComparer.OrdinalIgnoreCase))
                            movies = v.Trim();
                    }
                    if (inSeries)
                    {
                        var v = el.TryGetProperty("series", out var js) ? js.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(v) &&
                            !GenreCleanerMap.IsIdentity(genre, v) && // self-mapping = no-op
                            seriesAllowed.Contains(v.Trim(), StringComparer.OrdinalIgnoreCase))
                            series = v.Trim();
                    }

                    // Nouveau genre suggéré — seulement à défaut de cible
                    // existante (règle (7) du prompt).
                    string newGenre = null;
                    if (movies == null && series == null)
                    {
                        var v = el.TryGetProperty("new", out var jn) ? jn.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(v) && v.Trim().Length <= MaxNewGenreLength)
                            newGenre = v.Trim();
                    }

                    if (newGenre != null)
                        proposals.Add(new GenreProposalDto
                        {
                            Genre = genre,
                            New = newGenre,
                            InMovies = inMovies,
                            InSeries = inSeries
                        });
                    else if (movies != null || series != null)
                        proposals.Add(new GenreProposalDto
                        {
                            Genre = genre,
                            Movies = movies,
                            Series = series,
                            InMovies = inMovies,
                            InSeries = inSeries
                        });
                    else if (inMovies || inSeries)
                        orphans.Add(genre);
                }

                // Genres envoyés mais jamais mentionnés dans la réponse.
                FillOrphans(orphans, movieSent, seriesSent, seen);
            }
            catch { /* JSON ininterprétable : propositions déjà collectées */ }
            return proposals;
        }

        /// <summary>
        /// Complète <paramref name="orphans"/> : tous les genres envoyés
        /// absents de <paramref name="seen"/> (réponse du LLM), sans doublon.
        /// </summary>
        private static void FillOrphans(List<string> orphans,
            List<string> movieSent, List<string> seriesSent, HashSet<string> seen)
        {
            foreach (var g in movieSent.Concat(seriesSent))
            {
                if (seen != null && seen.Contains(g)) continue;
                if (!orphans.Contains(g, StringComparer.OrdinalIgnoreCase))
                    orphans.Add(g);
            }
        }

        // ------------------------------------------------------------------
        //  Langue des genres curatés (prompt LLM)
        // ------------------------------------------------------------------

        /// <summary>
        /// Nom lisible de la langue des genres curatés de l'usager — celle
        /// des nouveaux genres que le LLM peut suggérer. Suit la cascade
        /// <see cref="I18n.ResolveMetaLangKey"/> (explicit
        /// <see cref="PluginConfiguration.ResponseLanguage"/> → langue
        /// d'affichage Emby → legacy <see cref="PluginConfiguration.TmdbLanguage"/>
        /// → anglais) puis mappe la clé 2 lettres vers un nom de langue.
        /// </summary>
        private string ResolveGenreLang(PluginConfiguration cfg)
        {
            switch (I18n.ResolveMetaLangKey(cfg, ApplicationHost))
            {
                case "fr": return "français";
                case "es": return "espagnol";
                case "de": return "allemand";
                case "it": return "italien";
                case "pt": return "portugais";
                default: return "anglais";
            }
        }

        // ------------------------------------------------------------------
        //  Auth : résolution de l'administrateur appelant
        // ------------------------------------------------------------------

        /// <summary>
        /// Résout l'usager à partir du token d'authentification (calqué sur
        /// <see cref="ChatApiService"/>). L'appelant vérifie ensuite
        /// <see cref="User.Policy"/>'s IsAdministrator.
        /// </summary>
        private User ResolveAdmin()
        {
            try
            {
                var auth = AuthorizationContext?.GetAuthorizationInfo(Request);
                var user = auth?.User;
                if (user == null && auth != null && auth.UserId != 0)
                    user = UserManager.GetUserById(auth.UserId);
                return user;
            }
            catch { return null; }
        }
    }
}