using NetShareHub.Application.Contracts.Services;
using NetShareHub.Application.Services;
using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Entities;
using NetShareHub.Domain.Enums;
using NetShareHub.Domain.Exceptions;
using NetShareHub.Infrastructure.Repositories;
using NetShareHub.Infrastructure.Services;

namespace NetShareHub.Tests.Unit;

public class InternetSharingServiceTests
{
    private static readonly NetworkAdapter Wifi = TestAdapters.Make("wifi", "Wi-Fi", AdapterKind.WiFi);
    private static readonly NetworkAdapter Lan = TestAdapters.Make("lan", "Ethernet", ip: null, gateway: null);

    private sealed class FakeBackend : ISharingBackend
    {
        public Exception? FailWith { get; set; }

        public int Started { get; private set; }

        public int Stopped { get; private set; }

        public bool Recovered { get; set; }

        public Task<BackendCapabilities> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(TestAdapters.Caps(HostPlatform.Windows));

        public Task StartAsync(SharingPlan plan, CancellationToken cancellationToken = default)
        {
            Started++;
            return FailWith is null ? Task.CompletedTask : Task.FromException(FailWith);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Stopped++;
            return Task.CompletedTask;
        }

        public Task<bool> RecoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(Recovered);
    }

    private sealed class FakePrivilege(bool elevated) : IPrivilegeService
    {
        public bool IsElevated { get; } = elevated;

        public Task<bool> TryRelaunchElevatedAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class FakePlatform : IPlatformInfo
    {
        public HostPlatform Platform => HostPlatform.Windows;

        public string Description => "Test OS";
    }

    private static (InternetSharingService Service, FakeBackend Backend) Create(bool elevated = true)
    {
        var backend = new FakeBackend();
        var service = new InternetSharingService(
            new FakeInventory(Wifi, Lan), backend, new FakePrivilege(elevated), new FakePlatform(), new SharingPlanner(),
            Logs.For<InternetSharingService>());
        return (service, backend);
    }

    private static SharingRequest_InDto Request() => new() { SourceAdapterIds = { "wifi" }, TargetAdapterIds = { "lan" } };

    [Fact]
    public async Task Sans_droits_administrateur_le_partage_est_refuse_avant_toute_modification()
    {
        var (service, backend) = Create(elevated: false);

        await Assert.ThrowsAsync<PrivilegeRequiredException>(() => service.StartAsync(Request()));

        Assert.Equal(0, backend.Started);
        Assert.Equal(RunState.Stopped, service.Status.State);
    }

    [Fact]
    public async Task Demarrage_puis_arret_publient_les_bons_etats()
    {
        var (service, backend) = Create();
        var states = new List<RunState>();
        service.StatusChanged += (_, s) => states.Add(s.State);

        await service.StartAsync(Request());
        Assert.Equal(RunState.Running, service.Status.State);
        Assert.Equal(new[] { "Wi-Fi" }, service.Status.Sources);
        Assert.Equal(new[] { "Ethernet" }, service.Status.Targets);

        await service.StopAsync();
        Assert.Equal(RunState.Stopped, service.Status.State);
        Assert.Equal(new[] { RunState.Starting, RunState.Running, RunState.Stopping, RunState.Stopped }, states);
        Assert.Equal(1, backend.Stopped);
    }

    [Fact]
    public async Task Double_demarrage_est_refuse()
    {
        var (service, backend) = Create();
        await service.StartAsync(Request());

        var ex = await Assert.ThrowsAsync<SharingException>(() => service.StartAsync(Request()));

        Assert.Contains("déjà actif", ex.Message);
        Assert.Equal(1, backend.Started);
    }

    [Fact]
    public async Task Echec_du_moteur_passe_en_Faulted_avec_le_message_et_permet_de_reessayer()
    {
        var (service, backend) = Create();
        backend.FailWith = new SharingException("NAT impossible");

        var ex = await Assert.ThrowsAsync<SharingException>(() => service.StartAsync(Request()));

        Assert.Equal("NAT impossible", ex.Message);
        Assert.Equal(RunState.Faulted, service.Status.State);
        Assert.Equal("NAT impossible", service.Status.Message);

        backend.FailWith = null;
        await service.StartAsync(Request());
        Assert.Equal(RunState.Running, service.Status.State);
    }

    [Fact]
    public async Task Une_demande_invalide_est_refusee_par_le_planificateur_sans_toucher_au_moteur()
    {
        var (service, backend) = Create();

        await Assert.ThrowsAsync<SharingException>(() => service.StartAsync(new SharingRequest_InDto()));

        Assert.Equal(0, backend.Started);
        Assert.Equal(RunState.Faulted, service.Status.State);
    }

    [Fact]
    public async Task Arreter_un_partage_inactif_ne_fait_rien()
    {
        var (service, backend) = Create();

        await service.StopAsync();

        Assert.Equal(0, backend.Stopped);
    }

    [Fact]
    public async Task Infos_plateforme_reprennent_les_capacites_du_moteur()
    {
        var (service, _) = Create();

        var info = await service.GetPlatformInfoAsync();

        Assert.Equal(HostPlatform.Windows, info.Platform);
        Assert.True(info.IsElevated);
        Assert.True(info.SupportsWifiHotspot);
    }

    [Fact]
    public async Task Recover_est_ignore_sans_droits_et_delegue_sinon()
    {
        var (unprivileged, _) = Create(elevated: false);
        Assert.False(await unprivileged.RecoverAsync());

        var (privileged, backend) = Create();
        backend.Recovered = true;
        Assert.True(await privileged.RecoverAsync());
    }
}

public class SettingsAndSystemTests
{
    [Fact]
    public async Task Les_preferences_font_l_aller_retour_sur_disque()
    {
        using var dir = new TempDirectory();
        var repository = new JsonAppSettingsRepository(Logs.For<JsonAppSettingsRepository>(), dir.Path);
        var settings = new AppSettings_InDto
        {
            HotspotEnabled = true,
            Sharing =
            {
                SourceAdapterIds = { "a", "b" },
                TargetAdapterIds = { "c" },
                Hotspot = new WifiHotspot_InDto { Ssid = "Maison", Password = "motdepasse1", Band = WifiBand.Band5GHz },
            },
            Dispatch = { Port = 8080, Strategy = DispatchStrategy.LeastConnections, AllowLan = true, Uplinks = { new DispatchUplink_InDto { AdapterId = "a", Weight = 4 } } },
        };

        await repository.SaveAsync(settings);
        var loaded = await new JsonAppSettingsRepository(Logs.For<JsonAppSettingsRepository>(), dir.Path).GetAsync();

        Assert.True(loaded.HotspotEnabled);
        Assert.Equal(new[] { "a", "b" }, loaded.Sharing.SourceAdapterIds);
        Assert.Equal(WifiBand.Band5GHz, loaded.Sharing.Hotspot!.Band);
        Assert.Equal(8080, loaded.Dispatch.Port);
        Assert.Equal(DispatchStrategy.LeastConnections, loaded.Dispatch.Strategy);
        Assert.Equal(4, loaded.Dispatch.Uplinks.Single().Weight);
        Assert.Contains("LeastConnections", File.ReadAllText(Path.Combine(dir.Path, "settings.json"))); // enums lisibles
    }

    [Fact]
    public async Task Preferences_absentes_ou_corrompues_donnent_les_valeurs_par_defaut()
    {
        using var dir = new TempDirectory();
        var repository = new JsonAppSettingsRepository(Logs.For<JsonAppSettingsRepository>(), dir.Path);

        var missing = await repository.GetAsync();
        Assert.Equal(1080, missing.Dispatch.Port);

        await File.WriteAllTextAsync(Path.Combine(dir.Path, "settings.json"), "{ ceci n'est pas du json");
        var corrupted = await repository.GetAsync();
        Assert.Equal(1080, corrupted.Dispatch.Port);
    }

    [Fact]
    public async Task L_inventaire_reel_renvoie_des_adaptateurs_coherents()
    {
        using var inventory = new NetworkInventoryService(Logs.For<NetworkInventoryService>());

        var adapters = await inventory.GetAdaptersAsync();

        Assert.NotEmpty(adapters);
        Assert.All(adapters, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Id));
            Assert.False(string.IsNullOrWhiteSpace(a.Name));
        });
        Assert.Equal(adapters.Count, inventory.Snapshot.Count);
        // Une source valide a forcément une IPv4 et une passerelle.
        Assert.All(adapters.Where(a => a.HasInternetRoute), a =>
        {
            Assert.NotNull(a.IPv4Address);
            Assert.NotNull(a.Gateway);
        });
        // La boucle locale n'est jamais proposée comme source.
        Assert.DoesNotContain(adapters, a => a.IsLoopback && a.HasInternetRoute);
    }

    [Fact]
    public async Task Le_lanceur_de_commandes_capture_la_sortie_et_signale_les_erreurs()
    {
        var runner = new CommandRunner(Logs.For<CommandRunner>());

        var ok = await runner.RunAsync(CommandSpec.Of("dotnet", "--version"));
        Assert.True(ok.Success);
        Assert.Matches(@"^\d+\.\d+\.\d+", ok.StdOut);

        var missing = await runner.RunAsync(CommandSpec.Of("commande-inexistante-netsharehub"));
        Assert.Equal(127, missing.ExitCode);
        Assert.Contains("introuvable", missing.StdErr);
    }

    [Fact]
    public async Task Le_lanceur_de_commandes_arrete_une_commande_trop_longue()
    {
        var runner = new CommandRunner(Logs.For<CommandRunner>());
        var slow = OperatingSystem.IsWindows()
            ? new CommandSpec("powershell.exe", new[] { "-NoProfile", "-Command", "Start-Sleep 30" }, TimeoutSeconds: 1)
            : new CommandSpec("sleep", new[] { "30" }, TimeoutSeconds: 1);

        var started = DateTime.UtcNow;
        var result = await runner.RunAsync(slow);

        Assert.Equal(124, result.ExitCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task PowerShell_encode_gere_apostrophes_accents_et_ne_pollue_pas_stderr()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new CommandRunner(Logs.For<CommandRunner>());

        var result = await runner.RunAsync(CommandSpec.PowerShell("Write-Output 'L''été à Paris'; Write-Output ([string]'ok')", "test"));

        Assert.True(result.Success, result.StdErr);
        Assert.Contains("L'été à Paris", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task PowerShell_une_erreur_donne_un_code_non_nul_et_un_message()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new CommandRunner(Logs.For<CommandRunner>());

        var result = await runner.RunAsync(CommandSpec.PowerShell("throw 'Connexion introuvable : Fantôme'", "test"));

        Assert.False(result.Success);
        // Le message exact, sans enveloppe CLIXML ni position dans le script.
        Assert.Equal("Connexion introuvable : Fantôme", result.StdErr);
    }

    [Fact]
    public void CLIXML_est_decode_et_la_progression_ignoree()
    {
        const string progressOnly = "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">" +
                                    "<Obj S=\"progress\" RefId=\"0\"><MS><I64 N=\"SourceId\">1</I64></MS></Obj></Objs>";
        const string error = "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">" +
                             "<S S=\"Error\">Le service est d_x00E9_sactiv_x00E9_ &amp; absent_x000D__x000A_</S>" +
                             "<S S=\"Error\">At line:1 char:5_x000D__x000A_</S></Objs>";

        Assert.Equal(string.Empty, CommandRunner.CleanStdErr(progressOnly));
        Assert.Equal("Le service est désactivé & absent", CommandRunner.CleanStdErr(error));
        Assert.Equal("erreur simple", CommandRunner.CleanStdErr("  erreur simple \n"));
    }
}
