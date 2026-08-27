using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace LLM_AI
{
    /// <summary>
    /// Boucle agent autonome : le LLM accomplit une tâche en émettant des
    /// tableaux JSON d'appels d'outils, qu'on exécute et dont on réinjecte
    /// les résultats, jusqu'à une réponse finale (Markdown) ou la limite
    /// d'itérations. Protocole model-agnostic (gemma4, lfm2.5, …).
    /// </summary>
    public class LlmAgentService
    {
        private const int MaxIterations = 10;

        private readonly List<LlmBackend> _backends; // activés, triés par priorité (1 = premier)
        private readonly string _ollamaCloudKey;    // clé Bearer Ollama cloud (ollama.com)
        private readonly string _geminiKey;          // clé API Google Gemini
        private readonly string _ragDirectives;
        private readonly string _workflow;           // workflow d'exécution (séries/films) injecté dans le system prompt
        private readonly bool _verbose;              // loggue en intégral system/user prompt, itérations et résultats d'outils
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;

        // Index du backend actif dans _backends, résolu paresseusement au 1er
        // appel réussi. -1 = aucun verrouillé (première tentative). Si le backend
        // actif échoue en cours de boucle, on re-scanne toute la liste et on
        // verrouille le premier qui répond (changement de modèle possible).
        private int _activeIndex = -1;

        public LlmAgentService(IReadOnlyList<LlmBackend> backends, string ragDirectives,
                               string workflow,
                               string ollamaCloudKey, string geminiKey,
                               IJsonSerializer json, ILogger logger,
                               bool verbose = false)
        {
            _backends = backends != null
                ? new List<LlmBackend>(backends)
                : new List<LlmBackend>();
            _ragDirectives = ragDirectives ?? string.Empty;
            _workflow = workflow ?? string.Empty;
            _ollamaCloudKey = ollamaCloudKey ?? string.Empty;
            _geminiKey = geminiKey ?? string.Empty;
            _json = json;
            _logger = logger;
            _verbose = verbose;
        }

        /// <summary>
        /// Exécute la tâche <paramref name="userTask"/> en pilotant les outils.
        /// Retourne la réponse finale (Markdown) de l'agent ainsi que la liste
        /// des résultats d'outils exécutés pendant la boucle
        /// (<c>tool</c> = nom de l'outil, <c>result</c> = sortie JSON brute) —
        /// permet à l'appelant (tâche planifiée) d'enrichir les recommandations
        /// en matchant les titres aux items EPG qui portent l'Id/ChannelId.
        /// </summary>
        public async Task<(string reply, List<(string tool, string result)> toolResults)> RunAsync(
            string userTask, IReadOnlyList<ILlmTool> tools, CancellationToken ct)
        {
            var byName = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
            var system = BuildSystemPrompt(tools, _ragDirectives, _workflow);
            var toolResults = new List<(string tool, string result)>();

            if (_verbose)
            {
                _logger?.Info("[LLM_AI] === SYSTEM PROMPT ===\n{0}", system);
                _logger?.Info("[LLM_AI] === USER PROMPT ===\n{0}", userTask ?? string.Empty);
            }

            var messages = new List<LlmClient.ChatMessage>
            {
                new LlmClient.ChatMessage { Role = "system", Content = system },
                new LlmClient.ChatMessage { Role = "user",   Content = userTask ?? string.Empty }
            };

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                var reply = await ChatAsync(messages, ct).ConfigureAwait(false);
                _logger?.Info("[LLM_AI] Itération {0} — réponse: {1}", iter, _verbose ? reply : Truncate(reply, 300));

                var parsed = TryParseToolCalls(reply);
                if (parsed.Calls == null || parsed.Calls.Count == 0)
                {
                    if (parsed.MalformedToolAttempt)
                    {
                        // Le LLM a émis un tableau d'appels d'outils au JSON invalide :
                        // on ne le prend PAS pour la réponse finale (sinon on
                        // persisterait le texte d'appels comme « recommandations »).
                        // On lui demande de renvoyer un tableau bien formé et on
                        // continue la boucle. Borné par MaxIterations.
                        _logger?.Warn("[LLM_AI] Itération {0} — tableau d'appels d'outils malformé ({1}). Demande de renvoi.",
                            iter, parsed.Error);
                        messages.Add(new LlmClient.ChatMessage { Role = "assistant", Content = reply });
                        messages.Add(new LlmClient.ChatMessage { Role = "user", Content =
                            "Ton tableau d'appels d'outils est malformé (JSON invalide). " +
                            "Renvoie UNIQUEMENT un tableau JSON d'appels d'outils bien formé, " +
                            "ex. [{\"tool\":\"web_search\",\"arguments\":{\"query\":\"...\"}}], " +
                            "sans texte autour ni backticks. Si tu as terminé, réponds avec le " +
                            "tableau JSON des recommandations (champs title, kind, reason, priority, channel, start)." });
                        continue;
                    }
                    return (reply, toolResults); // réponse finale (Markdown ou tableau de recos)
                }

                var calls = parsed.Calls;
                // On exécute chaque outil et on agrège les résultats en un
                // tableau JSON réinjecté comme message user.
                var sb = new StringBuilder();
                sb.Append('[');
                bool first = true;
                foreach (var call in calls)
                {
                    if (!first) sb.Append(',');
                    first = false;

                    if (TryGetTool(byName, call.Tool, out var tool))
                    {
                        _logger?.Info("[LLM_AI] Appel outil {0} (args={1})",
                            call.Tool, _verbose ? call.Arguments.ToString() : Truncate(call.Arguments.ToString(), 120));
                        string res;
                        try
                        {
                            res = await tool.ExecuteAsync(call.Arguments, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger?.ErrorException("[LLM_AI] Outil {0} a levé : {1}", ex, call.Tool, ex.Message);
                            res = "{\"error\":\"" + JsonEscape(ex.Message) + "\"}";
                        }
                        // On mémorise le résultat pour l'enrichissement post-boucle.
                        toolResults.Add((call.Tool, res));
                        if (_verbose)
                            _logger?.Info("[LLM_AI] Résultat outil {0}: {1}", call.Tool, Truncate(res, 1500));
                        sb.Append("{\"tool\":\"").Append(JsonEscape(call.Tool)).Append("\",\"result\":")
                          .Append(res).Append('}');
                    }
                    else
                    {
                        sb.Append("{\"tool\":\"").Append(JsonEscape(call.Tool))
                          .Append("\",\"error\":\"outil inconnu\"}");
                    }
                }
                sb.Append(']');

                messages.Add(new LlmClient.ChatMessage { Role = "assistant", Content = reply });
                messages.Add(new LlmClient.ChatMessage { Role = "user",      Content = sb.ToString() });
            }

            _logger?.Warn("[LLM_AI] Limite de {0} itérations atteinte sans réponse finale.", MaxIterations);
            return ("Limite d'itérations atteinte sans réponse finale.", toolResults);
        }

        // ------------------------------------------------------------------
        //  Appel LLM avec repli prioritaire
        // ------------------------------------------------------------------

        /// <summary>
        /// Appelle le LLM avec repli : essaye le backend actif (s'il est déjà
        /// verrouillé), puis en cas d'échec re-scanne toute la liste par
        /// priorité jusqu'à trouver un backend qui répond. Le premier qui
        /// réussit devient le backend actif pour les appels suivants.
        /// Toute exception hors annulation est considérée comme
        /// « backend indisponible → suivant » (serveur down, HTTP 4xx/5xx,
        /// modèle introuvable, erreur Ollama…).
        /// <see cref="OperationCanceledException"/> (annulation de la tâche)
        /// est toujours propagée telle quelle.
        /// </summary>
        private async Task<string> ChatAsync(IReadOnlyList<LlmClient.ChatMessage> messages, CancellationToken ct)
        {
            if (_backends.Count == 0)
                throw new InvalidOperationException("Aucun backend LLM configuré/activé.");

            // 1) Backend actif déjà verrouillé : on le tente d'abord (chemin nominal).
            if (_activeIndex >= 0)
            {
                try
                {
                    return await CallBackendAsync(_backends[_activeIndex], messages, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    var b = _backends[_activeIndex];
                    _logger?.Warn("[LLM_AI] Backend actif {0}/{1} a échoué ({2}) — re-scanne les backends.",
                        b.Url, b.Model, ex.Message);
                    _activeIndex = -1; // on relève la sélection
                }
            }

            // 2) Re-scan complet par priorité (1 = le plus prioritaire d'abord).
            Exception last = null;
            for (int i = 0; i < _backends.Count; i++)
            {
                var b = _backends[i];
                try
                {
                    _logger?.Info("[LLM_AI] Tentative backend priorité {0} : {1} / {2}",
                        b.Priority, b.Url, b.Model);
                    var r = await CallBackendAsync(b, messages, ct).ConfigureAwait(false);
                    _activeIndex = i;
                    _logger?.Info("[LLM_AI] Backend retenu : {0} / {1} (priorité {2})",
                        b.Url, b.Model, b.Priority);
                    return r;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    _logger?.Warn("[LLM_AI] Backend {0} / {1} indisponible ({2}) — passe au suivant.",
                        b.Url, b.Model, ex.Message);
                }
            }

            throw new InvalidOperationException(
                "Aucun backend LLM disponible (tous les serveurs activés ont échoué).", last);
        }

        private Task<string> CallBackendAsync(LlmBackend b,
            IReadOnlyList<LlmClient.ChatMessage> messages, CancellationToken ct)
        {
            // Résout la clé selon le provider : ollama_local = pas de clé,
            // ollama_cloud = Bearer (clé ollama.com), gemini = clé API Google.
            string apiKey;
            switch (b.ProviderType)
            {
                case LlmProvider.OllamaCloud: apiKey = _ollamaCloudKey; break;
                case LlmProvider.Gemini:       apiKey = _geminiKey;      break;
                default:                       apiKey = null;           break;
            }
            return LlmClient.ChatAsync(b, apiKey, messages, _json, _logger, ct);
        }

        // ------------------------------------------------------------------
        //  System prompt (rôle + AVAILABLE TOOLS + directives RAG)
        // ------------------------------------------------------------------

        private static string BuildSystemPrompt(IReadOnlyList<ILlmTool> tools, string ragDirectives, string workflow)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Tu es un assistant Emby expert et autonome. Tu as accès à des outils ");
            sb.AppendLine("qui interrogent directement le serveur Emby (en lecture seule).");
            sb.AppendLine();
            sb.AppendLine("### AVAILABLE TOOLS");
            foreach (var t in tools)
            {
                sb.Append("- ").Append(t.Name).Append(" : ").AppendLine(t.Description);
                sb.AppendLine("  arguments (JSON) :");
                sb.Append("  ").AppendLine(t.ArgumentsSchema);
            }
            sb.AppendLine();
            sb.AppendLine("### PROTOCOLE DE TOOL-CALLING");
            sb.AppendLine("Pour demander de la donnée, réponds UNIQUEMENT par un tableau JSON d'appels d'outils, ");
            sb.AppendLine("sans texte autour. Exemple :");
            sb.AppendLine("[{\"tool\":\"get_emby_info\",\"arguments\":{\"action\":\"library\",\"type\":\"movie\",\"limit\":5}}]");
            sb.AppendLine("Tu peux mettre plusieurs appels dans le même tableau. Les résultats te seront ");
            sb.AppendLine("renvoyés sous la forme [{\"tool\":\"...\",\"result\":{...}}].");
            sb.AppendLine("Quand tu as toutes les informations nécessaires pour répondre à la demande, ");
            sb.AppendLine("réponds en Markdown (texte normal), SANS tableau JSON.");
            sb.AppendLine("Ne retourne que l'information demandée, rien de plus. Sois concis.");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(workflow))
            {
                sb.AppendLine("### WORKFLOW DE RECOMMANDATION");
                sb.AppendLine(workflow);
                sb.AppendLine();
            }
            sb.AppendLine("### FORMAT DES RECOMMANDATIONS");
            sb.AppendLine("Si la tâche demande des recommandations d'enregistrement, retourne un tableau JSON ");
            sb.AppendLine("d'objets avec, pour chaque item, les champs : ");
            sb.AppendLine("title, kind (series|movie), reason, priority (high|medium|low), ");
            sb.AppendLine("channel (nom de la chaîne), ");
            sb.AppendLine("start (date/heure de diffusion, format ISO 8601, ex. 2026-08-26T21:00:00), ");
            sb.AppendLine("et showbizz_match (booléen ou nom de l'émission Showbizz correspondante). ");
            sb.AppendLine("Les champs channel et start sont OBLIGATOIRES (servent à programmer l'enregistrement) : ");
            sb.AppendLine("reprends-les tels quels depuis les résultats de get_emby_info (epg_series/epg_movies). ");
            sb.AppendLine("priority reflète l'intérêt de la recommandation (high = à ne pas manquer, ");
            sb.AppendLine("medium = intéressant, low = bonus) — sert au badge couleur de la carte. ");
            sb.AppendLine("Reprends le title TEL QUEL depuis les résultats de get_emby_info (epg_series/epg_movies) ");
            sb.AppendLine("pour permettre le rattachement à l'Id programme (poster + programmation). ");
            sb.AppendLine("Même si le prompt utilisateur n'inclut pas channel/start/priority dans son schéma, AJOUTE-les toujours.");
            if (!string.IsNullOrWhiteSpace(ragDirectives))
            {
                sb.AppendLine();
                sb.AppendLine("### DIRECTIVES ADDITIONNELLES");
                sb.AppendLine(ragDirectives);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  Parsing du tableau JSON d'appels d'outils
        // ------------------------------------------------------------------

        /// <summary>
        /// Résultat du parsing d'une réponse potentiellement outil-appelante.
        /// <list type="bullet">
        /// <item><b>Calls</b> non vide : tableau d'appels d'outils valide à exécuter.</item>
        /// <item><b>MalformedToolAttempt</b> : la réponse ressemble à un tableau
        ///   d'appels (contient « tool ») mais le JSON est invalide. La boucle ne
        ///   doit PAS la prendre pour une réponse finale (sinon le texte
        ///   malformé serait persisté comme « recommandations ») : elle demande
        ///   au LLM de renvoyer un tableau bien formé.</item>
        /// <item>Sinon (Calls vide, pas de tentative outil) : réponse finale
        ///   (Markdown ou tableau de recommandations).</item>
        /// </list>
        /// </summary>
        private struct ToolCallParse
        {
            public List<ToolCall> Calls;
            public bool MalformedToolAttempt;
            public string Error;
        }

        /// <summary>
        /// Tente d'extraire un tableau JSON d'appels d'outils depuis la réponse.
        /// Distingue trois cas : appels valides, tentative malformée (à réparer),
        /// ou réponse finale (Markdown / tableau de recommandations).
        /// </summary>
        private static ToolCallParse TryParseToolCalls(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return new ToolCallParse();

            var arr = ExtractJsonArray(reply);
            if (string.IsNullOrEmpty(arr)) return new ToolCallParse();

            // Un tableau d'appels d'outils contient toujours un champ « tool ».
            // Un tableau de recommandations final (title, kind, reason…) n'en a
            // pas : c'est une réponse finale, pas une tentative outil.
            bool hasTool = arr.IndexOf("\"tool\"", StringComparison.Ordinal) >= 0;

            // Les modèles (gemma4…) émettent parfois des caractères de contrôle
            // non échappés (tabulation…) dans les strings JSON — par ex.
            // "tmdb\t_lookup". System.Text.Json est strict et rejette alors
            // TOUT le tableau, qu'on prendrait à tort pour une réponse finale.
            // On assainit en échappant ces contrôles avant désérialisation.
            arr = SanitizeJsonControlChars(arr);

            try
            {
                var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var calls = JsonSerializer.Deserialize<List<ToolCall>>(arr, opts);
                if (calls != null && calls.Count > 0 && !calls.Any(c => string.IsNullOrWhiteSpace(c.Tool)))
                    return new ToolCallParse { Calls = calls };
            }
            catch (Exception ex)
            {
                // JSON invalide alors que ça ressemble à un tableau d'appels
                // d'outils : on le signale pour réparation plutôt que de le
                // traiter comme une réponse finale (ce qui persisterait du
                // déchet à la place des recommandations).
                if (hasTool)
                    return new ToolCallParse { MalformedToolAttempt = true, Error = ex.Message };
                // Tableau final malformé (sans « tool ») : on ne répare pas,
                // on le laisse comme réponse finale (au pire dégradée).
                return new ToolCallParse();
            }
            // Tableau désérialisé mais sans champ « tool » non vide → réponse finale.
            return new ToolCallParse();
        }

        /// <summary>
        /// Échappe les caractères de contrôle (c &lt; 0x20) non échappés qui
        /// apparaissent à l'intérieur des chaînes JSON. System.Text.Json est
        /// strict et rejette les contrôles bruts (ex. tabulation 0x09) dans
        /// les strings ; on les remplace par leur forme échappée (\t, \n, \r,
        /// \uXXXX) pour tolérer les sorties malformées des LLM. Les contrôles
        /// hors-string (espaces/retours de formatage entre tokens) sont laissés
        /// intacts (le reader les ignore).
        /// </summary>
        private static string SanitizeJsonControlChars(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            var sb = new StringBuilder(json.Length);
            bool inStr = false, esc = false;
            foreach (var c in json)
            {
                if (inStr)
                {
                    if (esc) { sb.Append(c); esc = false; continue; }
                    if (c == '\\') { sb.Append(c); esc = true; continue; }
                    if (c == '"') { sb.Append(c); inStr = false; continue; }
                    if (c < 0x20)
                    {
                        switch (c)
                        {
                            case '\n': sb.Append("\\n"); break;
                            case '\r': sb.Append("\\r"); break;
                            case '\t': sb.Append("\\t"); break;
                            default: sb.Append("\\u").Append(((int)c).ToString("x4")); break;
                        }
                        continue;
                    }
                    sb.Append(c);
                    continue;
                }
                if (c == '"') { inStr = true; sb.Append(c); continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Extrait le premier tableau JSON « [ … ] » de la chaîne, en tolérant
        /// le texte/backticks autour. Répare aussi les tableaux tronqués
        /// (modèles « thinking » qui coupent l'émission avant la fermeture) en
        /// refermant les crochets/accolades encore ouverts.
        /// </summary>
        private static string ExtractJsonArray(string s)
        {
            int start = s.IndexOf('[');
            if (start < 0) return null;

            var stack = new Stack<char>();
            bool inStr = false;
            bool esc = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '[') stack.Push(']');
                else if (c == '{') stack.Push('}');
                else if (c == ']' || c == '}')
                {
                    if (stack.Count == 0) return null;          // fermeture orpheline → abandon
                    if (stack.Pop() != c) return null;          // inadéquation → abandon
                    if (stack.Count == 0)                       // tableau équilibré
                        return s.Substring(start, i - start + 1);
                }
            }

            // Tronqué : on referme ce qui reste ouvert (réparation tolérante).
            // Ne s'applique qu'aux tableaux évoquant un appel d'outil.
            if (stack.Count == 0) return null;
            var fragment = s.Substring(start);
            if (fragment.IndexOf("\"tool\"", StringComparison.Ordinal) < 0) return null;
            var sb2 = new StringBuilder(fragment);
            while (stack.Count > 0) sb2.Append(stack.Pop());
            return sb2.ToString();
        }

        // --- helpers JSON (échappement pour réinjection manuelle) ---------

        /// <summary>
        /// Recherche un outil par nom, avec tolérance : les LLM émettent parfois
        /// des caractères de contrôle/espaces dans le nom (ex. « tmdb\t_lookup »).
        /// On tente d'abord le nom exact, puis le nom nettoyé (sans espaces ni
        /// contrôles) — les noms d'outils ne contiennent que des underscores.
        /// </summary>
        private static bool TryGetTool(Dictionary<string, ILlmTool> byName, string name, out ILlmTool tool)
        {
            if (!string.IsNullOrEmpty(name) && byName.TryGetValue(name, out tool))
                return true;
            if (!string.IsNullOrEmpty(name))
            {
                var norm = new string(name.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
                if (norm.Length > 0 && byName.TryGetValue(norm, out tool))
                    return true;
            }
            tool = null;
            return false;
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }
}