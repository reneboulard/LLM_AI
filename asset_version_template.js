// TEMPLATE du module de cache-busting — NE PAS ÉDITER asset_version.js :
// ce fichier est généré à chaque build par la cible GenerateAssetVersionJs
// (LLM_AI.csproj) qui remplace __PLUGIN_VERSION__ par $(Version) du csproj,
// puis est embarqué comme ressource LLM_AI.asset_version.js (servi sur
// web/ConfigurationPage?name=LLMAIAssetVersion, chargé via require() par
// config.js / recommendations.js / chat.js — même pattern que LLMAII18n).
//
// PROBLÈME : Emby sert les ressources web/ConfigurationPage avec
// Cache-Control: public, sans max-age ni Last-Modified — le navigateur peut
// resservir un JS périmé depuis son cache disque sans jamais revalider
// (d'où le « hard reset » (Ctrl+Shift+R) nécessaire après chaque déploiement
// de DLL). L'ETag présent est correct (il suit l'assembly) mais rien ne
// force le navigateur à l'envoyer.
//
// SOLUTION : ce module porte la version du build QUI L'A GÉNÉRÉ + la logique
// de vérification. La page le charge puis interroge GET /Plugins/LLMAI/Version
// (route plugin authentifiée standard). En cas d'écart, le JS servi est
// périmé → on réécrit les entrées de cache HTTP de TOUTES nos pages
// (fetch cache:'reload' = requête réseau inconditionnelle qui REMPLACE
// l'entrée en cache), puis on recharge le dashboard : la passe suivante
// charge la version fraîche. Une copie PÉRIMÉE de ce module s'auto-détecte
// (sa version gravée est l'ancienne) — c'est le point clé du mécanisme.
define([], function () {
    "use strict";

    // Stamp du build (généré) — source de vérité : <Version> du .csproj.
    var VERSION = "__PLUGIN_VERSION__";

    // Toutes les ressources plugin servies par web/ConfigurationPage
    // (voir GetPages dans Plugin.cs) : le fetch cache:'reload' doit couvrir
    // HTML + JS + i18n + fond + ce module, sinon la recharge retombe sur une
    // ressource sœur périmée.
    var PAGE_NAMES = [
        "LLMAIConfigPage", "LLMAIConfigPageJS", "LLMAII18n",
        "LLMAIRecommendationsPage", "LLMAIRecommendationsPageJS",
        "LLMAIChatPage", "LLMAIChatPageJS", "LLMAIBg", "LLMAIAssetVersion"
    ];

    // Garde anti-boucle : une seule auto-correction par version serveur et
    // par session. Si le navigateur ignore cache:'reload' (cas rare), le
    // second check voit encore un écart → bandeau « rechargement dur » au
    // lieu d'un reload infini. La valeur (version serveur) fait expirer le
    // drapeau dès qu'une NOUVELLE version est déployée.
    var RELOADED_FLAG = "LLMAI.versionReloaded";

    // Bandeau quand l'auto-correction a échoué (cache:'reload' ignoré) :
    // seul un rechargement dur (Ctrl+Shift+R) rafraîchira les ressources.
    function showStaleBanner() {
        if (document.getElementById("LLMAI-stale-banner")) return;
        var d = document.createElement("div");
        d.id = "LLMAI-stale-banner";
        d.style.cssText = "position:fixed;top:0;left:0;right:0;z-index:9999;" +
            "background:#b35900;color:#fff;padding:8px 12px;font-size:13px;" +
            "text-align:center;box-shadow:0 2px 6px rgba(0,0,0,.4)";
        d.textContent = "LLM AI : nouvelle version détectée — rechargez la page " +
            "en dur (Ctrl+Shift+R) pour l'appliquer.";
        document.body.appendChild(d);
    }

    // Compare la version de ce build à celle du serveur. Retourne une
    // promesse : false = à jour (ou vérification impossible — silencieux),
    // "reloading" = page sur le point d'être rechargée, "stale" = écart
    // persistant après une tentative → bandeau. Best-effort : AUCUNE erreur
    // ne doit perturber le rendu de la page hôte.
    function checkForUpdate(apiClient, opts) {
        opts = opts || {};
        var req;
        try {
            req = apiClient.ajax({
                url: apiClient.getUrl("Plugins/LLMAI/Version"),
                type: "GET",
                dataType: "json"
            });
        } catch (e) { return Promise.resolve(false); }
        return req.then(function (srv) {
            var serverVersion = srv && srv.Version ? String(srv.Version) : "";
            // Pas de version côté serveur (endpoint absent/ancien, erreur) :
            // on ne fait rien — la page reste pleinement fonctionnelle.
            if (!serverVersion || serverVersion === VERSION) return false;

            var alreadyTried = false;
            try {
                alreadyTried = sessionStorage.getItem(RELOADED_FLAG) === serverVersion;
            } catch (e) { /* sessionStorage indisponible : on tente le reload */ }

            if (alreadyTried) { showStaleBanner(); return "stale"; }

            // Réécriture des entrées de cache HTTP de toutes nos pages.
            // web/ConfigurationPage répond à un GET simple, sans auth (le
            // require() du dashboard fait de même) — pas de token ici.
            var refreshes = PAGE_NAMES.map(function (n) {
                var u = apiClient.getUrl("web/ConfigurationPage", { name: n });
                return fetch(u, { cache: "reload" }).catch(function () {});
            });
            return Promise.all(refreshes).then(function () {
                try { sessionStorage.setItem(RELOADED_FLAG, serverVersion); } catch (e) {}
                if (opts.noReload) return "reloading"; // test : pas de reload
                window.location.reload();
                return "reloading";
            });
        }, function () { return false; });
    }

    return { version: VERSION, checkForUpdate: checkForUpdate };
});