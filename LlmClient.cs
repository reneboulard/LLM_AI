using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace LLM_AI
{
    /// <summary>
    /// Client léger pour l'API chat d'Ollama. Sans dépendance externe :
    /// la requête JSON est construite à la main (casing camelCase garanti),
    /// la réponse est désérialisée via le IJsonSerializer de l'hôte Emby
    /// (avec repli manuel si le mapping de propriétés échoue).
    /// </summary>
    public static class LlmClient
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };

        /// <summary>
        /// Longueur de contexte (num_ctx) : politique « instance chargée d'abord ».
        /// Avant chaque appel <i>local</i>, on interroge <c>GET /api/ps</c> (léger,
        /// aucune opération modèle) : si le modèle demandé est déjà chargé avec un
        /// context_length ≥ <see cref="MinNumCtx"/>, on réutilise ce ctx EXACTEMENT
        /// tel que chargé — Ollama conserve l'instance (aucun rechargement, mesuré
        /// 2026-09-30) ; sinon, l'appel part avec le seuil requis et devient
        /// l'instance chaude pour les appels suivants.
        ///
        /// Seuil fondé sur des mesures réelles (replay /api/chat, gemma4:latest 8B,
        /// 2026-09-30) : boucle Tonight système+EPG ≈ 3,2k tok, boucle agent lourde
        /// 7 msgs ≈ 13,9k tok, pic historique 18 messages ≈ 25k tok extrapolés,
        /// ratio ≈ 5–6 car./token. 32768 couvre le pire observé avec ≈ 30 % de
        /// marge. Pente KV mesurée : 9 216 o/token sur ce modèle (64k ≈ 0,6 Go).
        ///
        /// <see cref="_modelCtx"/> n'est plus qu'un repli : si <c>/api/ps</c> est
        /// indisponible (Ollama très ancien, hiccup réseau), on repart sur les
        /// valeurs par modèle historiques — elles matchent l'instance généralement
        /// chargée, évitant un rechargement accidentel par dégradation.
        /// </summary>
        private static readonly Dictionary<string, int> _modelCtx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "lfm2.5:latest", 65536 },
            { "gemma4:latest", 65536 },
            { "gemma4:26b",    65536 },
            { "qwen2.5:14b",   32768 },
        };

        private const int DefaultNumCtx = 65536;

        /// <summary>
        /// Seuil minimal de contexte requis pour les tâches du plugin (voir
        /// docstring de la classe). Demander moins tronquerait l'historique de
        /// boucle ; demander plus gaspille en permanence un KV jamais rempli.
        /// </summary>
        private const int MinNumCtx = 32768;

        private static int GetNumCtx(string model)
        {
            int ctx;
            return !string.IsNullOrEmpty(model) && _modelCtx.TryGetValue(model, out ctx) ? ctx : DefaultNumCtx;
        }

        /// <summary>
        /// Instance Ollama actuellement chargée pour <paramref name="model"/> :
        /// context_length, ou -1 si le modèle n'est pas chargé (les deux cas sont
        /// distincts !), ou null si <c>/api/ps</c> est indisponible (requête en
        /// erreur, très vieil Ollama). GET /api/ps : lecture d'état pur — pas de
        /// création ni d'éviction de runner côté serveur.
        /// </summary>
        private static async System.Threading.Tasks.Task<int?> GetLoadedCtxAsync(
            string url, string model, ILogger logger, CancellationToken ct)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url.TrimEnd('/') + "/api/ps"))
                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    // Modèle pas dans la liste = non chargé ; autre statut = état
                    // inconnu (null → repli par modèle).
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return -1;
                    if (!resp.IsSuccessStatusCode) return null;
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using (var doc = System.Text.Json.JsonDocument.Parse(text))
                    {
                        foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
                        {
                            // Tolérant aux variantes de nom : "gemma4", "gemma4:latest",
                            // ou le champ "model" (nom réel) plutôt que "name" (alias).
                            string name = m.TryGetProperty("model", out var mdl) ? mdl.GetString() : null;
                            bool match = string.Equals(name, model, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(name, model + ":latest", StringComparison.OrdinalIgnoreCase);
                            if (!match) continue;
                            if (!m.TryGetProperty("context_length", out var ctxEl)) return null;
                            // Garde ValueKind : TryGetInt64 lève sur null/chaîne
                            // (piège .NET documenté) ; taille inconnue → -1.
                            return ctxEl.ValueKind == JsonValueKind.Number && ctxEl.TryGetInt64(out var v)
                                ? (int)v : -1;
                        }
                        return -1;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // HttpClient.Timeout jette TaskCanceledException avec un ct NON
                // annulé (gotcha connu) : la vraie annulation se propage seule.
                throw;
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] GET /api/ps indisponible ({0}) : repli num_ctx par modèle", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Choix du num_ctx d'un appel Ollama local. Instance chargée adéquate ->
        /// ctx strictement identique (zéro reload, mesuré) ; chargée mais trop
        /// courte -> seuil requis (reload attendu, puis instance chaude) ; absente
        /// -> seuil requis (instance minimale fraîche, petit système friendly) ;
        /// /api/ps indisponible -> repli dict/défaut historique (ne pas risquer
        /// de dégrader une instance chaude de longueur inconnue). Cloud (clé
        /// présente) : le serveur impose le sien, comportement inchangé.
        /// </summary>
        private static async System.Threading.Tasks.Task<int> ResolveNumCtxAsync(
            string url, string model, string apiKey, ILogger logger, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(apiKey))
                return GetNumCtx(model);

            int? loaded = await GetLoadedCtxAsync(url, model, logger, ct).ConfigureAwait(false);
            if (loaded == null)
                return GetNumCtx(model);
            if (loaded.Value < 0)
            {
                logger?.Info("[LLM_AI] Ollama : {0} non chargé -> appel avec num_ctx requis {1} (instance minimale)", model, MinNumCtx);
                return MinNumCtx;
            }
            if (loaded.Value >= MinNumCtx)
            {
                logger?.Info("[LLM_AI] Ollama instance chargée : {0} ctx={1} ≥ seuil {2} -> num_ctx={1}, instance conservée (zéro reload)",
                    model, loaded.Value, MinNumCtx);
                return loaded.Value;
            }
            logger?.Info("[LLM_AI] Ollama instance chargée : {0} ctx={1} < seuil {2} -> appel avec num_ctx requis {2} (reload attendu)",
                model, loaded.Value, MinNumCtx);
            return MinNumCtx;
        }

        /// <summary>
        /// Message de conversation (role/content) pour la surcharge multi-message.
        /// </summary>
        public sealed class ChatMessage
        {
            public string Role { get; set; }
            public string Content { get; set; }
        }

        /// <summary>
        /// Appelle POST {url}/api/chat avec un prompt système + un prompt
        /// utilisateur et renvoie le contenu de la réponse de l'assistant.
        /// Délègue à la surcharge multi-message.
        /// </summary>
        public static Task<string> ChatAsync(
            string url,
            string model,
            string systemPrompt,
            string userPrompt,
            IJsonSerializer json,
            ILogger logger,
            CancellationToken ct)
        {
            var messages = new List<ChatMessage>();
            if (!string.IsNullOrEmpty(systemPrompt))
                messages.Add(new ChatMessage { Role = "system", Content = systemPrompt });
            messages.Add(new ChatMessage { Role = "user", Content = userPrompt ?? string.Empty });
            return ChatOllamaAsync(url, model, null, messages, json, logger, ct);
        }

        /// <summary>
        /// Dispatch multi-provider : route vers l'API Ollama (local ou cloud,
        /// protocole /api/chat) ou Gemini (generateContent) selon
        /// <see cref="LlmBackend.ProviderType"/>. <paramref name="apiKey"/>
        /// = clé Bearer pour ollama_cloud, clé API Gemini pour gemini,
        /// null pour ollama_local. L'URL par défaut est appliquée si
        /// <see cref="LlmBackend.Url"/> est vide. Porte la dynamique des 3
        /// providers de /var/www/llm_core.
        /// </summary>
        public static Task<string> ChatAsync(
            LlmBackend backend,
            string apiKey,
            IReadOnlyList<ChatMessage> messages,
            IJsonSerializer json,
            ILogger logger,
            CancellationToken ct)
        {
            if (backend == null)
                throw new ArgumentNullException(nameof(backend));

            var provider = backend.ProviderType;
            string url = string.IsNullOrWhiteSpace(backend.Url)
                ? LlmProviderHelper.DefaultUrl(provider)
                : backend.Url.TrimEnd('/');

            switch (provider)
            {
                case LlmProvider.OllamaLocal:
                case LlmProvider.OllamaCloud:
                    return ChatOllamaAsync(url, backend.Model, apiKey, messages, json, logger, ct);
                case LlmProvider.Gemini:
                    return ChatGeminiAsync(url, backend.Model, apiKey, messages, logger, ct);
                default:
                    return ChatOllamaAsync(url, backend.Model, apiKey, messages, json, logger, ct);
            }
        }

        /// <summary>
        /// Appelle POST {url}/api/chat (Ollama local ou cloud) avec une liste
        /// de messages (boucle agent) et renvoie le contenu de la réponse.
        /// <paramref name="apiKey"/> non null → en-tête Authorization: Bearer
        /// (ollama_cloud).
        /// </summary>
        private static async Task<string> ChatOllamaAsync(
            string url,
            string model,
            string apiKey,
            IReadOnlyList<ChatMessage> messages,
            IJsonSerializer json,
            ILogger logger,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL du LLM non configurée.", nameof(url));
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("Modèle non configuré.", nameof(model));

            var endpoint = url.TrimEnd('/') + "/api/chat";
            // Local : réutiliser l'instance chargée si elle couvre le seuil
            // (zéro reload), sinon demander le seuil requis ; cloud : historique.
            var numCtx = await ResolveNumCtxAsync(url, model, apiKey, logger, ct).ConfigureAwait(false);
            var body = BuildRequestBody(model, numCtx, messages);

            logger?.Info("[LLM_AI] Appel Ollama : {0} (modèle={1}, messages={2}, num_ctx={3}, auth={4})",
                endpoint, model, messages.Count, numCtx, apiKey != null ? "Bearer" : "non");

            using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                req.Content = new StringContent(body, Encoding.UTF8);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                if (!string.IsNullOrEmpty(apiKey))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var respText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!resp.IsSuccessStatusCode)
                        throw new HttpRequestException(
                            $"Ollama a répondu HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(respText, 500)}");

                    // Compteurs d'usage réels renvoyés par Ollama (tokens réellement
                    // évalués par l'appel) : mesure du contexte consommé par la boucle
                    // agent. Extraction manuelle (noms snake_case non mappés par le
                    // IJsonSerializer hôte). chars = car. du contenu envoyé — le ratio
                    // ch/tok observé sert au dimensionnement des défauts de num_ctx.
                    var promptTok = ExtractIntField(respText, "prompt_eval_count");
                    var genTok = ExtractIntField(respText, "eval_count");
                    if (promptTok > 0)
                    {
                        int chars = 0;
                        foreach (var m in messages) chars += m.Content?.Length ?? 0;
                        logger?.Info("[LLM_AI] Ollama usage : prompt={0} tok ({1} car., ≈{2} ch/tok), généré={3} tok (modèle={4}, num_ctx={5})",
                            promptTok, chars, Math.Round((double)chars / promptTok, 1),
                            genTok >= 0 ? genTok.ToString() : "?", model, numCtx);
                    }

                    return ExtractContent(respText, json, logger);
                }
            }
        }

        /// <summary>
        /// Appelle POST {baseUrl}/models/{model}:generateContent?key={apiKey}
        /// (Google Gemini) avec conversion du format de messages
        /// (system→systemInstruction, assistant→role "model"). Concatène les
        /// parts text de candidates[0].content.parts. Portage du
        /// GeminiProvider.php / dispatch ChatController de /var/www/llm_core.
        /// </summary>
        private static async Task<string> ChatGeminiAsync(
            string baseUrl,
            string model,
            string apiKey,
            IReadOnlyList<ChatMessage> messages,
            ILogger logger,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("Modèle Gemini non configuré.", nameof(model));
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Clé API Gemini manquante (GeminiApiKey / GEMINI_API_KEY).");

            var endpoint = baseUrl.TrimEnd('/') + "/models/" + Uri.EscapeDataString(model) + ":generateContent?key=" + Uri.EscapeDataString(apiKey);
            var body = BuildGeminiBody(messages);

            logger?.Info("[LLM_AI] Appel Gemini : {0} (modèle={1}, messages={2})",
                baseUrl, model, messages.Count);

            using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                req.Content = new StringContent(body, Encoding.UTF8);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var respText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!resp.IsSuccessStatusCode)
                        throw new HttpRequestException(
                            $"Gemini a répondu HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(respText, 500)}");

                    return ExtractGeminiContent(respText, logger);
                }
            }
        }

        // --- Construction de la requête (camelCase garanti) -----------------

        private static string BuildRequestBody(string model, int numCtx, IReadOnlyList<ChatMessage> messages)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"model\":\"").Append(JsonEscape(model)).Append("\",");
            sb.Append("\"messages\":[");
            for (int i = 0; i < messages.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"role\":\"").Append(JsonEscape(messages[i].Role)).Append("\",");
                sb.Append("\"content\":\"").Append(JsonEscape(messages[i].Content ?? string.Empty)).Append("\"}");
            }
            sb.Append("],");
            sb.Append("\"options\":{\"num_ctx\":").Append(numCtx).Append("},");
            sb.Append("\"stream\":false");
            sb.Append('}');
            return sb.ToString();
        }

        private static string JsonEscape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // --- Parsing de la réponse -----------------------------------------

        private sealed class OllamaMessage
        {
            public string Role { get; set; }
            public string Content { get; set; }
            // Modèles « thinking » (lfm2.5, gemma avec raisonnement) : le contenu
            // utile peut atterrir dans « thinking » avec un « content » vide.
            public string Thinking { get; set; }
        }

        private sealed class OllamaChatResponse
        {
            public OllamaMessage Message { get; set; }
            public string Error { get; set; }
            public bool Done { get; set; }
        }

        private static string ExtractContent(string respText, IJsonSerializer json, ILogger logger)
        {
            string content = null;
            string thinking = null;
            string error = null;

            try
            {
                var resp = (OllamaChatResponse)json.DeserializeFromString(respText, typeof(OllamaChatResponse));
                content = resp?.Message?.Content;
                thinking = resp?.Message?.Thinking;
                error = resp?.Error;
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Désérialisation IJsonSerializer échouée, repli manuel : {0}", ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(error))
                throw new InvalidOperationException("Ollama a renvoyé une erreur : " + error);

            // Repli manuel si la désérialisation n'a rien donné (problème de casing).
            if (string.IsNullOrWhiteSpace(content))
                content = ExtractStringField(respText, "content");

            // Modèles thinking : si content reste vide, on prend « thinking ».
            if (string.IsNullOrWhiteSpace(content))
            {
                if (string.IsNullOrWhiteSpace(thinking))
                    thinking = ExtractStringField(respText, "thinking");
                content = thinking;
            }

            return string.IsNullOrWhiteSpace(content)
                ? "(réponse vide du LLM)"
                : UnescapeJson(content);
        }

        // Recherche de la première occurrence de la clé <fieldName> et extraction
        // de la valeur chaîne qui suit (tolérant aux espaces). Sert pour "content"
        // et "thinking" selon le modèle.
        private static string ExtractStringField(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var key = "\"" + fieldName + "\"";
            int idx = 0;
            while ((idx = json.IndexOf(key, idx, StringComparison.Ordinal)) >= 0)
            {
                int colon = json.IndexOf(':', idx);
                if (colon < 0) break;
                int q = json.IndexOf('"', colon + 1);
                if (q < 0) break;
                // lire la chaîne échappée
                var sb = new StringBuilder();
                int i = q + 1;
                while (i < json.Length)
                {
                    char c = json[i];
                    if (c == '\\' && i + 1 < json.Length)
                    {
                        sb.Append(c).Append(json[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (c == '"') break;
                    sb.Append(c);
                    i++;
                }
                return sb.ToString();
            }
            return null;
        }

        private static string UnescapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t");
        }

        // Recherche de l'entier qui suit la clé <fieldName> (ex. prompt_eval_count).
        // Renvoie -1 si absente ou non numérique — les compteurs d'usage sont
        // purement indicatifs, jamais la base d'une décision du plugin.
        private static int ExtractIntField(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json)) return -1;
            var key = "\"" + fieldName + "\"";
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return -1;
            int colon = json.IndexOf(':', idx + key.Length);
            if (colon < 0) return -1;
            int i = colon + 1;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
            int start = i;
            while (i < json.Length && json[i] >= '0' && json[i] <= '9') i++;
            return i > start && int.TryParse(json.Substring(start, i - start), out var v) ? v : -1;
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");

        // --- Construction / parsing Gemini --------------------------------

        /// <summary>
        /// Construit le corps de requête Gemini : <c>contents</c> (rôles
        /// user/model) + <c>systemInstruction</c> (message system). Fusionne
        /// les tours consécutifs de même rôle (Gemini les rejette), comme le
        /// <c>GeminiProvider::formatPayload</c> / ChatController de
        /// /var/www/llm_core.
        /// </summary>
        private static string BuildGeminiBody(IReadOnlyList<ChatMessage> messages)
        {
            var turns = new List<KeyValuePair<string, string>>();
            string systemText = null;

            foreach (var m in messages)
            {
                if (string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                {
                    systemText = string.IsNullOrEmpty(systemText)
                        ? (m.Content ?? string.Empty)
                        : systemText + "\n\n" + (m.Content ?? string.Empty);
                    continue;
                }
                string role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? "model" : "user";
                if (turns.Count > 0 && turns[turns.Count - 1].Key == role)
                {
                    var last = turns[turns.Count - 1];
                    turns[turns.Count - 1] = new KeyValuePair<string, string>(
                        role, last.Value + "\n\n" + (m.Content ?? string.Empty));
                }
                else
                {
                    turns.Add(new KeyValuePair<string, string>(role, m.Content ?? string.Empty));
                }
            }

            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"contents\":[");
            for (int i = 0; i < turns.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"role\":\"").Append(JsonEscape(turns[i].Key)).Append("\",");
                sb.Append("\"parts\":[{\"text\":\"").Append(JsonEscape(turns[i].Value)).Append("\"}]}");
            }
            sb.Append(']');
            if (!string.IsNullOrEmpty(systemText))
            {
                sb.Append(",\"systemInstruction\":{\"parts\":[{\"text\":\"")
                  .Append(JsonEscape(systemText)).Append("\"}]}");
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Extrait le texte de la réponse Gemini : concatène les
        /// <c>text</c> de <c>candidates[*].content.parts</c>. Lève en cas
        /// d'erreur renvoyée par l'API.
        /// </summary>
        private static string ExtractGeminiContent(string respText, ILogger logger)
        {
            try
            {
                using (var doc = JsonDocument.Parse(respText))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var err))
                    {
                        string msg = err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                            ? m.GetString() : "Unknown Gemini error";
                        throw new InvalidOperationException("Gemini a renvoyé une erreur : " + msg);
                    }

                    var sb = new StringBuilder();
                    if (root.TryGetProperty("candidates", out var cands) &&
                        cands.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var cand in cands.EnumerateArray())
                        {
                            if (cand.TryGetProperty("content", out var candContent) &&
                                candContent.TryGetProperty("parts", out var parts) &&
                                parts.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var part in parts.EnumerateArray())
                                {
                                    if (part.TryGetProperty("text", out var t) &&
                                        t.ValueKind == JsonValueKind.String)
                                        sb.Append(t.GetString());
                                }
                            }
                        }
                    }
                    string text = sb.ToString();
                    return string.IsNullOrWhiteSpace(text)
                        ? "(réponse vide du LLM)" : text;
                }
            }
            catch (Exception ex) when (!(ex is InvalidOperationException))
            {
                logger?.Warn("[LLM_AI] Désérialisation Gemini échouée : {0}", ex.Message);
                return "(réponse Gemini illisible)";
            }
        }
    }
}