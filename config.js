// Page de configuration LLM_AI — module AMD chargé par le dashboard Emby via
// data-controller="__plugin/LLMAIConfigPageJS". Reçoit `view` (l'élément page)
// et branche load/save sur l'événement de cycle de vie `viewshow`.
// (Un <script> inline dans le HTML injecté par innerHTML n'est jamais exécuté
//  par le dashboard moderne — d'où ce module séparé. Cf. Emby.ComSkipper.)
define(["loading"], function (loading) {
    "use strict";

    var pluginId = "e7d3dee6-ef19-46a9-985f-06318b682e60";

    // Config chargée depuis le serveur au viewshow. Servira à carry-forward
    // les champs non édités par le formulaire (ex. StrmSecret, auto-généré
    // côté serveur et qu'il ne faut pas écraser lors d'un Enregistrer).
    var loadedCfg = null;

    // Module i18n (FR/EN). Chargé comme ressource plugin via require() sur
    // l'URL « web/ConfigurationPage?name=LLMAII18n » — C'EST LE MÊME mécanisme
    // que celui par lequel le viewmanager charge ce module config.js lui-même
    // (getConfigurationResourceUrl -> require([url])). On n'utilise PAS une
    // dépendance AMD « __plugin/LLMAII18n » : RequireJS ne mappe pas ce
    // préfixe comme id de module (il n'est géré que pour data-controller).
    // `i18n` est renseigné avant toute utilisation de t()/translateView().
    var i18n = null;
    var _i18nPromise = null;
    function i18nReady() {
        if (i18n) return Promise.resolve(i18n);
        if (_i18nPromise) return _i18nPromise;
        var url = ApiClient.getUrl("web/ConfigurationPage", { name: "LLMAII18n" });
        _i18nPromise = new Promise(function (resolve, reject) {
            require([url], function (mod) { i18n = mod; resolve(mod); }, reject);
        });
        return _i18nPromise;
    }

    // DroppedTitles est stocké côté C# comme un tableau JSON (string).
    // La page de config l'affiche/édite comme un textarea « un titre par ligne » :
    // on convertit array↔texte multiligne au chargement et à la sauvegarde.
    function droppedArrayToText(raw) {
        if (!raw) return "";
        try {
            var arr = JSON.parse(raw);
            if (Array.isArray(arr)) return arr.join("\n");
        } catch (e) { /* valeur corrompue : on affiche le brut */ }
        return raw;
    }

    function droppedTextToArray(text) {
        var lines = (text || "").split(/\r?\n/).map(function (l) { return l.trim(); })
            .filter(function (l) { return l.length > 0; });
        return JSON.stringify(lines);
    }

    // --- Filtres EPG (chaines / genres / flags opt-in) -----------------------

    function parseJsonArray(raw) {
        if (!raw) return [];
        try {
            var a = JSON.parse(raw);
            if (Array.isArray(a)) return a.filter(function (x) { return typeof x === "string"; });
        } catch (e) { /* corrompu */ }
        return [];
    }

    function arrayToJson(arr) { return JSON.stringify(arr || []); }

    // Rend une boîte de cases à cocher. items = [{value,label}].
    // Structure imposée par emby-checkbox : <label><input is="emby-checkbox">
    // <span class="checkboxLabel">…</span></label>. Le CSS d'emby-checkbox
    // masque l'<input> natif (z-index -1, transparent) et dessine la case
    // via .checkboxLabel::before/::after — qui n'existent QUE si le <span>
    // est le frère direct de l'input. Sans ce <span>, aucune case ne
    // s'affiche et l'input reste invisible (cf. emby-checkbox.js/css).
    function renderChecklist(host, items, selectedSet) {
        if (!host) return;
        if (!items || items.length === 0) {
            host.innerHTML = '<div class="wlEmpty">' + esc(i18n.t("cfg.wl.empty")) + '</div>';
            return;
        }
        var html = items.map(function (it) {
            var v = esc(it.value);
            var l = esc(it.label || it.value);
            var checked = selectedSet && selectedSet[it.value] ? "checked" : "";
            return '<label class="emby-checkbox-label wlItem">'
                + '<input type="checkbox" is="emby-checkbox" '
                + 'class="wlCheck" data-wl-value="' + v + '" ' + checked + ' />'
                + '<span class="checkboxLabel">' + l + '</span>'
                + '</label>';
        }).join("");
        host.innerHTML = html;
    }

    function collectChecked(host) {
        var checks = host.querySelectorAll(".wlCheck");
        var out = [];
        checks.forEach(function (c) {
            if (c.checked) {
                var v = c.getAttribute("data-wl-value") || "";
                if (v) out.push(v);
            }
        });
        return out;
    }

    function fetchJson(url) {
        return new Promise(function (resolve, reject) {
            ApiClient.ajax({ url: ApiClient.getUrl(url), type: "GET" })
                .then(function (data) { resolve(data); }, function (err) { reject(err); });
        });
    }

    // Flags orthogonaux opt-in (par catégorie séries/films) : on AJOUTE ces
    // types à la fiction. Les catégories series/films ne figurent pas ici —
    // elles sont garanties par l'appel outil (epg_series vs epg_movies).
    var FLAG_ITEMS = [
        { value: "kids",   label: "Kids" },
        { value: "news",    label: "News" },
        { value: "sports", label: "Sport" }
    ];

    function toSet(arr) {
        var s = {};
        (arr || []).forEach(function (v) { if (v) s[v] = true; });
        return s;
    }

    // Peuple les filtres : flags opt-in par catégorie (fixes) + chaines et genres (fetch API).
    function populateWhitelists(cfg, view) {
        var chSet = toSet(parseJsonArray(cfg.ChannelWhitelist));
        var geSet = toSet(parseJsonArray(cfg.GenreWhitelist));
        var seSet = toSet(parseJsonArray(cfg.SeriesFlags).map(function (v) { return (v || "").toLowerCase(); }));
        var moSet = toSet(parseJsonArray(cfg.MovieFlags).map(function (v) { return (v || "").toLowerCase(); }));

        renderChecklist(view.querySelector("#wlSeriesFlags"), FLAG_ITEMS, seSet);
        renderChecklist(view.querySelector("#wlMovieFlags"), FLAG_ITEMS, moSet);

        // Chaines vivantes (LiveTv/Channels).
        // dataType:"json" est OBLIGATOIRE : sans lui, ApiClient.fetch renvoie
        // l'objet Response brut (pas de .json()) et data.Items est undefined.
        ApiClient.ajax({ url: ApiClient.getUrl("LiveTv/Channels", { EnableImages: false }), type: "GET", dataType: "json" })
            .then(function (data) {
                var items = (data && data.Items ? data.Items : [])
                    .map(function (c) { return c.Name; })
                    .filter(function (n) { return !!n; })
                    .sort(function (a, b) { return a.localeCompare(b); })
                    .map(function (n) { return { value: n, label: n }; });
                renderChecklist(view.querySelector("#wlChannels"), items, chSet);
            }, function () {
                renderChecklist(view.querySelector("#wlChannels"), [], null);
            });

        // Genres EPG (depuis les programmes LiveTv). On PEUPLE DEPUIS L'EPG —
        // pas l'endpoint /Genres de la bibliothèque — car le filtre C#
        // (GetEmbyInfoTool) s'applique aux genres des programmes EPG (p.Genres).
        // On scanne les programmes et on collecte les genres distincts.
        // REQUÊTE calquée sur le script de référence /usr/local/bin/emby-absent-
        // series.sh (ligne 28) qui fonctionne : LiveTv/Programs?Fields=Genres.
        // NB: l'endpoint REST n'inclut Genres QUE si Fields=Genres est demandé
        // (l'outil C# in-process lit le DTO directement, sans cette contrainte).
        // On n'utilise PAS HasAired/fenêtre de temps : on veut juste le
        // vocabulaire des genres EPG (passé ou futur = même liste).
        ApiClient.ajax({ url: ApiClient.getUrl("LiveTv/Programs", {
            UserId: ApiClient.getCurrentUserId(),
            Fields: "Genres",
            EnableImages: false,
            ImageTypeLimit: 0,
            EnableUserData: false,
            EnableTotalRecordCount: false,
            SortBy: "StartDate",
            Limit: 1000
        }), type: "GET", dataType: "json" })
            .then(function (data) {
                var seen = {};
                var list = [];
                (data && data.Items ? data.Items : []).forEach(function (p) {
                    (p && p.Genres ? p.Genres : []).forEach(function (g) {
                        if (g && !seen[g]) { seen[g] = true; list.push(g); }
                    });
                });
                list.sort(function (a, b) { return a.localeCompare(b); });
                var items = list.map(function (n) { return { value: n, label: n }; });
                renderChecklist(view.querySelector("#wlGenres"), items, geSet);
            }, function () {
                renderChecklist(view.querySelector("#wlGenres"), [], null);
            });
    }

    // ----------------------------------------------------------------
    //  Backends LLM (repli par priorité)
    // ----------------------------------------------------------------

    function nextPriority(list) {
        // Prochaine priorité suggérée pour un nouveau backend = max+1, ou 1.
        var max = 0;
        (list || []).forEach(function (b) {
            var p = parseInt(b && b.Priority, 10);
            if (!isNaN(p) && p > max) max = p;
        });
        return max + 1;
    }

    function seedBackends(cfg) {
        // Depuis LlmBackends ; repli legacy si la liste est vide mais qu'un
        // LlmUrl est présent (migration d'une vieille config).
        if (cfg.LlmBackends && cfg.LlmBackends.length > 0) return cfg.LlmBackends;
        if (cfg.LlmUrl) {
            return [{ Provider: "ollama_local", Url: cfg.LlmUrl, Model: cfg.ModelName || "", Enabled: true, Priority: 1 }];
        }
        // Rien configuré : on pré-remplit un backend local pour guider.
        return [{ Provider: "ollama_local", Url: "", Model: "", Enabled: true, Priority: 1 }];
    }

    function esc(s) {
        return String(s == null ? "" : s)
            .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;");
    }

    // Mini-rendu Markdown -> HTML sûr pour le rapport d'audit. On échappe
    // d'abord tout le texte, puis on convertit les constructions supportées
    // (titres #/##/###, listes -/*, gras **…`, `code` inline, paragraphes,
    // lignes de code séparées). Pas de dépendance externe ; volontairement
    // minimal — le rapport est produit par notre propre agent.
    function renderMarkdown(md) {
        var lines = String(md == null ? "" : md).replace(/\r\n?/g, "\n").split("\n");
        var out = [];
        var inUl = false;
        var para = [];
        function flushPara() {
            if (para.length) {
                out.push("<p>" + para.join(" ") + "</p>");
                para = [];
            }
        }
        function closeUl() {
            if (inUl) { out.push("</ul>"); inUl = false; }
        }
        function inline(s) {
            // inline : **bold** et `code` (après escape global déjà fait sur
            // la ligne entière, on ne réintroduit aucun balisage utilisateur).
            var h = esc(s);
            h = h.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
            h = h.replace(/`([^`]+)`/g, "<code>$1</code>");
            return h;
        }
        for (var i = 0; i < lines.length; i++) {
            var raw = lines[i];
            var line = raw.replace(/^\s+|\s+$/g, "");
            if (!line) { flushPara(); closeUl(); continue; }
            var h = /^(#{1,3})\s+(.*)$/.exec(line);
            if (h) { flushPara(); closeUl(); var lvl = h[1].length; out.push("<h" + lvl + ">" + inline(h[2]) + "</h" + lvl + ">"); continue; }
            if (/^[-*]\s+/.test(line)) { flushPara(); if (!inUl) { out.push("<ul>"); inUl = true; } out.push("<li>" + inline(line.replace(/^[-*]\s+/, "")) + "</li>"); continue; }
            // Citation > ou ligne « tableau » (|…|) : rendue en monospace brut.
            if (/^>|^\|/.test(line)) { flushPara(); closeUl(); out.push("<p><code>" + esc(line) + "</code></p>"); continue; }
            para.push(inline(line));
        }
        flushPara();
        closeUl();
        return out.join("\n");
    }

    var PROVIDER_DEFAULTS = {
        "ollama_local": { url: "http://192.168.11.2:11434", model: "gemma4:26b" },
        "ollama_cloud": { url: "https://ollama.com",         model: "gemma4:31b" },
        "gemini":       { url: "",                           model: "gemini-2.5-flash" }
    };

    function providerOptions(selected) {
        var opts = [
            { v: "ollama_local", l: i18n.t("cfg.backend.provider.local") },
            { v: "ollama_cloud", l: i18n.t("cfg.backend.provider.cloud") },
            { v: "gemini",       l: i18n.t("cfg.backend.provider.gemini") }
        ];
        return opts.map(function (o) {
            var sel = o.v === selected ? "selected" : "";
            return '<option value="' + o.v + '" ' + sel + '>' + o.l + '</option>';
        }).join("");
    }

    function backendRowHtml(b, index) {
        var provider = (b && b.Provider) || "ollama_local";
        var url = esc(b && b.Url);
        var model = esc(b && b.Model);
        var prio = parseInt(b && b.Priority, 10);
        if (isNaN(prio)) prio = "";
        var enabled = b && b.Enabled !== false; // true par défaut
        var checked = enabled ? "checked" : "";
        return ''
            + '<div class="llmBackendRow" data-backend>'
            +   '<div class="backendHeader">'
            +     '<span>' + i18n.t("cfg.backend.num", (index + 1)) + '</span>'
            +     '<button is="emby-button" type="button" class="btnRemoveBackend">' + i18n.t("cfg.backend.remove") + '</button>'
            +   '</div>'
            +   '<div class="backendFields">'
            +     '<div class="inputContainer providerField">'
            +       '<select is="emby-select" class="beProvider" label="' + esc(i18n.t("cfg.backend.provider.label")) + '">'
            +         providerOptions(provider)
            +       '</select>'
            +     '</div>'
            +     '<div class="inputContainer">'
            +       '<input is="emby-input" type="text" class="beUrl" '
            +             'label="' + esc(i18n.t("cfg.backend.url.label")) + '" placeholder="http://192.168.11.2:11434" value="' + url + '" />'
            +     '</div>'
            +     '<div class="inputContainer">'
            +       '<input is="emby-input" type="text" class="beModel" '
            +             'label="' + esc(i18n.t("cfg.backend.model.label")) + '" placeholder="gemma4:26b" value="' + model + '" />'
            +     '</div>'
            +     '<div class="inputContainer priorityField">'
            +       '<input is="emby-input" type="number" class="bePriority" '
            +             'label="' + esc(i18n.t("cfg.backend.prio.label")) + '" min="1" value="' + prio + '" />'
            +     '</div>'
            +     '<label class="enabledField">'
            +       '<input type="checkbox" class="beEnabled" ' + checked + ' /> ' + esc(i18n.t("cfg.backend.enabled"))
            +     '</label>'
            +   '</div>'
            + '</div>';
    }

    function renderBackends(list, view) {
        var host = view.querySelector("#llmBackends");
        if (!host) return;
        var arr = list || [];
        var html = arr.map(function (b, i) { return backendRowHtml(b, i); }).join("");
        host.innerHTML = html;
    }

    function collectBackends(view) {
        var rows = view.querySelectorAll("#llmBackends .llmBackendRow");
        var out = [];
        rows.forEach(function (row) {
            var provider = (row.querySelector(".beProvider") || {}).value || "ollama_local";
            var url = (row.querySelector(".beUrl") || {}).value || "";
            var model = (row.querySelector(".beModel") || {}).value || "";
            var prioRaw = (row.querySelector(".bePriority") || {}).value;
            var prio = parseInt(prioRaw, 10);
            if (isNaN(prio) || prio < 1) prio = 1;
            var enabled = !!(row.querySelector(".beEnabled") || {}).checked;
            // ollama_local exige une URL ; cloud/gemini l'acceptent vide
            // (défaut appliqué côté serveur).
            if (provider === "ollama_local" && url.trim() === "") return;
            if (provider !== "ollama_local" && url.trim() === "" && model.trim() === "") return;
            out.push({
                Provider: provider,
                Url: url.trim(),
                Model: model.trim(),
                Enabled: enabled,
                Priority: prio
            });
        });
        return out;
    }

    // Pré-remplit l'URL et le modèle par défaut quand l'utilisateur change de
    // provider sur une ligne vide (pour guider la saisie des 3 choix).
    function wireProviderChange(host) {
        host.addEventListener("change", function (e) {
            var sel = e.target.closest ? e.target.closest(".beProvider") : null;
            if (!sel) return;
            var row = e.target.closest ? e.target.closest(".llmBackendRow") : null;
            if (!row) return;
            var d = PROVIDER_DEFAULTS[sel.value] || PROVIDER_DEFAULTS["ollama_local"];
            var urlInput = row.querySelector(".beUrl");
            var modelInput = row.querySelector(".beModel");
            if (urlInput && urlInput.value.trim() === "") urlInput.value = d.url;
            if (modelInput && modelInput.value.trim() === "") modelInput.value = d.model;
        });
    }

    function fill(cfg, view) {
        view.querySelector("#txtEmbyPublicUrl").value = cfg.EmbyPublicUrl || "";
        view.querySelector("#selResponseLanguage").value = cfg.ResponseLanguage || "";
        view.querySelector("#txtTmdbApiKey").value = cfg.TmdbApiKey || "";
        view.querySelector("#txtTmdbLanguage").value = cfg.TmdbLanguage || "";
        view.querySelector("#txtTvdbApiKey").value = cfg.TvdbApiKey || "";
        view.querySelector("#txtOllamaApiKey").value = cfg.OllamaApiKey || "";
        view.querySelector("#txtSearXngUrl").value = cfg.SearXngUrl || "";
        view.querySelector("#txtGeminiApiKey").value = cfg.GeminiApiKey || "";
        view.querySelector("#txtShowbizzUrl").value = cfg.ShowbizzUrl || "";
        view.querySelector("#txtShowbizzPattern").value = cfg.ShowbizzPattern || "";
        view.querySelector("#txtRagDirectives").value = cfg.RagDirectives || "";
        view.querySelector("#chkDebugVerbose").checked = !!cfg.DebugVerbose;
        view.querySelector("#chkWebFetchDirect").checked = cfg.WebFetchDirect !== false;
        view.querySelector("#txtScheduleTask").value = cfg.ScheduleTask || "";
        view.querySelector("#txtScheduleTaskMovies").value = cfg.ScheduleTaskMovies || "";
        var msb = parseInt(cfg.MaxSeriesBatch, 10);
        view.querySelector("#numMaxSeriesBatch").value = isNaN(msb) ? 40 : msb;
        var mmb = parseInt(cfg.MaxMovieBatch, 10);
        view.querySelector("#numMaxMovieBatch").value = isNaN(mmb) ? 30 : mmb;
        view.querySelector("#txtDroppedTitles").value = droppedArrayToText(cfg.DroppedTitles);
        view.querySelector("#chkTonightEnabled").checked = cfg.TonightEnabled !== false;
        view.querySelector("#chkTonightGenreTagEnabled").checked = !!cfg.TonightGenreTagEnabled;
        view.querySelector("#chkTonightCollectionEnabled").checked = !!cfg.TonightCollectionEnabled;
        view.querySelector("#txtTonightWindowStart").value = cfg.TonightWindowStart || "";
        view.querySelector("#txtTonightWindowEnd").value = cfg.TonightWindowEnd || "23:59";
        view.querySelector("#txtTonightPrompt").value = cfg.TonightPrompt || "";
        var tnb = parseInt(cfg.MaxTonightBatch, 10);
        view.querySelector("#numTonightBatch").value = isNaN(tnb) ? 10 : tnb;
        var tch = parseInt(cfg.TonightCacheHours, 10);
        view.querySelector("#numTonightCache").value = isNaN(tch) ? 4 : tch;
        var trd = parseInt(cfg.TonightRecordingsDays, 10);
        view.querySelector("#numTonightRecDays").value = isNaN(trd) ? 7 : trd;
        var tmr = parseInt(cfg.TonightMinRecommendations, 10);
        view.querySelector("#numTonightMinRec").value = isNaN(tmr) ? 3 : tmr;
        view.querySelector("#chkAutoProgram").checked = !!cfg.AutoProgram;
        // Badge « AI » (opt-out, non destructif — défaut coché).
        view.querySelector("#chkAiBadgeEnabled").checked = cfg.AiBadgeEnabled !== false;
        view.querySelector("#chkAiOwnedBadgeEnabled").checked = cfg.AiOwnedBadgeEnabled !== false;
        view.querySelector("#chkLoginPopup").checked = cfg.LoginPopup !== false;
        var lps = parseInt(cfg.LoginPopupSeconds, 10);
        view.querySelector("#numLoginPopupSeconds").value = isNaN(lps) ? 8 : lps;
        view.querySelector("#chkStrmLibraryEnabled").checked = !!cfg.StrmLibraryEnabled;
        view.querySelector("#txtStrmLibraryName").value = cfg.StrmLibraryName || "";
        // Identification des orphelins — opt-in (modifie des enregistrements), dry-run par défaut pour les premiers runs.
        view.querySelector("#chkOrphanIdentifyEnabled").checked = !!cfg.OrphanIdentifyEnabled;
        view.querySelector("#chkOrphanIdentifyDryRun").checked = !!cfg.OrphanIdentifyDryRun;
        view.querySelector("#chkOrphanSearXngEnabled").checked = cfg.OrphanSearXngEnabled !== false;
        view.querySelector("#chkOrphanRetryNeedsReview").checked = !!cfg.OrphanRetryNeedsReview;
        // Audit santé — Lecture seule par défaut, remédiation opt-in.
        view.querySelector("#chkAuditEnabled").checked = cfg.AuditEnabled !== false;
        view.querySelector("#chkAuditRemediationEnabled").checked = !!cfg.AuditRemediationEnabled;
        // Mode d'exécution de l'audit : single (boucle agent) | deterministic (rassemblement C# + synthèse).
        view.querySelector("#selAuditMode").value = cfg.AuditMode === "deterministic" ? "deterministic" : "single";
        view.querySelector("#txtAuditFocus").value = "";
        // Chat interactif — default true (opt-out), chargé à l'ouverture de la page.
        view.querySelector("#chkChatEnabled").checked = cfg.ChatEnabled !== false;
        renderBackends(seedBackends(cfg), view);
        populateWhitelists(cfg || {}, view);
    }

    function collect(view) {
        var backends = collectBackends(view);
        // Cohérence legacy : on renseigne LlmUrl/ModelName avec le backend
        // activé le plus prioritaire (priorité la plus basse), pour qu'un
        // lecteur legacy trouve encore quelque chose.
        var firstEnabled = backends.filter(function (b) { return b.Enabled; })
            .sort(function (a, b) { return a.Priority - b.Priority; })[0];
        return {
            LlmBackends: backends,
            LlmUrl: firstEnabled ? firstEnabled.Url : (backends[0] ? backends[0].Url : ""),
            ModelName: firstEnabled ? firstEnabled.Model : (backends[0] ? backends[0].Model : ""),
            EmbyPublicUrl: view.querySelector("#txtEmbyPublicUrl").value,
            ResponseLanguage: view.querySelector("#selResponseLanguage").value,
            TmdbApiKey: view.querySelector("#txtTmdbApiKey").value,
            TmdbLanguage: view.querySelector("#txtTmdbLanguage").value,
            TvdbApiKey: view.querySelector("#txtTvdbApiKey").value,
            OllamaApiKey: view.querySelector("#txtOllamaApiKey").value,
            SearXngUrl: view.querySelector("#txtSearXngUrl").value.trim(),
            GeminiApiKey: view.querySelector("#txtGeminiApiKey").value,
            ShowbizzUrl: view.querySelector("#txtShowbizzUrl").value,
            ShowbizzPattern: view.querySelector("#txtShowbizzPattern").value,
            RagDirectives: view.querySelector("#txtRagDirectives").value,
            DebugVerbose: view.querySelector("#chkDebugVerbose").checked,
            WebFetchDirect: view.querySelector("#chkWebFetchDirect").checked,
            ScheduleTask: view.querySelector("#txtScheduleTask").value,
            ScheduleTaskMovies: view.querySelector("#txtScheduleTaskMovies").value,
            MaxSeriesBatch: parseInt(view.querySelector("#numMaxSeriesBatch").value, 10) || 40,
            MaxMovieBatch: parseInt(view.querySelector("#numMaxMovieBatch").value, 10) || 30,
            DroppedTitles: droppedTextToArray(view.querySelector("#txtDroppedTitles").value),
            TonightEnabled: view.querySelector("#chkTonightEnabled").checked,
            TonightGenreTagEnabled: view.querySelector("#chkTonightGenreTagEnabled").checked,
            TonightCollectionEnabled: view.querySelector("#chkTonightCollectionEnabled").checked,
            TonightWindowStart: view.querySelector("#txtTonightWindowStart").value.trim(),
            TonightWindowEnd: view.querySelector("#txtTonightWindowEnd").value.trim(),
            TonightPrompt: view.querySelector("#txtTonightPrompt").value,
            MaxTonightBatch: parseInt(view.querySelector("#numTonightBatch").value, 10) || 10,
            TonightCacheHours: parseInt(view.querySelector("#numTonightCache").value, 10) || 4,
            TonightRecordingsDays: parseInt(view.querySelector("#numTonightRecDays").value, 10) || 7,
            TonightMinRecommendations: parseInt(view.querySelector("#numTonightMinRec").value, 10) || 3,
            AutoProgram: view.querySelector("#chkAutoProgram").checked,
            AiBadgeEnabled: view.querySelector("#chkAiBadgeEnabled").checked,
            AiOwnedBadgeEnabled: view.querySelector("#chkAiOwnedBadgeEnabled").checked,
            // Carry-forward : AiBadgeProgramIds est réécrit côté serveur par la
            // tâche planifiée (registre du badge) — on le renvoie tel quel pour
            // ne pas l'écraser (même contrainte que StrmSecret).
            AiBadgeProgramIds: (loadedCfg && loadedCfg.AiBadgeProgramIds) || [],
            LoginPopup: view.querySelector("#chkLoginPopup").checked,
            LoginPopupSeconds: parseInt(view.querySelector("#numLoginPopupSeconds").value, 10) || 8,
            StrmLibraryEnabled: view.querySelector("#chkStrmLibraryEnabled").checked,
            StrmLibraryName: (view.querySelector("#txtStrmLibraryName").value || "").trim(),
            // Carry-forward : StrmSecret est auto-généré côté serveur et n'est
            // pas édité ici — on le renvoie tel quel pour éviter de l'écraser.
            StrmSecret: (loadedCfg && loadedCfg.StrmSecret) || "",
            // Identification des enregistrements orphelins (opt-in + dry-run).
            OrphanIdentifyEnabled: view.querySelector("#chkOrphanIdentifyEnabled").checked,
            OrphanIdentifyDryRun: view.querySelector("#chkOrphanIdentifyDryRun").checked,
            OrphanSearXngEnabled: view.querySelector("#chkOrphanSearXngEnabled").checked,
            OrphanRetryNeedsReview: view.querySelector("#chkOrphanRetryNeedsReview").checked,
            ChannelWhitelist: arrayToJson(collectChecked(view.querySelector("#wlChannels"))),
            GenreWhitelist: arrayToJson(collectChecked(view.querySelector("#wlGenres"))),
            SeriesFlags: arrayToJson(collectChecked(view.querySelector("#wlSeriesFlags"))),
            MovieFlags: arrayToJson(collectChecked(view.querySelector("#wlMovieFlags"))),
            // Audit santé — pas d'éditeur de prompt en v1 : on porte le
            // template serveur (AuditPrompt) en carry-forward pour ne pas
            // l'écraser par défaut. Seuls les deux commutateurs sont édités.
            AuditEnabled: view.querySelector("#chkAuditEnabled").checked,
            AuditRemediationEnabled: view.querySelector("#chkAuditRemediationEnabled").checked,
            AuditMode: (view.querySelector("#selAuditMode").value === "deterministic") ? "deterministic" : "single",
            AuditPrompt: (loadedCfg && loadedCfg.AuditPrompt) || "",
            // Chat interactif — simple booléen, pas de carry-forward spécial.
            ChatEnabled: view.querySelector("#chkChatEnabled").checked
        };
    }

    return function (view) {
        view.addEventListener("viewshow", function () {
            // i18n : charge le module, résout la langue (globalize) puis traduit
            // le DOM statique avant de remplir / brancher. Les chaînes dynamiques
            // (lignes de backend, options, alerts) passent par i18n.t() au moment
            // de leur construction — toujours après init() (donc langue connue).
            i18nReady().then(function () {
                return i18n.init();
            }).then(function () {
                i18n.translateView(view);

                // Charge la config existante et remplit les champs.
                ApiClient.getPluginConfiguration(pluginId).then(function (cfg) {
                    loadedCfg = cfg || {};
                    fill(loadedCfg, view);
                });

            // Ajouter un backend.
            var addBtn = view.querySelector("#btnAddBackend");
            if (addBtn) {
                addBtn.addEventListener("click", function () {
                    var current = collectBackends(view);
                    var host = view.querySelector("#llmBackends");
                    var idx = host ? host.querySelectorAll(".llmBackendRow").length : 0;
                    var blank = { Url: "", Model: "", Enabled: true, Priority: nextPriority(current) };
                    var div = document.createElement("div");
                    div.innerHTML = backendRowHtml(blank, idx);
                    if (host) host.appendChild(div.firstChild);
                });
            }

            // Supprimer un backend (délégation sur le conteneur).
            var host = view.querySelector("#llmBackends");
            if (host) {
                host.addEventListener("click", function (e) {
                    var btn = e.target.closest ? e.target.closest(".btnRemoveBackend") : null;
                    if (!btn) return;
                    var row = e.target.closest ? e.target.closest(".llmBackendRow") : null;
                    if (row && row.parentNode) row.parentNode.removeChild(row);
                });
                // Pré-remplit URL/modèle par défaut quand on change de provider.
                wireProviderChange(host);
            }

            // Filtre de recherche de la liste des chaines.
            var chFilter = view.querySelector("#wlChannelsFilter");
            if (chFilter) {
                chFilter.addEventListener("input", function () {
                    var q = (chFilter.value || "").toLowerCase();
                    var box = view.querySelector("#wlChannels");
                    if (!box) return;
                    box.querySelectorAll(".wlItem").forEach(function (item) {
                        var label = item.textContent || "";
                        item.style.display = (!q || label.toLowerCase().indexOf(q) >= 0) ? "" : "none";
                    });
                });
            }

            // Audit santé : déclenche l'endpoint /Plugins/LLMAI/Audit et rend
            // le rapport Markdown retourné dans #auditReport. La page de config
            // étant déjà en contexte admin, la porte d'auth côté serveur passe.
            var runAuditBtn = view.querySelector("#btnRunAudit");
            if (runAuditBtn) {
                runAuditBtn.addEventListener("click", function () {
                    var reportEl = view.querySelector("#auditReport");
                    var focus = (view.querySelector("#txtAuditFocus").value || "").trim();
                    var url = ApiClient.getUrl("Plugins/LLMAI/Audit", focus ? { Focus: focus } : {});

                    // États UI : bouton désactivé + libellé « en cours ».
                    runAuditBtn.disabled = true;
                    var prevLabel = runAuditBtn.textContent;
                    runAuditBtn.textContent = i18n.t("cfg.audit.running");
                    if (reportEl) {
                        reportEl.style.display = "block";
                        reportEl.innerHTML =
                            '<div class="auditMeta">' + esc(i18n.t("cfg.audit.running")) + '</div>';
                    }

                    ApiClient.ajax({ url: url, type: "GET" }).then(function (resp) {
                        return resp.json();
                    }).then(function (data) {
                        runAuditBtn.disabled = false;
                        runAuditBtn.textContent = prevLabel;
                        if (!reportEl) return;
                        if (!data || data.Enabled === false) {
                            reportEl.innerHTML = '<div class="auditMeta">' +
                                esc(i18n.t("cfg.audit.disabled")) + '</div>';
                            return;
                        }
                        if (data.Error) {
                            reportEl.innerHTML = '<div class="auditMeta">' +
                                esc(data.Error) + '</div>';
                            return;
                        }
                        var meta = '<div class="auditMeta">' +
                            esc(i18n.t("cfg.audit.done")) +
                            (data.Date ? ' — ' + esc(data.Date) : '') +
                            '</div>';
                        reportEl.innerHTML = meta + renderMarkdown(data.Report || "");
                    }, function (err) {
                        runAuditBtn.disabled = false;
                        runAuditBtn.textContent = prevLabel;
                        if (reportEl) {
                            reportEl.style.display = "block";
                            reportEl.innerHTML = '<div class="auditMeta">' +
                                esc(i18n.t("cfg.alert.saveError",
                                    (err && err.statusText ? err.statusText : err))) +
                                '</div>';
                        }
                    });
                });
            }

            // (Le chat interactif vit désormais sur sa propre page
            //  « Chat LLM AI » — menu Serveur — servie par chat.js. Cette
            //  page ne conserve que le flag d'activation chkChatEnabled.)

            // Soumission du formulaire = sauvegarde.
            view.querySelector("form.LLMAIConfigForm").addEventListener("submit", function (e) {
                e.preventDefault();
                var cfg = collect(view);

                ApiClient.updatePluginConfiguration(pluginId, cfg).then(function () {
                    if (typeof Dashboard !== "undefined" && Dashboard.processPluginConfigurationUpdateResult) {
                        Dashboard.processPluginConfigurationUpdateResult(cfg);
                    }
                    if (typeof Dashboard !== "undefined" && Dashboard.alert) {
                        Dashboard.alert(i18n.t("cfg.alert.saved"));
                    }
                }, function (err) {
                    if (typeof Dashboard !== "undefined" && Dashboard.alert) {
                        Dashboard.alert(i18n.t("cfg.alert.saveError", (err && err.statusText ? err.statusText : err)));
                    }
                });

                return false;
            });
            }); // fin i18nReady().then(...).then(...)
        });
    };
});