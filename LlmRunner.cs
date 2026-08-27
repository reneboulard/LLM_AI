using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace LLM_AI
{
    /// <summary>
    /// Orchestration LLM partagée entre la tâche planifiée
    /// (<see cref="LlmScheduledTask"/>) et l'endpoint à-la-demande
    /// (<c>TonightApiService</c>) : résolution des backends LLM, construction
    /// des outils exposés au modèle, enrichissement des recommandations (match
    /// titre → id/channel_id/rating/image_url), et helpers JSON de
    /// nettoyage/fusion. Centralise cette logique pour éviter la duplication
    /// entre les deux consommateurs : ils obtiennent exactement le même
    /// comportement (backends, outils, enrichissement).
    /// <para>Les méthodes d'instance (<see cref="ResolveBackends"/>,
    /// <see cref="BuildTools"/>, <see cref="EnrichRecommendations"/>) ont
    /// besoin des services Emby (library/users/liveTv/host) + logger ; les
    /// méthodes statiques (<see cref="ExtractJsonPayload"/>,
    /// <see cref="NormTitle"/>, <see cref="MergeJsonArrays"/>,
    /// <see cref="ResolveKey"/>, <see cref="CountRecommendations"/>) sont
    /// pures et sans dépendance.</para>
    /// </summary>
    internal class LlmRunner
    {
        private readonly ILogger _logger;
        private readonly IJsonSerializer _json;
        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly ILiveTvManager _liveTv;
        private readonly IServerApplicationHost _host;

        public LlmRunner(ILogger logger, IJsonSerializer json, ILibraryManager library,
            IUserManager users, ILiveTvManager liveTv, IServerApplicationHost host)
        {
            _logger = logger;
            _json = json;
            _library = library;
            _users = users;
            _liveTv = liveTv;
            _host = host;
        }

        /// <summary>
        /// Construit la liste ordonnée des backends LLM à essayer, depuis la
        /// config : filtre les backends <c>Enabled</c> à URL non vide, triés
        /// par <c>Priority</c> croissante (1 = essayé en premier), puis par
        /// ordre de saisie. Repli de migration : si
        /// <see cref="PluginConfiguration.LlmBackends"/> est vide, on construit
        /// un backend unique depuis les champs legacy
        /// <see cref="PluginConfiguration.LlmUrl"/>/
        /// <see cref="PluginConfiguration.ModelName"/>. Retourne une liste
        /// vide si rien n'est configuré.
        /// </summary>
        public List<LlmBackend> ResolveBackends(PluginConfiguration cfg)
        {
            var list = new List<LlmBackend>();

            if (cfg.LlmBackends != null && cfg.LlmBackends.Count > 0)
            {
                for (int i = 0; i < cfg.LlmBackends.Count; i++)
                {
                    var b = cfg.LlmBackends[i];
                    if (b == null || !b.Enabled)
                        continue;
                    // URL vide acceptée pour cloud/gemini (valeur par défaut
                    // appliquée côté LlmClient). Pour ollama_local, on exige
                    // une URL explicite.
                    if (string.IsNullOrWhiteSpace(b.Url) &&
                        b.ProviderType == LlmProvider.OllamaLocal)
                        continue;
                    list.Add(new LlmBackend
                    {
                        Provider = b.Provider,
                        Url = (b.Url ?? string.Empty).Trim(),
                        Model = b.Model ?? string.Empty,
                        Enabled = true,
                        Priority = b.Priority
                    });
                }
            }
            else if (!string.IsNullOrWhiteSpace(cfg.LlmUrl))
            {
                // Migration : vieille config sans LlmBackends mais avec LlmUrl.
                list.Add(new LlmBackend
                {
                    Provider = "ollama_local",
                    Url = cfg.LlmUrl.Trim(),
                    Model = cfg.ModelName ?? string.Empty,
                    Enabled = true,
                    Priority = 1
                });
                _logger.Info("[LLM_AI] Aucun LlmBackends configuré — utilisation du legacy LlmUrl ({0}).",
                    cfg.LlmUrl.Trim());
            }

            // Tri par priorité croissante, ordre de saisie en cas d'égalité
            // (List<T>.Sort n'étant pas stable, on utilise un tri indexé).
            var indexed = new List<(LlmBackend b, int order)>();
            for (int i = 0; i < list.Count; i++) indexed.Add((list[i], i));
            indexed.Sort((x, y) =>
            {
                int c = x.b.Priority.CompareTo(y.b.Priority);
                return c != 0 ? c : x.order.CompareTo(y.order);
            });

            list = indexed.Select(t => t.b).ToList();

            _logger.Info("[LLM_AI] {0} backend(s) LLM activé(s) — ordre de tentative :",
                list.Count);
            foreach (var b in list)
                _logger.Info("[LLM_AI]   priorité {0} [{1}] : {2} / {3}",
                    b.Priority, b.ProviderType, b.Url, b.Model);

            return list;
        }

        /// <summary>
        /// Résout une clé API : valeur de config si renseignée, sinon repli
        /// sur la variable d'environnement <paramref name="envName"/>.
        /// </summary>
        public static string ResolveKey(string configValue, string envName)
        {
            if (!string.IsNullOrWhiteSpace(configValue)) return configValue.Trim();
            return Environment.GetEnvironmentVariable(envName) ?? string.Empty;
        }

        /// <summary>
        /// Construit la liste d'outils exposés au LLM pour un run. Les outils
        /// optionnels ne sont inclus que si la config correspondante est active.
        /// </summary>
        public List<ILlmTool> BuildTools(PluginConfiguration cfg)
        {
            var tools = new List<ILlmTool>
            {
                new GetEmbyInfoTool(_library, _users, _liveTv, _host, _logger)
            };
            if (!string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
                tools.Add(new TmdbLookupTool(_logger));
            if (!string.IsNullOrWhiteSpace(cfg.TvdbApiKey) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TVDB_API_KEY")))
                tools.Add(new TvdbSearchTool(_logger));
            if (!string.IsNullOrWhiteSpace(cfg.ShowbizzUrl))
                tools.Add(new ShowbizzTool(_logger));
            // web_search : SearXNG (auto-hébergé) OU Ollama cloud.
            bool webSearchConfigured = !string.IsNullOrWhiteSpace(cfg.SearXngUrl) ||
                !string.IsNullOrWhiteSpace(cfg.OllamaApiKey) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OLLAMA_API_KEY"));
            // web_fetch : backend direct auto-hébergé (défaut, sans clé) OU
            // repli Ollama cloud. Disponible dès l'installation pour la communauté.
            bool webFetchConfigured = cfg.WebFetchDirect ||
                !string.IsNullOrWhiteSpace(cfg.OllamaApiKey) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OLLAMA_API_KEY"));
            if (webSearchConfigured)
                tools.Add(new WebSearchTool(_logger));
            if (webFetchConfigured)
                tools.Add(new WebFetchTool(_logger));
            return tools;
        }

        /// <summary>
        /// Exécute un run agent complet (un seul fil de conversation) : résout
        /// les backends + construit les outils, lance la boucle de
        /// tool-calling <see cref="LlmAgentService.RunAsync"/>, nettoie la
        /// réponse finale et l'enrichit (match titres → id/channel_id/rating/
        /// image_url). Retourne (payload enrichi, ok). ok=false si le run a
        /// échoué (erreur catchée + loguée) — l'appelant décide quoi faire.
        /// <see cref="OperationCanceledException"/> est propagée (annulation
        /// réelle, pas un échec ordinaire).
        /// <para>Méthode commune utilisée par la tâche planifiée et par
        /// l'endpoint tonight : un seul endroit code la séquence
        /// backends → outils → run → extract → enrich.</para>
        /// </summary>
        public async System.Threading.Tasks.Task<(string payload, bool ok)> RunAsync(
            PluginConfiguration cfg, string label, string userPrompt, string workflow,
            System.Threading.CancellationToken ct)
        {
            try
            {
                var backends = ResolveBackends(cfg);
                if (backends.Count == 0)
                {
                    _logger.Warn("[LLM_AI] [{0}] Aucun LLM configuré/activé — run ignoré.", label);
                    return (string.Empty, false);
                }

                string ollamaCloudKey = ResolveKey(cfg.OllamaApiKey, "OLLAMA_API_KEY");
                string geminiKey = ResolveKey(cfg.GeminiApiKey, "GEMINI_API_KEY");

                var agent = new LlmAgentService(backends, cfg.RagDirectives, workflow,
                    ollamaCloudKey, geminiKey, _json, _logger, cfg.DebugVerbose);
                var tools = BuildTools(cfg);

                var (reply, toolResults) = await agent.RunAsync(userPrompt, tools, ct).ConfigureAwait(false);

                _logger.Info("[LLM_AI] [{0}] Réponse :\n{1}", label, reply);

                // Nettoie (balises markdown ```json … ```) puis enrichit (match
                // titres vs résultats epg_series/epg_movies → id/channel_id/
                // rating/image_url). Portage C# du matching par titre PHP.
                var payload = ExtractJsonPayload(reply);
                payload = EnrichRecommendations(payload, toolResults);
                return (payload, true);
            }
            catch (OperationCanceledException)
            {
                _logger.Info("[LLM_AI] [{0}] Run annulé.", label);
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[LLM_AI] [{0}] Échec du run : {1}", ex, label, ex.Message);
                return (string.Empty, false);
            }
        }

        /// <summary>
        /// Extrait un tableau JSON propre depuis la réponse du LLM : retire les
        /// balises markdown <c>```json … ```</c>, et isole le premier
        /// <c>[ … ]</c> équilibré. Si la réponse n'est pas du JSON (Markdown
        /// libre), elle est renvoyée telle quelle (la page l'affichera en brut).
        /// </summary>
        public static string ExtractJsonPayload(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return string.Empty;

            var s = reply.Trim();

            // Retire les balises de code markdown éventuelles.
            if (s.StartsWith("```", StringComparison.Ordinal))
            {
                int nl = s.IndexOf('\n');
                if (nl >= 0) s = s.Substring(nl + 1);
                int fenceEnd = s.LastIndexOf("```", StringComparison.Ordinal);
                if (fenceEnd >= 0) s = s.Substring(0, fenceEnd);
                s = s.Trim();
            }

            // Déjà un tableau JSON ?
            if (s.StartsWith("[", StringComparison.Ordinal)) return s;

            // Sinon, isole le premier [ ... ] si présent.
            int start = s.IndexOf('[');
            if (start >= 0)
            {
                int end = s.LastIndexOf(']');
                if (end > start) return s.Substring(start, end - start + 1);
            }

            // Pas de tableau : Markdown libre — on garde tel quel.
            return reply.Trim();
        }

        /// <summary>
        /// Normalise un titre pour le rapprochement : minuscules + retrait de
        /// tout ce qui n'est pas alphanumérique. Équivalent C# du
        /// <c>preg_replace('/[^a-z0-9]/i',''</c> + <c>mb_strtolower</c> du PHP
        /// (absent_series.php / ai_section.php) et du <c>Norm</c> de
        /// <see cref="GetEmbyInfoTool"/>. « Star Trek » et « star-trek! » →
        /// « startrek ».
        /// </summary>
        public static string NormTitle(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Lit une propriété chaîne d'un <see cref="System.Text.Json.Nodes.JsonObject"/>
        /// de façon tolérante : null si absente, nulle, ou non-chaîne.
        /// </summary>
        private static string JsonStr(System.Text.Json.Nodes.JsonObject obj, string key)
        {
            if (obj == null || !obj.TryGetPropertyValue(key, out var n) || n == null) return null;
            try { return n.GetValue<string>(); }
            catch { return null; }
        }

        /// <summary>
        /// Fusionne deux payloads de recommandations en un seul tableau JSON.
        /// Les deux runs (séries + films) produisent chacun un tableau JSON
        /// <c>[{...}]</c> ; on concatène leurs items. Si l'un est vide ou n'est
        /// pas un tableau JSON (Markdown libre — réponse dégradée), on garde
        /// l'autre tel quel. Si les deux sont vides, renvoie une chaîne vide
        /// (la page affichera « aucune recommandation »).
        /// </summary>
        public static string MergeJsonArrays(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) return string.Empty;
            if (string.IsNullOrWhiteSpace(a)) return b;
            if (string.IsNullOrWhiteSpace(b)) return a;

            var merged = new System.Text.Json.Nodes.JsonArray();
            foreach (var p in new[] { a, b })
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(p);
                    if (node is System.Text.Json.Nodes.JsonArray src)
                    {
                        foreach (var item in src)
                            merged.Add(item.DeepClone());
                    }
                    // Non-tableau (Markdown) : ignoré — on garde les items de
                    // l'autre run. On ne mélange pas du prose dans le JSON.
                }
                catch
                {
                    // Non parsable : ignoré.
                }
            }
            return merged.ToJsonString();
        }

        /// <summary>
        /// Compte le nombre d'items d'une réponse JSON tableau
        /// (pour le libellé de la notification). 0 si ce n'est pas un tableau.
        /// </summary>
        public static int CountRecommendations(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return 0;
            try
            {
                using (var doc = JsonDocument.Parse(reply))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        return doc.RootElement.GetArrayLength();
                }
            }
            catch { /* réponse Markdown : on signale juste « mises à jour » */ }
            return 0;
        }

        /// <summary>
        /// Données EPG rattachées à un titre (extraites au moment de la
        /// construction de la lookup) — on ne conserve pas de <c>JsonElement</c>
        /// au-delà de la durée de vie du <c>JsonDocument</c> source (sinon
        /// <c>ObjectDisposedException</c> à la lecture différée).
        /// </summary>
        private readonly struct EpgMatch
        {
            public readonly string Id;
            public readonly string ChannelId;
            public readonly double? Rating;
            public EpgMatch(string id, string channelId, double? rating)
            { Id = id; ChannelId = channelId; Rating = rating; }
        }

        /// <summary>
        /// Enrichit le payload de recommandations (tableau JSON
        /// <c>[{title,kind,reason,priority,channel,start,showbizz_match}]</c>)
        /// en rapprochant chaque titre des résultats d'outils
        /// <c>get_emby_info</c> (actions <c>epg_series</c>/<c>epg_movies</c>/
        /// <c>epg_tonight</c>) capturés pendant la boucle agent. Les items EPG
        /// portent <c>id</c>/<c>channel_id</c>/<c>rating</c> ; on les injecte
        /// dans la recommandation + <c>image_url</c> (poster) construit depuis
        /// l'id Emby.
        /// <para>Portage C# du matching par titre du PHP : on construit une
        /// lookup <c>norm(title) → EpgMatch</c>, puis pour chaque reco on
        /// cherche <c>norm(reco.title)</c>. Si pas de match (titre rewordé par
        /// le LLM), la reco est gardée telle quelle (pas de poster, Programmer
        /// désactivé côté UI). Retourne le payload inchangé si ce n'est pas un
        /// tableau JSON.</para>
        /// </summary>
        public string EnrichRecommendations(string payload,
            List<(string tool, string result)> toolResults)
        {
            if (string.IsNullOrWhiteSpace(payload)) return payload;

            // 1) Construit la lookup norm(title) → EpgMatch depuis les résultats
            //    d'outils. On ne garde que les résultats d'epg_series/epg_movies/
            //    epg_tonight ; leur forme est
            //    {total, results:[{title,id,channel_id,rating,...}]}.
            //    On extrait les primitives tout de suite (le JsonDocument est
            //    disposé à la sortie du bloc using — on ne garde aucune référence
            //    à ses JsonElement).
            var lookup = new Dictionary<string, EpgMatch>(StringComparer.Ordinal);
            foreach (var tr in toolResults)
            {
                if (string.IsNullOrEmpty(tr.result)) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(tr.result); }
                catch { continue; }
                using (doc)
                {
                    if (!doc.RootElement.TryGetProperty("results", out var results) ||
                        results.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var item in results.EnumerateArray())
                    {
                        if (!item.TryGetProperty("title", out var titleEl)) continue;
                        string title = titleEl.ValueKind == JsonValueKind.String
                            ? titleEl.GetString() : null;
                        string key = NormTitle(title);
                        if (string.IsNullOrEmpty(key)) continue;

                        string id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                            ? idEl.GetString() : null;
                        string channelId = item.TryGetProperty("channel_id", out var cidEl) && cidEl.ValueKind == JsonValueKind.String
                            ? cidEl.GetString() : null;
                        double? rating = null;
                        if (item.TryGetProperty("rating", out var rEl) && rEl.ValueKind == JsonValueKind.Number)
                            rating = rEl.GetDouble();

                        // Premier vu gagne (dedup) — les EPG peuvent lister la même
                        // série sur plusieurs chaînes ; on garde la 1re occurrence.
                        if (!lookup.ContainsKey(key))
                            lookup[key] = new EpgMatch(id, channelId, rating);
                    }
                }
            }

            if (lookup.Count == 0)
            {
                _logger?.Info("[LLM_AI] Enrichissement : aucun résultat epg capturé — reco laissées telles quelles.");
                return payload;
            }

            // 2) Reconstruit le tableau de recommandations en injectant les champs
            //    rattachés. On utilise JsonNode (mutable) pour merger proprement.
            System.Text.Json.Nodes.JsonNode root;
            try { root = System.Text.Json.Nodes.JsonNode.Parse(payload); }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Enrichissement : payload non parsable ({0}) — laissé tel quel.", ex.Message);
                return payload;
            }

            if (root is not System.Text.Json.Nodes.JsonArray arr)
            {
                // Pas un tableau (Markdown libre) : rien à enrichir.
                return payload;
            }

            int enriched = 0, matched = 0;
            foreach (var node in arr)
            {
                if (node is not System.Text.Json.Nodes.JsonObject obj) continue;

                string source = JsonStr(obj, "source");
                bool isWatchItem = string.Equals(source, "recording", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(source, "library", StringComparison.OrdinalIgnoreCase);

                // source="recording" ou "library" : l'id est un item Emby concret
                // (enregistrement ou item de bibliothèque) fourni par le LLM depuis
                // les listes injectées. On NE surcharge pas avec un match EPG — le
                // titre pourrait coller à un programme du soir sans que ce soit le
                // même item ; on garderait le mauvais id. On s'assure juste du
                // poster depuis cet id.
                if (isWatchItem)
                {
                    string recId = JsonStr(obj, "id");
                    if (!string.IsNullOrEmpty(recId) && string.IsNullOrEmpty(JsonStr(obj, "image_url")))
                    {
                        obj["image_url"] = "/emby/Items/" + recId + "/Images/Primary?maxWidth=400";
                        enriched++;
                    }
                    continue;
                }

                if (!obj.TryGetPropertyValue("title", out var titleNode)) continue;
                string title = titleNode?.GetValue<string>();
                string key = NormTitle(title);
                if (string.IsNullOrEmpty(key)) continue;

                if (lookup.TryGetValue(key, out var epg))
                {
                    matched++;
                    if (!string.IsNullOrEmpty(epg.Id))
                    {
                        obj["id"] = epg.Id;
                        // URL racine-relative : le navigateur la résout contre l'origine
                        // depuis laquelle l'utilisateur consulte la page (localhost, IP
                        // LAN, domaine, http/https) — aucun host codé. La page
                        // recommendations.js la rebâtira en absolu via ApiClient.serverAddress().
                        obj["image_url"] = "/emby/Items/" + epg.Id + "/Images/Primary?maxWidth=400";
                        enriched++;
                    }
                    if (!string.IsNullOrEmpty(epg.ChannelId))
                        obj["channel_id"] = epg.ChannelId;
                    if (epg.Rating.HasValue)
                        obj["rating"] = epg.Rating.Value;
                }
                else
                {
                    // Pas de match EPG : si la reco porte déjà un id (ex. LLM qui
                    // a fourni un id de programme), on bâtit quand même le poster.
                    string exId = JsonStr(obj, "id");
                    if (!string.IsNullOrEmpty(exId) && string.IsNullOrEmpty(JsonStr(obj, "image_url")))
                    {
                        obj["image_url"] = "/emby/Items/" + exId + "/Images/Primary?maxWidth=400";
                        enriched++;
                    }
                }
            }

            _logger?.Info("[LLM_AI] Enrichissement : {0}/{1} reco matchées, {2} avec id/image_url.",
                matched, arr.Count, enriched);

            return arr.ToJsonString();
        }

        /// <summary>
        /// Rapproche les recommandations de la bibliothèque :
        /// <list type="bullet">
        /// <item><c>source="live"</c> (EPG du soir) : si le titre est déjà possédé,
        /// on injecte <c>library_id</c> (+ poster si absent) → bouton
        /// « Regarder (bibli.) ».</item>
        /// <item><c>source="library"</c> (réserve bibliothèque) : si le LLM a omis
        /// l'<c>id</c>, on le backfill depuis le match (→ bouton « Regarder »).</item>
        /// <item><c>source="recording"</c> : ignorée (l'id vient de la liste
        /// d'enregistrements injectée par <see cref="TonightApiService"/>).</item>
        /// </list>
        /// Recherche par <c>Name</c> exact (indexé, peu coûteux) puis repli flou
        /// <c>NameContains</c>, confirmé par <see cref="NormTitle"/>.
        /// </summary>
        public string EnrichWithLibrary(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return payload;
            System.Text.Json.Nodes.JsonNode root;
            try { root = System.Text.Json.Nodes.JsonNode.Parse(payload); }
            catch { return payload; }
            if (root is not System.Text.Json.Nodes.JsonArray arr) return payload;

            int matched = 0;
            foreach (var node in arr)
            {
                if (node is not System.Text.Json.Nodes.JsonObject obj) continue;
                string source = JsonStr(obj, "source");
                bool isLib = string.Equals(source, "library", StringComparison.OrdinalIgnoreCase);
                bool isRec = string.Equals(source, "recording", StringComparison.OrdinalIgnoreCase);
                if (isRec) continue; // id fourni par le LLM depuis la liste enregistrements
                if (!isLib && !string.IsNullOrEmpty(JsonStr(obj, "library_id"))) continue; // live déjà matché

                string title = JsonStr(obj, "title");
                string key = NormTitle(title);
                if (string.IsNullOrEmpty(key)) continue;

                string libId = FindLibraryItemId(title, key);
                if (string.IsNullOrEmpty(libId)) continue;

                if (isLib)
                {
                    // Backfill l'id de lecture bibliothèque si le LLM l'a omis.
                    if (string.IsNullOrEmpty(JsonStr(obj, "id"))) obj["id"] = libId;
                }
                else
                {
                    // source live : library_id pour le bouton « Regarder (bibli.) ».
                    obj["library_id"] = libId;
                }
                if (string.IsNullOrEmpty(JsonStr(obj, "image_url")))
                    obj["image_url"] = "/emby/Items/" + libId + "/Images/Primary?maxWidth=400";
                matched++;
            }
            _logger?.Info("[LLM_AI] Enrichissement bibliothèque : {0} reco(s) rapprochée(s).", matched);
            return arr.ToJsonString();
        }

        /// <summary>
        /// Cherche un item de bibliothèque (film ou série) dont le nom matche le
        /// titre de la reco. <c>InternalItemsQuery.Name</c> = exact (insensible à
        /// la casse, indexé) ; repli <c>NameContains</c> si l'exact ne donne rien.
        /// Confirme par <see cref="NormTitle"/> pour éviter un faux positif.
        /// </summary>
        private BaseItem FindLibraryItem(string title, string normKey)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            try
            {
                var q = new InternalItemsQuery
                {
                    Name = title,
                    IncludeItemTypes = new[] { "Movie", "Series" },
                    Recursive = true,
                    Limit = 8,
                    EnableTotalRecordCount = false
                };
                var items = _library.GetItemList(q) ?? Array.Empty<BaseItem>();
                foreach (var it in items)
                    if (it != null && NormTitle(it.Name) == normKey) return it;

                // Repli flou (sous-chaîne) si l'exact n'a rien retourné.
                q = new InternalItemsQuery
                {
                    NameContains = title,
                    IncludeItemTypes = new[] { "Movie", "Series" },
                    Recursive = true,
                    Limit = 20,
                    EnableTotalRecordCount = false
                };
                var fuzzy = _library.GetItemList(q) ?? Array.Empty<BaseItem>();
                foreach (var it in fuzzy)
                    if (it != null && NormTitle(it.Name) == normKey) return it;
            }
            catch { }
            return null;
        }

        private string FindLibraryItemId(string title, string normKey)
        {
            var it = FindLibraryItem(title, normKey);
            return it?.Id.ToString();
        }
    }
}