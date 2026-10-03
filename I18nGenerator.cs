using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace LLM_AI
{
    /// <summary>
    /// Jalon de progression du moteur de génération de langues. Le moteur
    /// publie des événements STRUCTURÉS (pas de texte localisé) — l'appelant
    /// (endpoint, T1c) les traduit via <see cref="I18n.SDisplay"/> et les
    /// publie dans <see cref="I18nGenState"/>. Kinds : <c>collect</c> (base
    /// native + glossaire), <c>dose</c> (n/total — nom — clés),
    /// <c>repair</c> (clés), <c>write</c> (clés écrites).
    /// </summary>
    internal delegate void I18nMilestone(string kind, int doseIndex, int doseCount,
        string doseName, int keys);

    /// <summary>
    /// Moteur de génération de langues d'interface (v1.17.0, chantier
    /// « Atelier de langues », T1b) : produit la section d'une langue du
    /// fichier <c>LLM_AI_i18n.json</c> avec le LLM configuré du plugin.
    /// <para><b>Campagne par doses OBLIGATOIRE</b> (leçon du kit : la passe
    /// unique échoue — attention étirée sur le local, faux conforme cloud =
    /// copie EN verbatim que la Validation structurelle seule ne détecte pas).
    /// Le moteur porte la recette complète :</para>
    /// <list type="number">
    /// <item>Cibles selon mode (full = tout ; missing = manquantes + sautées ;
    /// skipped = sautées seulement), split en doses via <see cref="I18nDoses"/>.</item>
    /// <item>Chaque dose reçoit : directive (comptes DYNAMIQUES — jamais
    /// hardcodés) + <b>paires EN+FR</b> (le FR = rédaction auteur, la meilleure
    /// déclaration d'intention) + fiche de domaine + glossaire officiel Emby
    /// extrait des chaînes du hôte.</item>
    /// <item>Validation par dose : placeholders/balises (miroir chargeur) +
    /// <b>garde identique-EN</b> (une DOSE qui recopie l'anglais est re-tentée
    /// avec un avertissement dur ; une clé isolée identique-EN non triviale est
    /// ACCEPTÉE + flaggée au rapport — les marques/icônes égalent l'EN
    /// légitimement).</item>
    /// <item>Réparation ciblée ×1 des restes (les raisons de rejet voyagent
    /// avec la clé).</item>
    /// <item>Merge non destructif + écriture ATOMIQUE (tmp + move) + <c>.bak</c>
    /// du fichier précédent + <see cref="I18nOverlay.TryRefresh"/> (effectif au
    /// prochain accès, sans restart).</item>
    /// </list>
    /// <para><b>Invariants</b> : le moteur n'écrit QUE le fichier overlay ; il
    /// n'écrit que si ≥ 1 clé a réussi (jamais un écrasement à blanc) ; les
    /// autres langues du fichier sont conservées à l'identique ; fail-open en
    /// amont et en aval (le pire cas d'une valeur boiteuse = un libellé à
    /// corriger, l'UI reste entière).</para>
    /// </summary>
    /// <remarks>
    /// Découplé de la couche HTTP : le constructeur reçoit les backends déjà
    /// résolus (le gabarit <see cref="LlmRunner.ResolveBackends"/> reste dans
    /// l'appelant). L'appel LLM est une complétion simple à 2 messages
    /// (system = directive, user = dose) — zéro outillage d'agent : le
    /// protocole de sortie est un objet JSON unique (règle gemma4 : le schéma
    /// requis vit dans le prompt USER).
    /// </remarks>
    internal sealed class I18nGenerator
    {
        private readonly PluginConfiguration _cfg;
        private readonly IReadOnlyList<LlmBackend> _backends;
        private readonly string _ollamaKey, _geminiKey;
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;
        private readonly IServerApplicationHost _host;
        private readonly I18nMilestone _progress;
        private readonly CancellationToken _ct;

        private int _llmCalls;
        private readonly HashSet<string> _backendsUsed = new(StringComparer.Ordinal);

        internal I18nGenerator(PluginConfiguration cfg,
            IReadOnlyList<LlmBackend> backends, string ollamaCloudKey, string geminiKey,
            IJsonSerializer json, ILogger logger, IServerApplicationHost host,
            I18nMilestone progress, CancellationToken ct)
        {
            _cfg = cfg;
            _backends = backends ?? Array.Empty<LlmBackend>();
            _ollamaKey = ollamaCloudKey;
            _geminiKey = geminiKey;
            _json = json;
            _logger = logger;
            _host = host;
            _progress = progress;
            _ct = ct;
        }

        /// <summary>Exécute la génération. Ok=true → Report non null (aussi
        /// persisté via <see cref="I18nGenReportStore"/>). Ok=false → Error
        /// localisée (bucket interface).</summary>
        public async Task<(bool Ok, string Error, string Report)> RunAsync(string langParam, string mode)
        {
            string langKey = I18nOverlay.NormalizeLang(langParam ?? string.Empty, out string normNote);
            if (langKey == null)
                return Fail(FormatDisplay("i18n.gen.err.nolang", langParam ?? ""));
            if (normNote != null)
                _logger?.Info("[LLM_AI] I18n génération : langue « {0} » {1}.", langParam, normNote);

            // ---- 1) natives + paires FR ------------------------------------
            try { _progress?.Invoke("collect", 0, 0, null, 0); }
            catch { /* un jalon ne doit jamais tuer le run */ }

            var enWeb = I18nApiService.WebNativesLang("en", _logger);
            var enServer = I18n.EnServerDict;
            var enExt = I18n.EnExtDict;
            var frWeb = I18nApiService.WebNativesLang("fr", _logger);
            var frServer = I18n.ServerDictFor("fr");

            var nats = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["web"] = enWeb, ["server"] = enServer, ["ext"] = enExt
            };
            var frs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["web"] = frWeb, ["server"] = frServer, ["ext"] = null
            };

            // ---- 2) overlay usager + cibles selon mode ----------------------
            mode = mode == "full" || mode == "skipped" ? mode : "missing";
            var overlay = ReadOverlayValues();
            overlay.TryGetValue(langKey, out var cur);
            var userKeys = KeysPresent(cur);
            var skippedByFam = SimulateSkipped(cur, enWeb, enServer, enExt);

            var targets = TargetKeys(mode, enWeb, enServer, enExt, userKeys, skippedByFam);
            if (targets.Count == 0)
            {
                var noop = new StringBuilder();
                AppendLine(noop, FormatDisplay("i18n.gen.title", langKey, Display("i18n.gen.mode." + mode)));
                AppendLine(noop, "- " + Display("i18n.gen.nokeys"));
                return (true, null, noop.ToString());
            }
            var doses = I18nDoses.Split(targets);
            _logger?.Info("[LLM_AI] I18n génération : « {0} » mode {1} — {2} dose(s), {3} clé(s) visée(s) (cap {4}).",
                langKey, mode, doses.Count, targets.Count, I18nDoses.Cap);

            // ---- 3) glossaire officiel Emby ---------------------------------
            var seeds = SeedWords(enWeb, enServer, enExt);
            var glossary = I18nGlossary.Build(_host, langKey, seeds, _logger);
            if (glossary.Source == null)
                _logger?.Warn("[LLM_AI] I18n génération : glossaire vide — chaînes du hôte introuvables pour « {0} ».", langKey);
            else
                _logger?.Info("[LLM_AI] I18n génération : glossaire « {0} » — {1} terme(s) officiel(s).",
                    glossary.Source, glossary.Terms.Count);

            // ---- 4) directive (comptes dynamiques) --------------------------
            string system = I18nDirective.SystemPrompt(langKey, LangDisplayName(langKey),
                enWeb.Count, enServer.Count, enExt.Count,
                I18nGlossary.Markdown(glossary), glossary.Source);

            // ---- 5) boucle par dose -----------------------------------------
            var results = new Dictionary<(string Sec, string Key), string>();
            var suspects = new List<string>();
            var repairs = new List<(string Sec, string Key, string Reason)>();
            var deadDoses = new List<string>();
            string lastDoseError = null;

            void Collect((DoseOutcome vals, List<(string Sec, string Key, string Reason)> errs) r)
            {
                if (r.vals?.Accepted == null) return;
                foreach (var kv in r.vals.Accepted) results[(kv.Sec, kv.Key)] = kv.Value;
                foreach (var s in r.vals.Suspects) suspects.Add(s);
                foreach (var e in r.errs) repairs.Add((e.Sec, e.Key, e.Reason));
            }

            for (int i = 0; i < doses.Count; i++)
            {
                var dose = doses[i];
                try { _progress?.Invoke("dose", i + 1, doses.Count, dose.Name, dose.Size); }
                catch { }

                var first = await RunDoseAsync(dose, system, langKey, nats, frs, null)
                    .ConfigureAwait(false);
                _llmCalls += first.vals.Calls;
                Collect(first);
                if (first.vals.Accepted != null)
                {
                    if (first.errs.Count > 0) lastDoseError = "clés refusées à la validation";
                    continue;
                }

                // Échec complet : UNE re-tentative durcie (anti-copie-EN),
                // puis les clés tombent à la réparation ciblée.
                _logger?.Warn("[LLM_AI] I18n génération : dose {0} en échec ({1}) — re-tentative durcie.",
                    dose.Name, first.vals.LastError ?? "réponse inutilisable");
                lastDoseError ??= first.vals.LastError;
                var retry = await RunDoseAsync(dose, system, langKey, nats, frs, "hardened")
                    .ConfigureAwait(false);
                _llmCalls += retry.vals.Calls;
                Collect(retry);
                if (retry.vals.Accepted == null)
                {
                    deadDoses.Add(dose.Name);
                    lastDoseError = retry.vals.LastError ?? lastDoseError;
                    foreach (var e in dose.Entries)
                        repairs.Add((e.Sec, e.Key, "dose en échec"));
                }
            }

            // ---- 6) réparation ciblée ×1 -------------------------------------
            int repairAttempted = 0, repairCaught = 0;
            if (repairs.Count > 0)
            {
                try { _progress?.Invoke("repair", 0, 0, null, repairs.Count); }
                catch { }
                var repairTargets = repairs.Select(r => (r.Sec, r.Key)).Distinct().ToList();
                repairAttempted = repairTargets.Count;
                var repairReasons = repairs
                    .GroupBy(r => (r.Sec, r.Key))
                    .ToDictionary(g => g.Key, g => g.First().Reason);
                foreach (var dose in I18nDoses.Split(repairTargets))
                {
                    var (vals, _) = await RunDoseAsync(dose, system, langKey, nats, frs, "repair", repairReasons)
                        .ConfigureAwait(false);
                    _llmCalls += vals.Calls;
                    if (vals.Accepted == null) continue;
                    foreach (var kv in vals.Accepted)
                    {
                        results[(kv.Sec, kv.Key)] = kv.Value;
                        repairCaught++;
                        repairs.RemoveAll(r => r.Sec == kv.Sec && r.Key == kv.Key);
                    }
                    foreach (var s in vals.Suspects) suspects.Add(s);
                }
            }

            var stillMissing = targets.Where(t => !results.ContainsKey(t)).ToList();
            var suspectsUniq = suspects.Distinct().ToList();
            _logger?.Info("[LLM_AI] I18n génération : « {0} » — {1} clé(s) acceptée(s), {2} au repli natif, {3} appel(s) LLM{4}.",
                langKey, results.Count, stillMissing.Count, _llmCalls,
                deadDoses.Count > 0 ? ", doses mortes : " + string.Join(",", deadDoses) : "");

            if (results.Count == 0)
                return Fail(lastDoseError != null
                    ? Display("i18n.gen.err.novalue") + " (" + lastDoseError + ")"
                    : Display("i18n.gen.err.novalue"));

            // ---- 7) merge + écriture atomique (.bak) --------------------------
            try { _progress?.Invoke("write", 0, 0, null, results.Count); }
            catch { }
            string writtenPath;
            try { writtenPath = WriteOverlay(langKey, mode, cur, results); }
            catch (Exception ex)
            {
                _logger?.Error("[LLM_AI] I18n génération : écriture impossible : {0}", ex.Message);
                return Fail(FormatDisplay("i18n.gen.err.write", ex.Message));
            }
            SecurityMonitor.Record("I18N_GENERE",
                langKey + " " + mode + " : " + results.Count + " clés, " + _llmCalls
                + " appels LLM, " + stillMissing.Count + " au repli natif"
                + (suspectsUniq.Count > 0 ? ", " + suspectsUniq.Count + " suspecte(s) identiques-EN" : ""));

            // ---- 8) rapport ----------------------------------------------------
            var report = new StringBuilder();
            AppendLine(report, FormatDisplay("i18n.gen.title", langKey, Display("i18n.gen.mode." + mode)));
            AppendLine(report, "- " + FormatDisplay("i18n.gen.doses", doses.Count, targets.Count, I18nDoses.Cap));
            AppendLine(report, "- " + FormatDisplay("i18n.gen.calls", _llmCalls, string.Join(", ", _backendsUsed)));
            if (repairAttempted > 0)
                AppendLine(report, "- " + FormatDisplay("i18n.gen.repair", repairAttempted, repairCaught));
            var fin = FinalCounts(mode, cur, results, enWeb, enServer, enExt);
            AppendLine(report, "- " + FormatDisplay("i18n.gen.coverage",
                fin.Web, enWeb.Count, fin.Server, enServer.Count, fin.Ext, enExt.Count));
            if (stillMissing.Count > 0)
            {
                int disp = Math.Min(stillMissing.Count, 20);
                AppendLine(report, "- " + FormatDisplay("i18n.gen.skipped", stillMissing.Count,
                    string.Join(", ", stillMissing.Take(disp).Select(t => t.Item2))
                    + (disp < stillMissing.Count ? "…" : "")));
            }
            else
                AppendLine(report, "- " + Display("i18n.gen.skipped.none"));
            if (suspectsUniq.Count > 0)
            {
                int disp = Math.Min(suspectsUniq.Count, 20);
                AppendLine(report, "- " + FormatDisplay("i18n.gen.suspects", suspectsUniq.Count,
                    string.Join(", ", suspectsUniq.Take(disp)) + (disp < suspectsUniq.Count ? "…" : "")));
            }
            else
                AppendLine(report, "- " + Display("i18n.gen.suspects.none"));
            AppendLine(report, "- " + (glossary.Source == null
                ? FormatDisplay("i18n.gen.glossary.empty", langKey)
                : FormatDisplay("i18n.gen.glossary", glossary.Terms.Count, glossary.Source)));
            AppendLine(report, "- " + FormatDisplay("i18n.gen.written", writtenPath));
            if (deadDoses.Count > 0)
                AppendLine(report, "- " + FormatDisplay("i18n.gen.dosefail", deadDoses.Count, string.Join(", ", deadDoses)));

            var md = report.ToString();
            I18nGenReportStore.Save(new LastI18nGenReport
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                Lang = langKey,
                Mode = mode,
                Report = md
            }, _logger);
            return (true, null, md);
        }

        private static (bool, string, string) Fail(string error) => (false, error, null);

        // ------------------------------------------------------------------
        //  Cibles par mode
        // ------------------------------------------------------------------

        /// <summary>Sections non vides de la langue (copies défensives).</summary>
        private static Dictionary<string, Dictionary<string, string>> KeysPresent(
            Dictionary<string, Dictionary<string, string>> cur)
        {
            var res = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            if (cur == null) return res;
            foreach (var kv in cur)
                if (kv.Value.Count > 0)
                    res[kv.Key] = new Dictionary<string, string>(kv.Value, StringComparer.Ordinal);
            return res;
        }

        /// <summary>Cibles de campagne selon le mode : <b>full</b> = toutes les
        /// natives ; <b>missing</b> = natives absentes du fichier + sautées
        /// (présentes mais fautives) ; <b>skipped</b> = sautées seulement. Ordre
        /// web → server → ext (familles atomiques ensuite).</summary>
        private static List<(string Sec, string Key)> TargetKeys(string mode,
            IReadOnlyDictionary<string, string> enWeb,
            IReadOnlyDictionary<string, string> enServer,
            IReadOnlyDictionary<string, string> enExt,
            Dictionary<string, Dictionary<string, string>> userKeys,
            Dictionary<string, List<string>> skippedByFam)
        {
            var list = new List<(string, string)>();
            void FamAdd(string sec, IReadOnlyDictionary<string, string> nat)
            {
                if (nat == null || nat.Count == 0) return;
                userKeys.TryGetValue(sec, out var present);
                skippedByFam.TryGetValue(sec, out var skipped);
                foreach (var k in nat.Keys)
                {
                    if (mode == "full")
                        list.Add((sec, k));
                    else if (mode == "skipped")
                    {
                        if (skipped != null && skipped.Contains(k)) list.Add((sec, k));
                    }
                    else // missing
                    {
                        bool absent = present == null || !present.ContainsKey(k);
                        bool broken = skipped != null && skipped.Contains(k);
                        if (absent || broken) list.Add((sec, k));
                    }
                }
            }
            FamAdd("web", enWeb);
            FamAdd("server", enServer);
            FamAdd("ext", enExt);
            return list;
        }

        /// <summary>Simulation du chargeur sur les valeurs PRÉSENTES de la
        /// langue : mêmes règles (inconnu, {n}, balises — miroir
        /// <c>I18nOverlay</c>), web compris (le client n'a que la règle JS ; le
        /// moteur est plus strict, c'est voulu : une clé web cassée sera
        /// re-traduite). Les valeurs identiques-EN des fichiers manuels restent
        /// HORS de cette liste (garde du moteur = warn au rapport, pas sautée).</summary>
        private static Dictionary<string, List<string>> SimulateSkipped(
            Dictionary<string, Dictionary<string, string>> cur,
            IReadOnlyDictionary<string, string> enWeb,
            IReadOnlyDictionary<string, string> enServer,
            IReadOnlyDictionary<string, string> enExt)
        {
            var res = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (cur == null) return res;
            foreach (var fam in cur)
            {
                IReadOnlyDictionary<string, string> nat =
                    fam.Key == "web" ? enWeb :
                    fam.Key == "server" ? enServer :
                    fam.Key == "ext" ? enExt : null;
                if (nat == null) continue;
                foreach (var kv in fam.Value)
                {
                    I18nDoses.Verdict v = I18nDoses.Validate(
                        nat.TryGetValue(kv.Key, out var en) ? en : null, kv.Value);
                    if (v == I18nDoses.Verdict.Ok) continue;
                    if (!res.TryGetValue(fam.Key, out var l))
                        res[fam.Key] = l = new List<string>();
                    l.Add(kv.Key);
                }
            }
            return res;
        }

        // ------------------------------------------------------------------
        //  Exécution d'une dose
        // ------------------------------------------------------------------

        private sealed class AcceptedVal
        {
            internal string Sec, Key, Value;
        }

        private sealed class DoseOutcome
        {
            internal int Calls;
            /// <summary>null = dose en échec complet (rien d'exploitable).</summary>
            internal List<AcceptedVal> Accepted;
            /// <summary>Clés acceptées mais identiques-EN non triviales (⚠️).</summary>
            internal List<string> Suspects;
            internal string LastError;
        }

        /// <summary>Traduit une dose : prompt (fiche + paires FR + JSON), appel
        /// backend, parsage tolérant (béquilles JSON des petits modèles),
        /// validation par clé, garde anti-copie-EN. Accepted=null si la dose
        /// n'a rien produit d'exploitable (à l'appelant de retenter durci /
        /// passer les clés à la réparation).</summary>
        private async Task<(DoseOutcome vals, List<(string Sec, string Key, string Reason)> errs)> RunDoseAsync(
            I18nDoses.Dose dose, string system, string langKey,
            Dictionary<string, IReadOnlyDictionary<string, string>> nats,
            Dictionary<string, IReadOnlyDictionary<string, string>> frs,
            string variant, Dictionary<(string Sec, string Key), string> repairReasons = null)
        {
            var noErrs = new List<(string Sec, string Key, string Reason)>();
            var outcome = new DoseOutcome { Accepted = null, Suspects = new() };
            bool hardened = variant == "hardened";

            string user = I18nDirective.DoseUser(dose, langKey, nats, frs,
                hardened, repairReasons);
            if (_cfg?.DebugVerbose ?? false)
                _logger?.Info("[LLM_AI] I18n génération (verbose) — dose {0}\n=== SYSTEM ===\n{1}\n=== USER ===\n{2}",
                    dose.Name, system, user);
            string raw;
            try { raw = await ChatAsync(system, user).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                outcome.LastError = ex.Message;
                return (outcome, noErrs);
            }
            outcome.Calls = 1;

            var parsed = ParseDose(raw, langKey);
            if (parsed == null)
            {
                // Béquilles de LlmAgentService (petits modèles), puis dernier
                // recours sur la chaîne assainie des deux.
                var san = LlmAgentService.SanitizeJsonControlChars(raw);
                parsed = ParseDose(san, langKey)
                    ?? ParseDose(LlmAgentService.RepairUnescapedQuotes(san), langKey);
                if (parsed == null)
                {
                    _logger?.Warn("[LLM_AI] I18n génération : dose {0} — JSON non parsable, même après béquilles ({1} caractères).",
                        dose.Name, raw?.Length ?? 0);
                    outcome.LastError = "JSON non parsable";
                    return (outcome, noErrs);
                }
            }

            // Validation par clé (la native EN comme référence — règle kit).
            var refused = new List<(string Sec, string Key, string Reason)>();
            int identicalSuspect = 0;
            foreach (var e in dose.Entries)
            {
                if (!parsed.TryGetValue(e.Sec, out var famDict)
                    || !famDict.TryGetValue(e.Key, out var val))
                {
                    refused.Add((e.Sec, e.Key, "absente de la réponse"));
                    continue;
                }
                var enNative = nats[e.Sec].TryGetValue(e.Key, out var en) ? en : null;
                var verdict = I18nDoses.Validate(enNative, val);
                if (verdict != I18nDoses.Verdict.Ok)
                {
                    refused.Add((e.Sec, e.Key,
                        verdict == I18nDoses.Verdict.PlaceholderMismatch ? "placeholders {n} divergents"
                        : verdict == I18nDoses.Verdict.TagMismatch ? "balises HTML divergentes"
                        : verdict == I18nDoses.Verdict.Empty ? "valeur vide"
                        : "clé inconnue"));
                    continue;
                }
                if (string.Equals(val, enNative, StringComparison.Ordinal)
                    && !I18nDoses.TriviallyIdenticalEn(enNative, val))
                {
                    identicalSuspect++;
                    outcome.Suspects.Add(e.Key);
                }
                outcome.Accepted = outcome.Accepted ?? new List<AcceptedVal>();
                outcome.Accepted.Add(new AcceptedVal { Sec = e.Sec, Key = e.Key, Value = val });
            }

            if (outcome.Accepted == null || outcome.Accepted.Count == 0)
            {
                outcome.LastError = "aucune clé valide dans la réponse";
                return (outcome, noErrs);
            }

            // Garde anti-copie-EN : une DOSE (pas une clé) qui recopie
            // l'anglais = le faux conforme du kit (529 clés copiées, mesuré).
            // Majorité suspecte → dose refusée ENTIÈRE, re-tentative durcie par
            // l'appelant. Les suspects résiduels d'une dose acceptée sont
            // gardés (marques légitimes) + flag ⚠️ au rapport.
            if (!hardened && identicalSuspect * 2 > dose.Size)
            {
                outcome.Accepted = null;
                outcome.Suspects.Clear();
                outcome.LastError = "copie EN suspecte (" + identicalSuspect + "/" + dose.Size + " identiques)";
                _logger?.Warn("[LLM_AI] I18n génération : dose {0} — {1}/{2} valeurs identiques-EN (garde anti-copie).",
                    dose.Name, identicalSuspect, dose.Size);
                return (outcome, noErrs);
            }

            _logger?.Info("[LLM_AI] I18n génération : dose {0} — {1}/{2} clés validées ({3} refusée(s), {4} suspecte(s) identiques-EN).",
                dose.Name, outcome.Accepted.Count, dose.Size, refused.Count, outcome.Suspects.Count);
            return (outcome, refused);
        }

        // ------------------------------------------------------------------
        //  Parsage de la dose
        // ------------------------------------------------------------------

        /// <summary>Parsage tolérant de la réponse : premier objet équilibré
        /// extrait (fences ``` et prose ignorés), section de la langue cible
        /// retrouvée (clé exacte OU normalisable — « es-ES » accepté), familles
        /// connues → dict clés/valeurs string. Null si rien d'exploitable.</summary>
        internal static Dictionary<string, Dictionary<string, string>> ParseDose(string raw, string langKey)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            int open = raw.IndexOf('{');
            if (open < 0) return null;
            string json = ScanBalanced(raw, open) ?? raw.Substring(open);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { return null; }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                JsonElement langSec = default;
                bool found = false;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                    string norm = I18nOverlay.NormalizeLang(prop.Name, out _);
                    if (string.Equals(norm, langKey, StringComparison.Ordinal))
                    {
                        langSec = prop.Value.Clone();
                        found = true;
                        break;
                    }
                }
                if (!found) return null;
                var res = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                foreach (var fam in langSec.EnumerateObject())
                {
                    if (fam.Value.ValueKind != JsonValueKind.Object) continue;
                    if (fam.Name != "web" && fam.Name != "server" && fam.Name != "ext") continue;
                    var d = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var kv in fam.Value.EnumerateObject())
                        if (kv.Value.ValueKind == JsonValueKind.String)
                            d[kv.Name] = kv.Value.GetString();
                    res[fam.Name] = d;
                }
                return res.Count > 0 ? res : null;
            }
        }

        /// <summary>Premier objet {} équilibré à partir de <paramref name="start"/>
        /// (chaînes " ' ` avec \ échappées gérées) — tranche propre même quand
        /// le modèle a déposé de la prose autour.</summary>
        private static string ScanBalanced(string s, int start)
        {
            int depth = 0;
            char inStr = '\0';
            bool esc = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr != '\0')
                {
                    if (esc) { esc = false; continue; }
                    if (c == '\\') { esc = true; continue; }
                    if (c == inStr) inStr = '\0';
                    continue;
                }
                if (c == '"' || c == '\'' || c == '`') { inStr = c; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return s.Substring(start, i - start + 1);
                }
            }
            return null;
        }

        // ------------------------------------------------------------------
        //  Appel LLM (fallback multi-backend — version minimaliste de
        //  LlmAgentService.ChatAsync ; sans verrou de backend actif : un run
        //  éphémère n'a pas besoin de persistance de sélection)
        // ------------------------------------------------------------------

        private async Task<string> ChatAsync(string system, string user)
        {
            if (_backends.Count == 0)
                throw new InvalidOperationException(Display("i18n.gen.err.backend"));
            var messages = new List<LlmClient.ChatMessage>
            {
                new() { Role = "system", Content = system },
                new() { Role = "user", Content = user }
            };
            Exception last = null;
            foreach (var b in _backends)
            {
                _ct.ThrowIfCancellationRequested();
                try
                {
                    _llmCalls++;
                    string apiKey =
                        b.ProviderType == LlmProvider.OllamaCloud ? _ollamaKey :
                        b.ProviderType == LlmProvider.Gemini ? _geminiKey : null;
                    _backendsUsed.Add(b.Url + " / " + b.Model);
                    return await LlmClient.ChatAsync(b, apiKey, messages, _json, _logger, _ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    _logger?.Warn("[LLM_AI] I18n génération : backend {0} / {1} indisponible ({2}) — passe au suivant.",
                        b.Url, b.Model, ex.Message);
                    SecurityMonitor.Record("LLM_BACKEND_ECHEC",
                        b.Url + " / " + b.Model + " : " + ex.Message);
                }
            }
            throw new InvalidOperationException(Display("i18n.gen.err.backend"), last);
        }

        // ------------------------------------------------------------------
        //  Overlay : lecture brute + écriture merge
        // ------------------------------------------------------------------

        /// <summary>Lecture brute du fichier overlay (les VALEURS conservées —
        /// pas seulement les clés) : lang → fam → key → val. Tolérant : fichier
        /// absent/cassé → ce qui a été lu, sinon dicts vides. Miroir de
        /// <c>ReadUserOverlayKeys</c> (I18nApiService), avec les valeurs.</summary>
        internal static Dictionary<string, Dictionary<string, Dictionary<string, string>>> ReadOverlayValues()
        {
            var res = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);
            string path;
            try { path = I18nOverlay.OverlayPath; }
            catch { path = null; }
            string raw;
            try { raw = path == null || !File.Exists(path) ? null : File.ReadAllText(path); }
            catch { raw = null; }
            if (raw == null) return res;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return res;
                foreach (var langProp in doc.RootElement.EnumerateObject())
                {
                    if (langProp.Value.ValueKind != JsonValueKind.Object) continue;
                    string langKey = I18nOverlay.NormalizeLang(langProp.Name, out _)
                        ?? langProp.Name.Trim().ToLowerInvariant();
                    foreach (var fam in langProp.Value.EnumerateObject())
                    {
                        if (fam.Value.ValueKind != JsonValueKind.Object) continue;
                        if (fam.Name != "web" && fam.Name != "server" && fam.Name != "ext") continue;
                        if (!res.TryGetValue(langKey, out var fams))
                            res[langKey] = fams = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                        if (!fams.TryGetValue(fam.Name, out var dict))
                            fams[fam.Name] = dict = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var kv in fam.Value.EnumerateObject())
                            if (kv.Value.ValueKind == JsonValueKind.String)
                                dict[kv.Name] = kv.Value.GetString();
                    }
                }
            }
            catch { /* JSON cassé → ce qui a été lu (souvent tout) */ }
            return res;
        }

        /// <summary>Écrit le fichier overlay : les AUTRES langues conservées à
        /// l'identique, la section <paramref name="langKey"/> reconstruite selon
        /// le mode (full = générées seules ; sinon valeurs préservées + générées),
        /// ATOMIQUE (tmp + move) avec <c>.bak</c> du précédent, puis re-scan
        /// forcé (<see cref="I18nOverlay.TryRefresh"/>) — effectif au prochain
        /// accès, sans restart. Retourne le chemin écrit.</summary>
        private static string WriteOverlay(string langKey, string mode,
            Dictionary<string, Dictionary<string, string>> curSection,
            Dictionary<(string Sec, string Key), string> results)
        {
            string path;
            try { path = I18nOverlay.OverlayPath; }
            catch { path = null; }
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException("chemin overlay introuvable");

            var overlay = ReadOverlayValues();
            var root = new JsonObject();

            void AddLang(string lang)
            {
                bool isTarget = string.Equals(lang, langKey, StringComparison.Ordinal);
                var fams = new JsonObject();
                foreach (var sec in new[] { "web", "server", "ext" })
                {
                    Dictionary<string, string> dict = null;
                    if (isTarget)
                    {
                        dict = new Dictionary<string, string>(StringComparer.Ordinal);
                        if (mode != "full" && curSection != null
                            && curSection.TryGetValue(sec, out var prev))
                            foreach (var kv in prev) dict[kv.Key] = kv.Value;
                        foreach (var kv in results)
                            if (kv.Key.Sec == sec) dict[kv.Key.Key] = kv.Value;
                    }
                    else
                    {
                        overlay.TryGetValue(lang, out var of);
                        of?.TryGetValue(sec, out dict);
                    }
                    if (dict == null || dict.Count == 0) continue;
                    var jo = new JsonObject();
                    foreach (var kv in dict) jo[kv.Key] = kv.Value;
                    fams[sec] = jo;
                }
                if (fams.Count() > 0) root[lang] = fams;
            }

            AddLang(langKey);                       // la langue générée en tête
            foreach (var lang in overlay.Keys)
                if (!string.Equals(lang, langKey, StringComparison.Ordinal))
                    AddLang(lang);

            var opts = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            string json = root.ToJsonString(opts);

            string bak = path + ".bak";
            if (File.Exists(path)) File.Copy(path, bak, true);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, true);
            try { I18nOverlay.TryRefresh(); }
            catch { /* le re-scan throttlé finira par le prendre */ }
            return path;
        }

        // ------------------------------------------------------------------
        //  Couverture finale
        // ------------------------------------------------------------------

        /// <summary>Couverture APRÈS écriture : pour chaque famille, combien
        /// de natives ont une valeur servie (préservée ou générée — full ne
        /// compte que les générées).</summary>
        private static (int Web, int Server, int Ext) FinalCounts(string mode,
            Dictionary<string, Dictionary<string, string>> cur,
            Dictionary<(string Sec, string Key), string> results,
            IReadOnlyDictionary<string, string> enWeb,
            IReadOnlyDictionary<string, string> enServer,
            IReadOnlyDictionary<string, string> enExt)
        {
            int CountFam(string sec, IReadOnlyDictionary<string, string> nat)
            {
                if (nat == null) return 0;
                Dictionary<string, string> prev = null;
                if (cur != null) cur.TryGetValue(sec, out prev);
                int n = 0;
                foreach (var k in nat.Keys)
                {
                    bool hasResult = results.ContainsKey((sec, k));
                    bool hasPrev = prev != null && prev.ContainsKey(k);
                    if (mode == "full" ? hasResult : (hasPrev || hasResult)) n++;
                }
                return n;
            }
            return (CountFam("web", enWeb), CountFam("server", enServer), CountFam("ext", enExt));
        }

        // ------------------------------------------------------------------
        //  I18n helpers (rapport en langue d'AFFICHAGE, jamais la cible)
        // ------------------------------------------------------------------

        private static string DisplayLang(IServerApplicationHost host)
            => I18n.ResolveDisplayLangKey(host) ?? I18n.En;

        private string Display(string key) => I18n.S(key, DisplayLang(_host));

        private string FormatDisplay(string key, params object[] args)
        {
            string s = I18n.S(key, DisplayLang(_host));
            for (int i = 0; i < args.Length; i++)
                s = s.Replace("{" + i + "}",
                    Convert.ToString(args[i], CultureInfo.InvariantCulture) ?? "");
            return s;
        }

        /// <summary>Nom FR de la langue pour la directive (le prompt est
        /// rédigé en français). Inconnu → forme générique.</summary>
        private static string LangDisplayName(string langKey)
            => I18nDirective.LangName(langKey);

        private static void AppendLine(StringBuilder sb, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            sb.Append(line.TrimEnd('\r', '\n')).Append('\n');
        }

        /// <summary>Graine terminologique : les mots EN les plus fréquents des
        /// natives (≥ 4 lettres, hors stopwords et marques) — la demande au
        /// glossaire Emby (quels termes officiels chercher).</summary>
        internal static List<string> SeedWords(
            IReadOnlyDictionary<string, string> enWeb,
            IReadOnlyDictionary<string, string> enServer,
            IReadOnlyDictionary<string, string> enExt)
        {
            var freq = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var d in new[] { enWeb, enServer, enExt })
            {
                if (d == null) continue;
                foreach (var v in d.Values)
                {
                    string clean = Regex.Replace(v ?? "", @"\{[^}]*\}|<[^>]*>|&[a-z]+;", " ");
                    foreach (Match m in Regex.Matches(clean, "[A-Za-z]{4,}"))
                    {
                        var w = m.Value.ToLowerInvariant();
                        if (I18nGlossary.StopWords.Contains(w)
                            || I18nDosesTestableBrand(w)) continue;
                        freq[w] = (freq.TryGetValue(w, out var c) ? c : 0) + 1;
                    }
                }
            }
            return freq.Where(kv => kv.Value >= 2)
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key).Take(60).ToList();
        }

        private static bool I18nDosesTestableBrand(string w)
            // Mots-marques courants des natives (non traduits → pas de graine).
            => w == "emby" || w == "plugin" || w == "tonight" || w == "tmdb"
            || w == "tvdb" || w == "ollama" || w == "gemini" || w == "searxng"
            || w == "llm" || w == "strm" || w == "nfo" || w == "api";
    }

    // ---------------------------------------------------------------------
    //  I18nDirective : la directive système (comptes dynamiques) + le prompt
    //  dose (fiche de domaine, paires EN+FR, schéma strict dans le USER —
    //  règle gemma4 : le modèle suit le schéma du prompt user).
    // ---------------------------------------------------------------------
    internal static class I18nDirective
    {
        /// <summary>Noms FR des langues (directive rédigée en français) —
        /// couvre la table élargie ; inconnu → forme générique.</summary>
        private static readonly Dictionary<string, string> LangNames =
            new(StringComparer.Ordinal)
            {
                ["fr"] = "français", ["en"] = "anglais", ["es"] = "espagnol",
                ["de"] = "allemand", ["it"] = "italien", ["pt"] = "portugais",
                ["nl"] = "néerlandais", ["ca"] = "catalan", ["ar"] = "arabe",
                ["bg"] = "bulgare", ["cs"] = "tchèque", ["da"] = "danois",
                ["el"] = "grec", ["et"] = "estonien", ["fa"] = "persan",
                ["fi"] = "finnois", ["he"] = "hébreu", ["hi"] = "hindi",
                ["hr"] = "croate", ["hu"] = "hongrois", ["id"] = "indonésien",
                ["is"] = "islandais", ["ja"] = "japonais", ["kk"] = "kazakh",
                ["ko"] = "coréen", ["lt"] = "lituanien", ["lv"] = "letton",
                ["mk"] = "macédonien", ["ms"] = "malais", ["nb"] = "norvégien (bokmål)",
                ["pl"] = "polonais", ["ro"] = "roumain", ["ru"] = "russe",
                ["sk"] = "slovaque", ["sl"] = "slovène", ["sq"] = "albanais",
                ["sv"] = "suédois", ["th"] = "thaï", ["tr"] = "turc",
                ["uk"] = "ukrainien", ["vi"] = "vietnamien", ["zh"] = "chinois",
            };

        internal static string LangName(string langKey)
            => langKey != null && LangNames.TryGetValue(langKey, out var n)
                ? n : ("la langue « " + langKey + " »");

        /// <summary>Fiche de domaine par dose : ce QUE traduisent ces clés
        /// (écrans, registre, pièges). Les domaines hors table → ligne générique.
        /// Fusionné par dose (une dose fusionnée peut porter 2 domaines).</summary>
        private static readonly Dictionary<string, string> DomainNotes =
            new(StringComparer.Ordinal)
            {
                ["chat"] = "le chat admin du serveur : messages système, libellés de cartes d'action, en-têtes de conversation — registre direct et sobre",
                ["audit"] = "la fenêtre d'audit santé de la configuration : jalons de progression, boutons, descriptions descriptives",
                ["crosskind"] = "la file de régularisation cross-kind (enregistrements DVR mal typés) : libellés et statuts des cartes",
                ["i18n"] = "le panneau des langues d'interface (celui qui déclenche cette traduction) — parle du fichier et des doses",
                ["tonight"] = "AI Tonight : la playlist du soir générée par le plugin — « AI Tonight » reste non traduit",
                ["rec"] = "les recommandations IA : raisons de recommandation, en-têtes de sections, libellés — ton chaleureux mais factuel",
                ["memory"] = "la mémoire de conversation du chat (cartes de rappel, rétention) ",
                ["orphan"] = "l'identification des enregistrements orphelins (not-found / needs-review) : statuts et libellés",
                ["err"] = "messages d'erreur des endpoints HTTP : prose courte, factuelle, sans excuse ni formule",
                ["act"] = "les cartes d'action du chat (bouton d'application d'une suggestion) — 2 à 3 mots MAX par bouton",
                ["nfo"] = "fragments assemblés en UNE phrase du fichier .nfo (la chaîne nfo.airs.*) : chaque fragment est un morceau de la même phrase, placeholders identiques",
                ["task"] = "noms et descriptions des tâches planifiées (affichés dans Tâches planifiées du serveur Emby)",
                ["extchat"] = "la configuration du chat externe (panneau serveur)",
                ["ext"] = "l'app compagnon de chat externe (page Python autonome) : chrome de l'app + messages srv.* — la famille ext est TEXTE BRUT, jamais de HTML",
                ["chatext"] = "messages du chat externe côté serveur",
                ["disktag"] = "tags de disques d'enregistrements",
                ["activate"] = "l'endpoint d'activation/feedback (messages courts)",
                ["loginpopup"] = "la popup d'aide à la connexion",
                ["strmlib"] = "la bibliothèque de fichiers .strm",
                ["diskgate"] = "la gate disque des enregistrements (alertes d'espace)",
                ["autoprog"] = "le programmateur automatique d'AI Tonight",
                ["tmdb"] = "intégrations TMDB/TVDB (marques non traduites)",
            };

        /// <summary>Directive système (comptes DYNAMIQUES — jamais hardcodés,
        /// leçon du kit : les chiffres figés se percent). Sortie attendue par
        /// dose décrite aussi ici, mais le schéma complet vit dans le USER.</summary>
        internal static string SystemPrompt(string langKey, string langName,
            int webCount, int serverCount, int extCount,
            string glossaryMd, string glossarySource)
        {
            var sb = new StringBuilder();
            sb.Append("Tu es le traducteur d'interface du plugin Emby « LLM_AI ». Tu traduis l'interface du plugin en ");
            sb.Append(langName).Append(" (code « ").Append(langKey).Append(" »).");
            sb.Append("\n\nBase à traduire : ").Append(webCount)
              .Append(" clés web (pages de configuration et recommandations du plugin), ")
              .Append(serverCount).Append(" clés serveur (messages HTTP, jalons, tâches planifiées), ")
              .Append(extCount).Append(" clés « ext » (chrome de l'app compagnon de chat + ses messages srv.*).");
            sb.Append("\n\nLe travail arrive par DOSES. Chaque dose est un JSON { \"en\": { \"web\"|\"server\"|\"ext\": { clé: texte anglais } } } ; la sortie garde la MÊME structure et la tête « en » devient « ")
              .Append(langKey).Append(" » (les familles ne changent jamais de nom).");
            sb.Append("\n\nChaque dose porte aussi un CONTEXTE FR : la rédaction française de l'auteur du plugin — la meilleure déclaration d'intention. L'anglais est la source formelle ; le FR est la sémantique de référence. Là où les deux hésitent, cherche l'intention commune.");
            sb.Append(@"

RÈGLES (validation automatique par dose — une dose fautive est refusée et retentée) :
1. Placeholders {0} {1} {2}… : multiset IDENTIQUE à la native EN (ni omis, ni doublé). Un {0} en début de chaîne reste en début.
2. Balises HTML (web/server seulement) : multiset IDENTIQUE, attributs compris — recopie <b>…</b> tel quel autour du texte traduit.
3. Famille « ext » : TEXTE BRUT strict, AUCUNE balise HTML (rendu textContent).
4. Entités HTML (&lt;movie&gt;) : elles RESTENT des entités, ne les dés-échappe pas.
5. La chaîne nfo.airs.* s'assemble en UNE phrase du fichier .nfo : chaque fragment est un morceau de la même phrase.
6. NE TRADUIS PAS les marques : AI Tonight, AI Suggestions, LLM_AI, Emby, TMDB, TVDB, Ollama, Gemini, SearXNG, nfo, .strm, plugin, backend — recopie exacte.
7. Registre POLI et cohérent sur toute la langue (le FR source vouvoie — suis la même distance : usted en espagnol, etc.).
8. Longueur proche de la native (boutons = 2 à 3 mots) — l'UI Emby est dense.
9. Cohérence terminologique entre doses : même terme = même mot, sur toutes les doses.
10. Sortie = UN SEUL objet JSON valide, sans un mot de prose autour, sans fence markdown.");
            sb.Append("\n\nGLOSSAIRE Emby (terminologie officielle du serveur");
            if (string.IsNullOrEmpty(glossarySource))
                sb.Append(" — AUCUN fichier officiel trouvé pour cette langue : suit le sens, la terminologie ne peut pas être ancrée)");
            else
                sb.Append(", source : ").Append(glossarySource).Append(")");
            sb.Append(" :\n").Append(string.IsNullOrEmpty(glossaryMd)
                ? "(vide)"
                : glossaryMd);
            return sb.ToString();
        }

        /// <summary>Prompt utilisateur d'une dose : fiche de domaine, paires
        /// EN+FR (contexte auteur), variantes (hardened = avertissement anti-
        /// copie ; repair = raisons de rejet par clé), puis le JSON de la dose
        /// et le rappel de schéma STRICT (règle gemma4 : schéma dans le user).</summary>
        internal static string DoseUser(I18nDoses.Dose dose, string langKey,
            Dictionary<string, IReadOnlyDictionary<string, string>> nats,
            Dictionary<string, IReadOnlyDictionary<string, string>> frs,
            bool hardened, Dictionary<(string Sec, string Key), string> repairReasons)
        {
            var note = DomainNoteFor(dose.Name);
            var sb = new StringBuilder();
            sb.Append("Fiche de domaine — ").Append(note).Append("\n");
            if (hardened)
                sb.Append("\nAVERTISSEMENT : ta réponse précédente recopiait quasi intégralement l'anglais. Produis de VRAIES traductions — un seul objet JSON, sans texte autour.\n");
            if (repairReasons != null && repairReasons.Count > 0)
            {
                sb.Append("\nClés rejetées à la validation précédente (corrige la raison) :\n");
                foreach (var r in repairReasons)
                    sb.Append("- « ").Append(r.Key.Key).Append(" » : ").Append(r.Value).Append('\n');
            }

            // Paires EN+FR : intention mainteneur (contexte, pas cible formelle).
            var frLines = new List<string>();
            foreach (var e in dose.Entries)
            {
                IReadOnlyDictionary<string, string> frDict;
                if (!frs.TryGetValue(e.Sec, out frDict) || frDict == null) continue;
                if (!frDict.TryGetValue(e.Key, out var fr) || string.IsNullOrWhiteSpace(fr)) continue;
                frDict.TryGetValue(e.Key, out _);
                string frCut = fr.Length > 160 ? fr.Substring(0, 157) + "…" : fr;
                frLines.Add("· " + e.Key + " = " + frCut);
            }
            if (frLines.Count > 0)
            {
                sb.Append("\nCONTEXTE FR (rédaction auteur — sémantique de référence) :\n");
                foreach (var l in frLines) sb.Append(l).Append('\n');
            }

            if (repairReasons == null)
            {
                sb.Append("\nBase EN native de la dose « ").Append(dose.Name)
                  .Append(" » — ").Append(dose.Size)
                  .Append(" clés (sortie : même structure, tête « ").Append(langKey).Append(" ») :\n");
            }
            sb.Append(BuildDoseJson(dose, nats));
            sb.Append("\nRappel de sortie : UN SEUL objet JSON { \"").Append(langKey)
              .Append("\": { \"web\"|\"server\"|\"ext\": { clé: traduction } } } — mêmes clés que la dose, aucune de plus, aucun texte autour.\n");
            return sb.ToString();
        }

        /// <summary>Note de domaine (les doses « chat_p2 » résolvent leur base
        /// « chat » ; une dose fusionnée liste ses deux fiches).</summary>
        private static string DomainNoteFor(string doseName)
        {
            var parts = (doseName ?? "").Split('_');
            var notes = new List<string>();
            foreach (var p in parts)
            {
                if (p.Length == 0 || (p[0] == 'p' && p.Length <= 3 && int.TryParse(p.Substring(1), out _)))
                    continue;
                if (DomainNotes.TryGetValue(p, out var n)) notes.Add(n);
            }
            return notes.Count > 0
                ? string.Join(" ; ", notes)
                : "un panneau du plugin LLM_AI (libellés de l'interface)";
        }

        private static string BuildDoseJson(I18nDoses.Dose dose,
            Dictionary<string, IReadOnlyDictionary<string, string>> nats)
        {
            var fams = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var e in dose.Entries)
            {
                if (!fams.TryGetValue(e.Sec, out var d))
                    fams[e.Sec] = d = new(StringComparer.Ordinal);
                d[e.Key] = nats[e.Sec].TryGetValue(e.Key, out var en) ? en : "";
            }
            var root = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal)
            {
                ["en"] = fams
            };
            return Serialize(root);
        }

        private static string Serialize(object o)
        {
            return System.Text.Json.JsonSerializer.Serialize(o,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
        }
    }

    /// <summary>Résultat du glossaire : les termes officiels ancrés + la
    /// source (nom RELATIF du fichier — jamais de chemin hôte dans le rapport).</summary>
    internal sealed class I18nGlossaryResult
    {
        internal List<(string En, string Official)> Terms = new();
        internal string Source;
    }

    /// <summary>
    /// Glossaire officiel Emby : extrait des chaînes d'interface du SERVEUR
    /// (<c>dashboard-ui/strings/&lt;lang&gt;.json</c>, embarquées dans le
    /// dossier système) — le vocabulaire que les traducteurs Emby ont déjà
    /// fixé, l'ancre terminologique de chaque dose (leçon du kit : sans
    /// ancre, même le meilleur modèle invente « bibliothèque » en 3 façons).
    /// <para>Méthode : mots-graines fréquents des natives EN du plugin →
    /// recherche inverse dans les chaînes officielles (id dont la valeur EN
    /// contient le terme) → la valeur officielle de la langue cible. Pur
    /// best-effort fail-open : aucun fichier → glossaire vide (avertissement).</para>
    /// </summary>
    internal static class I18nGlossary
    {
        /// <summary>Cap de termes injectés (budget de prompt).</summary>
        private const int MaxTerms = 40;

        internal static readonly HashSet<string> StopWords =
            new(StringComparer.Ordinal)
            {
                // anglais générique (jamais des graines terminologiques)
                "this","that","with","from","your","will","when","what","have",
                "been","being","than","then","only","also","most","some","such",
                "very","much","more","less","other","which","where","after",
                "before","since","until","while","during","these","those","there",
                "their","would","could","should","must","might","enabled",
                "disabled","default","value","values","string","option","options",
                "about","each","into","they","them","page","list","label","none",
                "used","using","uses","true","false","cannot","does","every",
                "instead","whether","through","between","both","same","keep",
                "kept","like","need","needs","want","because","even","ever"
            };

        internal static I18nGlossaryResult Build(IServerApplicationHost host,
            string langKey, List<string> seeds, ILogger logger)
        {
            var res = new I18nGlossaryResult();
            try
            {
                var dir = ResolveStringsDir(host, logger);
                if (dir == null)
                {
                    logger?.Warn("[LLM_AI] I18n génération : dossier des chaînes Emby (dashboard-ui/strings) introuvable — glossaire vide.");
                    return res;
                }
                var enFile = LoadStrings(dir, new[] { "en.json", "en-US.json", "en-GB.json" }, logger);
                var langFile = LoadStrings(dir, new[] { langKey + ".json" }, logger);
                if (enFile == null || langFile == null || enFile.Count == 0 || langFile.Count == 0)
                {
                    logger?.Warn("[LLM_AI] I18n génération : chaînes {0}.json ou EN introuvables dans {1} — glossaire vide.",
                        langKey, "dashboard-ui/strings");
                    return res;
                }
                res.Source = "dashboard-ui/strings/" + langKey + ".json";

                var taken = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in enFile.Keys)
                {
                    if (taken.Count >= MaxTerms || seeds.Count == taken.Count) break;
                    string enVal = enFile[id];
                    if (string.IsNullOrWhiteSpace(enVal) || enVal.Length > 120) continue;
                    var words = new HashSet<string>(
                        Regex.Matches(enVal.ToLowerInvariant(), "[a-z]{3,}")
                             .Select(m => m.Value), StringComparer.Ordinal);
                    if (words.Count == 0) continue;
                    foreach (var seed in seeds)
                    {
                        if (taken.Contains(seed) || !words.Contains(seed)) continue;
                        if (!langFile.TryGetValue(id, out var official)
                            || string.IsNullOrWhiteSpace(official)
                            || string.Equals(official, enVal, StringComparison.Ordinal)
                            || official.Length > 60) continue;
                        res.Terms.Add((seed, official));
                        taken.Add(seed);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] I18n génération : extraction du glossaire échouée : {0}", ex.Message);
            }
            return res;
        }

        /// <summary>Rendu Markdown pour la directive (le seed est un mot NU :
        /// l'official l'ancre dans son contexte).</summary>
        internal static string Markdown(I18nGlossaryResult g)
        {
            if (g == null || g.Terms.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var (en, off) in g.Terms)
                sb.Append("- ").Append(en).Append(" → ").Append(off).Append('\n');
            return sb.ToString();
        }

        /// <summary>Résolution du dossier <c>dashboard-ui/strings</c> du hôte :
        /// le chemin système via les propriétés candidates (réflexion — même
        /// convention que RecordingDiskManager : pas de référence compilée à
        /// une propriété dont le nom a pu varier entre builds).</summary>
        private static string ResolveStringsDir(IServerApplicationHost host, ILogger logger)
        {
            var candidates = new List<object>();
            try { if (Plugin.Paths != null) candidates.Add(Plugin.Paths); }
            catch { }
            try
            {
                var mgr = host?.TryResolve<MediaBrowser.Controller.Configuration.IServerConfigurationManager>();
                if (mgr?.ApplicationPaths != null) candidates.Add(mgr.ApplicationPaths);
            }
            catch { }

            string[] propNames = { "ProgramSystemPath", "SystemPath", "ProgramPath" };
            foreach (var obj in candidates)
            {
                foreach (var name in propNames)
                {
                    string sysPath = GetStringProperty(obj, name);
                    if (string.IsNullOrWhiteSpace(sysPath)) continue;
                    var dir = Path.Combine(sysPath, "dashboard-ui", "strings");
                    if (Directory.Exists(dir)) return dir;
                }
            }
            return null;
        }

        private static string GetStringProperty(object obj, string name)
        {
            try
            {
                var p = obj?.GetType().GetProperty(name,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance);
                return p?.GetValue(obj) as string;
            }
            catch { return null; }
        }

        private static Dictionary<string, string> LoadStrings(string dir,
            string[] fileNames, ILogger logger)
        {
            foreach (var f in fileNames)
            {
                var path = Path.Combine(dir, f);
                if (!File.Exists(path)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                    var d = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var kv in doc.RootElement.EnumerateObject())
                        if (kv.Value.ValueKind == JsonValueKind.String)
                            d[kv.Name] = kv.Value.GetString();
                    return d;
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] I18n génération : lecture des chaînes {0} échouée : {1}", f, ex.Message);
                }
            }
            return null;
        }
    }
}