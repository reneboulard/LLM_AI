using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace LLM_AI
{
    /// <summary>
    /// Tâche planifiée hebdomadaire de la <b>mémoire réflexive</b> (opt-in
    /// <see cref="PluginConfiguration.MemoryCardEnabled"/>, défaut dimanche
    /// 4 h 30 — après l'analyse hebdo classique de 4 h). C'est la Phase C du
    /// concept : le LLM gère et met à jour lui-même sa <b>fiche mémoire</b>
    /// persistante (~250 mots, Markdown) — ses réussites, ses échecs, ses
    /// stratégies pour les prochains runs et chats.
    /// <para>
    /// Pour UN foyer (la fiche est globale) :
    /// <list type="bullet">
    /// <item>construit en C# (déterministe, zéro LLM) le tableau d'événements
    ///   bruts de la semaine : jointure des stores de la Phase A/B —
    ///   <see cref="DecisionStore"/> (décisions avec leur RAISON et la version
    ///   de fiche en vigueur) × <see cref="DecisionStore"/> télémétrie
    ///   (fraction lue : rejet immédiat &lt; 5 %, abandon 5–50 %, validé
    ///   &gt; 80 % ; v2.7.2 : jointure consommation en CASCADE — id d'item,
    ///   fenêtre du direct, puis TITRE/SÉRIE figé dans la télémétrie, qui
    ///   rattrape l'épisode ENREGISTRÉ d'une reco live et l'activation de
    ///   sa carte .strm) × pools de candidats (ce qui avait été écarté —
    ///   distingue une mauvaise reco d'une erreur de classement) ×
    ///   <see cref="EpgSnapshotStore"/> (durée de diffusion → % du direct) ;</item>
    /// <item>fait produire au LLM (un seul appel sans outils,
    ///   <see cref="LlmRunner.RunSynthesisAsync"/>) la NOUVELLE fiche, en
    ///   partant de l'actuelle ;</item>
    /// <item>persiste (version++, historisation immuable,
    ///   <see cref="MemoryCard.Save"/>) — elle est réinjectée dans les prompts
    ///   des runs suivants par <see cref="MemoryCard.BuildInjectionBlock"/> ;
    ///   quand la fiche est active, elle REMPLACE le bloc de directives de la
    ///   boucle de rétroaction classique dans les injections.</item>
    /// </list></para>
    /// <para><b>Fail-open</b> : pas de signal → pas de run ; un échec LLM
    /// conserve la fiche précédente ; aucune erreur ne casse les runs aval.</para>
    /// <para>Découverte par scanning d'assembly (comme
    /// <see cref="RecoAnalysisTask"/>) — services Emby injectés par DI.</para>
    /// </summary>
    public class MemoryTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _library;
        private readonly IServerApplicationHost _host;

        // Orchestration LLM partagée (résolution backends + appel sans
        // outils avec repli). Construite une fois à l'instanciation.
        private readonly LlmRunner _runner;

        public MemoryTask(
            ILogger logger,
            IJsonSerializer jsonSerializer,
            ILibraryManager library,
            IUserManager users,
            ILiveTvManager liveTv,
            IServerApplicationHost host)
        {
            _logger = logger;
            _library = library;
            _host = host;
            _runner = new LlmRunner(logger, jsonSerializer, library, users, liveTv, host);
        }

        public string Name => I18n.S("task.memory.name", I18n.ResolveDisplayLangKey(_host));

        /// <summary>Identifiant stable de la tâche.</summary>
        public string Key => "a9b4c7d1-6e52-4f83-9a10-3c7d5e8b2f41";

        public string Description => I18n.S("task.memory.desc", I18n.ResolveDisplayLangKey(_host));

        public string Category => I18n.S("task.category", I18n.ResolveDisplayLangKey(_host));

        public bool IsHidden => false;

        public bool IsEnabled => Plugin.Instance?.Configuration?.MemoryCardEnabled ?? false;

        public bool IsLogged => true;

        // ------------------------------------------------------------------
        //  Prompts (system)
        // ------------------------------------------------------------------

        internal const string MEMORY_ROLE =
            "Tu es l'assistant de recommandations TV/cinéma d'un serveur Emby domestique. " +
            "Une fois par semaine, tu réécris TA PROPRE FICHE MÉMOIRE : un résumé de ce que " +
            "tu sais de l'usager, de tes réussites et de tes échecs, et des stratégies " +
            "concrètes pour tes futures recommandations et conversations. On te fournit " +
            "ta fiche actuelle et les événements bruts de la semaine (décisions journalisées " +
            "avec leur résultat de visionnage, calibration de tes versions de fiche, " +
            "candidats écartés, visionnages sans recommandation).";

        internal const string MEMORY_RULES =
            "### TA TÂCHE\n" +
            "Produis la NOUVELLE fiche mémoire, en Markdown, qui remplacera l'actuelle.\n" +
            "### RÈGLES\n" +
            "- Repars TOUJOURS de la fiche actuelle : garde ce qui reste vrai, corrige ce que " +
            "  les événements contredisent, ajoute les nouveaux signaux. Ne repars jamais de zéro.\n" +
            "- UTILISE ces sections exactes (une seule fois chacune) :\n" +
            "  ## Ce que je sais de l'usager\n" +
            "  ## Ce qui a marché\n" +
            "  ## Ce qui a échoué et pourquoi\n" +
            "  ## Stratégies pour les prochaines recommandations\n" +
            "  ## Zones d'incertitude\n" +
            "- AUTO-ÉVALUATION : dans « Ce qui a échoué et pourquoi », nomme explicitement la " +
            "  croyance ou la stratégie qui a échoué (ex. « j'ai répété une reco similaire à " +
            "  une déjà ignorée »), pas seulement le titre raté.\n" +
            "- Nuance : un signal faible (1-2 cas) est formulé avec réserve (« semble », « un " +
            "  seul cas ») ; un signal fort (3+ cas) peut être affirmé. Ne retire une croyance " +
            "  que sur une preuve contraire (3+ cas ou calibration claire).\n" +
            "- PRÉSERVE LA DIVERSITÉ : n'interdis jamais une catégorie entière sur une seule " +
            "  semaine ; garde des axes d'exploration dans « Zones d'incertitude ».\n" +
            "- N'invente rien : chaque affirmation doit découler des événements fournis.\n" +
            "- Maximum ~250 mots STRICT. Puces courtes. Pas de JSON, pas de préambule. " +
            "La section STRATÉGIES doit rester COMPLÈTE et actionnable même si tu dois " +
            "raccourcir les autres — c'est elle qui pilote tes prochains runs.\n" +
            "- Réponds UNIQUEMENT par la fiche Markdown (commençant par « ## »), sans préambule.";

        // ------------------------------------------------------------------
        //  Exécution
        // ------------------------------------------------------------------

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
            {
                _logger?.Warn("[LLM_AI] Mémoire : aucune configuration disponible — ignorée.");
                return;
            }
            if (!cfg.MemoryCardEnabled)
            {
                _logger?.Info("[LLM_AI] Mémoire : désactivée (MemoryCardEnabled) — rien à faire.");
                return;
            }
            if (!(cfg.DecisionLogEnabled || cfg.PlaybackTelemetryEnabled))
            {
                _logger?.Info("[LLM_AI] Mémoire : aucun store activé (DecisionLogEnabled / PlaybackTelemetryEnabled) — rien à faire.");
                return;
            }

            progress?.Report(5);

            var weekCutoff = DateTimeOffset.Now.AddDays(-7);
            var decisions = DecisionStoreParse();
            var pools = DecisionStoreParsePools();
            var playback = DecisionStoreParsePlayback();
            var snapshot = EpgSnapshotStore.Parse();
            var (current, history) = MemoryCard.Load();

            var week = decisions.Where(d => d.Date >= weekCutoff).ToList();
            var weekPlayback = playback.Where(p => p.Date >= weekCutoff).ToList();
            if (week.Count == 0 && weekPlayback.Count == 0)
            {
                _logger?.Info("[LLM_AI] Mémoire : aucun événement cette semaine (décisions ni lectures) — rien à faire.");
                return;
            }
            _logger?.Info("[LLM_AI] Mémoire : {0} décision(s) et {1} lecture(s) cette semaine (store complet : {2} déc., {3} lect.).",
                week.Count, weekPlayback.Count, decisions.Count, playback.Count);

            // Porte LLM (capacité LlmMaxConcurrentTasks, défaut 1) : attend
            // un run LLM en cours au lieu de l'empiler sur le backend.
            await LlmTaskGate.AcquireAsync("Mémoire réflexive", _logger, cancellationToken).ConfigureAwait(false);

            try
            {
                string table = BuildEventTable(cfg, decisions, week, playback, weekPlayback,
                    pools, snapshot, current);
                if (table == null)
                {
                    _logger?.Info("[LLM_AI] Mémoire : tableau vide (aucun signal exploitable) — rien à faire.");
                    return;
                }

                progress?.Report(40);

                // Règle usager 2026-10-01 : config explicite, sinon langue de
                // l'interface Emby — la directive est toujours injectée.
                var system = MEMORY_ROLE + "\n\n" + MEMORY_RULES
                    + "\n\n" + LlmAgentService.BuildLanguageDirective(I18n.ResolveProseLangName(cfg, _host));
                var (reply, ok) = await _runner.RunSynthesisAsync(cfg, "MÉMOIRE", system, table,
                    cancellationToken).ConfigureAwait(false);
                if (!ok || string.IsNullOrWhiteSpace(reply))
                {
                    _logger?.Info("[LLM_AI] Mémoire : pas de réponse LLM — fiche précédente conservée.");
                    return;
                }

                var text = NormalizeCard(reply);
                if (string.IsNullOrWhiteSpace(text))
                {
                    _logger?.Info("[LLM_AI] Mémoire : réponse inutilisable — fiche précédente conservée.");
                    return;
                }

                progress?.Report(85);

                // Historisation immuable : la version courante devient une
                // entrée d'historique (les 4 dernières gardées).
                if (current.Version > 0 && !string.IsNullOrWhiteSpace(current.Text))
                {
                    history.Insert(0, new MemoryCardData
                    {
                        Version = current.Version,
                        Updated = current.Updated,
                        Text = current.Text
                    });
                    while (history.Count > MemoryCard.HistoryMax)
                        history.RemoveAt(history.Count - 1);
                }
                var updated = new MemoryCardData
                {
                    Version = current.Version + 1,
                    Updated = DateTimeOffset.UtcNow,
                    Text = text
                };
                MemoryCard.Save(updated, history, _logger);
                _logger?.Info("[LLM_AI] Mémoire : fiche v{0} enregistrée ({1} caractères) :\n{2}",
                    updated.Version, text.Length, text);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Fail-open : la fiche précédente reste en place.
                _logger?.ErrorException("[LLM_AI] Mémoire : échec global : {0}", ex, ex.Message);
                throw;
            }
            finally
            {
                LlmTaskGate.Release("Mémoire réflexive");
                progress?.Report(100);
            }
        }

        // ------------------------------------------------------------------
        //  Jointure déterministe (zéro LLM)
        // ------------------------------------------------------------------

        /// <summary>Catégorie comportementale d'une fraction lue.</summary>
        internal static string CategoryOf(double? pct)
        {
            if (!pct.HasValue) return "?";
            if (pct.Value < 0.05) return "rejet immédiat";
            if (pct.Value < 0.5) return "abandon";
            if (pct.Value < 0.8) return "partiel";
            return "validé";
        }

        /// <summary>
        /// Construit le tableau d'événements bruts de la semaine (texte) :
        /// décisions × résultats, calibration, écartés, vu-sans-reco, signaux
        /// explicites, fiche actuelle. Null si aucun signal exploitable.
        /// </summary>
        private string BuildEventTable(PluginConfiguration cfg,
            List<DecisionEntry> decisions, List<DecisionEntry> week,
            List<PlaybackEntry> playback, List<PlaybackEntry> weekPlayback,
            List<RunPool> pools, List<EpgSnapshotEntry> snapshot,
            MemoryCardData current)
        {
            var sb = new StringBuilder();

            // Index des pools par runId.
            var poolByRun = pools.Where(p => p != null && !string.IsNullOrEmpty(p.RunId))
                .GroupBy(p => p.RunId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            // Index des snapshots par programId.
            var snapByPid = snapshot.Where(s => s != null && !string.IsNullOrEmpty(s.ProgramId))
                .GroupBy(s => s.ProgramId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            // v2.7.2 — backfill titre/série : les entrées écrites avant le
            // figage (PlaybackWatcher) n'en portent pas ; résolution
            // bibliothèque best-effort, une fois par id. Les items
            // déplacés/supprimés depuis (routine DVR du foyer : dossiers
            // éphémères, migration quotidienne vers la bibliothèque
            // permanente) ne résolvent plus — c'est précisément ce que le
            // figage au moment de la lecture élimine pour l'avenir.
            var resolvedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in playback)
            {
                if (p == null || string.IsNullOrEmpty(p.ItemId) || resolvedIds.Contains(p.ItemId)) continue;
                if (string.IsNullOrWhiteSpace(p.Title) || string.IsNullOrWhiteSpace(p.SeriesName))
                {
                    resolvedIds.Add(p.ItemId);
                    try
                    {
                        var it = ItemIdResolver.Resolve(_library, p.ItemId);
                        if (string.IsNullOrWhiteSpace(p.Title))
                            p.Title = it?.Name ?? "";
                        if (string.IsNullOrWhiteSpace(p.SeriesName))
                            p.SeriesName = (it as Episode)?.SeriesName
                                ?? (it as LiveTvProgram)?.SeriesName ?? "";
                    }
                    catch { /* best-effort : id non résolvable */ }
                }
            }

            // Index titre/série → lectures (v2.7.2, jointure de repli) :
            // couvre TOUT le journal (30 j) — la consommation d'une reco
            // live/record est souvent DIFFÉRÉE (enregistrement regardé des
            // jours plus tard) et l'item regardé ne porte jamais l'id du
            // programme EPG pointé par la décision.
            var playByTitle = new Dictionary<string, List<PlaybackEntry>>(StringComparer.Ordinal);
            foreach (var p in playback)
            {
                IndexPlay(playByTitle, p.Title, p);
                IndexPlay(playByTitle, p.SeriesName, p);
            }

            var decisionTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in decisions)
                decisionTitles.Add(LlmRunner.NormTitle(d.Title));

            // 1) Décisions de la semaine × résultat de visionnage.
            sb.AppendLine("### DÉCISIONS DE LA SEMAINE (recommandations émises, avec leur raison)");
            sb.AppendLine("(résultat : jointures en cascade — id d'item, fenêtre du direct, puis TITRE/SÉRIE ;");
            sb.AppendLine(" un épisode ENREGISTRÉ d'une reco, ou l'activation de sa carte .strm, COMPTE comme consommation ;");
            sb.AppendLine(" IGNORÉ = rien de rattaché ; « durée inconnue » = lecture dont le client n'a pas remonté la position)");
            sb.AppendLine("(créneau = heure locale de la lecture ; v = version de la fiche en vigueur à l'émission)");
            int shown = 0;
            const int DecisionCap = 40;
            foreach (var d in week.OrderBy(d => d.Date))
            {
                if (d.Kind == "drop") continue; // signalés séparément
                decisionTitles.Add(LlmRunner.NormTitle(d.Title));
                var outcome = OutcomeOf(d, playback, snapByPid, playByTitle);
                var cr = d.Kind == "record" ? "(foyer)" : "";
                sb.AppendLine("- [" + d.Kind + (d.Mv > 0 ? " v" + d.Mv.ToString(CultureInfo.InvariantCulture) : "") + "] « " + d.Title + " » " + cr
                    + (string.IsNullOrWhiteSpace(d.Reason) ? "" : " — raison: " + d.Reason)
                    + " → " + outcome);
                shown++;
                if (shown >= DecisionCap) break;
            }
            if (shown == 0) sb.AppendLine("(aucune)");
            if (shown < week.Count(d => d.Kind != "drop"))
                sb.AppendLine("… (+" + (week.Count(d => d.Kind != "drop") - shown) + " autres)");

            // 2) Calibration des versions de fiche.
            var byMv = week.Where(d => d.Kind != "drop" && d.Mv > 0).GroupBy(d => d.Mv).ToList();
            if (byMv.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### CALIBRATION DES VERSIONS DE LA FICHE (ta propre auto-évaluation)");
                foreach (var g in byMv.OrderBy(g => g.Key))
                {
                    int ok = 0, bad = 0, ignored = 0;
                    foreach (var d in g)
                    {
                        var o = OutcomeOf(d, playback, snapByPid, playByTitle);
                        // v2.7.2 : « lu » (position non remontée), « CARTE
                        // activée » et « direct touché » comptent comme
                        // consommation — pas comme de l'ignoré.
                        if (o.StartsWith("validé") || o.StartsWith("partiel") || o.StartsWith("lu")
                            || o.StartsWith("CARTE") || o.StartsWith("direct touché")) ok++;
                        else if (o.StartsWith("rejet") || o.StartsWith("abandon")) bad++;
                        else ignored++;
                    }
                    sb.AppendLine("- recos émises sous la fiche v" + g.Key + " : " + g.Count()
                        + " — consommées " + ok + " (validé/partiel/lu/carte), rejet/abandon " + bad + ", ignoré " + ignored);
                }
                sb.AppendLine("(si une version ancienne perforne nettement mieux que la courante, " +
                              "réexamine les changements que tu avais introduits)");
            }

            // 3) Candidats écartés (pools des runs de la semaine).
            sb.AppendLine();
            sb.AppendLine("### CANDIDATS ÉCARTÉS (présents dans le menu soumis, non recommandés)");
            int poolsShown = 0;
            foreach (var d in week.Where(d => d.Kind != "drop" && !string.IsNullOrEmpty(d.RunId))
                .Select(d => d.RunId).Distinct(StringComparer.Ordinal).Take(6))
            {
                if (!poolByRun.TryGetValue(d, out var pool) || pool?.Candidates == null) continue;
                var chosen = week.Where(x => string.Equals(x.RunId, d, StringComparison.Ordinal))
                    .Select(x => LlmRunner.NormTitle(x.Title)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var notChosen = pool.Candidates
                    .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Title)
                                && !chosen.Contains(LlmRunner.NormTitle(c.Title)))
                    .Take(8).ToList();
                if (notChosen.Count == 0) continue;
                sb.AppendLine("- run " + d + " (" + pool.Date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ", " +
                    pool.Candidates.Count + " candidats) — écartés notamment : "
                    + string.Join(" ; ", notChosen.Select(c => "« " + c.Title + " » (" + c.Source + ")")));
                poolsShown++;
            }
            if (poolsShown == 0) sb.AppendLine("(aucun pool disponible cette semaine)");

            // 4) Vu sans recommandation (opportunités manquées). Le titre
            // (et la série, v2.7.2) est figé dans l'entrée — repli
            // résolution bibliothèque via le backfill ci-dessus — pour
            // exclure proprement ce qui figure déjà dans une décision :
            // PAR TITRE (film) OU PAR SÉRIE (l'épisode enregistré d'une
            // série recommandée n'est PAS une opportunité manquée). Une
            // carte .strm est l'activation d'une reco — jamais une
            // « vue sans reco ». Le filtre de substance tolère la
            // télémétrie sans position (vécu prod : 175/176 entrées à
            // dur=0 — un client qui ne remonte pas la position n'est pas
            // un zap).
            sb.AppendLine();
            sb.AppendLine("### VU SANS RECOMMANDATION (cette semaine — opportunités manquées)");
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            int surprisesShown = 0;
            foreach (var p in weekPlayback
                .Where(p => p != null && !string.IsNullOrEmpty(p.ItemId)
                    && ((p.Pct ?? 0) >= 0.5 || p.Completed || p.DurSec >= 300))
                .OrderByDescending(p => p.Date))
            {
                if (!seenIds.Add(p.ItemId)) continue;
                if (string.Equals(p.Src, "strm", StringComparison.OrdinalIgnoreCase)) continue; // activation d'une reco
                var title = p.Title;
                var series = p.SeriesName;
                if (!string.IsNullOrWhiteSpace(title)
                    && decisionTitles.Contains(LlmRunner.NormTitle(title)))
                    continue; // déjà recommandé (jointure titre)
                if (!string.IsNullOrWhiteSpace(series)
                    && decisionTitles.Contains(LlmRunner.NormTitle(series)))
                    continue; // série recommandée (jointure série, v2.7.2)
                var slot = p.Date.ToLocalTime();
                sb.AppendLine("- « " + (string.IsNullOrWhiteSpace(title) ? "?" : title)
                    + (string.IsNullOrWhiteSpace(series) ? "" : " [" + series + "]")
                    + " » (" + CategoryOf(p.Pct)
                    + ", " + slot.ToString("ddd HH\\hmm", CultureInfo.CurrentCulture)
                    + ", " + p.Src + ")");
                surprisesShown++;
                if (surprisesShown >= 12) break;
            }
            if (surprisesShown == 0) sb.AppendLine("(rien)");

            // 5) Signaux explicites (rejets).
            var drops = week.Where(d => d.Kind == "drop").ToList();
            sb.AppendLine();
            sb.AppendLine("### SIGNAUX EXPLICITES (rejets « Oublier »)");
            if (drops.Count == 0) sb.AppendLine("(aucun)");
            foreach (var d in drops.Take(15))
                sb.AppendLine("- « " + d.Title + " »");

            // 6) Contexte situationnel agrégé (créneaux de lecture).
            sb.AppendLine();
            sb.AppendLine("### CRÉNEAUX DE LECTURE (cette semaine, toutes lectures)");
            foreach (var g in weekPlayback.GroupBy(p => new
                     {
                         Jour = (int)p.Date.ToLocalTime().DayOfWeek,
                         Creneau = p.Date.ToLocalTime().Hour / 4   // tranche 4 h
                     }).OrderBy(g => g.Key.Jour).ThenBy(g => g.Key.Creneau))
            {
                sb.AppendLine("- " + JourFr(g.Key.Jour) + " " + (g.Key.Creneau * 4) + "h-"
                    + ((g.Key.Creneau * 4) + 4) + "h : " + g.Count() + " lecture(s), % moyen "
                    + (g.Average(p => p.Pct ?? 0) * 100).ToString("0", CultureInfo.InvariantCulture) + " %");
            }

            // 7) Fiche actuelle.
            sb.AppendLine();
            sb.AppendLine("### TA FICHE ACTUELLE (v" + current.Version + ")");
            sb.AppendLine(string.IsNullOrWhiteSpace(current.Text)
                ? "(aucune — première rédaction)"
                : current.Text);

            return sb.ToString();
        }

        /// <summary>Résultat de visionnage d'une décision (texte court).
        /// Trois jointures en cascade (v2.7.2) :
        /// <list type="number">
        /// <item><b>id d'item</b> — bibliothèque/enregistrement pointé
        /// directement par la reco (jointure exacte) ;</item>
        /// <item><b>fenêtre du direct</b> — programme EPG regardé à
        /// l'antenne (chaîne + fenêtre de diffusion du snapshot) ;</item>
        /// <item><b>titre/série</b> — rattrape la consommation dominante
        /// du foyer : l'usager regarde l'<b>enregistrement</b> d'une reco
        /// live/record (item distinct du programme EPG pointé, souvent
        /// déplacé hors du DVR avant l'analyse) ou active sa carte
        /// <c>.strm</c> (le titre de la carte est celui de la reco). Le
        /// couple (titre, série) figé dans la télémétrie porte la
        /// jointure.</item>
        /// </list>
        /// Vécu prod (fiche v7, 2026-10-25) : sans la 3e jointure, les
        /// recos live/record sont mesurées uniquement sur le direct — le
        /// foyer consomme 88 % de ses lectures en bibliothèque → taux
        /// apparent 0-2 % → la fiche a conclu « l'usager ignore toutes
        /// mes suggestions » et a ordonné l'arrêt des suggestions sur une
        /// leçon de mesure, pas de comportement.</summary>
        private static string OutcomeOf(DecisionEntry d, List<PlaybackEntry> playback,
            Dictionary<string, EpgSnapshotEntry> snapByPid,
            Dictionary<string, List<PlaybackEntry>> playByTitle)
        {
            // 1) Item bibliothèque/enregistrement : jointure directe par itemId.
            if (!string.IsNullOrEmpty(d.ItemId))
            {
                var p = playback.Where(x => string.Equals(x.ItemId, d.ItemId, StringComparison.Ordinal))
                    .OrderByDescending(x => x.Date).FirstOrDefault();
                if (p != null)
                    return PlayOutcome(p, "jointure id");
            }

            // 2) Programme EPG (direct) : jointure via le snapshot (chaîne +
            // fenêtre de diffusion) → % = durée lue ÷ durée de diffusion.
            // dur=0 (client sans position au stop) : lecture touchée, pas
            // un « rejet immédiat » (v2.7.2 — un zap et un client muet ne
            // se distinguent pas au log, l'honnêteté prime).
            if (!string.IsNullOrEmpty(d.ProgramId)
                && snapByPid.TryGetValue(d.ProgramId, out var snap)
                && snap.RuntimeMin.HasValue && snap.RuntimeMin.Value > 0)
            {
                var start = snap.Start;
                var end = start.AddMinutes(snap.RuntimeMin.Value + 30);
                var p = playback.Where(x =>
                        string.Equals(x.Src, "livetv", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.Channel ?? "", snap.Channel ?? "", StringComparison.OrdinalIgnoreCase)
                        && x.Date >= start && x.Date <= end)
                    .OrderByDescending(x => x.Date).FirstOrDefault();
                if (p != null)
                {
                    if (p.DurSec <= 0)
                        return "direct touché (durée inconnue, " + SlotOf(p) + ")";
                    var pct = (double)p.DurSec / (snap.RuntimeMin.Value * 60);
                    pct = Math.Max(0.0, Math.Min(1.0, pct));
                    return CategoryOf(pct) + " (direct " + snap.Channel + ", " + snap.RuntimeMin.Value + " min, "
                        + (pct * 100).ToString("0", CultureInfo.InvariantCulture) + " %, " + SlotOf(p) + ")";
                }
            }

            // 3) Titre/série (v2.7.2) : repli quand id et direct n'ont
            // rien joint. La carte .strm est un geste d'ACTION (clic →
            // programmation), signal positif distinct d'un visionnage.
            var key = LlmRunner.NormTitle(d.Title ?? "");
            if (key.Length > 0 && playByTitle.TryGetValue(key, out var tps) && tps.Count > 0)
            {
                var p = tps.OrderByDescending(x => x.Date).First();
                if (string.Equals(p.Src, "strm", StringComparison.OrdinalIgnoreCase))
                    return "CARTE .strm activée (l'usager a agi sur cette reco, " + SlotOf(p) + ")";
                return PlayOutcome(p, "jointure titre/série");
            }

            if (!string.IsNullOrEmpty(d.ItemId) || !string.IsNullOrEmpty(d.ProgramId))
                return "IGNORÉ (aucune lecture rattachée)";
            return "? (non joignable — item sans id ni titre exploitable)";
        }

        /// <summary>Libellé d'une lecture jointe par id ou par titre :
        /// catégorie par % lu (ou complétion), texte de durée robuste au
        /// client qui ne remonte pas la position au stop (vécu prod :
        /// 175/176 entrées à dur=0 — la lecture a BIEN eu lieu).</summary>
        private static string PlayOutcome(PlaybackEntry p, string via)
        {
            var effPct = p.Pct ?? (p.Completed ? 1.0 : (double?)null);
            var cat = effPct.HasValue ? CategoryOf(effPct) : "lu";
            string pctTxt = effPct.HasValue
                ? (effPct.Value * 100).ToString("0", CultureInfo.InvariantCulture) + " %"
                : (p.DurSec > 0 ? "~" + (p.DurSec / 60) + " min lues" : "durée inconnue");
            return cat + " (" + pctTxt + ", " + (p.Src ?? "?") + ", " + via + ", " + SlotOf(p) + ")";
        }

        /// <summary>Indexe une lecture par un titre (ou nom de série)
        /// normalisé — clé de la jointure de repli v2.7.2.</summary>
        private static void IndexPlay(Dictionary<string, List<PlaybackEntry>> idx,
            string title, PlaybackEntry p)
        {
            var k = LlmRunner.NormTitle(title ?? "");
            if (k.Length == 0) return;
            if (!idx.TryGetValue(k, out var l))
                idx[k] = l = new List<PlaybackEntry>();
            l.Add(p);
        }

        private static string SlotOf(PlaybackEntry p)
        {
            var lt = p.Date.ToLocalTime();
            return JourFr((int)lt.DayOfWeek) + " " + lt.Hour + "h";
        }

        private static string JourFr(int dow) => dow switch
        {
            0 => "dim", 1 => "lun", 2 => "mar", 3 => "mer", 4 => "jeu", 5 => "ven", _ => "sam"
        };

        /// <summary>Nettoie la réponse LLM en fiche : retire les clôtures de
        /// code, borne au plafond. Vide si rien d'utilisable.</summary>
        internal static string NormalizeCard(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return "";
            var t = reply.Trim();
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                int firstNl = t.IndexOf('\n');
                if (firstNl > 0) t = t.Substring(firstNl + 1);
                int fence = t.LastIndexOf("```", StringComparison.Ordinal);
                if (fence >= 0) t = t.Substring(0, fence);
            }
            return t.Trim();
        }

        // ------------------------------------------------------------------
        //  Accès stores (wrappers tolérants)
        // ------------------------------------------------------------------

        private static List<DecisionEntry> DecisionStoreParse() => DecisionStore.ParseAllDecisions();

        private static List<RunPool> DecisionStoreParsePools() => DecisionStore.ParseAllPools();

        private static List<PlaybackEntry> DecisionStoreParsePlayback() => DecisionStore.ParseAllPlayback();

        // ------------------------------------------------------------------
        //  Déclencheur
        // ------------------------------------------------------------------

        /// <summary>Dimanche 4 h 30 — après l'analyse hebdo classique (4 h),
        /// avant les runs diurnes. La tâche ne tourne que si
        /// <see cref="PluginConfiguration.MemoryCardEnabled"/> est coché.</summary>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = "WeeklyTrigger",
                DayOfWeek = 0,
                // 4h40 : décalé de l'analyse hebdo (4h10) — cf. LlmTaskGate.
                TimeOfDayTicks = new TimeSpan(4, 40, 0).Ticks
            };
        }
    }
}