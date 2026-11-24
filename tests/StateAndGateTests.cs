using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LLM_AI.Tests
{
    public class AtomicFileTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "llmai-tests-" + Guid.NewGuid().ToString("N"));
        public AtomicFileTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void Ecrit_puis_remplace_sans_laisser_de_temporaire()
        {
            var p = Path.Combine(_dir, "state.json");
            AtomicFile.WriteAllText(p, "{\"v\":1}");
            AtomicFile.WriteAllText(p, "{\"v\":2}");
            Assert.Equal("{\"v\":2}", File.ReadAllText(p));
            Assert.Equal(new[] { p }, Directory.GetFiles(_dir));
        }

        [Fact]
        public void Echec_d_ecriture_ne_laisse_pas_de_temporaire_et_garde_l_ancien()
        {
            var p = Path.Combine(_dir, "state.json");
            File.WriteAllText(p, "ancien");
            // Cible = un RÉPERTOIRE : le renommage échoue après l'écriture du temporaire.
            var dirAsTarget = Path.Combine(_dir, "cible");
            Directory.CreateDirectory(dirAsTarget);
            Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(dirAsTarget, "x"));
            Assert.DoesNotContain(Directory.GetFiles(_dir), f => f.EndsWith(".tmp"));
            Assert.Equal("ancien", File.ReadAllText(p));
        }

        [Fact]
        public void Quarantine_met_le_fichier_de_cote()
        {
            var p = Path.Combine(_dir, "state.json");
            File.WriteAllText(p, "{corrompu");
            Assert.True(AtomicFile.Quarantine(p));
            Assert.False(File.Exists(p));
            Assert.Single(Directory.GetFiles(_dir, "state.json.corrupt-*"));
        }

        [Fact]
        public void Quarantine_fichier_absent_retourne_false()
            => Assert.False(AtomicFile.Quarantine(Path.Combine(_dir, "absent.json")));
    }

    [CollectionDefinition("etat-statique", DisableParallelization = true)]
    public class EtatStatique { }

    /// <summary>LlmTaskGate est un état statique : tests sérialisés.</summary>
    [Collection("etat-statique")]
    public class LlmTaskGateTests
    {
        private static Task Within(Task t, int ms = 3000)
            => Task.WhenAny(t, Task.Delay(ms)).ContinueWith(w =>
            {
                if (w.Result != t) throw new TimeoutException("la porte est restée bloquée");
                return t;
            }).Unwrap();

        [Fact]
        public async Task Un_attendant_annule_ne_consomme_pas_le_slot()
        {
            await LlmTaskGate.AcquireAsync("A", null, CancellationToken.None);          // capacité 1 : pleine
            using var cts = new CancellationTokenSource();
            var waiter = LlmTaskGate.AcquireAsync("B", null, cts.Token);                 // attend
            Assert.False(waiter.IsCompleted);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
            LlmTaskGate.Release("A");                                                    // libère : B parti
            // Si le slot avait fuité, ce passage resterait bloqué à jamais.
            await Within(LlmTaskGate.AcquireAsync("C", null, CancellationToken.None));
            LlmTaskGate.Release("C");
        }

        [Fact]
        public async Task Un_attendant_vivant_est_reveille_a_la_liberation()
        {
            await LlmTaskGate.AcquireAsync("A", null, CancellationToken.None);
            var waiter = LlmTaskGate.AcquireAsync("B", null, CancellationToken.None);
            Assert.False(waiter.IsCompleted);
            LlmTaskGate.Release("A");
            await Within(waiter);
            LlmTaskGate.Release("B");
        }
    }

    [Collection("etat-statique")]
    public class ChatActionStoreTests
    {
        [Fact]
        public void Consume_proprietaire_session_et_usage_unique()
        {
            var sid = "t-" + Guid.NewGuid().ToString("N");
            var a = ChatActionStore.Create(sid, "alice", "outil_test", "{}", "libellé");
            Assert.NotNull(a);
            Assert.Null(ChatActionStore.Consume(a.ActionId, sid, "mallory"));      // autre usager
            Assert.Null(ChatActionStore.Consume(a.ActionId, "autre-session", "alice")); // autre session
            Assert.NotNull(ChatActionStore.Consume(a.ActionId, sid, "alice"));     // bon propriétaire
            Assert.Null(ChatActionStore.Consume(a.ActionId, sid, "alice"));        // déjà consommée
        }

        [Fact]
        public void Create_sans_outil_ou_sans_arguments_retourne_null()
        {
            Assert.Null(ChatActionStore.Create("s", "u", "", "{}", "l"));
            Assert.Null(ChatActionStore.Create("s", "u", "outil", null, "l"));
        }
    }

    public class RecordingVolumeTests
    {
        [Theory]
        [InlineData("/mnt/rec/serie/x.ts", "/mnt/rec", true)]
        [InlineData("/mnt/rec", "/mnt/rec", true)]
        [InlineData("/mnt/record/x.ts", "/mnt/rec", false)]  // préfixe de chaîne ≠ sous le volume
        [InlineData("F:\\Rec\\x.ts", "F:\\", true)]           // nom de volume Windows avec séparateur
        [InlineData("/data/x.ts", "/", true)]
        [InlineData("/data/x.ts", "", false)]
        public void IsUnderVolume(string path, string volume, bool expected)
            => Assert.Equal(expected, RecordingDiskManager.IsUnderVolume(path, volume));
    }
}
