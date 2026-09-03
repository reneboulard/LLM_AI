using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace LLM_AI
{
    /// <summary>
    /// Tâche planifiée hebdomadaire de la boucle de rétroaction des
    /// recommandations (opt-in <see cref="PluginConfiguration.RecoFeedbackEnabled"/>,
    /// défaut dimanche 4 h). Pour chaque usager :
    /// <list type="bullet">
    /// <item>construit en C# (déterministe, zéro LLM pour le rassemblement)
    ///   le tableau de corrélation de la semaine : recommandations de
    ///   l'usager (run « À regarder ce soir ») + recommandations
    ///   d'enregistrement globales (foyer), chacune classée REGARDÉE /
    ///   IGNORÉE selon l'historique réellement joué (<c>DatePlayed</c> via
    ///   <see cref="IUserDataManager"/>) ; rejets explicites (« Oublier ») ;
    ///   visionnages SANS recommandation (opportunités manquées) ;</item>
    /// <item>fait produire au LLM (un seul appel sans outils,
    ///   <see cref="LlmRunner.RunSynthesisAsync"/>) une NOUVELLE DIRECTIVE
    ///   concise, en partant de la directive de la semaine précédente ;</item>
    /// <item>persiste la directive (<see cref="RecoFeedback.SaveDirectives"/>)
    ///   — elle sera réinjectée dans les prompts des runs suivants par
    ///   <see cref="TonightService"/> (par usager) et
    ///   <see cref="LlmScheduledTask"/> (fusion des usagers).</item>
    /// </list>
    /// <para><b>Fail-open</b> : un usager sans signal (aucune reco, aucun
    /// rejet) est sauté ; un échec LLM conserve la directive précédente ;
    /// aucune erreur ne casse la tâche ni les runs aval.</para>
    /// <para>Découverte par scanning d'assembly (comme
    /// <see cref="LlmScheduledTask"/>) — aucune inscription dans
    /// <c>Plugin.cs</c>. Services Emby injectés par DI.</para>
    /// </summary>
    public class RecoAnalysisTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly ILiveTvManager _liveTv;
        private readonly IServerApplicationHost _host;
        private readonly IUserDataManager _userData;

        // Orchestration LLM partagée (résolution backends + appel sans
        // outils avec repli). Construite une fois à l'instanciation.
        private readonly LlmRunner _runner;

        public RecoAnalysisTask(
            ILogger logger,
            IJsonSerializer jsonSerializer,
            ILibraryManager library,
            IUserManager users,
            ILiveTvManager liveTv,
            IServerApplicationHost host,
            IUserDataManager userData)
        {
            _logger = logger;
            _library = library;
            _users = users;
            _liveTv = liveTv;
            _host = host;
            _userData = userData;
            _runner = new LlmRunner(logger, jsonSerializer, library, users, liveTv, host);
        }

        public string Name => I18n.S("task.analysis.name", I18n.ResolveDisplayLangKey(_host));

        /// <summary>Identifiant stable de la tâche.</summary>
        public string Key => "c5e8a2f7-3b91-4d64-8a70-9f2c1d6e4b53";

        public string Description => I18n.S("task.analysis.desc", I18n.ResolveDisplayLangKey(_host));

        public string Category => I18n.S("task.category", I18n.ResolveDisplayLangKey(_host));

        public bool IsHidden => false;

        /// <summary>Reflete l'opt-in config : la tâche n'est planifiée par
        /// Emby que si la boucle de rétroaction est activée (le planificateur
        /// Emby relit cette propriété ; un lancement manuel passe quand même
        /// dans <see cref="Execute"/> qui re-vérifie).</summary>
        public bool IsEnabled => Plugin.Instance?.Configuration?.RecoFeedbackEnabled ?? false;

        public bool IsLogged => true;

        // ------------------------------------------------------------------
        //  Prompts d'analyse (system)
        // ------------------------------------------------------------------

        /// <summary>Rôle du system prompt de la synthèse hebdo.</summary>
        internal const string ANALYSIS_ROLE =
            "Tu es un analyste de recommandations TV/cinéma pour un serveur Emby domestique. " +
            "On te fournit, pour UN usager : les recommandations faites cette semaine par " +
            "l'assistant (ce qu'il a suggéré de regarder ou d'enregistrer), ce que l'usager a " +
            "réellement regardé, les titres explicitement rejetés, et la directive de la " +
            "semaine précédente.";

        /// <summary>Règles de production de la directive (system prompt).</summary>
        internal const string ANALYSIS_RULES =
            "### TA TÂCHE\n" +
            "Produis une NOUVELLE DIRECTIVE de recommandation, concise et actionnable, qui\n" +
            "sera injectée telle quelle dans les prompts futurs de l'assistant.\n" +
            "### RÈGLES\n" +
            "- Réponds UNIQUEMENT par la directive : liste à puces courtes, sans préambule,\n" +
            "  sans conclusion, sans JSON, sans titre de section.\n" +
            "- Maximum ~900 caractères ; chaque puce = une tendance concrète et actionnable\n" +
            "  (genre, époque, langue, format, chaîne, style de raison qui motive l'usager).\n" +
            "- Repars de la directive précédente : garde ce qui reste valable, remplace ce\n" +
            "  que la nouvelle semaine contredit.\n" +
            "- Un signal faible (1-2 cas) doit être nuancé (« semble préférer ») ; un signal\n" +
            "  fort (3+ cas) peut être affirmé.\n" +
            "- PRÉSERVE LA DIVERSITÉ : ne réduis pas l'usager à un seul genre ; garde des axes\n" +
            "  d'exploration ; n'interdis jamais une catégorie entière sur la seule base de\n" +
            "  cette semaine.\n" +
            "- N'invente rien : chaque puce doit découler des données fournies.\n" +
            "- Formule des consignes pour l'assistant (« privilégier X », « éviter Y sauf\n" +
            "  signal contraire »), pas une description de l'usager.";

        // ------------------------------------------------------------------
        //  Exécution
        // ------------------------------------------------------------------

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
            {
                _logger?.Warn("[LLM_AI] Analyse hebdo : aucune configuration disponible — ignorée.");
                return;
            }
            if (!cfg.RecoFeedbackEnabled)
            {
                _logger?.Info("[LLM_AI] Analyse hebdo : boucle de rétroaction désactivée (RecoFeedbackEnabled) — rien à faire.");
                return;
            }

            progress?.Report(5);

            // Journal (déjà pruné à l'écriture) : fenêtre d'analyse = 7 jours.
            var log = RecoFeedback.ParseLog(cfg);
            if (log.Count == 0)
            {
                _logger?.Info("[LLM_AI] Analyse hebdo : journal vide — rien à analyser.");
                return;
            }

            var weekCutoff = DateTimeOffset.Now.AddDays(-7);
            var week = log.Where(e => e.Date >= weekCutoff).ToList();
            if (week.Count == 0)
            {
                _logger?.Info("[LLM_AI] Analyse hebdo : aucune reco/rejet dans les 7 derniers jours — rien à analyser.");
                return;
            }
            _logger?.Info("[LLM_AI] Analyse hebdo : {0} entrée(s) de journal, dont {1} cette semaine.", log.Count, week.Count);

            try
            {
                var users = (_users.GetUserList(new UserQuery()) ?? Enumerable.Empty<User>()).ToList();
                int analyzed = 0, updated = 0;
                var directives = RecoFeedback.ParseDirectives(cfg);
                bool changed = false;

                int total = Math.Max(1, users.Count);
                int idx = 0;
                foreach (var user in users)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    idx++;
                    if (user == null) continue;

                    try
                    {
                        string table = BuildWeeklyTable(cfg, user, week, log, weekCutoff);
                        if (table == null) continue; // pas de signal pour cet usager
                        analyzed++;

                        progress?.Report(10 + 80.0 * idx / total);

                        string system = BuildSystemPrompt(cfg);
                        var (reply, ok) = await _runner.RunSynthesisAsync(cfg, "ANALYSE",
                            system, table, cancellationToken).ConfigureAwait(false);
                        if (!ok || string.IsNullOrWhiteSpace(reply))
                        {
                            _logger?.Info("[LLM_AI] Analyse hebdo : pas de réponse LLM pour « {0} » — directive précédente conservée.", user.Name);
                            continue;
                        }

                        string text = NormalizeDirective(reply);
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        string uid = user.Id.ToString();
                        directives.RemoveAll(d => string.Equals(d.User, uid, StringComparison.OrdinalIgnoreCase));
                        directives.Add(new RecoDirective
                        {
                            User = uid,
                            Name = user.Name ?? "",
                            Date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            Text = text
                        });
                        changed = true;
                        updated++;
                        _logger?.Info("[LLM_AI] Analyse hebdo : nouvelle directive pour « {0} » ({1} caractères) :\n{2}",
                            user.Name, text.Length, text);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger?.Warn("[LLM_AI] Analyse hebdo : usager « {0} » échoué : {1}", user.Name, ex.Message);
                    }
                }

                if (changed)
                    RecoFeedback.SaveDirectives(cfg, directives, _logger);

                _logger?.Info("[LLM_AI] Analyse hebdo terminée : {0} usager(s) avec signal, {1} directive(s) mise(s) à jour.",
                    analyzed, updated);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Fail-open : une tâche en échec ne touche à rien — les
                // directives précédentes restent en place.
                _logger?.ErrorException("[LLM_AI] Analyse hebdo : échec global : {0}", ex, ex.Message);
                throw;
            }
            finally
            {
                progress?.Report(100);
            }
        }

        // ------------------------------------------------------------------
        //  Tableau de corrélation (déterministe C#, zéro LLM)
        // ------------------------------------------------------------------

        /// <summary>
        /// Construit le tableau de corrélation textuel de la semaine pour un
        /// usager : recos classées REGARDÉ/IGNORÉ, rejets, visionnages sans
        /// reco, directive précédente. Retourne null si l'usager n'a aucun
        /// signal cette semaine (aucune reco propre ou globale, aucun rejet).
        /// Ne lève pas (fail-open par usager).
        /// </summary>
        private string BuildWeeklyTable(PluginConfiguration cfg, User user,
            List<RecoLogEntry> week, List<RecoLogEntry> log,
            DateTimeOffset weekCutoff)
        {
            string uid = user.Id.ToString();

            // Recos de la semaine : les tonight de CET usager + les record
            // globaux (reco d'enregistrement du foyer, attribuée à tous) +
            // ses rejets explicites.
            var recos = week.Where(e => e.Kind == "tonight"
                && string.Equals(e.User, uid, StringComparison.OrdinalIgnoreCase)).ToList();
            var records = week.Where(e => e.Kind == "record").ToList();
            var drops = week.Where(e => e.Kind == "drop"
                && string.Equals(e.User, uid, StringComparison.OrdinalIgnoreCase)).ToList();
            if (recos.Count == 0 && records.Count == 0 && drops.Count == 0) return null;

            // Historique réellement joué de la semaine (fenêtre 8 jours :
            // couvre la semaine log + la marge de la tâche du dimanche 4 h).
            // DatePlayed n'est pas filtrable en requête — on prend les 250
            // items joués les plus récents, puis on filtre en C# via
            // IUserDataManager (exact par item).
            var playedMovies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var playedSeries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var playedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var playedInfos = new List<(string Title, bool IsSeries, string Genres)>();
            try
            {
                string excludedRoot = StrmLibraryGenerator.ResolveLibraryRoot(
                    _library, cfg.StrmLibraryName, _logger);
                var minPlayed = DateTime.Now.AddDays(-8);
                var q = new InternalItemsQuery
                {
                    User = user,
                    IsPlayed = true,
                    Recursive = true,
                    IncludeItemTypes = new[] { "Episode", "Movie" },
                    OrderBy = new[] { ("DatePlayed", SortOrder.Descending) },
                    Limit = 250,
                    EnableTotalRecordCount = false
                };
                foreach (var it in _library.GetItemList(q) ?? Array.Empty<BaseItem>())
                {
                    var episode = it as MediaBrowser.Controller.Entities.TV.Episode;
                    if (it == null) continue;
                    if (IsUnderPath(it.Path, excludedRoot)) continue; // cartes .strm ≠ contenu regardé

                    var played = _userData?.GetUserData(user, it)?.LastPlayedDate;
                    if (!(played.HasValue && played.Value >= minPlayed)) continue;

                    playedIds.Add(it.InternalId.ToString());
                    string title = episode != null ? episode.SeriesName : it.Name;
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    string genres = string.Join(", ", it.Genres ?? Array.Empty<string>());
                    if (episode != null) playedSeries.Add(LlmRunner.NormTitle(episode.SeriesName));
                    else playedMovies.Add(LlmRunner.NormTitle(it.Name));
                    playedInfos.Add((title, episode != null, genres));
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Analyse hebdo : historique de « {0} » indisponible ({1}) — usager sauté (fail-open).",
                    user.Name, ex.Message);
                return null;
            }

            // Titres déjà recommandés (30 derniers jours, tous kinds/usagers) :
            // un visionnage de l'un d'eux n'est pas une « surprise ».
            var knownRecoTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in log)
                knownRecoTitles.Add(LlmRunner.NormTitle(e.Title));
            foreach (var e in week)
                knownRecoTitles.Add(LlmRunner.NormTitle(e.Title));

            var sb = new StringBuilder();
            sb.AppendLine("### RECOMMANDATIONS FAITES CETTE SEMAINE");
            sb.AppendLine("(REGARDÉ = l'usager a joué l'item/un épisode de la série après la reco ; " +
                         "IGNORÉ = aucune trace de visionnage)");
            int hits = 0, misses = 0, missShown = 0;
            const int MissCap = 20;
            foreach (var grp in new[] { recos, records })
            {
                foreach (var e in grp)
                {
                    string key = LlmRunner.NormTitle(e.Title);
                    bool hit = (!string.IsNullOrEmpty(e.Id) && playedIds.Contains(e.Id))
                        || (!string.IsNullOrEmpty(key) && (playedSeries.Contains(key) || playedMovies.Contains(key)));
                    if (hit)
                    {
                        hits++;
                        sb.AppendLine("- REGARDÉ : « " + e.Title + " » (" + Describe(e) + ")");
                    }
                    else
                    {
                        misses++;
                        if (missShown < MissCap)
                        {
                            missShown++;
                            sb.AppendLine("- IGNORÉ : « " + e.Title + " » (" + Describe(e) + ")");
                        }
                    }
                }
            }
            if (misses > missShown)
                sb.AppendLine("… (+" + (misses - missShown) + " autres recos ignorées)");
            if (hits == 0 && misses == 0)
                sb.AppendLine("(aucune)");

            if (drops.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### REJETS EXPLICITES (bouton « Oublier »)");
                foreach (var e in drops.Take(15))
                    sb.AppendLine("- « " + e.Title + " »");
                if (drops.Count > 15) sb.AppendLine("… (+" + (drops.Count - 15) + " autres)");
            }

            sb.AppendLine();
            sb.AppendLine("### VU SANS RECOMMANDATION (8 derniers jours, opportunités manquées)");
            var surprises = playedInfos
                .GroupBy(p => LlmRunner.NormTitle(p.Title), StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrEmpty(g.Key) && !knownRecoTitles.Contains(g.Key))
                .Select(g => g.First())
                .Take(15)
                .ToList();
            if (surprises.Count == 0) sb.AppendLine("(rien — l'usager a regardé ce qui lui avait été recommandé)");
            foreach (var p in surprises)
                sb.AppendLine("- « " + p.Title + " » (" + (p.IsSeries ? "série" : "film") +
                    (string.IsNullOrWhiteSpace(p.Genres) ? "" : " ; genres : " + p.Genres) + ")");

            sb.AppendLine();
            sb.AppendLine("### DIRECTIVE DE LA SEMAINE PRÉCÉDENTE");
            var old = RecoFeedback.DirectiveFor(cfg, uid);
            sb.AppendLine(old != null ? old.Text : "(aucune — première analyse)");

            return sb.ToString();
        }

        /// <summary>Libellé court d'une reco du journal (source/kind).</summary>
        private static string Describe(RecoLogEntry e)
        {
            switch ((e.Source ?? "").ToLowerInvariant())
            {
                case "live": return "EPG ce soir";
                case "recording": return "enregistrement";
                case "library": return "bibliothèque";
                case "series": return "série à enregistrer";
                case "movie": return "film à enregistrer";
                default: return e.Kind == "record" ? "à enregistrer" : (e.Source ?? "reco");
            }
        }

        /// <summary>
        /// Indique si <paramref name="itemPath"/> est situé sous le dossier
        /// <paramref name="root"/> (comparaison insensible à la casse). Null/
        /// vide sur l'un ou l'autre → false (rien à exclure). Réplique de
        /// <c>TonightService.IsUnderPath</c> (cette classe n'y a pas accès).
        /// </summary>
        private static bool IsUnderPath(string itemPath, string root)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(itemPath)) return false;
            string r = root.TrimEnd('/', '\\', System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            if (string.Equals(itemPath, r, StringComparison.OrdinalIgnoreCase)) return true;
            return itemPath.StartsWith(r + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || itemPath.StartsWith(r + System.IO.Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        //  Directive
        // ------------------------------------------------------------------

        private static string BuildSystemPrompt(PluginConfiguration cfg)
        {
            string s = ANALYSIS_ROLE + "\n\n" + ANALYSIS_RULES;
            var langDir = LlmAgentService.BuildLanguageDirective(cfg?.ResponseLanguage);
            if (langDir.Length > 0) s += "\n\n" + langDir;
            return s;
        }

        /// <summary>
        /// Nettoie la réponse LLM en directive : retire les balises markdown
        /// de code et les éventuels titres de section, borne la taille au
        /// plafond. Vide si rien d'utilisable.
        /// </summary>
        internal static string NormalizeDirective(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return "";
            var t = reply.Trim();
            // Balises ```…``` éventuelles.
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                int firstNl = t.IndexOf('\n');
                if (firstNl > 0) t = t.Substring(firstNl + 1);
                int fence = t.LastIndexOf("```", StringComparison.Ordinal);
                if (fence >= 0) t = t.Substring(0, fence);
            }
            var lines = t.Replace("\r", "").Split('\n');
            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                var l = line.Trim();
                if (l.Length == 0) { if (sb.Length > 0) sb.AppendLine(); continue; }
                // Titres de section éventuels (« ### Directive ») : retirés,
                // la directive est une liste à puces nue.
                if (l.StartsWith("#", StringComparison.Ordinal)) continue;
                sb.AppendLine(l);
            }
            return RecoFeedback.TruncateDirective(sb.ToString());
        }

        // ------------------------------------------------------------------
        //  Déclencheur
        // ------------------------------------------------------------------

        /// <summary>
        /// Trigger par défaut : hebdomadaire le dimanche à 4 h du matin —
        /// après le nettoyage nocturne « AI Tonight » (3 h) et la passe
        /// d'identification des orphelins (4 h), avant les runs diurnes de
        /// la journée. L'utilisateur peut l'ajuster dans le planificateur
        /// Emby. La tâche ne tourne que si RecoFeedbackEnabled est coché
        /// (<see cref="IsEnabled"/>).
        /// </summary>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = "WeeklyTrigger",
                DayOfWeek = 0,   // dimanche (DayOfWeek : dimanche = 0)
                TimeOfDayTicks = new TimeSpan(4, 0, 0).Ticks
            };
        }
    }
}