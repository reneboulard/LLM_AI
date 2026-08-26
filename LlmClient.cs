using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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
        /// Appelle POST {url}/api/chat et renvoie le contenu de la réponse
        /// de l'assistant. Lève en cas d'erreur HTTP ou de parsing.
        /// </summary>
        public static async Task<string> ChatAsync(
            string url,
            string model,
            string systemPrompt,
            string userPrompt,
            IJsonSerializer json,
            ILogger logger,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL du LLM non configurée.", nameof(url));
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("Modèle non configuré.", nameof(model));

            var endpoint = url.TrimEnd('/') + "/api/chat";
            var body = BuildRequestBody(model, systemPrompt, userPrompt);

            logger?.Info("[LLM_AI] Appel Ollama : {0} (modèle={1})", endpoint, model);

            using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                req.Content = new StringContent(body, Encoding.UTF8);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var respText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!resp.IsSuccessStatusCode)
                        throw new HttpRequestException(
                            $"Ollama a répondu HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(respText, 500)}");

                    return ExtractContent(respText, json, logger);
                }
            }
        }

        // --- Construction de la requête (camelCase garanti) -----------------

        private static string BuildRequestBody(string model, string systemPrompt, string userPrompt)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"model\":\"").Append(JsonEscape(model)).Append("\",");
            sb.Append("\"messages\":[");
            var first = true;
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                sb.Append("{\"role\":\"system\",\"content\":\"").Append(JsonEscape(systemPrompt)).Append("\"}");
                first = false;
            }
            if (!first) sb.Append(',');
            sb.Append("{\"role\":\"user\",\"content\":\"").Append(JsonEscape(userPrompt ?? string.Empty)).Append("\"}");
            sb.Append("],");
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
            string error = null;

            try
            {
                var resp = (OllamaChatResponse)json.DeserializeFromString(respText, typeof(OllamaChatResponse));
                content = resp?.Message?.Content;
                error = resp?.Error;
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Désérialisation IJsonSerializer échouée, repli manuel : {0}", ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(error))
                throw new InvalidOperationException("Ollama a renvoyé une erreur : " + error);

            // Repli : extraction manuelle du champ "content" si la
            // désérialisation n'a rien donné (problème de casing).
            if (string.IsNullOrWhiteSpace(content))
                content = ExtractContentField(respText);

            return string.IsNullOrWhiteSpace(content)
                ? "(réponse vide du LLM)"
                : UnescapeJson(content);
        }

        // Recherche de la première occurrence de la clé "content" et extraction
        // de la valeur chaîne qui suit (tolérant aux espaces).
        private static string ExtractContentField(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int idx = 0;
            while ((idx = json.IndexOf("\"content\"", idx, StringComparison.Ordinal)) >= 0)
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

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }
}