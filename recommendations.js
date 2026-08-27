// Page « Recommandations LLM AI » — module AMD chargé par le dashboard via
// data-controller="__plugin/LLMAIRecommendationsPageJS". Relit la dernière
// réponse de l'agent depuis la config du plugin (champs Recommendations /
// RecommendationsDate) et l'affiche sous forme de **grille de cartes**
// actionnables (poster, badge priorité, ⭐, date/chaîne, raison, boutons
// Programmer / Oublier). Réplique de la section IA de absent_series.php.
//
// Programmer : POST /LiveTv/SeriesTimers {ProgramId, RecordNewOnly, SkipEpisodesInLibrary, ChannelId}
//   via ApiClient.ajax (gère le token admin) — réplique de record_series.php.
// Oublier : round-trip config — getPluginConfiguration → ajoute le titre à
//   DroppedTitles (JSON array, dedup case-insensitive) → updatePluginConfiguration
//   (renvoie la cfg complète, les autres champs sont préservés) → fade out de la
//   carte. Exclusion effective à la prochaine exécution (epg_series lit DroppedTitles).
define([], function () {
    "use strict";

    var pluginId = "e7d3dee6-ef19-46a9-985f-06318b682e60";

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
        if (v === "high") return { color: "#ff4500", label: "⚡ Haute" };
        if (v === "low") return { color: "#555", label: "🔵 Basse" };
        return { color: "#f39c12", label: "🔶 Moyenne" };
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

    // Construit le HTML d'une carte pour un item de recommandation.
    // it = {title, kind, reason, priority, channel, start, showbizz_match,
    //       id, channel_id, rating, image_url}  (champs enrichis côté tâche).
    function cardHtml(it, idx) {
        var title = it.title || it.name || "?";
        var pr = priorityStyle(it.priority);
        var hasId = !!it.id;
        var src = posterSrc(it);
        var isMovie = kindIsMovie(it.kind);
        var poster = src
            ? '<img src="' + esc(src) + '" alt="' + esc(title) + '" loading="lazy" referrerpolicy="no-referrer">'
            : '<div class="ai-poster-placeholder">' + (isMovie ? "🎬" : "📺") + '</div>';
        var rating = (typeof it.rating === "number" && it.rating > 0)
            ? '<div class="rating">⭐ ' + it.rating.toFixed(1) + '</div>'
            : "";
        var channel = it.channel || "—";
        var dateStr = fmtDate(it.start);

        return '<div class="ai-card" data-idx="' + idx + '">' +
            '<div class="ai-card-poster">' +
                poster +
                '<div class="ai-priority-badge" style="background:' + pr.color + '">' + esc(pr.label) + '</div>' +
                rating +
            '</div>' +
            '<div class="ai-card-info">' +
                '<div class="ai-card-title" title="' + esc(title) + '">' + esc(title) + '</div>' +
                '<div class="ai-card-meta">📅 ' + esc(dateStr) + ' • 📺 ' + esc(channel) + '</div>' +
                '<div class="ai-card-reason" title="' + esc(it.reason || "") + '">🤖 ' + esc(it.reason || "") + '</div>' +
                '<div class="ai-card-actions">' +
                    '<button class="ai-btn-record" type="button"' +
                        (hasId ? '' : ' disabled') +
                        ' data-id="' + esc(it.id || "") + '"' +
                        ' data-channel="' + esc(it.channel_id || "") + '"' +
                        ' data-title="' + esc(title) + '"' +
                        (hasId ? '' : ' title="Aucun Id programme rattaché (titre non matché)"') +
                        '>✅ Programmer</button>' +
                    '<button class="ai-btn-drop" type="button" data-title="' + esc(title) + '">🗑️ Oublier</button>' +
                '</div>' +
            '</div>' +
        '</div>';
    }

    // Un item est un « film » si son kind vaut movie/film (sinon : série).
    function kindIsMovie(k) { return /^(movie|film)/i.test(k || ""); }

    function renderCards(items) {
        return '<div class="ai-cards-container">' +
            items.map(function (it, i) { return cardHtml(it, i); }).join("") +
            '</div>';
    }

    // Une section de recommandations : titre + compteur + grille (ou empty).
    function sectionHtml(kind, title, emoji, items) {
        var count = items.length;
        var head = '<h3 class="recSectionTitle">' + emoji + ' ' + esc(title) +
            ' <span class="section-counter">' + (count ? count + ' recommandation(s)' : '') + '</span></h3>';
        var body = count ? renderCards(items)
            : '<div class="recEmpty">Aucune recommandation dans cette section.</div>';
        return '<div class="recSection" data-kind="' + kind + '">' + head + body + '</div>';
    }

    // ---- Actions ----

    // Crée un timer série Emby (réplique de record_series.php).
    function recordSeries(btn) {
        if (btn.disabled || btn.classList.contains("success")) return;
        var id = btn.getAttribute("data-id");
        var channelId = btn.getAttribute("data-channel");
        if (!id) return;

        var original = btn.innerText;
        btn.disabled = true;
        btn.innerText = "…";

        var body = {
            ProgramId: id,
            RecordNewOnly: true,
            SkipEpisodesInLibrary: true
        };
        if (channelId) body.ChannelId = channelId;

        ApiClient.ajax({
            url: ApiClient.getUrl("LiveTv/SeriesTimers"),
            type: "POST",
            data: JSON.stringify(body),
            contentType: "application/json"
        }).then(function () {
            btn.classList.add("success");
            btn.innerText = "✓ Programmée";
        }, function (err) {
            // En cas d'échec, on vérifie si le timer n'existe pas déjà
            // (Emby refuse les doublons) → on le signale comme succès.
            ApiClient.ajax({
                url: ApiClient.getUrl("LiveTv/SeriesTimers"),
                type: "GET"
            }).then(function (data) {
                var exists = false;
                try {
                    var parsed = typeof data === "string" ? JSON.parse(data) : data;
                    var items = (parsed && parsed.Items) || [];
                    for (var i = 0; i < items.length; i++) {
                        if (String(items[i].ProgramId) === String(id)) { exists = true; break; }
                    }
                } catch (e) {}
                if (exists) {
                    btn.classList.add("success");
                    btn.innerText = "✓ Déjà programmée";
                } else {
                    btn.disabled = false;
                    btn.innerText = original;
                    if (typeof Dashboard !== "undefined" && Dashboard.alert) {
                        Dashboard.alert("Programmation refusée par Emby : " +
                            (err && err.statusText ? err.statusText : "erreur"));
                    }
                }
            }, function () {
                btn.disabled = false;
                btn.innerText = original;
                if (typeof Dashboard !== "undefined" && Dashboard.alert) {
                    Dashboard.alert("Programmation refusée par Emby.");
                }
            });
        });
    }

    // Ajoute le titre à la drop list persistante (DroppedTitles) via round-trip
    // config, puis fade out de la carte (réplique de drop_series.php + exclusion).
    function dropSeries(btn) {
        if (btn.classList.contains("loading")) return;
        var title = (btn.getAttribute("data-title") || "").trim();
        if (!title) return;

        var card = btn.closest(".ai-card");
        var original = btn.innerText;
        btn.classList.add("loading");
        btn.innerText = "…";

        ApiClient.getPluginConfiguration(pluginId).then(function (cfg) {
            cfg = cfg || {};
            var arr = [];
            try {
                var parsed = JSON.parse(cfg.DroppedTitles || "[]");
                if (Array.isArray(parsed)) arr = parsed;
            } catch (e) { arr = []; }

            // Dedup case-insensitive.
            var lower = arr.map(function (t) { return String(t || "").toLowerCase(); });
            if (lower.indexOf(title.toLowerCase()) >= 0) {
                // Déjà présent : on ferme la carte sans réécrire la config.
                finishDrop(card, btn);
                return;
            }
            arr.push(title);
            cfg.DroppedTitles = JSON.stringify(arr);

            ApiClient.updatePluginConfiguration(pluginId, cfg).then(function () {
                finishDrop(card, btn);
            }, function (err) {
                btn.classList.remove("loading");
                btn.innerText = original;
                if (typeof Dashboard !== "undefined" && Dashboard.alert) {
                    Dashboard.alert("Impossible d'enregistrer la drop list : " +
                        (err && err.statusText ? err.statusText : "erreur"));
                }
            });
        }, function (err) {
            btn.classList.remove("loading");
            btn.innerText = original;
            if (typeof Dashboard !== "undefined" && Dashboard.alert) {
                Dashboard.alert("Impossible de lire la config : " +
                    (err && err.statusText ? err.statusText : "erreur"));
            }
        });
    }

    function finishDrop(card, btn) {
        btn.innerText = "✓ Oublié";
        if (card) {
            card.classList.add("fading");
            setTimeout(function () { card.remove(); updateCount(); }, 300);
        }
    }

    function updateCount() {
        // Recompte les cartes restantes : total + par section (Séries/Films).
        var all = document.querySelectorAll(".LLMAIRecommendationsPage .ai-card");
        var el = document.querySelector("#recCount");
        if (el) el.textContent = all.length ? all.length + " recommandation(s)" : "";
        document.querySelectorAll(".LLMAIRecommendationsPage .recSection").forEach(function (sec) {
            var c = sec.querySelectorAll(".ai-card").length;
            var span = sec.querySelector(".section-counter");
            if (span) span.textContent = c ? c + " recommandation(s)" : "";
        });
    }

    function render(view, cfg) {
        var meta = view.querySelector("#recMeta");
        var content = view.querySelector("#recContent");
        var raw = view.querySelector("#recRaw");
        var btn = view.querySelector("#btnToggleRaw");
        var countEl = view.querySelector("#recCount");

        var dateStr = cfg.RecommendationsDate ? fmtDate(cfg.RecommendationsDate) : "";
        meta.innerHTML = dateStr ? "Dernière exécution : " + dateStr : "Aucune exécution enregistrée pour l'instant.";

        var payload = cfg.Recommendations || "";
        raw.textContent = payload || "";

        if (!payload) {
            content.innerHTML = '<div class="recEmpty">Aucune recommandation pour l&#39;instant. ' +
                                'Lancez la tâche « LLM AI Task » dans Tâches planifiées.</div>';
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

        if (items && items.length) {
            // Section 1 : séries — Section 2 : films (tri par kind).
            var series = items.filter(function (it) { return !kindIsMovie(it.kind); });
            var movies = items.filter(function (it) { return kindIsMovie(it.kind); });
            content.innerHTML =
                sectionHtml("series", "Séries", "📺", series) +
                sectionHtml("movie", "Films", "🎬", movies);
            btn.style.display = "";
            if (countEl) countEl.textContent = items.length + " recommandation(s)";
        } else {
            // Pas un tableau : on affiche le texte tel quel (Markdown brut).
            content.innerHTML = '<pre class="rawJson">' + esc(payload) + '</pre>';
            btn.style.display = "none";
            if (countEl) countEl.textContent = "";
        }
    }

    return function (view) {
        view.addEventListener("viewshow", function () {
            ApiClient.getPluginConfiguration(pluginId).then(function (cfg) {
                render(view, cfg || {});
            }, function () {
                view.querySelector("#recContent").innerHTML =
                    '<div class="recEmpty">Impossible de charger la configuration du plugin.</div>';
            });

            view.querySelector("#btnToggleRaw").addEventListener("click", function () {
                var raw = view.querySelector("#recRaw");
                raw.style.display = (raw.style.display === "none") ? "block" : "none";
            });

            // Délégation d'événements pour les boutons des cartes (rendu dynamique).
            view.addEventListener("click", function (e) {
                var target = e.target;
                if (!(target instanceof Element)) return;
                var recordBtn = target.closest ? target.closest(".ai-btn-record") : null;
                if (recordBtn && view.contains(recordBtn)) { recordSeries(recordBtn); return; }
                var dropBtn = target.closest ? target.closest(".ai-btn-drop") : null;
                if (dropBtn && view.contains(dropBtn)) { dropSeries(dropBtn); return; }
            });
        });
    };
});