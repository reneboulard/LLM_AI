using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Auto-programmation des recommandations : crée les timers Emby (séries →
    /// <c>CreateSeriesTimer</c>, films → <c>CreateTimer</c>) pour les recos du
    /// <b>record bucket</b> — programmes EPG à venir, non déjà possédés, non
    /// déjà programmés, non dans la drop list. C'est ce qui fait ressortir les
    /// recos dans le guide EPG natif (badge d'enregistrement) sur tous les
    /// clients, y compris Android / Android TV qui ne rendent pas les pages
    /// plugin HTML.
    /// <para>Le <b>watch bucket</b> (recos déjà possédées / enregistrées /
    /// bibliothèque) n'est PAS programmé : il est surfacé séparément par le
    /// popup au login (<c>TonightLoginService</c>). Aucune reco possédée
    /// (<c>library_id</c> présent, ou match bibliothèque) n'est programmée —
    /// l'usager l'a déjà.</para>
    /// <para>Portage serveur de la logique « Programmer » de
    /// <c>recommendations.js</c> (recordSeries) : on ne POSTE pas un timer
    /// minimal (champs requis manquants — le serveur ne crée alors rien) ; on
    /// récupère d'abord les valeurs par défaut dérivées du programme via
    /// <see cref="ILiveTvManager.GetNewTimerDefaults(string,System.Threading.CancellationToken)"/>
    /// (Start/End/Channel/paddings), on les ajuste (RecordNewOnly /
    /// SkipEpisodesInLibrary pour les séries), puis on crée le timer.</para>
    /// <para><b>Gating absolu</b> : aucun timer n'est créé tant que
    /// <c>cfg.AutoProgram == false</c>. Les appelants
    /// (<c>LlmScheduledTask</c>, <c>TonightLoginService</c>) vérifient ce flag
    /// AVANT d'appeler <see cref="Program"/> — la méthode elle-même ne le
    /// revérifie pas (elle suppose l'opt-in acquis) pour garder la logique de
    /// bucket pure.</para>
    /// </summary>
    internal class AutoProgrammer
    {
        private readonly ILiveTvManager _liveTv;
        private readonly ILibraryManager _library;
        private readonly ILogger _logger;

        public AutoProgrammer(ILiveTvManager liveTv, ILibraryManager library, ILogger logger)
        {
            _liveTv = liveTv;
            _library = library;
            _logger = logger;
        }

        /// <summary>
        /// Bilan d'une passe d'auto-programmation. <see cref="Programmed"/> :
        /// timers créés. <see cref="SkippedDedup"/> : recos déjà couvertes par un
        /// timer existant. <see cref="SkippedOwnedOrDropped"/> : recos possédées
        /// (bibliothèque) ou dans la drop list. <see cref="SkippedNoId"/> : recos
        /// sans id de programme EPG (impossible à programmer).
        /// <see cref="Watch"/> : recos du watch bucket (rien à programmer).
        /// </summary>
        public struct ProgramStats
        {
            public int Programmed;
            public int SkippedDedup;
            public int SkippedOwnedOrDropped;
            public int SkippedNoId;
            public int Watch;
        }

        /// <summary>
        /// Crée les timers pour le record bucket du <paramref name="payload"
        /// />&gt; JSON (tableau de recommandations). Ne fait rien pour le watch
        /// bucket. Robuste : une reco qui échoue (timer conflictuel, programme
        /// introuvable…) est loguée et n'interrompt pas la suite. On programme
        /// par <c>priority</c> décroissante pour que, en cas de conflit de
        /// tuners sur des chevauchements horaires, les recos les moins
        /// prioritaires soient les sautées (queue basse).
        /// </summary>
        /// <param name="user">Usager (pour le log seulement — les timers Emby
        /// sont au niveau serveur, pas par usager). Null pour la tâche planifiée
        /// (admin).</param>
        public async Task<ProgramStats> Program(string payload, User user, PluginConfiguration cfg, CancellationToken ct)
        {
            var stats = new ProgramStats();
            if (string.IsNullOrWhiteSpace(payload)) return stats;

            // Liste ordonnée des recos (tableau JSON). On tri par priority desc
            // (high=0, medium=1, low=2) : les plus prioritaires passent d'abord,
            // les conflits de tuners éliminent les moins prioritaires.
            var recos = ParseRecommendations(payload);
            if (recos.Count == 0)
            {
                _logger?.Info("[LLM_AI] Auto-program : payload vide (rien à programmer).");
                return stats;
            }
            recos = recos
                .OrderBy(r => PriorityRank(r.Priority))
                .ToList();

            // Timers existants (dedup) : on croise ProgramId exact (single timers
            // + series timers) ET nom normalisé (un series timer couvre toute la
            // série, pas seulement le programme d'origine). Port serveur du check
            // « déjà programmée » de recommendations.js (markScheduledCards).
            var existingProgramIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingNames = new HashSet<string>(StringComparer.Ordinal);
            BuildExistingTimerSets(existingProgramIds, existingNames);

            string who = user != null ? user.Name : "(server)";
            _logger?.Info("[LLM_AI] Auto-program [{0}] : {1} reco(s) à considérer, {2} timer(s) existant(s) en dedup.",
                who, recos.Count, existingProgramIds.Count + existingNames.Count);

            foreach (var r in recos)
            {
                // Logique factorisée dans ProgramOneAsync (réutilisée par
                // l'endpoint /Plugins/LLMAI/Activate pour une reco unique).
                var outcome = await ProgramOneAsync(r, existingProgramIds, existingNames, ct).ConfigureAwait(false);
                switch (outcome)
                {
                    case OneOutcome.Created:          stats.Programmed++; break;
                    case OneOutcome.Watch:            stats.Watch++; break;
                    case OneOutcome.OwnedOrDropped:   stats.SkippedOwnedOrDropped++; break;
                    case OneOutcome.NoId:             stats.SkippedNoId++; break;
                    case OneOutcome.Dedup:            stats.SkippedDedup++; break;
                    // OneOutcome.Failed : déjà logué dans ProgramOneAsync.
                }
            }

            _logger?.Info("[LLM_AI] Auto-program [{0}] bilan : {1} programmé(s), {2} watch, {3} dedup, {4} owned/dropped, {5} sans id.",
                who, stats.Programmed, stats.Watch, stats.SkippedDedup, stats.SkippedOwnedOrDropped, stats.SkippedNoId);
            return stats;
        }

        // ------------------------------------------------------------------
        //  Programmation d'une reco unique (réutilisée par l'endpoint Activate)
        // ------------------------------------------------------------------

        /// <summary>
        /// Résultat du traitement d'une reco par <see cref="ProgramOneAsync"/>.
        /// <see cref="Created"/> = timer créé ; <see cref="Watch"/> = watch
        /// bucket (rien à programmer) ; <see cref="OwnedOrDropped"/> = déjà
        /// possédée (library_id) ou dans la drop list ; <see cref="NoId"/> =
        /// sans id EPG ; <see cref="Dedup"/> = déjà couverte par un timer
        /// existant ; <see cref="Failed"/> = la création a levé (conflit tuner,
        /// programme introuvable…) — déjà logué.
        /// </summary>
        internal enum OneOutcome { Created, Watch, OwnedOrDropped, NoId, Dedup, Failed }

        /// <summary>
        /// Traite une reco : applique les garde-fous (watch bucket, owned-guard,
        /// drop list, dedup) puis crée le timer adéquat (SeriesTimer pour une
        /// série, Timer unique pour un film). Méthode partagée entre la boucle
        /// <see cref="Program"/> (tâche planifiée) et l'endpoint
        /// <c>/Plugins/LLMAI/Activate</c> (une reco unique déclenchée à la
        /// lecture d'une carte .strm). Les ensembles <paramref name="programIds"
        /// />&amp;<paramref name="names"/> sont fournis par l'appelant (construits
        /// via <see cref="BuildExistingTimerSets"/>) et mutés en cas de création
        /// (dedup intra-passe).
        /// </summary>
        internal async Task<OneOutcome> ProgramOneAsync(
            Reco r, HashSet<string> programIds, HashSet<string> names, CancellationToken ct)
        {
            // Watch bucket : déjà disponible (rien à programmer).
            if (IsWatchBucket(r)) return OneOutcome.Watch;

            // Owned-guard : déjà possédé en bibliothèque.
            if (!string.IsNullOrEmpty(r.LibraryId))
            {
                _logger?.Info("[LLM_AI] Auto-program : « {0} » déjà possédé (library_id) → non programmé.", r.Title);
                return OneOutcome.OwnedOrDropped;
            }

            // Drop list : titre exclu par l'usager.
            string norm = GetEmbyInfoTool.Norm(r.Title ?? string.Empty);
            var dropped = GetEmbyInfoTool.DroppedTitlesSet();
            if (!string.IsNullOrEmpty(norm) && dropped.Contains(norm))
            {
                _logger?.Info("[LLM_AI] Auto-program : « {0} » dans la drop list → non programmé.", r.Title);
                return OneOutcome.OwnedOrDropped;
            }

            // Sans id de programme EPG, impossible de créer un timer.
            if (string.IsNullOrEmpty(r.Id))
            {
                _logger?.Info("[LLM_AI] Auto-program : « {0} » sans id EPG → non programmé.", r.Title);
                return OneOutcome.NoId;
            }

            // Dedup : déjà un timer sur ce programme ou ce nom de série.
            if (programIds.Contains(r.Id) ||
                (!string.IsNullOrEmpty(norm) && names.Contains(norm)))
            {
                _logger?.Info("[LLM_AI] Auto-program : « {0} » déjà programmé (timer existant) → sauté.", r.Title);
                return OneOutcome.Dedup;
            }

            try
            {
                if (IsSeries(r.Kind))
                {
                    await CreateSeriesTimerAsync(r.Id, ct).ConfigureAwait(false);
                }
                else
                {
                    CreateMovieTimer(r.Id);
                }
                _logger?.Info("[LLM_AI] Auto-program : « {0} » → {1} timer créé (programId={2}).",
                    r.Title, IsSeries(r.Kind) ? "series" : "movie", r.Id);
                // Mémorise pour le dedup intra-passe (deux recos du même titre).
                programIds.Add(r.Id);
                if (!string.IsNullOrEmpty(norm)) names.Add(norm);
                return OneOutcome.Created;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Auto-program : échec création timer pour « {0} » (programId={1}) : {2}",
                    r.Title, r.Id, ex.Message);
                return OneOutcome.Failed;
            }
        }

        // ------------------------------------------------------------------
        //  Buckets
        // ------------------------------------------------------------------

        /// <summary>
        /// Watch bucket = reco déjà disponible (rien à enregistrer) :
        /// <c>source="recording"</c> (déjà enregistré), <c>source="library"</c>
        /// (bibliothèque). Le cas <c>source="live"</c> mais possédé est géré
        /// séparément via <c>library_id</c> (owned-guard) — on le laisse passer
        /// ici comme record candidate, l'owned-guard le retire ensuite.
        /// </summary>
        internal static bool IsWatchBucket(Reco r)
        {
            if (string.Equals(r.Source, "recording", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(r.Source, "library", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Série → SeriesTimer ; film/one-off → Timer unique.</summary>
        internal static bool IsSeries(string kind) =>
            string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Rang de tri par priority (high=0, medium=1, low=2, inconnu=3). Les
        /// recos les plus prioritaires sont programmées en premier.
        /// </summary>
        private static int PriorityRank(string priority)
        {
            switch ((priority ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "high": return 0;
                case "medium": return 1;
                case "low": return 2;
                default: return 3;
            }
        }

        // ------------------------------------------------------------------
        //  Création des timers
        // ------------------------------------------------------------------

        /// <summary>
        /// Crée un series timer (série récurrente). Récupère les valeurs par
        /// défaut dérivées du programme (Start/End/Channel/paddings/SeriesId)
        /// via <see cref="ILiveTvManager.GetNewTimerDefaults"/>, applique
        /// RecordNewOnly + SkipEpisodesInLibrary, convertit le DTO en
        /// <see cref="SeriesTimerInfo"/> (le type attendu par
        /// <see cref="ILiveTvManager.CreateSeriesTimer"/>) puis crée le timer.
        /// </summary>
        private async Task CreateSeriesTimerAsync(string programId, CancellationToken ct)
        {
            var dto = _liveTv.GetNewTimerDefaults(programId, ct);
            if (dto == null) throw new InvalidOperationException("GetNewTimerDefaults a renvoyé null.");

            // Séries : on n'enregistre que les nouveaux épisodes, et on saute
            // ceux déjà en bibliothèque (évite de re-enregistrer ce qu'on a).
            dto.RecordNewOnly = true;
            dto.SkipEpisodesInLibrary = true;

            // CreateSeriesTimer attend le type interne SeriesTimerInfo (pas le
            // DTO) — on copie les champs communs. TimerType est calculé (get
            // only) sur les deux types : pas besoin de le transférer.
            var info = new SeriesTimerInfo
            {
                ProgramId = dto.ProgramId,
                ChannelId = dto.ChannelId,
                ChannelIds = dto.ChannelIds,
                Name = dto.Name,
                Overview = dto.Overview,
                StartDate = dto.StartDate,
                EndDate = dto.EndDate,
                Priority = dto.Priority,
                PrePaddingSeconds = dto.PrePaddingSeconds,
                PostPaddingSeconds = dto.PostPaddingSeconds,
                IsPrePaddingRequired = dto.IsPrePaddingRequired,
                IsPostPaddingRequired = dto.IsPostPaddingRequired,
                KeepUntil = dto.KeepUntil,
                RecordNewOnly = dto.RecordNewOnly,
                SkipEpisodesInLibrary = dto.SkipEpisodesInLibrary,
                RecordAnyTime = dto.RecordAnyTime,
                KeepUpTo = dto.KeepUpTo,
                SeriesId = dto.SeriesId,
                Days = dto.Days,
                MaxRecordingSeconds = dto.MaxRecordingSeconds,
            };

            await _liveTv.CreateSeriesTimer(info, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Crée un timer unique (film / one-off). Récupère les valeurs par
        /// défaut dérivées du programme via <see cref="ILiveTvManager.GetNewTimerDefaults"/>,
        /// copie les champs communs dans un <see cref="TimerInfoDto"/> (le type
        /// attendu par <see cref="ILiveTvManager.CreateTimer"/>) puis crée le
        /// timer. <c>CreateTimer</c> est synchrone (void) côté Emby.
        /// </summary>
        private void CreateMovieTimer(string programId)
        {
            var dto = _liveTv.GetNewTimerDefaults(programId, CancellationToken.None);
            if (dto == null) throw new InvalidOperationException("GetNewTimerDefaults a renvoyé null.");

            var timer = new TimerInfoDto
            {
                ProgramId = dto.ProgramId,
                ChannelId = dto.ChannelId,
                Name = dto.Name,
                Overview = dto.Overview,
                StartDate = dto.StartDate,
                EndDate = dto.EndDate,
                Priority = dto.Priority,
                PrePaddingSeconds = dto.PrePaddingSeconds,
                PostPaddingSeconds = dto.PostPaddingSeconds,
                IsPrePaddingRequired = dto.IsPrePaddingRequired,
                IsPostPaddingRequired = dto.IsPostPaddingRequired,
                KeepUntil = dto.KeepUntil,
            };

            _liveTv.CreateTimer(timer);
        }

        // ------------------------------------------------------------------
        //  Dedup : timers existants
        // ------------------------------------------------------------------

        /// <summary>
        /// Remplit <paramref name="programIds"/> (ProgramId des timers existants)
        /// et <paramref name="names"/> (noms normalisés — series timers couvrent
        /// toute la série, single timers portent SeriesName ou Name). Tolérant :
        /// un échec de lecture n'empêche pas la programmation (sets vides → on
        /// tente, le tuner host refusera les vrais doublons).
        /// </summary>
        internal void BuildExistingTimerSets(HashSet<string> programIds, HashSet<string> names)
        {
            try
            {
                // Series timers (enregistrements récurrents).
                var st = _liveTv.GetSeriesTimers(new SeriesTimerQuery())?.Items;
                if (st != null)
                {
                    foreach (var s in st)
                    {
                        if (s == null) continue;
                        if (!string.IsNullOrEmpty(s.ProgramId)) programIds.Add(s.ProgramId);
                        if (!string.IsNullOrEmpty(s.Name)) names.Add(GetEmbyInfoTool.Norm(s.Name));
                    }
                }
            }
            catch (Exception ex) { _logger?.Warn("[LLM_AI] Auto-program : lecture des series timers échouée ({0}).", ex.Message); }

            try
            {
                // Single timers (films + timers ponctuels).
                var tt = _liveTv.GetTimers(new TimerQuery { IsScheduled = true })?.Items;
                if (tt != null)
                {
                    foreach (var t in tt)
                    {
                        if (t == null) continue;
                        if (!string.IsNullOrEmpty(t.ProgramId)) programIds.Add(t.ProgramId);
                        // Pour une série : SeriesName ; sinon le nom du film/programme.
                        var nm = t.ProgramInfo != null
                            ? (!string.IsNullOrEmpty(t.ProgramInfo.SeriesName) ? t.ProgramInfo.SeriesName : t.ProgramInfo.Name)
                            : null;
                        if (!string.IsNullOrEmpty(nm)) names.Add(GetEmbyInfoTool.Norm(nm));
                    }
                }
            }
            catch (Exception ex) { _logger?.Warn("[LLM_AI] Auto-program : lecture des single timers échouée ({0}).", ex.Message); }
        }

        // ------------------------------------------------------------------
        //  Parsing du payload JSON
        // ------------------------------------------------------------------

        /// <summary>
        /// Représentation partielle d'une reco (champs utiles à la
        /// programmation). On n'extrait que ce dont on a besoin : title, kind,
        /// source, priority, id (programId EPG), library_id (signal owned-guard).
        /// </summary>
        internal struct Reco
        {
            public string Title;
            public string Kind;
            public string Source;
            public string Priority;
            public string Id;
            public string LibraryId;
            // Champs extraits pour la génération .nfo (bibliothèque .strm).
            public string Reason;
            public string Channel;
            public string Start;
        }

        internal static List<Reco> ParseRecommendations(string payload)
        {
            var list = new List<Reco>();
            try
            {
                using (var doc = JsonDocument.Parse(payload))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        if (el.ValueKind != JsonValueKind.Object) continue;
                        list.Add(new Reco
                        {
                            Title = Str(el, "title"),
                            Kind = Str(el, "kind"),
                            Source = Str(el, "source"),
                            Priority = Str(el, "priority"),
                            Id = Str(el, "id"),
                            LibraryId = Str(el, "library_id"),
                            Reason = Str(el, "reason"),
                            Channel = Str(el, "channel"),
                            Start = Str(el, "start"),
                        });
                    }
                }
            }
            catch
            {
                // Payload non parsable (Markdown libre ?) — rien à programmer.
            }
            return list;
        }

        private static string Str(JsonElement obj, string key)
        {
            if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return null;
        }
    }
}