using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;

namespace LLM_AI
{
    /// <summary>
    /// Outils d'identification assistée dans le chat administrateur (v2.21.0) :
    /// <list type="bullet">
    /// <item><see cref="IdentifyListTool"/> (<c>identify_list</c>, lecture seule) :
    /// liste les items à identifier (needs-review, not-found, orphelins) et à
    /// valider (identifiés nativement jamais audités).</item>
    /// <item><see cref="IdentifyResolveTool"/> (<c>identify_resolve</c>, lecture
    /// seule) : exécute la cascade <see cref="OrphanResolver"/> en dry-run sur un
    /// item désigné, retourne candidat retenu + preuve + candidats rejetés et
    /// mémorise l'instantané de preuves côté serveur pour la session.</item>
    /// <item><see cref="IdentifyProposeTool"/> (<c>identify_propose</c>, deux
    /// phases) : dépose une carte Approuver/Refuser enrichie d'un diff visuel
    /// serveur-side (posters côte à côte, preuve, changements, indices) ;
    /// l'approbation ré-valide anti-TOCTOU et applique via
    /// <see cref="OrphanResolver.ApplyAsync"/> (mode remplacement inclus).</item>
    /// <item><see cref="IdentifyUndoTool"/> (<c>identify_undo</c>, deux phases) :
    /// retire ids provider + tags <c>llmai-*</c> + verrous posés, et restaure
    /// la vérité EPG si elle vit encore au store.</item>
    /// </list>
    /// Réservé exclusivement au chat administrateur (<see cref="ChatApiService"/>
    /// + <see cref="ChatActions"/>) — jamais exposé au chat externe.
    /// </summary>
    internal static class ChatIdentifyTools
    {
        internal sealed class ResolveSnapshot
        {
            public string Kind;
            public bool CrossKind;
            public string ProofLevel;
            public string ProofSource;
            public string ProofEvidence;
            public string TruthSource;
            public bool Accepted;
            public string VerdictReason;
            public List<string> RejectedSummary = new List<string>();
        }

        /// <summary>Plafond serveur de cartes identify_propose concurrentes par item.</summary>
        internal const int MaxCandidateCardsPerItem = 3;

        public static List<ILlmTool> BuildReadTools(PluginConfiguration cfg, string sessionId,
            ILibraryManager library, IUserManager users, ILiveTvManager liveTv,
            IServerApplicationHost host, IJsonSerializer json, ILogger logger)
        {
            return new List<ILlmTool>
            {
                new IdentifyListTool(library, host, logger),
                new IdentifyResolveTool(cfg, sessionId, library, users, liveTv, host, json, logger)
            };
        }

        public static string BuildWorkflowBlock(bool hasActionLayer)
        {
            var b = new StringBuilder();
            b.Append("\n\n### IDENTIFICATION ASSISTÉE (ADMIN)\n");
            b.Append("Outils dédiés : identify_list, identify_resolve");
            if (hasActionLayer)
                b.Append(", identify_propose (deux phases), identify_undo (deux phases)");
            b.Append(".\n");
            b.Append("- Pour lister les vidéos à identifier / à valider : appelle identify_list.\n");
            b.Append("- Pour identifier ou corriger un item : retrouve-le d'abord avec get_emby_info action=\"find\" " +
                     "(si plusieurs items ont des titres proches sans égalité exacte unique, DEMANDE lequel à l'admin " +
                     "avant de lancer identify_resolve). Appelle ensuite identify_resolve sur le SEUL item_id désigné.\n");
            if (hasActionLayer)
            {
                b.Append("- Si identify_resolve retourne un candidat (status=\"resolvable\"), ou si des candidats " +
                         "faibles existent en \"needs_review\" et que l'admin veut trancher, appelle identify_propose " +
                         "avec item_id et tmdb_id (et kind si différent). La carte affiche les posters côte à côte, " +
                         "la preuve et le diff — l'admin tranche au clic. Ne dépose PAS de carte si status=\"already_identified\" " +
                         "(dis simplement que l'item est déjà identifié), sauf demande explicite de correction.\n");
                b.Append("- LA CARTE EST L'APERÇU : l'admin ne voit NI poster NI synopsis avant le dépôt d'une carte, et " +
                         "la carte n'exécute rien avant son clic « Approuver ». Ne demande donc JAMAIS la permission de " +
                         "déposer une carte et ne propose pas de « recherches supplémentaires » à la place : dès que " +
                         "identify_resolve expose un candidat (status resolvable OU needs_review, preuve forte, faible " +
                         "ou écart de durée compris), DÉPOSE la carte, puis résume en une phrase la preuve et les " +
                         "réserves (écart de durée, homonyme, preuve faible) pour guider le coup d'œil de l'admin. " +
                         "N'écarte jamais toi-même un candidat sur un titre ou une durée : l'admin juge sur l'affiche.\n");
                b.Append("- HOMONYMES DANS LA BIBLIOTHÈQUE : si get_emby_info find renvoie plusieurs items au même titre, " +
                         "ne te contente pas de les nommer — pour chacun donne année, chemin et si une fiche (ids) est " +
                         "déjà posée ; un item sans année ni ids est presque toujours celui à identifier (privilégie-le " +
                         "et dis-le), les autres sont déjà identifiés.\n");
                b.Append("- MULTI-CANDIDATS : si identify_resolve expose plusieurs candidats plausibles que la " +
                         "preuve ne départage pas (homonymes, fiches croisées, années proches), dépose UNE carte " +
                         "identify_propose PAR candidat (plafond 3), du plus probable au moins probable, EN UN SEUL " +
                         "TOUR — l'admin compare les affiches et approuve la bonne ; dis-lui de refuser les cartes " +
                         "non retenues (elles restent en attente). Une preuve forte (juge synopsis, titre exact) " +
                         "départage déjà : une seule carte. Le serveur plafonne à 3 cartes par item et retire " +
                         "les cartes sœurs dès qu'une est approuvée (inutile de demander de les refuser).\n");
                b.Append("- Pour retirer une identification / remettre un item à zéro : appelle identify_undo.\n");
            }
            if (MccClient.IsEnabledForInstall)
                b.Append("- mcc_lookup : Répertoire des films classés du Québec (gouvernemental). Quand le titre de " +
                         "l'item est un titre de distribution québécois, ou que des fiches homonymes se disputent, " +
                         "consulte-le AVANT de trancher : il donne la v.o. (relance tmdb_lookup avec), l'année, le genre " +
                         "et le synopsis de chaque fiche MCC. La carte identify affiche en plus la concordance MCC " +
                         "calculée côté serveur.\n");
            b.Append("- INDICE ADMIN : si l'admin fournit lui-même un id IMDb/TMDB, un titre original ou une année, " +
                     "passe-les à identify_resolve via hint_imdb_id / hint_tmdb_id / hint_title / hint_year " +
                     "(recopiés tels quels, jamais déduits ni inventés). Le serveur relit la fiche sur TMDB et la " +
                     "présente comme candidat « indice admin » NON corroboré — jamais une preuve : valide-la visuellement.\n");
            b.Append("- N'invente JAMAIS un tmdb_id ni une preuve : les preuves sur la carte sont recalculées côté serveur.\n");
            return b.ToString();
        }

        private static OrphanResolver CreateResolver(ILogger logger, IJsonSerializer json,
            ILibraryManager library, IUserManager users, ILiveTvManager liveTv, IServerApplicationHost host)
        {
            IProviderManager providers = null;
            try { providers = host?.TryResolve<IProviderManager>(); } catch { /* optionnel */ }
            return new OrphanResolver(logger, json, library, users, liveTv, host, providers);
        }

        private static string ResolveUserTmdb(PluginConfiguration cfg, IServerApplicationHost host)
            => I18n.ToTmdbLang(I18n.ResolveMetaLangKey(cfg, host));

        private static async Task<string> BuildItemDeepLinkAsync(IServerApplicationHost host, BaseItem item)
        {
            if (host == null || item == null) return null;
            try
            {
                var pub = await host.GetPublicSystemInfo(CancellationToken.None).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(pub?.Id)) return null;
                bool isSeries = item.GetType().Name.IndexOf("Series", StringComparison.OrdinalIgnoreCase) >= 0;
                return "/web/index.html#!/item?id=" + item.InternalId.ToString(CultureInfo.InvariantCulture)
                    + "&serverId=" + pub.Id
                    + (isSeries ? "&asSeries=true" : string.Empty);
            }
            catch { return null; }
        }

        private static string BuildItemImageUrl(BaseItem item)
        {
            if (item == null) return null;
            try
            {
                if (!item.HasImage(ImageType.Primary, 0)) return null;
                return "/emby/Items/" + item.InternalId.ToString(CultureInfo.InvariantCulture) + "/Images/Primary?maxWidth=300";
            }
            catch { return null; }
        }

        private static Dictionary<string, string> ReadProviderIds(BaseItem item)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (item?.ProviderIds == null) return dict;
            foreach (var kv in item.ProviderIds)
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    dict[kv.Key] = kv.Value;
            return dict;
        }

        private static string SafeProvider(BaseItem item, string key)
        {
            if (item == null) return null;
            try
            {
                var v = item.GetProviderId(key);
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }
            catch { return null; }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim();
            return t.Length <= max ? t : t.Substring(0, max).TrimEnd() + "…";
        }

        // ------------------------------------------------------------------
        //  Tool 1 : identify_list (lecture seule)
        // ------------------------------------------------------------------

        private sealed class IdentifyListTool : ILlmTool
        {
            private readonly ILibraryManager _library;
            private readonly IServerApplicationHost _host;
            private readonly ILogger _logger;

            public IdentifyListTool(ILibraryManager library, IServerApplicationHost host, ILogger logger)
            {
                _library = library; _host = host; _logger = logger;
            }

            public string Name => "identify_list";
            public string Description =>
                "Liste les films/séries de la bibliothèque qui attendent une identification " +
                "(llmai-needs-review, llmai-not-found, orphelins sans id) ou une validation " +
                "(identifiés nativement jamais audités). Lecture seule.";
            public string ArgumentsSchema =>
                "{\"type\":\"object\",\"properties\":{" +
                "\"filter\":{\"type\":\"string\",\"enum\":[\"all\",\"to_identify\",\"to_validate\",\"needs_review\",\"not_found\",\"orphans\"],\"description\":\"Filtre de file (défaut: all)\"}," +
                "\"limit\":{\"type\":\"integer\",\"description\":\"Nombre max d'items par section (défaut: 15, max: 30)\"}}}";

            public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
            {
                string filter = (ChatActions.ArgString(args, "filter") ?? "all").Trim().ToLowerInvariant();
                int limit = 15;
                if (args.ValueKind == JsonValueKind.Object
                    && args.TryGetProperty("limit", out var limEl)
                    && limEl.TryGetInt32(out int lVal))
                    limit = Math.Clamp(lVal, 1, 30);

                var dvrRoots = new List<string>();
                try
                {
                    if (RecordingDiskManager.TryResolveRecordingPaths(_host, _logger, out var roots) && roots != null)
                        dvrRoots = roots.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
                }
                catch { /* optionnel */ }

                var needsReview = new List<BaseItem>();
                var notFound = new List<BaseItem>();
                var orphans = new List<BaseItem>();
                var neverAuditedDvr = new List<BaseItem>();
                var neverAuditedLib = new List<BaseItem>();

                try
                {
                    var res = _library.GetItemsResult(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { "Movie", "Series" },
                        Recursive = true,
                        Limit = 5000,
                        EnableTotalRecordCount = false
                    });
                    foreach (var it in res?.Items ?? Array.Empty<BaseItem>())
                    {
                        if (it == null || OrphanResolver.IsStrmCard(it)) continue;
                        if (OrphanResolver.HasTag(it, OrphanIdentifyTask.TagCrossKindIgnored)) continue;

                        bool hasReview = OrphanResolver.HasTag(it, OrphanIdentifyTask.TagNeedsReview);
                        bool hasNotFound = OrphanResolver.HasTag(it, OrphanIdentifyTask.TagNotFound);
                        bool hasOk = OrphanResolver.HasTag(it, OrphanIdentifyTask.TagIdentified);
                        bool isOrphan = OrphanResolver.IsOrphanItem(it);

                        if (hasReview) needsReview.Add(it);
                        else if (hasNotFound) notFound.Add(it);
                        else if (!hasOk && isOrphan) orphans.Add(it);
                        else if (!hasOk && !isOrphan)
                        {
                            bool isDvr = dvrRoots.Count > 0
                                && !string.IsNullOrWhiteSpace(it.Path)
                                && dvrRoots.Any(r => TonightService.IsUnderPath(it.Path, r));
                            if (isDvr) neverAuditedDvr.Add(it);
                            else neverAuditedLib.Add(it);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] identify_list échoué : {0}", ex.Message);
                    return Task.FromResult(ChatActions.Json(new { error = ex.Message }));
                }

                object Proj(BaseItem it, string state)
                {
                    bool isSeries = it.GetType().Name.IndexOf("Series", StringComparison.OrdinalIgnoreCase) >= 0;
                    return new
                    {
                        id = it.InternalId.ToString(CultureInfo.InvariantCulture),
                        name = it.Name,
                        type = isSeries ? "series" : "movie",
                        year = it.ProductionYear,
                        path = it.Path,
                        state = state,
                        tags = it.Tags ?? Array.Empty<string>(),
                        provider_ids = ReadProviderIds(it)
                    };
                }

                var toIdentify = new List<object>();
                bool wantReview = filter == "all" || filter == "to_identify" || filter == "needs_review";
                bool wantOrphans = filter == "all" || filter == "to_identify" || filter == "orphans";
                bool wantNotFound = filter == "all" || filter == "to_identify" || filter == "not_found";
                bool wantValidate = filter == "all" || filter == "to_validate";

                if (wantReview)
                    foreach (var it in needsReview)
                    {
                        if (toIdentify.Count >= limit) break;
                        string state = OrphanResolver.HasTag(it, OrphanIdentifyTask.TagCrossKind)
                            ? "needs_review (fiche croisée)"
                            : "needs_review";
                        toIdentify.Add(Proj(it, state));
                    }
                if (wantOrphans)
                    foreach (var it in orphans)
                    {
                        if (toIdentify.Count >= limit) break;
                        toIdentify.Add(Proj(it, "orphan"));
                    }
                if (wantNotFound)
                    foreach (var it in notFound)
                    {
                        if (toIdentify.Count >= limit) break;
                        toIdentify.Add(Proj(it, "not_found"));
                    }

                var toValidate = new List<object>();
                if (wantValidate)
                {
                    foreach (var it in neverAuditedDvr.Concat(neverAuditedLib))
                    {
                        if (toValidate.Count >= limit) break;
                        toValidate.Add(Proj(it, "identified_never_audited"));
                    }
                }

                int totalToIdentify = (wantReview ? needsReview.Count : 0)
                    + (wantOrphans ? orphans.Count : 0)
                    + (wantNotFound ? notFound.Count : 0);
                int totalToValidate = wantValidate ? (neverAuditedDvr.Count + neverAuditedLib.Count) : 0;

                return Task.FromResult(ChatActions.Json(new
                {
                    counts = new
                    {
                        needs_review = needsReview.Count,
                        orphans = orphans.Count,
                        not_found = notFound.Count,
                        never_audited_dvr = neverAuditedDvr.Count,
                        never_audited_library = neverAuditedLib.Count
                    },
                    total_to_identify = totalToIdentify,
                    to_identify = toIdentify,
                    total_to_validate = totalToValidate,
                    to_validate = toValidate
                }));
            }
        }

        // ------------------------------------------------------------------
        //  Tool 2 : identify_resolve (lecture + dry-run structuré)
        // ------------------------------------------------------------------

        private sealed class IdentifyResolveTool : ILlmTool
        {
            private readonly PluginConfiguration _cfg;
            private readonly string _sessionId;
            private readonly ILibraryManager _library;
            private readonly IUserManager _users;
            private readonly ILiveTvManager _liveTv;
            private readonly IServerApplicationHost _host;
            private readonly IJsonSerializer _json;
            private readonly ILogger _logger;

            public IdentifyResolveTool(PluginConfiguration cfg, string sessionId,
                ILibraryManager library, IUserManager users, ILiveTvManager liveTv,
                IServerApplicationHost host, IJsonSerializer json, ILogger logger)
            {
                _cfg = cfg; _sessionId = sessionId; _library = library; _users = users;
                _liveTv = liveTv; _host = host; _json = json; _logger = logger;
            }

            public string Name => "identify_resolve";
            public string Description =>
                "Exécute la cascade d'identification (SB/S0/S1/S2/S3 + juge synopsis + juge web) en mode " +
                "dry-run (aucune écriture) sur UN item désigné (indices admin optionnels hint_*), et retourne le candidat retenu avec sa " +
                "preuve, les candidats rejetés avec leurs raisons, et l'état actuel de l'item.";
            public string ArgumentsSchema =>
                "{\"type\":\"object\",\"properties\":{" +
                "\"item_id\":{\"type\":\"string\",\"description\":\"Identifiant de l'item (id renvoyé par find ou identify_list)\"}," +
                "\"hint_imdb_id\":{\"type\":\"string\",\"description\":\"Indice de l'admin : id IMDb (tt…) — optionnel, JAMAIS inventé\"}," +
                "\"hint_tmdb_id\":{\"type\":\"integer\",\"description\":\"Indice de l'admin : id TMDB — optionnel, JAMAIS inventé\"}," +
                "\"hint_title\":{\"type\":\"string\",\"description\":\"Indice de l'admin : titre (ex. titre original) à chercher sur TMDB — optionnel\"}," +
                "\"hint_year\":{\"type\":\"integer\",\"description\":\"Année associée à hint_title — optionnel\"}}," +
                "\"required\":[\"item_id\"]}";

            public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
            {
                string idStr = ChatActions.ArgString(args, "item_id");
                if (string.IsNullOrWhiteSpace(idStr))
                    return ChatActions.Json(new { status = "refused", detail = "paramètre 'item_id' requis." });

                var item = ItemIdResolver.Resolve(_library, idStr);
                if (item == null)
                    return ChatActions.Json(new { status = "refused", detail = "Item introuvable : " + idStr });
                if (OrphanResolver.IsStrmCard(item))
                    return ChatActions.Json(new { status = "refused", detail = "Les cartes .strm AI Suggestions ne sont pas identifiables." });

                if (!ChatActions.TryConsumeResolveQuota(_sessionId))
                    return ChatActions.Json(new { status = "refused",
                        detail = "Plafond de résolutions atteint (" + ChatActions.MaxResolvesPerConversation + " par conversation)." });

                var resolver = CreateResolver(_logger, _json, _library, _users, _liveTv, _host);
                string userTmdb = ResolveUserTmdb(_cfg, _host);
                var hint = new OrphanResolver.ChatHint
                {
                    ImdbId = ChatActions.ArgString(args, "hint_imdb_id"),
                    Title = ChatActions.ArgString(args, "hint_title")
                };
                if (args.ValueKind == JsonValueKind.Object)
                {
                    if (args.TryGetProperty("hint_tmdb_id", out var htEl))
                    {
                        int ht = 0;
                        if (htEl.ValueKind == JsonValueKind.Number) htEl.TryGetInt32(out ht);
                        else if (htEl.ValueKind == JsonValueKind.String) int.TryParse(htEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ht);
                        hint.TmdbId = ht;
                    }
                    if (args.TryGetProperty("hint_year", out var hyEl))
                    {
                        int hy = 0;
                        if (hyEl.ValueKind == JsonValueKind.Number) hyEl.TryGetInt32(out hy);
                        else if (hyEl.ValueKind == JsonValueKind.String) int.TryParse(hyEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out hy);
                        if (hy >= 1888 && hy <= 2100) hint.Year = hy;
                    }
                }
                var report = await resolver.ResolveForChatAsync(item, _cfg, userTmdb, ct, hint.IsEmpty ? null : hint).ConfigureAwait(false);

                string normItemId = item.InternalId.ToString(CultureInfo.InvariantCulture);
                // Liste des écartés PAR candidat : la fiche proposée sur une carte
                // n'est jamais listée parmi ses propres « candidats écartés ».
                var rejectedAll = report.Candidates
                    .Where(c => !c.Accepted && c.Meta != null)
                    .ToList();
                List<string> RejectedFor(int tmdbId) => rejectedAll
                    .Where(c => c.Meta.TmdbId != tmdbId)
                    .Take(5)
                    .Select(c => (c.Meta.Title ?? "?") + " (tmdb=" + c.Meta.TmdbId + ", " + c.Stage + ") — " + (c.Reason ?? "rejeté"))
                    .ToList();

                foreach (var c in report.Candidates)
                {
                    if (c?.Meta == null || c.Meta.TmdbId <= 0) continue;
                    ChatActions.SaveResolveSnapshot(_sessionId, normItemId, c.Meta.TmdbId, new ResolveSnapshot
                    {
                        Kind = c.Meta.Kind ?? (c.CrossKind ? "movie" : report.Kind),
                        CrossKind = c.CrossKind,
                        ProofLevel = c.Accepted ? (c.ProofLevel ?? "forte") : "faible",
                        ProofSource = c.ProofSource ?? c.Stage,
                        ProofEvidence = c.Accepted ? c.ProofEvidence : (c.Reason ?? "rejeté par la cascade"),
                        TruthSource = report.TruthSource,
                        Accepted = c.Accepted,
                        VerdictReason = c.Reason,
                        RejectedSummary = RejectedFor(c.Meta.TmdbId)
                    });
                }

                _logger?.Info("[LLM_AI] [CHAT] identify_resolve « {0} » (id={1}) → {2} (candidats={3}, gagnant={4}).",
                    item.Name, normItemId, report.Status, report.Candidates.Count,
                    report.Winner != null ? ("tmdb=" + report.Winner.TmdbId) : "aucun");

                return ChatActions.Json(new
                {
                    status = report.Status,
                    item = new
                    {
                        id = normItemId,
                        name = item.Name,
                        type = report.IsSeries ? "series" : "movie",
                        year = item.ProductionYear,
                        path = item.Path,
                        runtime_min = report.RuntimeMinutes,
                        provider_ids = ReadProviderIds(item),
                        tags = item.Tags ?? Array.Empty<string>(),
                        locked_fields = item.LockedFields?.Select(f => f.ToString()).ToArray() ?? Array.Empty<string>(),
                        image_url = BuildItemImageUrl(item)
                    },
                    truth = new
                    {
                        source = report.TruthSource,
                        year = report.TruthYear,
                        has_overview = report.TruthHasOverview
                    },
                    winner = report.Winner == null ? null : new
                    {
                        tmdb_id = report.Winner.TmdbId,
                        imdb_id = report.Winner.ImdbId,
                        tvdb_id = report.Winner.TvdbId,
                        kind = report.Winner.Kind,
                        cross_kind = report.CrossKind,
                        stage = report.WinningStage,
                        title = report.Winner.Title,
                        year = report.Winner.Year,
                        runtime_min = report.Winner.Runtime,
                        poster_url = report.Winner.PosterUrl,
                        overview_head = Truncate(report.Winner.Overview, 280),
                        proof_level = report.WinningProofLevel,
                        proof_source = report.WinningProofSource,
                        proof_evidence = report.WinningProofEvidence
                    },
                    candidates = report.Candidates.Select(c => new
                    {
                        tmdb_id = c.Meta.TmdbId,
                        imdb_id = c.Meta.ImdbId,
                        kind = c.Meta.Kind,
                        cross_kind = c.CrossKind,
                        stage = c.Stage,
                        title = c.Meta.Title,
                        year = c.Meta.Year,
                        runtime_min = c.Meta.Runtime,
                        poster_url = c.Meta.PosterUrl,
                        proof_level = c.ProofLevel,
                        proof_source = c.ProofSource,
                        proof_evidence = c.ProofEvidence,
                        verdict = c.Accepted ? "retenu" : "rejeté",
                        reason = c.Reason
                    }).ToList(),
                    existing_carriers = report.ExistingCarriers
                        .Where(x => x != null)
                        .Select(x => new { id = x.InternalId.ToString(CultureInfo.InvariantCulture), name = x.Name, path = x.Path })
                        .ToList()
                });
            }
        }

        // ------------------------------------------------------------------
        //  Tool 3 : identify_propose (deux phases)
        // ------------------------------------------------------------------

        internal sealed class IdentifyProposeTool : ILlmTool, IChatActionTool
        {
            private readonly PluginConfiguration _cfg;
            private readonly string _sessionId;
            private readonly string _adminUserId;
            private readonly ILibraryManager _library;
            private readonly IUserManager _users;
            private readonly ILiveTvManager _liveTv;
            private readonly IServerApplicationHost _host;
            private readonly IJsonSerializer _json;
            private readonly ILogger _logger;

            public IdentifyProposeTool(PluginConfiguration cfg, string sessionId, string adminUserId,
                ILibraryManager library, IUserManager users, ILiveTvManager liveTv,
                IServerApplicationHost host, IJsonSerializer json, ILogger logger)
            {
                _cfg = cfg; _sessionId = sessionId; _adminUserId = adminUserId;
                _library = library; _users = users; _liveTv = liveTv;
                _host = host; _json = json; _logger = logger;
            }

            public string ToolKey => "identify_propose";
            public string Name => "identify_propose";
            public string Description =>
                "Dépose une carte de décision visuelle pour identifier un item avec une fiche TMDB " +
                "(posters côte à côte, preuve, diff avant/après, indices). DEUX PHASES : l'appel DÉPOSE " +
                "une proposition (status \"awaiting_approval\") sans exécuter — l'admin approuve en " +
                "cliquant la carte « Approuver », le résultat arrive ensuite comme note [Admin].";
            public string ArgumentsSchema =>
                "{\"type\":\"object\",\"properties\":{" +
                "\"item_id\":{\"type\":\"string\",\"description\":\"Identifiant de l'item (obligatoire)\"}," +
                "\"tmdb_id\":{\"type\":\"integer\",\"description\":\"Identifiant TMDB de la fiche candidate (obligatoire)\"}," +
                "\"kind\":{\"type\":\"string\",\"enum\":[\"movie\",\"series\"],\"description\":\"Type de fiche TMDB (défaut : résolu ou type de l'item)\"}}," +
                "\"required\":[\"item_id\",\"tmdb_id\"]}";

            public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
            {
                string idStr = ChatActions.ArgString(args, "item_id");
                if (string.IsNullOrWhiteSpace(idStr))
                    return ChatActions.Json(new { status = "refused", detail = "paramètre 'item_id' requis." });

                int tmdbId = 0;
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("tmdb_id", out var tEl))
                {
                    if (tEl.ValueKind == JsonValueKind.Number) tEl.TryGetInt32(out tmdbId);
                    else if (tEl.ValueKind == JsonValueKind.String) int.TryParse(tEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out tmdbId);
                }
                if (tmdbId <= 0)
                    return ChatActions.Json(new { status = "refused", detail = "paramètre 'tmdb_id' (> 0) requis." });

                var item = ItemIdResolver.Resolve(_library, idStr);
                if (item == null)
                    return ChatActions.Json(new { status = "refused", detail = "Item introuvable : " + idStr });
                if (OrphanResolver.IsStrmCard(item))
                    return ChatActions.Json(new { status = "refused", detail = "Les cartes .strm ne sont pas identifiables." });

                string normItemId = item.InternalId.ToString(CultureInfo.InvariantCulture);
                bool isSeriesItem = item.GetType().Name.IndexOf("Series", StringComparison.OrdinalIgnoreCase) >= 0;
                string itemKind = isSeriesItem ? "series" : "movie";

                var snap = ChatActions.GetResolveSnapshot(_sessionId, normItemId, tmdbId);
                string reqKind = (ChatActions.ArgString(args, "kind") ?? string.Empty).Trim().ToLowerInvariant();
                string kind = (reqKind == "movie" || reqKind == "series")
                    ? reqKind
                    : (snap?.Kind ?? itemKind);
                bool crossKind = !string.Equals(kind, itemKind, StringComparison.OrdinalIgnoreCase);

                var resolver = CreateResolver(_logger, _json, _library, _users, _liveTv, _host);
                string userTmdb = ResolveUserTmdb(_cfg, _host);
                var meta = await resolver.LookupMetaForChatAsync(tmdbId, kind, item, _cfg, userTmdb, ct).ConfigureAwait(false);
                if (meta == null || meta.TmdbId <= 0)
                    return ChatActions.Json(new { status = "refused",
                        detail = "Fiche TMDB introuvable pour tmdb=" + tmdbId + " (" + kind + ")." });

                string curTmdb = SafeProvider(item, "tmdb");
                string curImdb = SafeProvider(item, "imdb");
                string curTvdb = SafeProvider(item, "tvdb");
                bool needsReplace = (!string.IsNullOrWhiteSpace(curTmdb) && !string.Equals(curTmdb, meta.TmdbId.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(curImdb) && !string.IsNullOrWhiteSpace(meta.ImdbId) && !string.Equals(curImdb, meta.ImdbId, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(curTvdb) && !string.IsNullOrWhiteSpace(meta.TvdbId) && !string.Equals(curTvdb, meta.TvdbId, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(curImdb) && string.IsNullOrWhiteSpace(meta.ImdbId));

                // Diff exact calculé côté serveur
                var changes = new List<string>();
                string newTmdbStr = meta.TmdbId.ToString(CultureInfo.InvariantCulture);
                if (!string.Equals(curTmdb, newTmdbStr, StringComparison.OrdinalIgnoreCase))
                    changes.Add("tmdb : " + (curTmdb ?? "—") + " → " + newTmdbStr);
                if (!string.Equals(curImdb, meta.ImdbId, StringComparison.OrdinalIgnoreCase) && (!string.IsNullOrWhiteSpace(curImdb) || !string.IsNullOrWhiteSpace(meta.ImdbId)))
                    changes.Add("imdb : " + (curImdb ?? "—") + " → " + (meta.ImdbId ?? "—"));
                if ((isSeriesItem || string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase))
                    && !string.Equals(curTvdb, meta.TvdbId, StringComparison.OrdinalIgnoreCase)
                    && (!string.IsNullOrWhiteSpace(curTvdb) || !string.IsNullOrWhiteSpace(meta.TvdbId)))
                    changes.Add("tvdb : " + (curTvdb ?? "—") + " → " + (meta.TvdbId ?? "—"));

                if ((!item.ProductionYear.HasValue || item.ProductionYear.Value <= 0) && meta.Year.HasValue && meta.Year.Value > 0)
                    changes.Add("Année : — → " + meta.Year.Value.ToString(CultureInfo.InvariantCulture));
                if (string.IsNullOrWhiteSpace(item.Overview) && !string.IsNullOrWhiteSpace(meta.Overview))
                    changes.Add("Synopsis : vide → rempli");
                if ((item.Genres == null || item.Genres.Length == 0) && meta.Genres != null && meta.Genres.Length > 0)
                    changes.Add("Genres : vides → " + string.Join(", ", meta.Genres));
                if (!item.HasImage(ImageType.Primary, 0) && !string.IsNullOrWhiteSpace(meta.PosterUrl))
                    changes.Add("Poster : absent → téléchargé depuis TMDB");

                var tagsAdded = new List<string>();
                if (!OrphanResolver.HasTag(item, OrphanIdentifyTask.TagIdentified))
                    tagsAdded.Add("+" + OrphanIdentifyTask.TagIdentified);
                if (crossKind && !OrphanResolver.HasTag(item, OrphanIdentifyTask.TagCrossKind))
                    tagsAdded.Add("+" + OrphanIdentifyTask.TagCrossKind);
                if (OrphanResolver.HasTag(item, OrphanIdentifyTask.TagNeedsReview))
                    tagsAdded.Add("-" + OrphanIdentifyTask.TagNeedsReview);
                if (OrphanResolver.HasTag(item, OrphanIdentifyTask.TagNotFound))
                    tagsAdded.Add("-" + OrphanIdentifyTask.TagNotFound);
                if (tagsAdded.Count > 0)
                    changes.Add("Tags : " + string.Join(" ", tagsAdded));
                changes.Add("Verrous : Name"
                    + (string.IsNullOrWhiteSpace(item.Overview) && !string.IsNullOrWhiteSpace(meta.Overview) ? ", Overview" : "")
                    + ((item.Genres == null || item.Genres.Length == 0) && meta.Genres != null && meta.Genres.Length > 0 ? ", Genres" : ""));

                // Indices pour la décision admin (jamais des portes bloquantes)
                var hints = new List<string>();
                string warning = null;

                int? itemRuntime = (item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0)
                    ? (int?)Math.Round(item.RunTimeTicks.Value / (double)TimeSpan.TicksPerMinute)
                    : null;
                if (itemRuntime.HasValue && meta.Runtime.HasValue && meta.Runtime.Value > 0)
                {
                    int delta = Math.Abs(itemRuntime.Value - meta.Runtime.Value);
                    hints.Add((delta >= 15 ? "⚠️ Écart durée : " : "Durée : ")
                        + "fichier " + itemRuntime.Value + " min vs fiche " + meta.Runtime.Value + " min");
                }
                else if (meta.Runtime.HasValue && meta.Runtime.Value > 0)
                    hints.Add("Durée fiche : " + meta.Runtime.Value + " min");

                if (string.IsNullOrWhiteSpace(meta.Overview))
                    hints.Add("Synopsis TMDB muet dans les langues configurées");

                // Concordance MCC (Répertoire des films classés — Québec) :
                // le canal « validation MCC » de la carte, calculé côté
                // serveur comme toute preuve affichée (décision usager
                // 2026-11-20). Film natif uniquement — le répertoire ne
                // couvre pas les séries télé. L'axe est la v.o. de la fiche
                // MCC vs le titre ORIGINAL de la fiche proposée, JAMAIS le
                // titre QC seul (un homonyme porte par construction le même
                // titre QC : « Sur la route » = Big Drive 2011 vs On the
                // Road 2012 — le titre QC corroborerait le mauvais candidat
                // tout seul). Fail-open total : MCC muet/indisponible →
                // aucune ligne.
                if (MccClient.IsEnabledForInstall
                    && !crossKind
                    && string.Equals(kind, "movie", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(itemKind, "movie", StringComparison.OrdinalIgnoreCase))
                {
                    var mccFiches = await MccClient.LookupFichesAsync(item.Name, 3, _logger, ct).ConfigureAwait(false);
                    if (mccFiches != null && mccFiches.Count > 0)
                    {
                        var ficheCheck = await new TmdbLookupTool(_logger)
                            .FetchValidationFicheAsync(meta.TmdbId, kind, userTmdb, ct).ConfigureAwait(false);
                        if (ficheCheck != null)
                        {
                            var concordants = mccFiches.Where(f => MccClient.Corroborates(f, ficheCheck)).ToList();
                            if (concordants.Count > 0)
                            {
                                var f = concordants[0];
                                string line = "MCC (Répertoire des films classés) : « " + f.Titre + " »" +
                                          (string.IsNullOrWhiteSpace(f.Vo) ? "" : " → v.o. « " + f.Vo + " »") +
                                          (f.Year.HasValue ? " (" + f.Year.Value.ToString(CultureInfo.InvariantCulture) + ")" : "") +
                                          " — concorde avec la fiche proposée (v.o./titre original)";
                                // Homonymes restants : quand le répertoire porte
                                // PLUSIEURS fiches au même titre QC (terrain
                                // 2026-11-20 : « Sur la route » = Big Drive
                                // 2011 ET On the Road 2012), une proposition
                                // fausse peut concorder avec la MAUVAISE fiche
                                // — la ligne positive seule ne distinguerait
                                // pas les deux mondes. On liste les homonymes
                                // non concordantes (v.o. + année) : le geste
                                // de départage manuel, visible sur la carte.
                                var others = mccFiches.Where(x => !concordants.Contains(x)).ToList();
                                if (others.Count > 0)
                                {
                                    line += " ; " + others.Count + " fiche(s) homonyme(s) au répertoire : " +
                                        string.Join(" | ", others.Take(2).Select(o =>
                                            "« " + (string.IsNullOrWhiteSpace(o.Vo) ? o.Titre : o.Vo) + " »" +
                                            (o.Year.HasValue ? " (" + o.Year.Value.ToString(CultureInfo.InvariantCulture) + ")" : "")));
                                }
                                hints.Add(line);
                            }
                            else
                            {
                                string msg = "MCC : " + mccFiches.Count + " fiche(s) à ce titre au répertoire, " +
                                             "aucune ne concorde avec la fiche proposée (v.o. divergente) — vérifiez visuellement";
                                hints.Add("⚠️ " + msg);
                                warning = string.IsNullOrEmpty(warning) ? msg : warning + " ; " + msg;
                            }
                        }
                    }
                }

                if (crossKind)
                    hints.Add("⚠️ Fiche croisée : fiche " + kind + " appliquée sur un item " + itemKind + " (type d'item conservé)");

                var carriers = resolver.FindFicheCarriersPublic(meta, item);
                if (carriers.Count > 0)
                {
                    var divergent = carriers.Where(c => c != null && OrphanResolver.SameTitleKey(c.Name) != OrphanResolver.SameTitleKey(item.Name)).ToList();
                    var sameName = carriers.Where(c => c != null && OrphanResolver.SameTitleKey(c.Name) == OrphanResolver.SameTitleKey(item.Name)).ToList();
                    if (sameName.Count > 0)
                        hints.Add("Doublon de dossier : même fiche portée par « " + sameName[0].Name + " » (" + sameName[0].Path + ")");
                    if (divergent.Count > 0)
                    {
                        string msg = "Conflit potentiel : cette fiche est déjà portée par « " + divergent[0].Name + " » (" + divergent[0].Path + ")";
                        hints.Add("⚠️ " + msg);
                        warning = msg;
                    }
                }

                string proofLevel;
                string proofSource;
                string proofEvidence;
                string truthSource;
                List<string> rejectedSummary = null;
                if (snap != null)
                {
                    proofLevel = snap.ProofLevel;
                    proofSource = snap.ProofSource;
                    proofEvidence = snap.ProofEvidence;
                    truthSource = snap.TruthSource;
                    rejectedSummary = snap.RejectedSummary;
                    if (!snap.Accepted)
                    {
                        string weakWarn = "Preuve faible (" + (snap.VerdictReason ?? "non confirmé par la cascade") + ") — validez visuellement les affiches avant d'approuver.";
                        warning = string.IsNullOrEmpty(warning) ? weakWarn : (warning + " ; " + weakWarn);
                    }
                }
                else
                {
                    proofLevel = "manuel";
                    proofSource = "proposition directe (hors cascade)";
                    proofEvidence = "Candidat non passé par identify_resolve — validez visuellement les affiches.";
                    truthSource = "non évaluée";
                    string directWarn = "Candidat proposé sans passage préalable par identify_resolve — vérifiez visuellement.";
                    warning = string.IsNullOrEmpty(warning) ? directWarn : (warning + " ; " + directWarn);
                }

                string tmdbLink = "https://www.themoviedb.org/"
                    + (string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase) ? "tv/" : "movie/")
                    + newTmdbStr;

                var diff = new ChatApiService.ChatActionDiffInfo
                {
                    Kind = "identify",
                    CrossKind = crossKind,
                    ItemId = normItemId,
                    ItemName = item.Name,
                    ItemPath = item.Path,
                    ItemImageUrl = BuildItemImageUrl(item),
                    ItemLink = await BuildItemDeepLinkAsync(_host, item).ConfigureAwait(false),
                    CandidateTitle = meta.Title,
                    CandidateYear = meta.Year,
                    CandidateOverview = Truncate(meta.Overview, 320),
                    CandidatePosterUrl = meta.PosterUrl,
                    CandidateTmdbLink = tmdbLink,
                    ProofLevel = proofLevel,
                    ProofSource = proofSource,
                    ProofEvidence = proofEvidence,
                    TruthSource = truthSource,
                    Changes = changes,
                    Hints = hints,
                    Rejected = rejectedSummary,
                    Warning = warning
                };

                // Arguments normalisés et figés anti-TOCTOU
                JsonElement frozenArgs;
                using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    item_id = normItemId,
                    tmdb_id = meta.TmdbId,
                    kind = kind,
                    cross_kind = crossKind,
                    replace = needsReplace,
                    expected_tmdb = curTmdb ?? string.Empty,
                    expected_imdb = curImdb ?? string.Empty,
                    expected_tvdb = curTvdb ?? string.Empty
                })))
                {
                    frozenArgs = doc.RootElement.Clone();
                }

                string label = ChatActions.LF("act.label.identify",
                    item.Name ?? normItemId,
                    (meta.Title ?? newTmdbStr) + (meta.Year.HasValue ? " (" + meta.Year.Value + ")" : ""),
                    newTmdbStr);
                // Plafond serveur des candidats concurrents d'un même item (3) +
                // exclusion mutuelle à l'approbation (ChatActionStore.Consume).
                return ChatActions.CreatePending(_sessionId, _adminUserId, ToolKey, frozenArgs, label, diff,
                    group: "identify:" + normItemId, maxPerGroup: MaxCandidateCardsPerItem);
            }

            public async Task<string> ExecuteCoreAsync(JsonElement args, CancellationToken ct)
            {
                try
                {
                    string itemId = ChatActions.ArgString(args, "item_id");
                    string kind = ChatActions.ArgString(args, "kind") ?? "movie";
                    int tmdbId = args.TryGetProperty("tmdb_id", out var tEl) && tEl.TryGetInt32(out int t) ? t : 0;
                    bool crossKind = args.TryGetProperty("cross_kind", out var ckEl) && ckEl.ValueKind == JsonValueKind.True;
                    bool replace = args.TryGetProperty("replace", out var rpEl) && rpEl.ValueKind == JsonValueKind.True;
                    string expTmdb = ChatActions.ArgString(args, "expected_tmdb") ?? string.Empty;
                    string expImdb = ChatActions.ArgString(args, "expected_imdb") ?? string.Empty;
                    string expTvdb = ChatActions.ArgString(args, "expected_tvdb") ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(itemId) || tmdbId <= 0)
                        return ChatActions.Json(new { status = "refused", detail = ChatActions.L("act.ref.identify.invalid") });

                    var item = ItemIdResolver.Resolve(_library, itemId);
                    if (item == null)
                        return ChatActions.Json(new { status = "refused", detail = ChatActions.LF("act.ref.identify.notfound", itemId) });

                    // Anti-TOCTOU : vérifier que les ids de l'item n'ont pas changé depuis le dépôt
                    string curTmdb = SafeProvider(item, "tmdb") ?? string.Empty;
                    string curImdb = SafeProvider(item, "imdb") ?? string.Empty;
                    string curTvdb = SafeProvider(item, "tvdb") ?? string.Empty;
                    if (!string.Equals(curTmdb, expTmdb, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(curImdb, expImdb, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(curTvdb, expTvdb, StringComparison.OrdinalIgnoreCase))
                    {
                        return ChatActions.Json(new { status = "refused", detail = ChatActions.LF("act.ref.identify.changed", item.Name) });
                    }

                    var verdict = ChatActions.Reserve(_sessionId, 1, _cfg);
                    if (verdict != ChatActions.BudgetVerdict.Ok)
                        return ChatActions.BudgetRefusal(_sessionId, verdict, _cfg);

                    var resolver = CreateResolver(_logger, _json, _library, _users, _liveTv, _host);
                    string userTmdb = ResolveUserTmdb(_cfg, _host);
                    var meta = await resolver.LookupMetaForChatAsync(tmdbId, kind, item, _cfg, userTmdb, ct).ConfigureAwait(false);
                    if (meta == null || meta.TmdbId <= 0)
                    {
                        ChatActions.Refund(_sessionId, 1);
                        return ChatActions.Json(new { status = "failed", detail = ChatActions.LF("act.ref.identify.tmdbfail", tmdbId) });
                    }

                    bool isSeries = item.GetType().Name.IndexOf("Series", StringComparison.OrdinalIgnoreCase) >= 0;
                    await resolver.ApplyAsync(item, meta, kind, isSeries, ct, crossKind,
                        replaceIds: replace, forceIdentifiedTag: true).ConfigureAwait(false);

                    _logger?.Info("[LLM_AI] Chat action : « {0} » (id={1}) identifié → tmdb={2} imdb={3} (replace={4}, crossKind={5}).",
                        item.Name, item.InternalId, meta.TmdbId, meta.ImdbId ?? "—", replace, crossKind);

                    await ChatActions.ToastActionAsync(_sessionId,
                        ChatActions.LF("act.toast.identify", item.Name, meta.TmdbId), _logger).ConfigureAwait(false);
                    return ChatActions.Json(new
                    {
                        status = "ok",
                        detail = ChatActions.LF("act.ok.identify", item.Name, meta.Title ?? "?", meta.TmdbId)
                    });
                }
                catch (OperationCanceledException) { return ChatActions.Json(new { status = "failed", detail = ChatActions.L("act.ref.cancelled") }); }
                catch (Exception ex)
                {
                    ChatActions.Refund(_sessionId, 1);
                    _logger?.Warn("[LLM_AI] Chat action identify_propose : {0}", ex.Message);
                    return ChatActions.Json(new { status = "failed", detail = ex.Message });
                }
            }
        }

        // ------------------------------------------------------------------
        //  Tool 4 : identify_undo (deux phases)
        // ------------------------------------------------------------------

        internal sealed class IdentifyUndoTool : ILlmTool, IChatActionTool
        {
            private readonly PluginConfiguration _cfg;
            private readonly string _sessionId;
            private readonly string _adminUserId;
            private readonly ILibraryManager _library;
            private readonly IUserManager _users;
            private readonly ILiveTvManager _liveTv;
            private readonly IServerApplicationHost _host;
            private readonly IJsonSerializer _json;
            private readonly ILogger _logger;

            public IdentifyUndoTool(PluginConfiguration cfg, string sessionId, string adminUserId,
                ILibraryManager library, IUserManager users, ILiveTvManager liveTv,
                IServerApplicationHost host, IJsonSerializer json, ILogger logger)
            {
                _cfg = cfg; _sessionId = sessionId; _adminUserId = adminUserId;
                _library = library; _users = users; _liveTv = liveTv;
                _host = host; _json = json; _logger = logger;
            }

            public string ToolKey => "identify_undo";
            public string Name => "identify_undo";
            public string Description =>
                "Retire les identifiants provider (tmdb/imdb/tvdb), les tags llmai-* et les verrous d'un " +
                "item (et restaure la vérité EPG si disponible dans le store). DEUX PHASES : l'appel DÉPOSE " +
                "une proposition (status \"awaiting_approval\") sans exécuter — l'admin approuve sur la carte.";
            public string ArgumentsSchema =>
                "{\"type\":\"object\",\"properties\":{" +
                "\"item_id\":{\"type\":\"string\",\"description\":\"Identifiant de l'item à réinitialiser (obligatoire)\"}}," +
                "\"required\":[\"item_id\"]}";

            public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
            {
                string idStr = ChatActions.ArgString(args, "item_id");
                if (string.IsNullOrWhiteSpace(idStr))
                    return ChatActions.Json(new { status = "refused", detail = "paramètre 'item_id' requis." });

                var item = ItemIdResolver.Resolve(_library, idStr);
                if (item == null)
                    return ChatActions.Json(new { status = "refused", detail = "Item introuvable : " + idStr });
                if (OrphanResolver.IsStrmCard(item))
                    return ChatActions.Json(new { status = "refused", detail = "Les cartes .strm ne sont pas concernées." });

                string curTmdb = SafeProvider(item, "tmdb");
                string curImdb = SafeProvider(item, "imdb");
                string curTvdb = SafeProvider(item, "tvdb");
                var pluginTags = new[]
                {
                    OrphanIdentifyTask.TagIdentified,
                    OrphanIdentifyTask.TagNeedsReview,
                    OrphanIdentifyTask.TagNotFound,
                    OrphanIdentifyTask.TagCrossKind
                }.Where(t => OrphanResolver.HasTag(item, t)).ToList();

                var locks = new[] { MetadataFields.Name, MetadataFields.Overview, MetadataFields.Genres }
                    .Where(f => OrphanResolver.HasLock(item, f))
                    .Select(f => f.ToString())
                    .ToList();

                if (curTmdb == null && curImdb == null && curTvdb == null && pluginTags.Count == 0 && locks.Count == 0)
                    return ChatActions.Json(new { status = "refused",
                        detail = "Rien à retirer sur « " + item.Name + " » (aucun id provider, tag llmai-* ou verrou)." });

                var resolver = CreateResolver(_logger, _json, _library, _users, _liveTv, _host);
                bool isSeries = item.GetType().Name.IndexOf("Series", StringComparison.OrdinalIgnoreCase) >= 0;
                var truth = resolver.TryRecoverStoreTruth(item, isSeries, out string truthSource);

                var changes = new List<string>();
                if (curTmdb != null) changes.Add("tmdb : " + curTmdb + " → —");
                if (curImdb != null) changes.Add("imdb : " + curImdb + " → —");
                if (curTvdb != null) changes.Add("tvdb : " + curTvdb + " → —");
                if (pluginTags.Count > 0) changes.Add("Tags retirés : " + string.Join(", ", pluginTags));
                if (locks.Count > 0) changes.Add("Verrous levés : " + string.Join(", ", locks));
                if (truth != null && !string.IsNullOrWhiteSpace(truth.Overview))
                    changes.Add("Synopsis EPG restauré depuis " + truthSource);

                string normItemId = item.InternalId.ToString(CultureInfo.InvariantCulture);
                var diff = new ChatApiService.ChatActionDiffInfo
                {
                    Kind = "undo",
                    ItemId = normItemId,
                    ItemName = item.Name,
                    ItemPath = item.Path,
                    ItemImageUrl = BuildItemImageUrl(item),
                    ItemLink = await BuildItemDeepLinkAsync(_host, item).ConfigureAwait(false),
                    TruthSource = truthSource,
                    Changes = changes
                };

                JsonElement frozenArgs;
                using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    item_id = normItemId,
                    expected_tmdb = curTmdb ?? string.Empty,
                    expected_imdb = curImdb ?? string.Empty,
                    expected_tvdb = curTvdb ?? string.Empty
                })))
                {
                    frozenArgs = doc.RootElement.Clone();
                }

                string label = ChatActions.LF("act.label.identify.undo", item.Name ?? normItemId);
                return ChatActions.CreatePending(_sessionId, _adminUserId, ToolKey, frozenArgs, label, diff);
            }

            public async Task<string> ExecuteCoreAsync(JsonElement args, CancellationToken ct)
            {
                try
                {
                    string itemId = ChatActions.ArgString(args, "item_id");
                    string expTmdb = ChatActions.ArgString(args, "expected_tmdb") ?? string.Empty;
                    string expImdb = ChatActions.ArgString(args, "expected_imdb") ?? string.Empty;
                    string expTvdb = ChatActions.ArgString(args, "expected_tvdb") ?? string.Empty;

                    var item = ItemIdResolver.Resolve(_library, itemId);
                    if (item == null)
                        return ChatActions.Json(new { status = "refused", detail = ChatActions.LF("act.ref.identify.notfound", itemId ?? "?") });

                    string curTmdb = SafeProvider(item, "tmdb") ?? string.Empty;
                    string curImdb = SafeProvider(item, "imdb") ?? string.Empty;
                    string curTvdb = SafeProvider(item, "tvdb") ?? string.Empty;
                    if (!string.Equals(curTmdb, expTmdb, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(curImdb, expImdb, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(curTvdb, expTvdb, StringComparison.OrdinalIgnoreCase))
                    {
                        return ChatActions.Json(new { status = "refused", detail = ChatActions.LF("act.ref.identify.changed", item.Name) });
                    }

                    var verdict = ChatActions.Reserve(_sessionId, 1, _cfg);
                    if (verdict != ChatActions.BudgetVerdict.Ok)
                        return ChatActions.BudgetRefusal(_sessionId, verdict, _cfg);

                    var resolver = CreateResolver(_logger, _json, _library, _users, _liveTv, _host);
                    string summary = await resolver.UndoForChatAsync(item, ct).ConfigureAwait(false);

                    _logger?.Info("[LLM_AI] Chat action : identification annulée sur « {0} » (id={1}) — {2}.",
                        item.Name, item.InternalId, summary);

                    await ChatActions.ToastActionAsync(_sessionId,
                        ChatActions.LF("act.toast.identify.undo", item.Name), _logger).ConfigureAwait(false);
                    return ChatActions.Json(new
                    {
                        status = "ok",
                        detail = ChatActions.LF("act.ok.identify.undo", item.Name, summary)
                    });
                }
                catch (OperationCanceledException) { return ChatActions.Json(new { status = "failed", detail = ChatActions.L("act.ref.cancelled") }); }
                catch (Exception ex)
                {
                    ChatActions.Refund(_sessionId, 1);
                    _logger?.Warn("[LLM_AI] Chat action identify_undo : {0}", ex.Message);
                    return ChatActions.Json(new { status = "failed", detail = ex.Message });
                }
            }
        }
    }
}
