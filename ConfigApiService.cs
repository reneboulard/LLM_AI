using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;

namespace LLM_AI
{
    /// <summary>
    /// Endpoints HTTP utilitaires de la page de configuration :
    /// <list type="bullet">
    /// <item><c>POST /Plugins/LLMAI/TestLlm</c> — appel rapide à UN backend
    /// LLM (provider/url/modèle postés par la page, donc testables AVANT
    /// enregistrement) pour vérifier qu'il répond ; renvoie OK/échec +
    /// latence. Réservé aux administrateurs.</item>
    /// <item><c>POST /Plugins/LLMAI/TestNewReleaseSources</c> — scrappe les
    /// lignes ÉDITÉES du champ « Sources nouveautés » (sans enregistrement,
    /// sans toucher au cache du run) et renvoie, par source : mode détecté,
    /// décompte d'items, 3 titres d'aperçu ou l'erreur (HTTP/timeout/regex).
    /// Réservé aux administrateurs.</item>
    /// <item><c>GET /Plugins/LLMAI/DefaultPrompts</c> — les cinq
    /// prompts/directives par défaut dans la langue configurée, pour le
    /// bouton « Réinitialiser » de la page (usager qui a modifié une
    /// directive et veut retrouver la version propre).</item>
    /// </list>
    /// Couche HTTP fine calquée sur <see cref="ChatApiService"/> : même
    /// résolution d'usager (admin uniquement), mêmes DTO
    /// <see cref="RouteAttribute"/>.
    /// </summary>
    public class ConfigApiService : BaseApiService
    {
        private readonly IJsonSerializer _json;

        public ConfigApiService(IJsonSerializer json)
        {
            _json = json;
        }

        // ------------------------------------------------------------------
        //  Test d'un backend LLM
        // ------------------------------------------------------------------

        /// <summary>
        /// Requête POST <c>/Plugins/LLMAI/TestLlm</c>. Le backend est posté
        /// tel qu'édité dans la page (donc non encore enregistré) :
        /// <c>Provider</c> (<c>ollama_local</c>/<c>ollama_cloud</c>/<c>gemini</c>),
        /// <c>Url</c> (vide = défaut du provider), <c>Model</c>. Les clés API
        /// ne sont PAS postées : elles sont relues côté serveur depuis la
        /// config enregistrée (avec repli variable d'environnement) — le
        /// navigateur ne reçoit jamais une clé en retour.
        /// </summary>
        [Route("/Plugins/LLMAI/TestLlm", "POST")]
        public class TestLlmRequest : IReturn<object>
        {
            public string Provider { get; set; }
            public string Url { get; set; }
            public string Model { get; set; }
        }

        /// <summary>
        /// Réponse du test. <c>Ok</c> : le backend a répondu. <c>Ms</c> :
        /// latence totale de l'appel. <c>Reply</c> : extrait de la réponse
        /// du modèle (borné). <c>Error</c> : message d'échec (connexion,
        /// timeout 30 s, HTTP, clé manquante…).
        /// </summary>
        public class TestLlmResponse
        {
            public bool Ok { get; set; }
            public string Reply { get; set; }
            public int Ms { get; set; }
            public string Error { get; set; }
        }

        public async Task<object> Post(TestLlmRequest req)
        {
            // Réservé aux administrateurs : le test consomme des tokens LLM
            // et peut révéler l'état d'un serveur interne (même porte que le
            // chat / l'audit).
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new TestLlmResponse { Ok = false, Error = I18n.SDisplay("err.admin", ApplicationHost) };

            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new TestLlmResponse { Ok = false, Error = I18n.SDisplay("err.noconfig", ApplicationHost) };

            var backend = new LlmBackend
            {
                Provider = req?.Provider,
                Url = (req?.Url ?? string.Empty).Trim(),
                Model = (req?.Model ?? string.Empty).Trim(),
                Enabled = true,
                Priority = 1
            };

            // Validations locales avant tout appel réseau (messages clairs
            // pour la page plutôt qu'une exception HTTP brute).
            if (string.IsNullOrWhiteSpace(backend.Model))
                return new TestLlmResponse { Ok = false, Error = "Modèle non renseigné." };
            if (backend.ProviderType == LlmProvider.OllamaLocal &&
                string.IsNullOrWhiteSpace(backend.Url))
                return new TestLlmResponse { Ok = false, Error = "URL non renseignée (exigée pour Ollama local)." };

            // Clé API : relue depuis la config enregistrée + repli variable
            // d'environnement (même résolution que les runs : LlmRunner.ResolveKey).
            string apiKey = null;
            if (backend.ProviderType == LlmProvider.OllamaCloud)
                apiKey = LlmRunner.ResolveKey(cfg.OllamaApiKey, "OLLAMA_API_KEY");
            else if (backend.ProviderType == LlmProvider.Gemini)
                apiKey = LlmRunner.ResolveKey(cfg.GeminiApiKey, "GEMINI_API_KEY");

            // Prompt minimal dans la langue configurée : on veut juste vérifier
            // que le serveur ET le modèle répondent.
            string lang = I18n.ResolveMetaLangKey(cfg, ApplicationHost);
            string probe = string.Equals(lang, I18n.Fr, StringComparison.Ordinal)
                ? "Réponds uniquement « OK »."
                : "Reply with exactly: OK";
            var messages = new List<LlmClient.ChatMessage>
            {
                new LlmClient.ChatMessage { Role = "user", Content = probe }
            };

            // Timeout court (30 s) : c'est un test de bouton, pas un run agent.
            // NB : HttpClient lève TaskCanceledException à son propre timeout —
            // on distingue via IsCancellationRequested (cf. gotcha LLM HTTP).
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    string reply = await LlmClient.ChatAsync(backend, apiKey, messages,
                        _json, Logger, cts.Token).ConfigureAwait(false);
                    sw.Stop();
                    return new TestLlmResponse
                    {
                        Ok = true,
                        Ms = (int)sw.ElapsedMilliseconds,
                        Reply = Truncate(reply, 120)
                    };
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    return new TestLlmResponse { Ok = false, Ms = (int)sw.ElapsedMilliseconds, Error = "Aucune réponse en 30 s (timeout)." };
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    return new TestLlmResponse { Ok = false, Ms = (int)sw.ElapsedMilliseconds, Error = Truncate(ex.Message, 300) };
                }
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }

        // ------------------------------------------------------------------
        //  Tests de connexion TMDB / TVDB / SearXNG (boutons de la section
        //  « Clés API ») — même modèle que TestLlm : les clés ne sont PAS
        //  postées (relues depuis la config ENREGISTRÉE + variable
        //  d'environnement pour TVDB), l'URL SearXNG est postée telle
        //  qu'éditée (non secrète), réponse {Ok, Ms, Reply, Error}, timeout
        //  court, aucune écriture de config.
        // ------------------------------------------------------------------

        /// <summary>Réponse des tests de connexion TMDB/TVDB/SearXNG (même
        /// forme que <see cref="TestLlmResponse"/>) : <c>Ok</c> — le service
        /// a répondu ; <c>Ms</c> — latence ; <c>Reply</c> — résumé court du
        /// succès ; <c>Error</c> — message d'échec (clé absente/rejetée,
        /// connexion, timeout, HTTP…).</summary>
        public class TestProbeResult
        {
            public bool Ok { get; set; }
            public string Reply { get; set; }
            public int Ms { get; set; }
            public string Error { get; set; }
        }

        /// <summary>POST <c>/Plugins/LLMAI/TestTmdb</c> : interroge
        /// <c>/3/configuration</c> — la sonde la plus légère qui valide la
        /// clé (sans coût de quota de recherche).</summary>
        [Route("/Plugins/LLMAI/TestTmdb", "POST")]
        public class TestTmdbRequest : IReturn<object> { }

        /// <summary>POST <c>/Plugins/LLMAI/TestTvdb</c> : réalise le
        /// <c>POST /v4/login</c> réel de l'outil (l'authentification EST la
        /// validation).</summary>
        [Route("/Plugins/LLMAI/TestTvdb", "POST")]
        public class TestTvdbRequest : IReturn<object> { }

        /// <summary>POST <c>/Plugins/LLMAI/TestSearxng</c> : recherche JSON
        /// réelle sur l'instance postée telle qu'éditée (le champ n'est pas
        /// secret) — le 403 typique d'un format JSON non activé produit un
        /// message explicite.</summary>
        [Route("/Plugins/LLMAI/TestSearxng", "POST")]
        public class TestSearxngRequest : IReturn<object>
        {
            public string Url { get; set; }
        }

        /// <summary>Test TMDB : la clé est relue depuis la configuration
        /// enregistrée (testez une clé NOUVELLE après enregistrement).
        /// Réservé aux administrateurs (état d'un service externe).</summary>
        public async Task<object> Post(TestTmdbRequest req)
        {
            var guarded = GuardProbe();
            if (guarded != null) return guarded;

            string key = (Plugin.Instance.Configuration.TmdbApiKey ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
                return new TestProbeResult { Ok = false, Error = "Clé TMDB non renseignée — enregistrez-la d'abord." };

            var sw = Stopwatch.StartNew();
            var (ok, status, body) = await ProbeHttpAsync(
                "GET", "https://api.themoviedb.org/3/configuration?api_key=" + Uri.EscapeDataString(key),
                null).ConfigureAwait(false);
            sw.Stop();

            if (!ok)
                return new TestProbeResult { Ok = false, Ms = (int)sw.ElapsedMilliseconds,
                    Error = Truncate(InterpretTmdbError(status, body), 300) };
            return new TestProbeResult { Ok = true, Ms = (int)sw.ElapsedMilliseconds, Reply = "configuration TMDB récupérée" };
        }

        /// <summary>Test TVDB : résolution de clé identique au vrai outil
        /// (config enregistrée, repli variable d'environnement
        /// <c>TVDB_API_KEY</c>).</summary>
        public async Task<object> Post(TestTvdbRequest req)
        {
            var guarded = GuardProbe();
            if (guarded != null) return guarded;

            var cfg = Plugin.Instance.Configuration;
            string key = LlmRunner.ResolveKey(cfg.TvdbApiKey, "TVDB_API_KEY");
            if (string.IsNullOrWhiteSpace(key))
                return new TestProbeResult { Ok = false, Error = "Clé TVDB non renseignée — enregistrez-la d'abord." };

            var sw = Stopwatch.StartNew();
            var (ok, status, body) = await ProbeHttpAsync("POST",
                "https://api4.thetvdb.com/v4/login",
                JsonSerializer.Serialize(new { apikey = key })).ConfigureAwait(false);
            sw.Stop();

            if (!ok)
                return new TestProbeResult { Ok = false, Ms = (int)sw.ElapsedMilliseconds,
                    Error = Truncate((status == 401 || status == 403)
                        ? "Clé TVDB rejetée (HTTP " + status + ")." : body, 300) };

            string token = null;
            try
            {
                using var doc = JsonDocument.Parse(body ?? string.Empty);
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
                    token = t.GetString();
            }
            catch { }
            if (string.IsNullOrWhiteSpace(token))
                return new TestProbeResult { Ok = false, Ms = (int)sw.ElapsedMilliseconds,
                    Error = "Réponse TVDB inattendue (token absent)." };
            return new TestProbeResult { Ok = true, Ms = (int)sw.ElapsedMilliseconds, Reply = "token TVDB obtenu (valide ~23 h)" };
        }

        /// <summary>Test SearXNG : recherche JSON réelle (une requête
        /// « test ») sur l'URL postée telle qu'éditée — testable avant
        /// enregistrement.</summary>
        public async Task<object> Post(TestSearxngRequest req)
        {
            var guarded = GuardProbe();
            if (guarded != null) return guarded;

            string url = (req?.Url ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(url))
                return new TestProbeResult { Ok = false, Error = "URL SearXNG non renseignée (ex. http://localhost:8888)." };

            var sw = Stopwatch.StartNew();
            var (ok, status, body) = await ProbeHttpAsync("GET",
                url.TrimEnd('/') + "/search?q=" + Uri.EscapeDataString("test") + "&format=json",
                null).ConfigureAwait(false);
            sw.Stop();

            if (!ok && status == 403)
                return new TestProbeResult { Ok = false, Ms = (int)sw.ElapsedMilliseconds,
                    Error = "SearXNG HTTP 403 — format JSON non activé dans ses réglages (search.formats doit inclure « json »)." };
            if (!ok)
                return new TestProbeResult { Ok = false, Ms = (int)sw.ElapsedMilliseconds, Error = Truncate(body, 300) };

            int results = 0;
            try
            {
                using var doc = JsonDocument.Parse(body ?? string.Empty);
                results = doc.RootElement.GetProperty("results").GetArrayLength();
            }
            catch
            {
                return new TestProbeResult { Ok = false, Ms = (int)sw.ElapsedMilliseconds,
                    Error = "HTTP 200 mais réponse non JSON — format=json n'est pas actif sur cette instance." };
            }
            return new TestProbeResult { Ok = true, Ms = (int)sw.ElapsedMilliseconds,
                Reply = results + " résultat(s) retourné(s)" };
        }

        /// <summary>Garde commune aux trois sondes : administrateur +
        /// configuration disponible (même porte que TestLlm).</summary>
        private object GuardProbe()
        {
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new TestProbeResult { Ok = false, Error = I18n.SDisplay("err.admin", ApplicationHost) };
            if (Plugin.Instance?.Configuration == null)
                return new TestProbeResult { Ok = false, Error = I18n.SDisplay("err.noconfig", ApplicationHost) };
            return null;
        }

        /// <summary>Appel HTTP d'une sonde : client jetable, timeout 20 s,
        /// statut et corps rendus pour interprétation par service. NB :
        /// le timeout HttpClient lève TaskCanceledException alors que le
        /// token passé n'est PAS annulé — distingué par
        /// <c>cts.IsCancellationRequested</c> (cf. gotcha LLM HTTP).</summary>
        private static async Task<(bool Ok, int Status, string Body)> ProbeHttpAsync(
            string method, string url, string jsonBody)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(20);
                using (var req = new HttpRequestMessage(new HttpMethod(method), url))
                {
                    if (!string.IsNullOrEmpty(jsonBody))
                        req.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

                    try
                    {
                        using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        return (false, 0, "Aucune réponse en 20 s (timeout).");
                    }
                    catch (Exception ex)
                    {
                        return (false, 0, ex.Message);
                    }
                }
            }
        }

        private static string InterpretTmdbError(int status, string body)
        {
            if (status == 401)
                return "Clé TMDB rejetée (401) — vérifiez la clé sur themoviedb.org.";
            if (!string.IsNullOrEmpty(body) && body.TrimStart().StartsWith("<"))
                return "Réponse TMDB inattendue (HTTP " + status + ").";
            return "TMDB HTTP " + status + " — " + (body ?? string.Empty);
        }

        // ------------------------------------------------------------------
        //  Test des sources new_releases (bouton « Tester les sources »)
        // ------------------------------------------------------------------

        /// <summary>Résultat du test d'UNE ligne de sources (média centre : les
        /// chaînes et aperçus viennent du web — l'UI doit les rendre en
        /// textContent, jamais en innerHTML).</summary>
        public class TestSourceResultItem
        {
            public string Source { get; set; }
            public string Mode { get; set; }
            public int Count { get; set; }
            public List<string> Samples { get; set; }
            public string Error { get; set; }
            public int Ms { get; set; }
        }

        /// <summary>Réponse du test : un entrée par ligne testée, plus le
        /// total consolidé. <c>Ok</c> : la requête a abouti (les échecs
        /// individuels sont dans <c>Results[].Error</c>).</summary>
        public class TestNewReleaseSourcesResponse
        {
            public bool Ok { get; set; }
            public int SourcesChecked { get; set; }
            public int TotalItems { get; set; }
            public List<TestSourceResultItem> Results { get; set; }
            public string Note { get; set; }
            public string Error { get; set; }
        }

        /// <summary>
        /// Requête POST <c>/Plugins/LLMAI/TestNewReleaseSources</c>.
        /// <c>Sources</c> : le CONTENU ÉDITÉ du champ sources de la page —
        /// donc non encore enregistré —, une ligne par source au même format
        /// que la config (URL seule = flux RSS/Atom auto-détecté ;
        /// <c>URL :: @showbizz</c> ; <c>URL :: regex .NET</c>). Chaque ligne
        /// est scrapée en direct (aucune écriture de config, aucun impact sur
        /// le cache 24h du run) ; jusqu'à 8 lignes, 8 s par source.
        /// </summary>
        [Route("/Plugins/LLMAI/TestNewReleaseSources", "POST")]
        public class TestNewReleaseSourcesRequest : IReturn<object>
        {
            public string Sources { get; set; }
        }

        public async Task<object> Post(TestNewReleaseSourcesRequest req)
        {
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new TestNewReleaseSourcesResponse
                    { Ok = false, Error = I18n.SDisplay("err.admin", ApplicationHost) };

            var specs = NewReleasesTool.ParseSources(req?.Sources);
            if (specs.Count == 0)
                return new TestNewReleaseSourcesResponse
                {
                    Ok = false,
                    Error = "Aucune URL — collez une ligne « URL » (ou « URL :: @showbizz » / « URL :: <regex> »)."
                };

            // Filet de temps : chaque ligne coûte au plus 8 s de scraping —
            // un test reste raisonnable même sur une liste volontairement
            // longue. Au-delà de 8 lignes on teste les 8 premières (note).
            string note = null;
            const int maxProbe = 8;
            if (specs.Count > maxProbe)
            {
                note = specs.Count + " lignes détectées, " + maxProbe + " testées max.";
                specs = specs.Take(maxProbe).ToList();
            }

            var items = new List<TestSourceResultItem>(specs.Count);
            int total = 0;
            foreach (var spec in specs)
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                {
                    var probe = await NewReleasesTool.ProbeAsync(spec, cts.Token)
                        .ConfigureAwait(false);
                    total += probe.Count;
                    items.Add(new TestSourceResultItem
                    {
                        Source = probe.Url,
                        Mode = probe.Mode,
                        Count = probe.Count,
                        Samples = probe.Samples,
                        Error = probe.Error,
                        Ms = probe.Ms
                    });
                }
            }

            return new TestNewReleaseSourcesResponse
            {
                Ok = true,
                SourcesChecked = items.Count,
                TotalItems = total,
                Results = items,
                Note = note
            };
        }

        // ------------------------------------------------------------------
        //  Prompts par défaut (bouton « Réinitialiser »)
        // ------------------------------------------------------------------

        /// <summary>
        /// Requête GET <c>/Plugins/LLMAI/DefaultPrompts</c>.
        /// <c>Lang</c> : clé optionnelle (« fr »/« en ») pour forcer la
        /// langue ; vide = résolution : <see cref="PluginConfiguration.ResponseLanguage"/>
        /// si renseignée, sinon langue d'affichage Emby
        /// (<see cref="I18n.ResolveDisplayLangKey"/>), sinon anglais. On
        /// n'utilise PAS <see cref="I18n.ResolveMetaLangKey"/> ici : sa
        /// cascade retombe sur le legacy TmdbLanguage (langue des MÉTADONNÉES),
        /// alors que ces prompts sont du contenu édité par l'admin dans SA
        /// langue d'interface.
        /// </summary>
        [Route("/Plugins/LLMAI/DefaultPrompts", "GET")]
        public class DefaultPromptsRequest : IReturn<object>
        {
            public string Lang { get; set; }
        }

        /// <summary>
        /// Réponse : la langue résolue (<c>Lang</c>) et les cinq prompts
        /// par défaut dans cette langue. La page remplit le textarea visé —
        /// rien n'est enregistré tant que l'admin n'a pas cliqué
        /// « Enregistrer ».
        /// </summary>
        public class DefaultPromptsResponse
        {
            public string Lang { get; set; }
            public string RagDirectives { get; set; }
            public string ScheduleTask { get; set; }
            public string ScheduleTaskMovies { get; set; }
            public string TonightPrompt { get; set; }
            public string AuditPrompt { get; set; }
            public string Error { get; set; }
        }

        public object Get(DefaultPromptsRequest req)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new DefaultPromptsResponse { Lang = I18n.En, Error = I18n.SDisplay("err.noconfig", ApplicationHost) };

            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new DefaultPromptsResponse { Lang = I18n.En, Error = I18n.SDisplay("err.admin", ApplicationHost) };

            // Langue : forçage explicite ?Lang=…, sinon ResponseLanguage
            // (langue de réponse choisie en config), sinon langue d'affichage
            // Emby — PAS la cascade métadonnées (TmdbLanguage legacy).
            string lang = I18n.ParseLangName(req?.Lang);
            if (string.IsNullOrEmpty(lang))
                lang = I18n.ParseLangName(cfg.ResponseLanguage);
            if (string.IsNullOrEmpty(lang))
                lang = I18n.ResolveDisplayLangKey(ApplicationHost);

            var d = DefaultPrompts.For(lang);
            return new DefaultPromptsResponse
            {
                Lang = lang,
                RagDirectives = d.RagDirectives,
                ScheduleTask = d.ScheduleTask,
                ScheduleTaskMovies = d.ScheduleTaskMovies,
                TonightPrompt = d.TonightPrompt,
                AuditPrompt = d.AuditPrompt
            };
        }

        // ------------------------------------------------------------------
        //  Fiche mémoire réflexive (Phase C) : consultation + édition admin
        // ------------------------------------------------------------------

        /// <summary>
        /// <c>GET /Plugins/LLMAI/MemoryCard</c> — la fiche mémoire réflexive
        /// courante (version, date, texte) pour la zone d'administration de
        /// la page de configuration. Réservé aux administrateurs.
        /// </summary>
        [Route("/Plugins/LLMAI/MemoryCard", "GET")]
        public class MemoryCardRequest : IReturn<object>
        {
        }

        /// <summary>Réponse : fiche courante (version 0 = pas encore rédigée).</summary>
        public class MemoryCardResponse
        {
            public int Version { get; set; }
            public string Updated { get; set; }
            public string Text { get; set; }
            public int HistoryCount { get; set; }
            public string Error { get; set; }
        }

        public object Get(MemoryCardRequest req)
        {
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new MemoryCardResponse { Error = I18n.SDisplay("err.admin", ApplicationHost) };

            var (current, history) = MemoryCard.Load();
            return new MemoryCardResponse
            {
                Version = current.Version,
                Updated = current.Updated == default
                    ? ""
                    : current.Updated.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                Text = current.Text ?? "",
                HistoryCount = history.Count
            };
        }

        /// <summary>
        /// <c>POST /Plugins/LLMAI/MemoryCard</c> — écriture de la fiche par
        /// l'administrateur (correctif manuel, garde-fou face au LLM). Le
        /// texte est plafonné comme une fiche LLM ; version et historique
        /// inchangés. Réservé aux administrateurs.
        /// </summary>
        [Route("/Plugins/LLMAI/MemoryCard", "POST")]
        public class MemoryCardPostRequest : IReturn<object>
        {
            public string Text { get; set; }
        }

        public object Post(MemoryCardPostRequest req)
        {
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new MemoryCardResponse { Error = I18n.SDisplay("err.admin", ApplicationHost) };

            var (current, history) = MemoryCard.Load();
            var updated = new MemoryCardData
            {
                Version = current.Version,
                Updated = current.Updated == default ? DateTimeOffset.Now : current.Updated,
                Text = req?.Text ?? ""
            };
            MemoryCard.Save(updated, history, Logger);
            Logger.Info("[LLM_AI] Fiche mémoire éditée manuellement par l'administrateur (v{0}).", updated.Version);
            return new MemoryCardResponse
            {
                Version = updated.Version,
                Text = updated.Text,
                HistoryCount = history.Count
            };
        }

        // ------------------------------------------------------------------
        //  Auth : résolution de l'administrateur appelant
        // ------------------------------------------------------------------

        /// <summary>
        /// Résout l'usager à partir du token d'authentification. Calqué sur
        /// <see cref="ChatApiService"/> : priorité au User du token, puis au
        /// UserId (Int64) du token. Retourne null si non authentifié.
        /// L'appelant vérifie ensuite <see cref="User.Policy"/>'s IsAdministrator.
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