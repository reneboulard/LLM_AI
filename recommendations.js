// Page « Recommandations LLM AI » — module AMD chargé par le dashboard via
// data-controller="__plugin/LLMAIRecommendationsPageJS". Relit la dernière
// réponse de l'agent depuis l'endpoint plugin /Plugins/LLMAI/Recos (champs
// Recommendations / RecommendationsDate de la config, servis sans exiger
// ManageServer) et l'affiche sous forme de **grille de cartes** actionnables
// (poster, badge priorité, ⭐, date/chaîne, raison, boutons
// Programmer / Oublier). Réplique de la section IA de absent_series.php.
//
// Programmer : POST /LiveTv/SeriesTimers {ProgramId, RecordNewOnly, SkipEpisodesInLibrary, ChannelId}
//   via ApiClient.ajax (gère le token admin) — réplique de record_series.php.
// Lecture des recos : GET /Plugins/LLMAI/Recos (endpoint plugin authentifié
//   standard). PAS getPluginConfiguration : l'endpoint hôte
//   /Plugins/{id}/Configuration est réservé ManageServer (admin) et renvoyait
//   403 aux usagers non-admin — la page est pourtant servie dans le menu
//   utilisateur (EnableInUserMenu).
// Oublier : POST /Plugins/LLMAI/Forget {Title} — l'écriture de la drop list
//   DroppedTitles se fait serveur-side (même raison : le round-trip config
//   get/update est admin-only). Réponse {Added} — false = déjà présent.
//   Exclusion effective à la prochaine exécution (epg_series lit DroppedTitles).
define([], function () {
    "use strict";

    // Cache-busting : compare la version du build qui a servi ce JS à celle
    // du serveur (module généré asset_version.js, chargé via require() comme
    // LLMAII18n). Tâche de fond non bloquante : si le JS servi est périmé
    // (cache disque), les entrées de cache HTTP sont réécrites puis la page
    // rechargée — une seule fois par session (voir asset_version_template.js).
    // Toute erreur est silencieuse : la page reste pleinement fonctionnelle.
    (function checkAssetVersion() {
        try {
            var url = ApiClient.getUrl("web/ConfigurationPage", { name: "LLMAIAssetVersion" });
            require([url], function (av) {
                if (av && typeof av.checkForUpdate === "function") av.checkForUpdate(ApiClient);
            }, function () { /* module absent : silencieux */ });
        } catch (e) { /* require/ApiClient indisponible : silencieux */ }
    })();

    // Module i18n (FR/EN) : chargé comme ressource plugin via require() sur
    // « web/ConfigurationPage?name=LLMAII18n » (même mécanisme que le
    // viewmanager pour les data-controller). Pas de dépendance AMD
    // « __plugin/... » (RequireJS ne mappe pas ce préfixe comme id de module).
    // `i18n` est renseigné au viewshow avant toute utilisation de t() /
    // translateView().
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

    function esc(s) {
        // Échappe le HTML pour éviter toute injection depuis le contenu du LLM.
        return String(s == null ? "" : s)
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;");
    }

    function fmtDate(iso) {
        if (!iso) return "";
        var d = new Date(iso);
        if (isNaN(d.getTime())) return esc(iso);
        // Date/heure locale lisible : 26/08 21:00 (comme ai_section.php).
        var p = function (n) { return (n < 10 ? "0" : "") + n; };
        return p(d.getDate()) + "/" + p(d.getMonth() + 1) + " " +
               p(d.getHours()) + ":" + p(d.getMinutes());
    }

    function priorityStyle(p) {
        var v = String(p || "medium").toLowerCase();
        if (v === "high") return { color: "#ff4500", label: i18n.t("rec.prio.high") };
        if (v === "low") return { color: "#555", label: i18n.t("rec.prio.low") };
        return { color: "#f39c12", label: i18n.t("rec.prio.medium") };
    }

    // Base URL d'Emby telle que connue du client navigateur (ApiClient la tient
    // à jour : IP locale, nom de domaine, port http/https — selon l'origine
    // depuis laquelle l'utilisateur consulte la page). On ne code AUCUNE IP :
    // l'image doit se charger depuis le même serveur que la page, quel que soit
    // l'hôte (localhost, IP LAN, domaine, http ou https). Repli root-relative
    // si ApiClient n'est pas disponible.
    function serverBase() {
        try {
            var a = ApiClient && ApiClient.serverAddress && ApiClient.serverAddress();
            if (a) return String(a).replace(/\/+$/, "");
        } catch (e) {}
        return "";
    }

    function posterSrc(it) {
        var id = it.id || it.Id || "";
        if (id) {
            var b = serverBase();
            var path = "/emby/Items/" + encodeURIComponent(id) + "/Images/Primary?maxWidth=400";
            return b ? b + path : path;   // absolu (origine courante) ou root-relative
        }
        // Ancien payload sans id : on tente le champ image_url tel quel.
        return it.image_url || "";
    }

    // Badge de type : différencie une reco « À venir » (programme EPG du soir
    // à regarder en direct / à enregistrer) d'une reco déjà disponible
    // (enregistrement ou item de bibliothèque, prêts à lire maintenant). Un
    // programme EPG déjà diffusé (aired=true, injecté par la validation
    // backend) → « Diffusé » (carte conservée, actions masquées).
    function sourceBadge(it) {
        if (it.aired) return { cls: "ai-type-aired", icon: "✓", text: i18n.t("rec.type.aired") };
        var s = String(it.source || "").toLowerCase();
        if (s === "recording") return { cls: "ai-type-rec", icon: "📼", text: i18n.t("rec.type.recording") };
        if (s === "library") return { cls: "ai-type-lib", icon: "📚", text: i18n.t("rec.type.library") };
        return { cls: "ai-type-upcoming", icon: "⏰", text: i18n.t("rec.type.upcoming") };
    }

    // Construit le HTML d'une carte pour un item de recommandation.
    // it = {title, kind, reason, priority, channel, start, showbizz_match,
    //       id, channel_id, rating, year, image_url}  (champs enrichis côté tâche).
    function cardHtml(it, idx) {
        var title = it.title || it.name || "?";
        var pr = priorityStyle(it.priority);
        var hasId = !!it.id;
        var src = posterSrc(it);
        var isMovie = kindIsMovie(it.kind);
        // Année de production : injectée côté tâche (EPG matché, tmdb_lookup
        // ou item bibliothèque). Tolère number et "2024" ; ignore le bruit.
        var yr = parseInt(it.year, 10);
        var year = (!isNaN(yr) && yr > 1900 && yr < 2100) ? yr : null;
        var poster = src
            ? '<img src="' + esc(src) + '" alt="' + esc(title) + '" loading="lazy" referrerpolicy="no-referrer">'
            : '<div class="ai-poster-placeholder">' + (isMovie ? "🎬" : "📺") + '</div>';
        var rating = (typeof it.rating === "number" && it.rating > 0)
            ? '<div class="rating">⭐ ' + it.rating.toFixed(1) + '</div>'
            : "";
        var channel = it.channel || "—";
        var dateStr = fmtDate(it.start);

        // source="recording" ou "library" : l'item est déjà disponible (enregis-
        // trement ou item de bibliothèque non visionné). On propose « Regarder »
        // (lecture par id) au lieu de « Programmer »/« Regarder en direct ».
        // source="live" (ou absent) : programme EPG du soir → « Regarder en
        // direct » (si en cours) + « Programmer ».
        var isWatchItem = it.source === "recording" || it.source === "library";
        // Un programme EPG déjà diffusé (aired, injecté par la validation
        // backend) : on garde la carte mais on masque les actions obsolètes
        // (Programmer / Regarder en direct) — l'usager voit la reco passée.
        var isAired = !!it.aired;
        var badge = sourceBadge(it);

        // Méta-ligne : un item disponible (recording/library) n'a ni date ni
        // canal — on affiche son libellé de disponibilité plutôt que « 📅  • 📺 — ».
        // Un programme EPG (live, à venir ou diffusé) porte en plus date + canal,
        // précédés du libellé de type (l'icône ⏰ « À venir » demandée) et de
        // l'année de production (🎬) quand les métadonnées la fournissent.
        var meta = esc(badge.icon) + " " + esc(badge.text);
        if (year) meta += " · 🎬 " + year;
        if (!isWatchItem) meta += " · 📅 " + esc(dateStr) + " • 📺 " + esc(channel);

        // Bouton « Regarder en direct » : uniquement pour la section tonight
        // (it.section==="tonight"), source live, si le programme a déjà
        // commencé et qu'on dispose d'un channel_id, ET que la lecture client
        // est disponible (playbackManager via require).
        var watchLive = "";
        if (it.section === "tonight" && !isWatchItem && !isAired && (it.channel_id || it.channel_id === 0)
            && canWatch() && hasAiringStarted(it)) {
            watchLive = '<button class="ai-btn-watchlive" type="button" data-channel="' +
                esc(it.channel_id) + '" title="' + esc(i18n.t("rec.tonight.watchLive")) +
                '">▶ ' + esc(i18n.t("rec.tonight.watchLive")) + '</button>';
        }

        // Bouton « Regarder (bibli.) » : pour une reco source="live" dont le
        // titre est déjà dans la bibliothèque (library_id injecté par
        // EnrichWithLibrary) — lecture depuis la bibliothèque, sans attendre le
        // direct ni enregistrer. Réutilise le handler watchRecording (play id).
        var watchLib = "";
        if (!isWatchItem && it.library_id && canWatch()) {
            watchLib = '<button class="ai-btn-watchrec" type="button" data-id="' +
                esc(it.library_id) + '" title="' + esc(i18n.t("rec.tonight.watchLib")) +
                '">▶ ' + esc(i18n.t("rec.tonight.watchLib")) + '</button>';
        }

        // Bouton « Regarder » : pour un enregistrement ou un item de bibliothèque
        // (lecture par id). Remplace « Programmer ».
        var watchRec = "";
        if (isWatchItem && it.id && canWatch()) {
            watchRec = '<button class="ai-btn-watchrec" type="button" data-id="' +
                esc(it.id) + '" title="' + esc(i18n.t("rec.tonight.watch")) +
                '">▶ ' + esc(i18n.t("rec.tonight.watch")) + '</button>';
        }

        // « Programmer » : uniquement pour source live non encore diffusé (un
        // enregistrement / un item de bibliothèque n'a pas de timer à créer,
        // et un programme déjà diffusé n'a plus rien à programmer).
        var recordBtn = (isWatchItem || isAired) ? '' :
            '<button class="ai-btn-record" type="button"' +
                (hasId ? '' : ' disabled') +
                ' data-id="' + esc(it.id || "") + '"' +
                ' data-channel="' + esc(it.channel_id || "") + '"' +
                ' data-title="' + esc(title) + '"' +
                ' data-kind="' + esc(it.kind || "") + '"' +
                (hasId ? '' : ' title="' + esc(i18n.t("rec.btn.noId")) + '"') +
                '>' + i18n.t("rec.btn.program") + '</button>';

        return '<div class="ai-card"' + (isAired ? ' data-aired="1"' : '') + ' data-idx="' + idx + '">' +
            '<div class="ai-card-poster">' +
                poster +
                '<div class="ai-type-badge ' + badge.cls + '" title="' + esc(badge.text) + '">' +
                    esc(badge.icon) + '</div>' +
                '<div class="ai-priority-badge" style="background:' + pr.color + '">' + esc(pr.label) + '</div>' +
                rating +
            '</div>' +
            '<div class="ai-card-info">' +
                '<div class="ai-card-title" title="' + esc(title) + '">' + esc(title) + '</div>' +
                '<div class="ai-card-meta">' + meta + '</div>' +
                // Raison : libellé localisé « 🤖 Pourquoi ce soir / Why tonight »
                // UNIQUEMENT pour les cartes de la section « À regarder ce soir »
                // (it.section==="tonight") ; les recos d'enregistrement (sections
                // séries/films, cartes .strm) portent l'emoji 🤖 seul.
                '<div class="ai-card-reason" title="' + esc(it.reason || "") + '">' +
                esc((it.section === "tonight" ? i18n.t("rec.tonight.why") : "🤖 ") + (it.reason || "")) + '</div>' +
                '<div class="ai-card-actions">' +
                    watchLive +
                    watchLib +
                    watchRec +
                    recordBtn +
                    '<button class="ai-btn-drop" type="button" data-title="' + esc(title) + '">' + i18n.t("rec.btn.drop") + '</button>' +
                '</div>' +
            '</div>' +
        '</div>';
    }

    // Un programme tonight « a déjà commencé » si son start est dans le passé
    // (l'EPG tonight ne renvoie que des programmes de la soirée ; un start
    // passé = en cours ou juste diffusé → candidat au direct). Heuristique
    // (l'item reco ne porte pas end) — bonus, non bloquant.
    function hasAiringStarted(it) {
        if (!it.start) return false;
        var s = new Date(it.start).getTime();
        return !isNaN(s) && Date.now() >= s;
    }

    // Lance la lecture d'un item Emby (chaîne LiveTV, enregistrement ou item de
    // bibliothèque) via le playbackManager du client web Emby. L'alias AMD
    // "playbackManager" est déclaré par le dashboard (index.html) ; le module y
    // expose le manager en default export. On ne teste PAS ApiClient.play
    // (inexistant — la lecture passe par playbackManager, pas l'ApiClient).
    function playById(id) {
        if (!id || typeof require !== "function") return;
        try {
            require(["playbackManager"], function (pm) {
                var m = (pm && (pm.default || pm)) || pm;
                if (m && typeof m.play === "function") {
                    m.play({ ids: [id], serverId: ApiClient.serverId() });
                }
            }, function () { /* échec require : non bloquant */ });
        } catch (e) { /* non bloquant */ }
    }

    // Les boutons de lecture s'affichent si l'AMD require est disponible (le
    // playbackManager du dashboard s'y charge). Vérifié au rendre, pas au define
    // (ApiClient / require sont peuplés par le dashboard au runtime).
    function canWatch() {
        try { return typeof require === "function" && !!ApiClient; }
        catch (e) { return false; }
    }

    // Lance la lecture d'une chaîne LiveTV (bouton « Regarder en direct »).
    function watchLive(btn) {
        var cid = btn.getAttribute("data-channel");
        if (cid) playById(cid);
    }

    // Lance la lecture d'un enregistrement (bouton « Regarder » sur une reco
    // source="recording") : l'id est l'identifiant Emby de l'enregistrement.
    function watchRecording(btn) {
        var id = btn.getAttribute("data-id");
        if (id) playById(id);
    }

    // Un item est un « film » si son kind vaut movie/film (sinon : série).
    function kindIsMovie(k) { return /^(movie|film)/i.test(k || ""); }

    // Normalisation de titre identique au Norm() C# (GetEmbyInfoTool) :
    // minuscules, retrait d'un article de tête FR/EN séparé par une espace,
    // puis suppression de tout ce qui n'est pas alphanumérique. Utilisé pour
    // rapprocher le titre d'une reco d'un timer existant dont le nom peut
    // différer par l'article (« Le suspect » / « The Suspect » / « Suspect »).
    function normTitle(s) {
        s = String(s == null ? "" : s).toLowerCase();
        s = s.replace(/^(?:le|la|les|un|une|des|du|de|the|a|an)\b\s+/, "");
        return s.replace(/[^a-z0-9]/g, "");
    }

    function renderCards(items) {
        return '<div class="ai-cards-container">' +
            items.map(function (it, i) { return cardHtml(it, i); }).join("") +
            '</div>';
    }

    // Une section de recommandations : titre + compteur + grille (ou empty).
    function sectionHtml(kind, title, emoji, items) {
        var count = items.length;
        var head = '<h3 class="recSectionTitle">' + emoji + ' ' + esc(title) +
            ' <span class="section-counter">' + (count ? i18n.t("rec.count", count) : '') + '</span></h3>';
        var body = count ? renderCards(items)
            : '<div class="recEmpty">' + i18n.t("rec.section.empty") + '</div>';
        return '<div class="recSection" data-kind="' + kind + '">' + head + body + '</div>';
    }

    // ---- Section « À regarder ce soir » (endpoint plugin personnalisé) ----

    // Section tonight rendue après réception de l'endpoint : en-tête (titre +
    // compteur + badge « depuis cache » + bouton Rafraîchir) + grille de cartes.
    function tonightSectionHtml(items, fromCache) {
        var count = items.length;
        var badge = fromCache
            ? ' <span class="tonight-cache">' + esc(i18n.t("rec.tonight.fromCache")) + '</span>'
            : '';
        var head = '<h3 class="recSectionTitle">🌙 ' + esc(i18n.t("rec.section.tonight")) +
            ' <span class="section-counter">' + (count ? i18n.t("rec.count", count) : '') + '</span>' +
            badge + '</h3>' +
            '<div class="tonight-actions"><button is="emby-button" type="button" class="raised ai-btn-tonight-refresh">' +
            esc(i18n.t("rec.tonight.refresh")) + '</button></div>';
        var body = count ? renderCards(items)
            : '<div class="recEmpty">' + i18n.t("rec.tonight.empty") + '</div>';
        return '<div class="recSection tonightSection" data-kind="tonight">' + head + body + '</div>';
    }

    function tonightLoadingHtml() {
        return '<div class="recSection tonightSection" data-kind="tonight">' +
            '<h3 class="recSectionTitle">🌙 ' + esc(i18n.t("rec.section.tonight")) + '</h3>' +
            '<div class="recEmpty"><span class="tonight-spinner"></span> ' +
            esc(i18n.t("rec.tonight.loading")) + '</div></div>';
    }

    function tonightErrorHtml(msg) {
        return '<div class="recSection tonightSection" data-kind="tonight">' +
            '<h3 class="recSectionTitle">🌙 ' + esc(i18n.t("rec.section.tonight")) + '</h3>' +
            '<div class="recEmpty">' + esc(i18n.t("rec.tonight.error", msg || "")) + '</div></div>';
    }

    // Appelle l'endpoint plugin GET /Plugins/LLMAI/Tonight (personnalisé par
    // usager, token géré par ApiClient.ajax) et rend la section tonight dans le
    // conteneur #recTonight. refresh=true bypass le cache serveur et force un
    // nouveau run LLM. Le run pouvant prendre 10–60 s, on affiche un état
    // loading immédiatement (les sections Séries/Films sont déjà rendues).
    function loadTonight(view, refresh) {
        var host = view.querySelector("#recTonight");
        if (!host) return;
        host.innerHTML = tonightLoadingHtml();

        var uid = "";
        try { uid = (ApiClient.getCurrentUserId && ApiClient.getCurrentUserId()) || ""; }
        catch (e) {}

        var url = ApiClient.getUrl("Plugins/LLMAI/Tonight",
            { userId: uid, refresh: refresh ? "1" : "0" });

        ApiClient.ajax({ url: url, type: "GET", dataType: "json" }).then(function (data) {
            data = data || {};
            // Section désactivée en config : on masque le conteneur.
            if (data.Enabled === false) { host.innerHTML = ""; return; }
            if (data.Error) { host.innerHTML = tonightErrorHtml(data.Error); return; }

            var items = [];
            try {
                var p = JSON.parse(data.Items || "[]");
                if (Array.isArray(p)) items = p;
            } catch (e) { items = []; }

            // Marque les items comme appartenant à la section tonight (active le
            // bouton « Regarder en direct » dans cardHtml).
            items.forEach(function (it) { it.section = "tonight"; });

            host.innerHTML = tonightSectionHtml(items, !!data.FromCache);
            updateCount();
            markScheduledCards(view);
        }, function (err) {
            host.innerHTML = tonightErrorHtml((err && err.statusText) ? err.statusText : "erreur");
        });
    }

    // ---- Actions ----

    // Crée un timer Emby (série ou film) à partir d'un ProgramId EPG.
    // Réplique du flux canonique du dashboard Emby (recordinghelper.js) :
    //   1. ApiClient.getNewLiveTvTimerDefaults({ programId }) → objet timer
    //      complet (Name, SeriesId, StartDate, EndDate, ChannelId, paddings…)
    //      dérivé du programme. POSTER UNIQUEMENT {ProgramId, RecordNewOnly,
    //      SkipEpisodesInLibrary, ChannelId} NE SUFFIT PAS : le serveur ne crée
    //      alors aucun timer (champs requis manquants).
    //   2. POST de cet objet complet vers LiveTv/SeriesTimers (série) ou
    //      LiveTv/Timers (film — single timer, pas un series timer).
    //   RecordNewOnly/SkipEpisodesInLibrary ne s'appliquent qu'aux séries.
    function recordSeries(btn) {
        if (btn.disabled || btn.classList.contains("success")) return;
        var id = btn.getAttribute("data-id");
        if (!id) return;
        var isMovie = kindIsMovie(btn.getAttribute("data-kind"));

        var original = btn.innerText;
        btn.disabled = true;
        btn.innerText = "…";

        ApiClient.getNewLiveTvTimerDefaults({ programId: id }).then(function (item) {
            item = item || {};
            if (!isMovie) {
                item.RecordNewOnly = true;
                item.SkipEpisodesInLibrary = true;
            }
            return isMovie
                ? ApiClient.createLiveTvTimer(item)
                : ApiClient.createLiveTvSeriesTimer(item);
        }).then(function () {
            btn.classList.add("success");
            btn.innerText = i18n.t("rec.btn.scheduled");
        }, function (err) {
            // En cas d'échec, on vérifie si le timer n'existe pas déjà
            // (Emby refuse les doublons) → on le signale comme déjà programmé.
            // getLiveTvTimers / getLiveTvSeriesTimers renvoient du JSON parsé
            // (ajax brut retournerait un Response non parsé → Items undefined).
            var getter = isMovie
                ? function () { return ApiClient.getLiveTvTimers(); }
                : function () { return ApiClient.getLiveTvSeriesTimers(); };
            getter().then(function (data) {
                var exists = false;
                var items = (data && data.Items) || [];
                for (var i = 0; i < items.length; i++) {
                    if (String(items[i].ProgramId) === String(id)) { exists = true; break; }
                }
                if (exists) {
                    btn.classList.add("already");
                    btn.innerText = i18n.t("rec.btn.already");
                } else {
                    btn.disabled = false;
                    btn.innerText = original;
                    if (typeof Dashboard !== "undefined" && Dashboard.alert) {
                        Dashboard.alert(i18n.t("rec.alert.refused",
                            (err && err.statusText ? err.statusText : "erreur")));
                    }
                }
            }, function () {
                btn.disabled = false;
                btn.innerText = original;
                if (typeof Dashboard !== "undefined" && Dashboard.alert) {
                    Dashboard.alert(i18n.t("rec.alert.refusedShort"));
                }
            });
        });
    }

    // Ajoute le titre à la drop list persistante (DroppedTitles) via l'endpoint
    // plugin POST /Plugins/LLMAI/Forget (écriture serveur-side — le round-trip
    // config admin est interdit aux non-admin), puis fade out de la carte.
    function dropSeries(btn) {
        if (btn.classList.contains("loading")) return;
        var title = (btn.getAttribute("data-title") || "").trim();
        if (!title) return;

        var card = btn.closest(".ai-card");
        var original = btn.innerText;
        btn.classList.add("loading");
        btn.innerText = "…";

        ApiClient.ajax({
            url: ApiClient.getUrl("Plugins/LLMAI/Forget"),
            type: "POST",
            data: JSON.stringify({ Title: title }),
            contentType: "application/json"
        }).then(function (data) {
            // Added=false (déjà présent) : on ferme la carte pareillement —
            // le titre est exclu, inutile de réécrire la config.
            finishDrop(card, btn);
        }, function (err) {
            btn.classList.remove("loading");
            btn.innerText = original;
            if (typeof Dashboard !== "undefined" && Dashboard.alert) {
                Dashboard.alert(i18n.t("rec.alert.dropSave",
                    (err && err.statusText ? err.statusText : "erreur")));
            }
        });
    }

    function finishDrop(card, btn) {
        btn.innerText = i18n.t("rec.btn.forgotten");
        if (card) {
            card.classList.add("fading");
            setTimeout(function () { card.remove(); updateCount(); }, 300);
        }
    }

    function updateCount() {
        // Recompte les cartes restantes : total + par section (Séries/Films).
        var all = document.querySelectorAll(".LLMAIRecommendationsPage .ai-card");
        var el = document.querySelector("#recCount");
        if (el) el.textContent = all.length ? i18n.t("rec.count", all.length) : "";
        document.querySelectorAll(".LLMAIRecommendationsPage .recSection").forEach(function (sec) {
            var c = sec.querySelectorAll(".ai-card").length;
            var span = sec.querySelector(".section-counter");
            if (span) span.textContent = c ? i18n.t("rec.count", c) : "";
        });
    }

    // Au chargement de la page, marque les recommandations déjà programmées
    // (timer existant) pour que le feedback persiste au rechargement. On croise
    // deux signaux :
    //   - ProgramId exact (data-id) présent dans les timers (single + series) ;
    //     couvre le cas « je viens de cliquer Programmer sur cette reco ».
    //   - titre normalisé (avec retrait d'article) : un series timer couvre
    //     toute la série, pas seulement le programme d'origine ; un film peut
    //     avoir été programmé en single timer sous le titre EPG. On garde les
    //     noms de séries et de films séparés pour éviter qu'un film « Suspect »
    //     ne matche une série « Le suspect » déjà programmée.
    function markScheduledCards(view) {
        var cards = view.querySelectorAll(".ai-card");
        if (!cards.length) return;

        var done = { ids: {}, seriesNames: {}, movieNames: {} };

        // getLiveTvSeriesTimers / getLiveTvTimers renvoient du JSON déjà parsé
        // (getJSON) ; à l'inverse, ApiClient.ajax brut retourne un objet Response
        // non parsé (data.Items === undefined) — d'où l'absence de match avant.
        function getItems(which) {
            return (which === "series"
                ? ApiClient.getLiveTvSeriesTimers()
                : ApiClient.getLiveTvTimers()
            ).then(function (data) { return (data && data.Items) || []; },
                   function () { return []; });
        }

        function addName(map, name) {
            var n = normTitle(name);
            if (n) map[n] = true;
        }

        Promise.all([
            getItems("series").then(function (items) {
                items.forEach(function (t) {
                    var pid = t.ProgramId || (t.ProgramInfo && t.ProgramInfo.Id);
                    if (pid) done.ids[String(pid)] = true;
                    if (t.Name) addName(done.seriesNames, t.Name);
                });
            }),
            getItems("timers").then(function (items) {
                items.forEach(function (t) {
                    var pid = t.ProgramId || (t.ProgramInfo && t.ProgramInfo.Id);
                    if (pid) done.ids[String(pid)] = true;
                    var pi = t.ProgramInfo || {};
                    if (pi.SeriesName) addName(done.seriesNames, pi.SeriesName);
                    else if (pi.Name) addName(done.movieNames, pi.Name);
                    else if (t.SeriesName) addName(done.seriesNames, t.SeriesName);
                    else if (t.Name) addName(done.movieNames, t.Name);
                });
            })
        ]).then(function () {
            view.querySelectorAll(".ai-card").forEach(function (card) {
                var btn = card.querySelector(".ai-btn-record");
                if (!btn || btn.disabled) return;
                var id = btn.getAttribute("data-id");
                var nk = normTitle(btn.getAttribute("data-title") || "");
                var isMovie = kindIsMovie(btn.getAttribute("data-kind"));
                var byId = id && done.ids[String(id)];
                var byName = nk && (isMovie ? done.movieNames[nk] : done.seriesNames[nk]);
                if (byId || byName) {
                    btn.classList.add("already");
                    btn.innerText = i18n.t("rec.btn.already");
                    btn.disabled = true;
                }
            });
        });
    }

    function render(view, cfg) {
        var meta = view.querySelector("#recMeta");
        var content = view.querySelector("#recContent");
        var raw = view.querySelector("#recRaw");
        var btn = view.querySelector("#btnToggleRaw");
        var countEl = view.querySelector("#recCount");

        var dateStr = cfg.RecommendationsDate ? fmtDate(cfg.RecommendationsDate) : "";
        meta.innerHTML = dateStr ? i18n.t("rec.lastRun", dateStr) : i18n.t("rec.noRun");

        var payload = cfg.Recommendations || "";
        raw.textContent = payload || "";

        if (!payload) {
            // Conteneur tonight en tête (rempli asynchronement par loadTonight)
            // + message « aucune recommandation » pour la partie planifiée.
            content.innerHTML = '<div id="recTonight" class="recSection"></div>' +
                '<div class="recEmpty">' + i18n.t("rec.empty") + '</div>';
            btn.style.display = "none";
            if (countEl) countEl.textContent = "";
            return;
        }

        // La réponse est soit un tableau JSON d'items, soit du Markdown libre.
        var items = null;
        try {
            var parsed = JSON.parse(payload);
            if (Array.isArray(parsed)) items = parsed;
        } catch (e) { items = null; }

        // Le conteneur tonight (#recTonight) précède toujours les sections
        // planifiées : c'est la recommandation la plus pertinente « maintenant ».
        var tonightHost = '<div id="recTonight" class="recSection"></div>';

        if (items && items.length) {
            // Section 1 : séries — Section 2 : films (tri par kind).
            var series = items.filter(function (it) { return !kindIsMovie(it.kind); });
            var movies = items.filter(function (it) { return kindIsMovie(it.kind); });
            content.innerHTML = tonightHost +
                sectionHtml("series", i18n.t("rec.section.series"), "📺", series) +
                sectionHtml("movie", i18n.t("rec.section.movies"), "🎬", movies);
            btn.style.display = "";
            if (countEl) countEl.textContent = i18n.t("rec.count", items.length);
            markScheduledCards(view);
        } else {
            // Pas un tableau : on affiche le texte tel quel (Markdown brut).
            content.innerHTML = tonightHost + '<pre class="rawJson">' + esc(payload) + '</pre>';
            btn.style.display = "none";
            if (countEl) countEl.textContent = "";
        }
    }

    return function (view) {
        view.addEventListener("viewshow", function () {
            // i18n : charge le module, résout la langue (globalize) puis traduit
            // le DOM statique (titre, bouton JSON brut) avant de charger / rendre
            // les cartes. Les chaînes dynamiques (sections, boutons, alerts)
            // passent par t().
            i18nReady().then(function () {
                return i18n.init();
            }).then(function () {
                i18n.translateView(view);

                // Fond de page image : LLMAIBg est un module AMD exportant une data
                // URI (ASCII base64). L'endpoint web/ConfigurationPage sert les
                // ressources en UTF-8 et corromprait un binaire, d'où la data URI.
                // Chargé via require (même mécanisme que i18nReady). Tant que le
                // module n'est pas chargé, le fallback background-color #101010
                // (posé en CSS) reste visible ; en cas d'échec on garde ce fallback.
                require([
                    ApiClient.getUrl("web/ConfigurationPage", { name: "LLMAIBg" })
                ], function (dataUri) {
                    if (!dataUri) return;
                    view.style.backgroundImage =
                        "linear-gradient(rgba(10,10,10,0.55), rgba(16,16,16,0.72)), url('" +
                        dataUri + "')";
                    view.style.backgroundSize = "cover";
                    view.style.backgroundPosition = "center center";
                    view.style.backgroundAttachment = "fixed";
                    view.style.backgroundRepeat = "no-repeat";
                }, function () { /* échec chargement : on garde le fallback CSS */ });

                // Lecture des recommandations via l'endpoint plugin (usager
                // standard), PAS getPluginConfiguration (admin-only → 403).
                // Mêmes champs que ceux que render() consommait dans la cfg.
                ApiClient.ajax({
                    url: ApiClient.getUrl("Plugins/LLMAI/Recos"),
                    type: "GET",
                    dataType: "json"
                }).then(function (data) {
                    data = data || {};
                    if (data.Error) {
                        view.querySelector("#recContent").innerHTML =
                            '<div class="recEmpty">' + esc(data.Error) + '</div>';
                        return;
                    }
                    render(view, {
                        Recommendations: data.Items || "",
                        RecommendationsDate: data.Date || ""
                    });
                    // Section « À regarder ce soir » : appel endpoint plugin
                    // personnalisé (asynchrone, peut prendre 10–60 s la 1re fois).
                    // Lancé après le rendu des sections planifiées (non bloquant).
                    loadTonight(view, false);
                }, function () {
                    view.querySelector("#recContent").innerHTML =
                        '<div class="recEmpty">' + i18n.t("rec.alert.cfgLoad") + '</div>';
                });

                view.querySelector("#btnToggleRaw").addEventListener("click", function () {
                    var raw = view.querySelector("#recRaw");
                    raw.style.display = (raw.style.display === "none") ? "block" : "none";
                });

                // Délégation d'événements pour les boutons des cartes (rendu dynamique).
                view.addEventListener("click", function (e) {
                    var target = e.target;
                    if (!(target instanceof Element)) return;
                    var refreshBtn = target.closest ? target.closest(".ai-btn-tonight-refresh") : null;
                    if (refreshBtn && view.contains(refreshBtn)) { loadTonight(view, true); return; }
                    var watchBtn = target.closest ? target.closest(".ai-btn-watchlive") : null;
                    if (watchBtn && view.contains(watchBtn)) { watchLive(watchBtn); return; }
                    var watchRecBtn = target.closest ? target.closest(".ai-btn-watchrec") : null;
                    if (watchRecBtn && view.contains(watchRecBtn)) { watchRecording(watchRecBtn); return; }
                    var recordBtn = target.closest ? target.closest(".ai-btn-record") : null;
                    if (recordBtn && view.contains(recordBtn)) { recordSeries(recordBtn); return; }
                    var dropBtn = target.closest ? target.closest(".ai-btn-drop") : null;
                    if (dropBtn && view.contains(dropBtn)) { dropSeries(dropBtn); return; }
                });
            }); // fin i18nReady().then(...).then(...)
        });
    };
});