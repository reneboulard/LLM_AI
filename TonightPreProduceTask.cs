using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Emby.Notifications;

namespace LLM_AI
{
    /// <summary>
    /// Tâche planifiée de <b>production anticipée</b> « AI Tonight » : produit,
    /// pour chaque usager non ignoré, les recommandations « À regarder ce soir »
    /// <b>en avance</b> (déclencheur quotidien par défaut 17 h — réglable dans
    /// le planificateur Emby). Le résultat atterrit dans le cache par usager de
    /// <see cref="TonightService"/> ; au login suivant, le déclencheur
    /// <see cref="TonightLoginService"/> sert ce cache frais d'un toast
    /// <b>immédiat</b> (chemin existant « cache frais → toast immédiat ») — le
    /// run LLM (30-60 s, voire plusieurs minutes sur un backend lent) devient
    /// invisible pour l'usager.
    /// <para>Raison d'être du toast instantané : le popup « À regarder ce soir »
    /// a le plus d'impact juste après le login, AVANT que l'usager n'explore
    /// les sections de l'interface Emby — c'est là qu'il oriente sa soirée.</para>
    /// <para>Complétude des données anticipées : à l'heure du pré-run, la grille
    /// EPG du soir est complète (jours de grille en avance) et les
    /// enregistrements du soir sont représentés par leurs timers déjà
    /// programmés — le bloc « ENREGISTREMENTS À VENIR CE SOIR »
    /// (<see cref="TonightService"/> / <see
    /// cref="TonightService.BuildUpcomingRecordings"/>... bloc injecté dans
    /// le même prompt) donne au LLM l'équivalent de ce qu'un run au moment du
    /// login verrait. Les enregistrements complétés entre le pré-run et le
    /// login restent la seule classe manquante — couverte par leurs timers.</para>
    /// <para><b>Lead-time d'auto-programmation</b> : la tâche crée les timers
    /// du record bucket 2-3 h plus tôt qu'un run à 19 h 30 — les recos des
    /// diffusions 17 h 45-19 h 30 deviennent enregistrables (un run au login
    /// ne peut plus capter ce qui a déjà commencé).</para>
    /// <para>Symétrie exacte avec le chemin login (piège vérifié dans
    /// <see cref="TonightLoginService"/> : le chemin « cache frais au login »
    /// <b>saute</b> l'auto-programmation ET la cloche par conception — il les
    /// assume déjà faites au premier run) : la tâche DOIT donc exécuter
    /// elle-même (1) l'auto-programmation (gated
    /// <see cref="PluginConfiguration.AutoProgram"/>, même
    /// <see cref="AutoProgrammer"/> que le login — 4e surface après
    /// Activate/planifié/chat de la v2.6.6) et (2) la cloche persistante à
    /// l'heure du pré-run (le toast reste au login — chaque canal à son
    /// meilleur moment : le courriel/notifier AVANT la soirée, le rappel
    /// éphémère à l'arrivée sur le client).</para>
    /// <para>Gates et garde-fous :
    /// <list type="bullet">
    /// <item>No-op si <see cref="PluginConfiguration.TonightEnabled"/> est
    /// décoché (la tâche reste visible/planifiée, comme
    /// <see cref="AiTonightCleanupTask"/>).</item>
    /// <item>Comptes ignorés (<see cref="TonightService.IgnoredUserKeys"/>)
    /// sautés — même liste que le déclencheur login (v2.2.0).</item>
    /// <item>Cache frais (produit aujourd'hui) → SAUTÉ sans re-run ni cloche
    /// (un run login/chat a déjà produit ; pas de double coût LLM).</item>
    /// <item>Garde-fou in-flight partagé
    /// (<see cref="TonightLoginService.TryBeginUserRun"/>) : un login ou un
    /// second appareil simultané ne doublent pas le run.</item>
    /// <item>Hint TTL : la tâche vérifie que
    /// <see cref="PluginConfiguration.TonightCacheHours"/> couvre pré-run →
    /// fin de fenêtre de grande écoute (sinon le login re-produirait quand
    /// même — Warn greppable avec la valeur suggérée).</item>
    /// <item>La fenêtre de grande écoute n'est PAS re-vérifiée : la tâche est
    /// une demande explicite du serveur (comme l'outil chat
    /// <c>run_tonight_run</c> et le bouton « Rafraîchir »), pas un
    /// déclencheur login/page — l'invariant « hors fenêtre, login/page
    /// inertes » reste intact.</item>
    /// </list></para>
    /// <para>Découverte par scanning d'assembly (comme
    /// <see cref="AiTonightCleanupTask"/>) — aucune inscription dans
    /// <c>Plugin.cs</c>. Services Emby injectés par DI.</para>
    /// </summary>
    public class TonightPreProduceTask : IScheduledTask
    {
        private readonly IUserManager _users;
        private readonly ILibraryManager _library;
        private readonly ILiveTvManager _liveTv;
        private readonly IJsonSerializer _json;
        private readonly IServerApplicationHost _host;
        private readonly INotificationManager _notifications;
        private readonly ILogger _logger;
        private readonly ICollectionManager _collections;
        private readonly IPlaylistManager _playlists;
        private readonly IUserDataManager _userData;

        public TonightPreProduceTask(IUserManager users, ILibraryManager library,
            ILiveTvManager liveTv, IJsonSerializer json, IServerApplicationHost host,
            INotificationManager notifications, ILogger logger,
            ICollectionManager collections, IPlaylistManager playlists,
            IUserDataManager userData)
        {
            _users = users;
            _library = library;
            _liveTv = liveTv;
            _json = json;
            _host = host;
            _notifications = notifications;
            _logger = logger;
            _collections = collections;
            _playlists = playlists;
            _userData = userData;
        }

        public string Name => I18n.S("task.preproduce.name", I18n.ResolveDisplayLangKey(_host));

        /// <summary>Identifiant stable de la tâche.</summary>
        public string Key => "6d1f4a97-3b52-4c8e-a0d9-7e4c5b8f1a26";

        public string Description => I18n.S("task.preproduce.desc", I18n.ResolveDisplayLangKey(_host));

        public string Category => I18n.S("task.category", I18n.ResolveDisplayLangKey(_host));

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        // ------------------------------------------------------------------
        //  Exécution
        // ------------------------------------------------------------------

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null || !cfg.TonightEnabled)
            {
                _logger?.Info("[LLM_AI] Production anticipée : « À regarder ce soir » désactivé (TonightEnabled=false) → no-op.");
                return;
            }

            // Hint TTL AVANT de produire : un cache trop court annulerait le
            // bénéfice (le login re-produirait quand même, cache expiré).
            LogCacheCoverageHint(cfg);

            List<User> users;
            try
            {
                users = _users.GetUserList(new UserQuery()).ToList();
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] Production anticipée : récupération des usagers impossible — {0}", ex, ex.Message);
                return;
            }
            if (users.Count == 0)
            {
                _logger?.Info("[LLM_AI] Production anticipée : aucun usager — no-op.");
                return;
            }

            // Même liste d'ignorés que le déclencheur login (v2.2.0) : les
            // comptes techniques/admins ignorés ne consomment ni run LLM ni
            // cloche.
            var ignored = TonightService.IgnoredUserKeys(_users, cfg, _logger);

            int produced = 0, cachedSkip = 0, failed = 0, skipped = 0;
            int done = 0;
            foreach (var user in users)
            {
                if (cancellationToken.IsCancellationRequested) break;
                done++;
                progress?.Report(5 + 90.0 * (done - 1) / users.Count);

                if (user == null) continue;
                if (ignored.Contains(TonightService.NormUserName(user.Name)))
                {
                    skipped++;
                    _logger?.Info("[LLM_AI] Production anticipée : « {0} » ignoré (liste des comptes ignorés) → sauté.", user.Name);
                    continue;
                }

                // Garde-fou in-flight PARTAGÉ avec le login : un run déjà en
                // vol pour cet usager (login, autre appareil) → on saute (le
                // premier arrivé produit ; pas de double run LLM).
                if (!TonightLoginService.TryBeginUserRun(user.Id.ToString()))
                {
                    skipped++;
                    _logger?.Info("[LLM_AI] Production anticipée : run Tonight déjà en cours pour « {0} » → sauté.", user.Name);
                    continue;
                }

                try
                {
                    var svc = new TonightService(_users, _library, _liveTv, _json, _host, _logger, _collections, _playlists, _userData);
                    var res = await svc.GenerateTonightAsync(user, cfg, refresh: false, cancellationToken)
                        .ConfigureAwait(false);

                    // Cache frais (produit aujourd'hui par login/chat/page) :
                    // SAUTER — la production existe déjà, la cloche a été (ou
                    // sera) envoyée par le chemin producteur d'origine. Pas de
                    // double coût LLM, pas de double cloche.
                    if (res.FromCache)
                    {
                        cachedSkip++;
                        _logger?.Info("[LLM_AI] Production anticipée : « {0} » cache Tonight frais → sauté (déjà produit).", user.Name);
                        continue;
                    }

                    if (res.Error != null || string.IsNullOrEmpty(res.Payload))
                    {
                        failed++;
                        _logger?.Info("[LLM_AI] Production anticipée : « {0} » run sans résultat ({1}).", user.Name, res.Error ?? "vide");
                        continue;
                    }

                    // 1) Auto-programmation du record bucket — GATING ABSOLU
                    //    (aucun timer tant que cfg.AutoProgram == false) et
                    //    SYMÉTRIE avec le login : le chemin « cache frais au
                    //    login » saute la programmation PAR CONCEPTION (il
                    //    l'assume faite au premier run) — si la tâche ne
                    //    programmait pas, personne ne le ferait. C'est le
                    //    lead-time de la v2.7.0 : les recos des diffusions
                    //    17 h 45-19 h 30 deviennent enregistrables.
                    int programmed = -1;
                    if (cfg.AutoProgram)
                    {
                        try
                        {
                            var ap = new AutoProgrammer(_liveTv, _library, _logger, _host);
                            var stats = await ap.Program(res.Payload, user, cfg, cancellationToken).ConfigureAwait(false);
                            programmed = stats.Programmed;
                        }
                        catch (Exception ex)
                        {
                            _logger?.ErrorException("[LLM_AI] Auto-program (production anticipée « {0} ») : {1}", ex, user.Name, ex.Message);
                        }
                    }

                    // 2) Cloche persistante à l'heure du pré-run (décision
                    //    usager v2.7.0) : le digest complet (une ligne par reco
                    //    + raison) part AVANT la soirée via les notifiers
                    //    configurés ; le toast éphémère, lui, partira du login
                    //    (cache frais → toast immédiat). Même forme de texte
                    //    que la cloche du chemin login.
                    SendBell(user, TonightLoginService.BuildBellText(res.Payload, programmed));

                    produced++;
                    _logger?.Info("[LLM_AI] Production anticipée : « {0} » produit ({1} reco(s){2}).",
                        user.Name, LlmRunner.CountRecommendations(res.Payload),
                        programmed > 0 ? $", {programmed} programmé(s)" : string.Empty);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    _logger?.ErrorException("[LLM_AI] Production anticipée « {0} » : {1}", ex, user.Name, ex.Message);
                }
                finally
                {
                    TonightLoginService.EndUserRun(user.Id.ToString());
                }
            }

            progress?.Report(100);
            _logger?.Info("[LLM_AI] Production anticipée : bilan — {0} produit(s), {1} cache frais (sauté(s)), {2} échec(s), {3} sauté(s).",
                produced, cachedSkip, failed, skipped);
        }

        // ------------------------------------------------------------------
        //  Hint TTL (couverture du cache pré-run → fin de fenêtre)
        // ------------------------------------------------------------------

        /// <summary>
        /// Vérifie que <see cref="PluginConfiguration.TonightCacheHours"/>
        /// couvre la distance pré-run → fin de fenêtre de grande écoute. Un
        /// cache trop court = le login du soir re-produirait quand même (cache
        /// expiré) et le bénéfice du pré-run serait perdu. Warn greppable avec
        /// la valeur suggérée — jamais une exception (la tâche produit quand
        /// même ; le repli login reste le comportement actuel).
        /// </summary>
        private void LogCacheCoverageHint(PluginConfiguration cfg)
        {
            int cacheHours = Math.Max(0, cfg.TonightCacheHours);
            if (cacheHours <= 0)
            {
                _logger?.Warn("[LLM_AI] Production anticipée : cache désactivé (TonightCacheHours=0) — le pré-run ne sera PAS servi au login (re-run à chaque login). Règle TonightCacheHours pour couvrir la soirée.");
                return;
            }
            if (!TonightService.TryParseProductionWindow(cfg, out _, out var wEnd))
                return; // fenêtre vide/24 h : pas de borne de fin mesurable — pas de hint

            var now = DateTimeOffset.Now;
            var endDt = now.Date.Add(wEnd);
            // Fenêtre franchissant minuit (ou fin déjà passée aujourd'hui) :
            // la fin pertinente est la prochaine occurrence.
            if (endDt <= now) endDt = endDt.AddDays(1);

            var span = endDt - now;
            if (span > TimeSpan.FromHours(cacheHours))
                _logger?.Warn("[LLM_AI] Production anticipée : TonightCacheHours={0} ne couvre pas la fin de fenêtre de grande écoute ({1:hh\\:mm}, dans {2:hh\\:mm}) — le login du soir re-produirait (cache expiré). Suggestion : TonightCacheHours ≥ {3}.",
                    cacheHours, wEnd, span, (int)Math.Ceiling(span.TotalHours));
        }

        // ------------------------------------------------------------------
        //  Cloche persistante (notification Emby — repli du pattern
        //  TonightLoginService.SendBellNotification, v2.4.5/2.4.7)
        // ------------------------------------------------------------------

        /// <summary>
        /// Envoie la notification persistante « À regarder ce soir » à
        /// l'usager produit — même construction que
        /// <c>TonightLoginService.SendBellNotification</c> (8e site
        /// SendNotification du plugin, audit v2.4.7) : deep-link
        /// <see cref="NotificationRequest.Url"/> ET URL nue ajoutée à la fin
        /// de la description (v2.4.5 : les canaux de lecture — webhooks,
        /// courriel — ignorent le champ Url), LF uniquement. Livrée par les
        /// notifiers configurés de l'usager (ex. SMTP).
        /// </summary>
        private void SendBell(User user, string text)
        {
            if (_notifications == null || user == null || string.IsNullOrWhiteSpace(text)) return;
            try
            {
                string url = ResolveEmbyUrl();
                if (!string.IsNullOrWhiteSpace(url))
                    text = text + "\n" + url;
                var req = new NotificationRequest
                {
                    // 🤖 en préfixe du titre : même marque que les popups et
                    // la cloche du chemin login.
                    Title = "🤖 LLM AI — À regarder ce soir",
                    Description = text,
                    Url = url,
                    Date = DateTimeOffset.UtcNow,
                    Severity = LogSeverity.Info,
                    User = user,
                };
                _notifications.SendNotification(req);
                _logger?.Info("[LLM_AI] Cloche (production anticipée) envoyée à « {0} » — lien : {1}.", user.Name, url ?? "(aucun)");
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] Cloche (production anticipée) à « {0} » échouée : {1}", ex, user.Name, ex.Message);
            }
        }

        /// <summary>
        /// Résout l'URL de base d'Emby pour le deep-link (aucune IP codée).
        /// Repli sur <see cref="PluginConfiguration.EmbyPublicUrl"/> —
        /// identique à <c>TonightLoginService.ResolveEmbyUrl</c> et
        /// <c>LlmScheduledTask.ResolveEmbyUrl</c>.
        /// </summary>
        private string ResolveEmbyUrl()
        {
            try
            {
                if (_host != null)
                {
                    string local = _host.GetLocalHostApiUrl();
                    if (!string.IsNullOrWhiteSpace(local)) return local;
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] ResolveEmbyUrl : GetLocalHostApiUrl a échoué ({0}) — repli sur EmbyPublicUrl.", ex.Message);
            }
            return (Plugin.Instance?.Configuration?.EmbyPublicUrl ?? string.Empty).Trim();
        }

        // ------------------------------------------------------------------
        //  Déclencheur par défaut
        // ------------------------------------------------------------------

        /// <summary>
        /// Trigger par défaut : quotidien à 17 h — assez tôt pour le lead-time
        /// d'auto-programmation (diffusions 17 h 45+ enregistrables) et assez
        /// tard pour un profil de goût encore frais, AVANT la fenêtre de grande
        /// écoute usuelle (le pré-run est une demande explicite du serveur —
        /// hors fenêtre par conception, comme l'outil chat). Réglable dans le
        /// planificateur Emby sans toucher au plugin.
        /// </summary>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = "DailyTrigger",
                // 17 h — le run LLM de chaque usager (30-60 s et plus) est
                // absorbé ici ; le toast du login devient immédiat.
                TimeOfDayTicks = new TimeSpan(17, 0, 0).Ticks
            };
        }
    }
}