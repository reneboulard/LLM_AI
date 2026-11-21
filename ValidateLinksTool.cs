using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    // ---------------------------------------------------------------------
    //  validate_links (v2.22.0) : outil CHAT ADMIN de validation des liens
    //  de fiches — lecture seule, verdict rendu côté serveur (le LLM
    //  rapporte, il n'arbitre jamais — banc 2026-11-19 : les modèles
    //  échouent seuls sur « l'id qui a servi à trouver la fiche n'est
    //  jamais une preuve »). Voir <see cref="LinkValidator"/> pour la
    //  doctrine du croisement.
    //
    //  Sélection libre : items (ids explicites — le LLM les obtient par
    //  ses outils de recherche), library (nom/chemin), since (date
    //  d'ajout), full (re-scan complet). Ce tool n'ÉCRIT JAMAIS l'état
    //  d'audit : le curseur incrémental n'appartient qu'à l'action
    //  links_check de system_audit.
    // ---------------------------------------------------------------------

    /// <summary>Tool chat admin : valide la cohérence des ids de fiches
    /// d'une sélection de films/séries.</summary>
    public class ValidateLinksTool : ILlmTool
    {
        /// <summary>Plafond dur de la sélection chat (même plafond que
        /// l'audit — un run reste borné, jamais de passe interminable).</summary>
        internal const int HardCap = 1000;

        private readonly User _admin;
        private readonly ILibraryManager _library;
        private readonly IServerApplicationHost _host;
        private readonly ILogger _logger;

        public string Name => "validate_links";

        public string Description =>
            "Valide la cohérence des ids de fiches (TMDB/IMDb/TVDB) de films/séries : relit la fiche TMDB " +
            "correspondant à l'id de l'item et croise imdb/tvdb/titre/année. LECTURE SEULE (admin) — ne " +
            "modifie JAMAIS les ids. Retourne un rapport avec verdicts (corroboré / variante / à réviser / " +
            "divergent / non vérifiable) et le lien Emby de chaque item notable.";

        public string ArgumentsSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""items"": {""type"": ""array"", ""items"": {""type"": ""string""}, ""description"": ""ids d'items Emby à valider (obtenus via get_emby_info action=\""find\"")""},
    ""library"": {""type"": ""string"", ""description"": ""nom de bibliothèque (ex. \""Séries\"") — optionnel""},
    ""since"": {""type"": ""string"", ""description"": ""date aaaa-mm-jj : items AJOUTÉS à la bibliothèque depuis cette date — optionnel""},
    ""full"": {""type"": ""boolean"", ""description"": ""true : re-scan complet par date d'ajout (plafond 1000 items) — optionnel""}
  }
}";

        public ValidateLinksTool(User admin, ILibraryManager library, IServerApplicationHost host, ILogger logger)
        {
            _admin = admin;
            _library = library;
            _host = host;
            _logger = logger;
        }

        public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            string lang = I18n.ResolveDisplayLangKey(_host);
            try
            {
                if (_admin == null || !(_admin.Policy?.IsAdministrator ?? false))
                    return ChatActions.Json(new { error = I18n.S("links.err.admin", lang) });

                var cfg = Plugin.Instance?.Configuration;
                if (string.IsNullOrWhiteSpace(cfg?.TmdbApiKey))
                    return ChatActions.Json(new { error = I18n.S("links.err.nokey", lang) });

                if (args.ValueKind != JsonValueKind.Object) args = default;
                List<string> itemIds = ReadIdList(args, "items");
                string libraryName = ChatActions.ArgString(args, "library");
                string sinceRaw = ChatActions.ArgString(args, "since");
                bool full = ReadBool(args, "full");

                if ((itemIds == null || itemIds.Count == 0) && string.IsNullOrWhiteSpace(libraryName)
                    && string.IsNullOrWhiteSpace(sinceRaw) && !full)
                    return ChatActions.Json(new { error = I18n.S("links.err.selector", lang) });

                var lot = new List<BaseItem>();
                var skipped = new List<object>();

                // 1. Items explicites (prioritaires).
                if (itemIds != null)
                {
                    foreach (var raw in itemIds)
                    {
                        var item = ItemIdResolver.Resolve(_library, raw);
                        if (item == null) { skipped.Add(new { id = raw, why = "introuvable" }); continue; }
                        if (!(item is Movie) && !(item is Series)) { skipped.Add(new { id = raw, why = "pas un film/série" }); continue; }
                        if (OrphanResolver.IsStrmCard(item)) { skipped.Add(new { id = raw, why = "carte .strm" }); continue; }
                        if (!lot.Any(i => i.InternalId == item.InternalId)) lot.Add(item);
                    }
                }

                // 2. Sélecteurs de périmètre : bibliothèque / date / full.
                if (!string.IsNullOrWhiteSpace(libraryName) || !string.IsNullOrWhiteSpace(sinceRaw) || full)
                {
                    var pool = QueryPool();
                    BaseItem root = null;
                    if (!string.IsNullOrWhiteSpace(libraryName))
                    {
                        root = ResolveLibrary(libraryName);
                        if (root == null)
                            return ChatActions.Json(new { error = string.Format(CultureInfo.InvariantCulture,
                                I18n.S("links.err.library", lang), libraryName) });
                    }
                    DateTime? since = ParseDate(sinceRaw);
                    if (!string.IsNullOrWhiteSpace(sinceRaw) && !since.HasValue)
                        return ChatActions.Json(new { error = I18n.S("links.err.date", lang) });

                    foreach (var it in pool)
                    {
                        if (root != null && !IsUnder(it, root)) continue;
                        if (since.HasValue && it.DateCreated < since.Value) continue;
                        if (!LinkValidator.IsEligible(it)) continue;
                        if (!lot.Any(i => i.InternalId == it.InternalId)) lot.Add(it);
                    }
                }

                if (lot.Count == 0)
                    return ChatActions.Json(new { error = I18n.S("links.err.noitems", lang), skipped = skipped });

                var ordered = lot.OrderBy(i => i.DateCreated).ThenBy(i => i.InternalId).Take(HardCap).ToList();
                string serverId = await LinkValidator.ResolveServerIdAsync(_host).ConfigureAwait(false);
                string tmdbLang = I18n.ToTmdbLang(I18n.ResolveMetaLangKey(cfg, _host));

                var engine = new LinkValidator(new TmdbLookupTool(_logger), _logger);
                var batch = await engine.CheckItemsAsync(ordered, tmdbLang, serverId, ct).ConfigureAwait(false);

                _logger?.Info("[LLM_AI] validate_links (chat admin) : {0} item(s) sélectionné(s), {1} validé(s), divergent={2}.",
                    lot.Count, batch.Audited, batch.Divergent);

                return ChatActions.Json(new
                {
                    report = LinkValidator.BuildReportText(batch, lang),
                    audited = batch.Audited,
                    corroborated = batch.Corroborated,
                    variant = batch.Variant,
                    internal_epg = batch.InternalEpg,
                    non_verifiable = batch.NonVerifiable,
                    review = batch.Review,
                    divergent = batch.Divergent,
                    tmdb_calls = batch.TmdbCalls,
                    elapsed_sec = Math.Round(batch.ElapsedSec, 1),
                    notable = LinkValidator.BuildNotableProjection(batch, lang),
                    skipped = skipped.Count > 0 ? skipped : null
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] validate_links a levé : {0}", ex, ex.Message);
                return ChatActions.Json(new { error = ex.Message });
            }
        }

        // ------------------------------------------------------------------
        //  Sélection
        // ------------------------------------------------------------------

        /// <summary>Périmètre candidat : tous les films/séries (le filtre
        /// d'éligibilité — ≥1 id — est appliqué ensuite en mémoire ; la
        /// requête reste celle des autres actions d'audit).</summary>
        private List<BaseItem> QueryPool()
        {
            try
            {
                var res = _library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Movie", "Series" },
                    Recursive = true,
                    EnableTotalRecordCount = false
                });
                return (res ?? Array.Empty<BaseItem>()).Where(i => i != null).ToList();
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] validate_links : requête bibliothèque échouée ({0}).", ex.Message);
                return new List<BaseItem>();
            }
        }

        /// <summary>Résout une bibliothèque par nom (insensible à la
        /// casse) ou par préfixe de chemin, via les dossiers virtuels
        /// déclarés (<c>GetVirtualFolders</c> — chaque
        /// <c>VirtualFolderInfo</c> porte un <c>ItemId</c> résolu en
        /// racine de bibliothèque).</summary>
        private BaseItem ResolveLibrary(string nameOrPath)
        {
            string q = (nameOrPath ?? "").Trim();
            if (q.Length == 0) return null;
            try
            {
                var folders = _library.GetVirtualFolders();
                foreach (var f in folders ?? new List<MediaBrowser.Model.Entities.VirtualFolderInfo>())
                {
                    if (f == null) continue;
                    bool match = NameMatches(f.Name, q)
                        || (f.Locations ?? Array.Empty<string>()).Any(l => PathMatches(l, q));
                    if (!match) continue;
                    if (Guid.TryParse(f.ItemId, out var gid))
                    {
                        try { return _library.GetItemById(gid); } catch { }
                    }
                }
            }
            catch (Exception ex) { _logger?.Warn("[LLM_AI] validate_links : dossiers virtuels illisibles ({0}).", ex.Message); }
            return null;
        }

        /// <summary>L'item est-il sous la racine de bibliothèque ? (Le
        /// filtre AncestorIds serait plus précis en profondeur, mais le
        /// périmètre éligible est petit — le croisé par parent direct
        /// suffit et tolère les structures déplacées.)</summary>
        private static bool IsUnder(BaseItem item, BaseItem root)
        {
            try
            {
                var p = item.GetParents();
                foreach (var anc in p ?? Array.Empty<BaseItem>())
                    if (anc != null && anc.InternalId == root.InternalId) return true;
                return item.InternalId == root.InternalId;
            }
            catch { return false; }
        }

        private static bool NameMatches(string name, string q) =>
            !string.IsNullOrWhiteSpace(name)
            && string.Equals(name.Trim(), q, StringComparison.OrdinalIgnoreCase);

        private static bool PathMatches(string path, string q)
        {
            if (string.IsNullOrWhiteSpace(path) || q.Length < 2) return false;
            string a = NormalizePath(path), b = NormalizePath(q);
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string p) =>
            (p ?? "").TrimEnd('\\', '/').Replace('/', '\\').ToLowerInvariant();

        private static DateTime? ParseDate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            if (DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.None, out d)) return d;
            return null;
        }

        /// <summary>Liste d'ids : tableau de chaînes (ou de nombres), ou
        /// chaîne unique « id1,id2 » — même tolérance que ChatActions.</summary>
        private static List<string> ReadIdList(JsonElement args, string name)
        {
            try
            {
                if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return null;
                var list = new List<string>();
                if (v.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in v.EnumerateArray())
                    {
                        if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                            list.Add(e.GetString().Trim());
                        else if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n))
                            list.Add(n.ToString(CultureInfo.InvariantCulture));
                    }
                }
                else if (v.ValueKind == JsonValueKind.String)
                {
                    foreach (var s in v.GetString().Split(','))
                        if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                }
                return list;
            }
            catch { return null; }
        }

        private static bool ReadBool(JsonElement args, string name)
        {
            try
            {
                if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return false;
                if (v.ValueKind == JsonValueKind.True) return true;
                return v.ValueKind == JsonValueKind.String
                    && bool.TryParse(v.GetString()?.Trim(), out var b) && b;
            }
            catch { return false; }
        }
    }
}
