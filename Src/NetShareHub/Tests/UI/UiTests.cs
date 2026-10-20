using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using NetShareHub.Application.Contracts.Persistence;
using NetShareHub.Application.Contracts.Services;
using NetShareHub.Desktop.Logging;
using NetShareHub.Desktop.ViewModels;
using NetShareHub.Desktop.Views;
using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Dtos.Outgoing;
using NetShareHub.Domain.Entities;
using NetShareHub.Domain.Enums;

namespace NetShareHub.Tests.UI;

internal sealed class FakeSharingService : IInternetSharingService
{
    public SharingStatus_OutDto Status { get; private set; } = new() { State = RunState.Stopped, Message = "Partage arrêté." };

    public SharingRequest_InDto? LastRequest { get; private set; }

    public PlatformInfo_OutDto Platform { get; set; } = new()
    {
        Platform = HostPlatform.Windows,
        OsDescription = "Windows",
        IsElevated = true,
        SupportsWifiHotspot = true,
    };

    public event EventHandler<SharingStatus_OutDto>? StatusChanged;

    public Task<PlatformInfo_OutDto> GetPlatformInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(Platform);

    public Task StartAsync(SharingRequest_InDto request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        Push(new SharingStatus_OutDto
        {
            State = RunState.Running,
            Message = "Partage actif.",
            Sources = request.SourceAdapterIds,
            Targets = request.TargetAdapterIds,
            HotspotSsid = request.Hotspot?.Ssid,
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Push(new SharingStatus_OutDto { State = RunState.Stopped, Message = "Partage arrêté." });
        return Task.CompletedTask;
    }

    public Task<bool> RecoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public void Push(SharingStatus_OutDto status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}

internal sealed class FakeDispatchService : IDispatchService
{
    public DispatchStatus_OutDto Status { get; set; } = new() { State = RunState.Stopped };

    public DispatchConfig_InDto? LastConfig { get; private set; }

    public DispatchStatus_OutDto GetStatus() => Status;

    public Task StartAsync(DispatchConfig_InDto config, CancellationToken cancellationToken = default)
    {
        LastConfig = config;
        Status = new DispatchStatus_OutDto
        {
            State = RunState.Running,
            ListenEndpoint = "0.0.0.0:1080",
            ClientEndpoints = new List<string> { "127.0.0.1:1080", "192.168.1.20:1080" },
            Strategy = config.Strategy,
            Uplinks = config.Uplinks.Select(u => new UplinkStats_OutDto { AdapterId = u.AdapterId, Name = u.AdapterId, Weight = u.Weight, Enabled = true, Health = UplinkHealth.Healthy }).ToList(),
        };
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Status = new DispatchStatus_OutDto { State = RunState.Stopped };
        return Task.CompletedTask;
    }
}

internal sealed class FakeSettings : IAppSettingsRepository
{
    public AppSettings_OutDto Current { get; set; } = new();

    public AppSettings_InDto? Saved { get; private set; }

    public Task<AppSettings_OutDto> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

    public Task SaveAsync(AppSettings_InDto settings, CancellationToken cancellationToken = default)
    {
        Saved = settings;
        return Task.CompletedTask;
    }
}

internal sealed class FakePrivilegeService(bool elevated) : IPrivilegeService
{
    public bool IsElevated { get; } = elevated;

    public int RelaunchCalls { get; private set; }

    public bool TryRelaunchElevated()
    {
        RelaunchCalls++;
        return true;
    }
}

internal sealed class UiRig : IDisposable
{
    private readonly TempDirectory _dir = new();

    public UiRig(bool elevated = true, params NetworkAdapter[] adapters)
    {
        Inventory = new FakeInventory(adapters.Length > 0 ? adapters : DefaultAdapters());
        Sharing = new FakeSharingService { Platform = new PlatformInfo_OutDto { Platform = HostPlatform.Windows, OsDescription = "Windows", IsElevated = elevated, SupportsWifiHotspot = true } };
        Dispatch = new FakeDispatchService();
        Settings = new FakeSettings();
        Privilege = new FakePrivilegeService(elevated);
        Logs = new LogStore(_dir.Path);
        Catalog = new AdapterCatalog();
        Main = new MainViewModel(Inventory, Sharing, Dispatch, Privilege, Settings, Catalog, Logs, Tests.Logs.For<MainViewModel>());
    }

    public FakeInventory Inventory { get; }

    public FakeSharingService Sharing { get; }

    public FakeDispatchService Dispatch { get; }

    public FakeSettings Settings { get; }

    public FakePrivilegeService Privilege { get; }

    public LogStore Logs { get; }

    public AdapterCatalog Catalog { get; }

    public MainViewModel Main { get; }

    public static NetworkAdapter[] DefaultAdapters() => new[]
    {
        TestAdapters.Make("wifi", "Wi-Fi", AdapterKind.WiFi, ip: "192.168.1.20", gateway: "192.168.1.1", index: 3, speed: 866_000_000),
        TestAdapters.Make("eth", "Ethernet", ip: "10.0.0.5", gateway: "10.0.0.1", index: 4),
        TestAdapters.Make("usb", "Ethernet 2", ip: null, gateway: null, index: 5),
        TestAdapters.Make("usb2", "USB Ethernet", ip: null, gateway: null, index: 6, up: false),
        TestAdapters.Make("vm", "vEthernet (WSL)", ip: "172.20.0.1", gateway: null, virtualAdapter: true, index: 7),
    };

    public async Task<MainWindow> OpenAsync()
    {
        var window = new MainWindow { DataContext = Main, Width = 1200, Height = 800 };
        window.Show();
        await Main.InitializeAsync();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    public void Dispose() => _dir.Dispose();
}

public class UiTests
{
    private static string ScreenshotDirectory
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("NSH_SCREENSHOTS");
            var dir = string.IsNullOrEmpty(path) ? Path.Combine(AppContext.BaseDirectory, "screenshots") : path;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
#pragma warning disable CS0618 // Bitmap.Save(string) : seule surcharge disponible pour écrire les captures de contrôle
        frame!.Save(Path.Combine(ScreenshotDirectory, name + ".png"));
#pragma warning restore CS0618
    }

    private static void Theme(ThemeVariant variant) => Avalonia.Application.Current!.RequestedThemeVariant = variant;

    [Fact]
    public Task La_fenetre_s_ouvre_avec_les_connexions_detectees_et_pre_cochees() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig();
        var window = await rig.OpenAsync();

        // Première utilisation : les connexions Internet réelles sont cochées, les adaptateurs virtuels masqués.
        Assert.Equal(new[] { "Wi-Fi", "Ethernet" }, rig.Catalog.Sources.Select(s => s.Name));
        Assert.All(rig.Catalog.Sources, s => Assert.True(s.IsSource));
        Assert.Equal(new[] { "Ethernet", "Ethernet 2", "USB Ethernet" }, rig.Catalog.Targets.Select(t => t.Name));
        Assert.DoesNotContain(rig.Catalog.Sources, s => s.IsVirtual);

        rig.Catalog.ShowVirtual = true;
        Assert.Contains(rig.Catalog.Targets, t => t.Name == "vEthernet (WSL)");

        Theme(ThemeVariant.Light);
        Capture(window, "01-partage-clair");
        Theme(ThemeVariant.Dark);
        Capture(window, "01-partage-sombre");
        window.Close();
    });

    [Fact]
    public Task Une_source_cochee_ne_peut_pas_etre_cible_et_inversement() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig();
        await rig.OpenAsync();
        var eth = rig.Catalog.Sources.Single(s => s.Id == "eth");

        Assert.True(eth.IsSource);
        eth.IsTarget = true;

        Assert.True(eth.IsTarget);
        Assert.False(eth.IsSource);
        eth.IsSource = true;
        Assert.False(eth.IsTarget);
    });

    [Fact]
    public Task Le_partage_envoie_la_selection_au_service_et_affiche_l_etat() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig();
        var window = await rig.OpenAsync();
        rig.Catalog.Sources.Single(s => s.Id == "eth").IsSource = false;
        rig.Catalog.Sources.Single(s => s.Id == "wifi").Weight = 4;
        rig.Catalog.Targets.Single(t => t.Id == "usb").IsTarget = true;
        rig.Main.Sharing.HotspotEnabled = true;
        rig.Main.Sharing.Ssid = "Maison";
        rig.Main.Sharing.Password = "motdepasse1";
        Assert.True(rig.Main.Sharing.CanStart);

        await rig.Main.Sharing.StartCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var request = rig.Sharing.LastRequest!;
        Assert.Equal(new[] { "wifi" }, request.SourceAdapterIds);
        Assert.Equal(new[] { "usb" }, request.TargetAdapterIds);
        Assert.Equal(4, request.SourceWeights["wifi"]);
        Assert.Equal("Maison", request.Hotspot!.Ssid);
        Assert.Equal(RunState.Running, rig.Main.Sharing.State);
        Assert.True(rig.Main.Sharing.IsRunning);

        rig.Sharing.Push(new SharingStatus_OutDto
        {
            State = RunState.Running,
            Sources = new List<string> { "Wi-Fi" },
            Targets = new List<string> { "Ethernet 2" },
            HotspotSsid = "Maison",
            Warnings = new List<string> { "« Ethernet 2 » est un adaptateur virtuel (VM, VPN, conteneur) : vérifiez que c'est bien voulu." },
        });
        Dispatcher.UIThread.RunJobs();
        Theme(ThemeVariant.Light);
        Capture(window, "02-partage-actif-clair");

        await rig.Main.Sharing.StopCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(RunState.Stopped, rig.Main.Sharing.State);
        window.Close();
    });

    [Fact]
    public Task Sans_droits_administrateur_le_partage_est_bloque_et_le_relancement_propose() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig(elevated: false);
        var window = await rig.OpenAsync();
        rig.Catalog.Targets.Single(t => t.Id == "usb").IsTarget = true;

        Assert.True(rig.Main.NeedsElevation);
        Assert.False(rig.Main.Sharing.CanStart);

        rig.Main.ElevateCommand.Execute(null);

        Assert.Equal(1, rig.Privilege.RelaunchCalls);
        Assert.NotNull(rig.Settings.Saved); // les préférences sont enregistrées avant de quitter
        Theme(ThemeVariant.Light);
        Capture(window, "03-droits-limites");
        window.Close();
    });

    [Fact]
    public Task Le_dispatch_utilise_les_connexions_cochees_avec_leurs_poids_et_affiche_les_debits() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig();
        var window = await rig.OpenAsync();
        rig.Catalog.Sources.Single(s => s.Id == "wifi").Weight = 3;
        var dispatch = rig.Main.Dispatch;
        dispatch.AllowLan = true;
        dispatch.Port = 1080;

        await dispatch.StartCommand.ExecuteAsync(null);
        var config = rig.Dispatch.LastConfig!;
        Assert.Equal(new[] { "wifi", "eth" }, config.Uplinks.Select(u => u.AdapterId));
        Assert.Equal(new[] { 3, 1 }, config.Uplinks.Select(u => u.Weight));
        Assert.True(config.AllowLan);

        rig.Dispatch.Status = new DispatchStatus_OutDto
        {
            State = RunState.Running,
            ListenEndpoint = "0.0.0.0:1080",
            ClientEndpoints = new List<string> { "127.0.0.1:1080", "192.168.1.20:1080", "10.0.0.5:1080" },
            Strategy = DispatchStrategy.WeightedRoundRobin,
            ActiveConnections = 17,
            Uplinks = new List<UplinkStats_OutDto>
            {
                new() { AdapterId = "wifi", Name = "Wi-Fi", IPv4 = "192.168.1.20", Health = UplinkHealth.Healthy, Weight = 3, ActiveConnections = 11, TotalConnections = 1284, BytesReceived = 842_000_000, BytesSent = 31_000_000, ReceiveRateBytesPerSecond = 5_400_000, SendRateBytesPerSecond = 210_000, LatencyMs = 14 },
                new() { AdapterId = "eth", Name = "Ethernet", IPv4 = "10.0.0.5", Health = UplinkHealth.Healthy, Weight = 1, ActiveConnections = 6, TotalConnections = 431, FailedConnections = 2, BytesReceived = 301_000_000, BytesSent = 12_000_000, ReceiveRateBytesPerSecond = 1_800_000, SendRateBytesPerSecond = 64_000, LatencyMs = 38 },
            },
        };
        dispatch.Refresh();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, dispatch.Uplinks.Count);
        Assert.Equal("5,15 Mo/s", dispatch.Uplinks[0].Download);
        // Une ligne par adresse (le même port accepte SOCKS5 et HTTP), libellée par l'adaptateur qui la porte.
        Assert.Equal(new[] { "Cet ordinateur", "Wi-Fi", "Ethernet" }, dispatch.Endpoints.Select(e => e.Label));
        Assert.Equal(17, dispatch.ActiveConnections);
        // Les débits en direct apparaissent aussi dans la liste de gauche.
        Assert.Contains("5,15 Mo/s", rig.Catalog.Sources.Single(s => s.Id == "wifi").LiveText);

        rig.Main.SelectedTabIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Theme(ThemeVariant.Light);
        Capture(window, "04-dispatch-clair");
        Theme(ThemeVariant.Dark);
        Capture(window, "04-dispatch-sombre");

        await dispatch.StopCommand.ExecuteAsync(null);
        Assert.Equal(RunState.Stopped, dispatch.State);
        Assert.Empty(dispatch.Uplinks);
        window.Close();
    });

    [Fact]
    public Task La_selection_est_verrouillee_pendant_un_partage_ou_un_dispatch_actif() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig();
        var window = await rig.OpenAsync();
        var wifi = rig.Catalog.Sources.Single(s => s.Id == "wifi");
        var usb = rig.Catalog.Targets.Single(t => t.Id == "usb");
        var unplugged = rig.Catalog.Targets.Single(t => t.Id == "usb2");
        usb.IsTarget = true;

        Assert.True(wifi.CanEdit);
        Assert.True(usb.CanSelectTarget);
        Assert.False(unplugged.CanSelectTarget); // un adaptateur débranché n'est jamais sélectionnable

        await rig.Main.Dispatch.StartCommand.ExecuteAsync(null);
        Assert.True(rig.Catalog.IsLocked);
        Assert.False(wifi.CanEdit);
        Assert.False(usb.CanSelectTarget);

        await rig.Main.Dispatch.StopCommand.ExecuteAsync(null);
        Assert.False(rig.Catalog.IsLocked);
        Assert.True(wifi.CanEdit);

        // Même chose pour le partage de connexion.
        await rig.Main.Sharing.StartCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(rig.Catalog.IsLocked);
        Assert.False(rig.Main.Sharing.CanConfigure);
        await rig.Main.Sharing.StopCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(rig.Catalog.IsLocked);
        Assert.True(rig.Main.Sharing.CanConfigure);

        window.Close();
    });

    [Fact]
    public Task Le_dispatch_sans_connexion_cochee_ne_peut_pas_demarrer() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig();
        await rig.OpenAsync();
        foreach (var source in rig.Catalog.Sources)
        {
            source.IsSource = false;
        }

        Assert.False(rig.Main.Dispatch.CanStart);
    });

    [Fact]
    public Task Le_dispatch_avec_authentification_incomplete_ne_demarre_pas_en_proxy_ouvert() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig();
        await rig.OpenAsync();
        var dispatch = rig.Main.Dispatch;
        Assert.True(dispatch.CanStart);

        dispatch.AllowLan = true;
        dispatch.UseAuthentication = true; // case cochée, identifiant vide : le service ouvrirait le proxy à tous
        Assert.False(dispatch.CanStart);
        Assert.True(dispatch.ShowCredentialsMissing);
        Assert.True(dispatch.ShowOpenProxyWarning);

        dispatch.Username = "   ";
        dispatch.Password = "secret";
        Assert.False(dispatch.CanStart); // un identifiant fait d'espaces est vidé par BuildConfig

        dispatch.Username = "moi";
        Assert.True(dispatch.CanStart);
        Assert.False(dispatch.ShowOpenProxyWarning);
        Assert.Equal("moi", dispatch.BuildConfig().Username);

        dispatch.UseAuthentication = false;
        Assert.True(dispatch.CanStart);
        Assert.True(dispatch.ShowOpenProxyWarning);
    });

    [Fact]
    public Task Les_preferences_sont_restaurees_au_lancement_suivant() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig();
        rig.Settings.Current = new AppSettings_OutDto
        {
            HotspotEnabled = true,
            Sharing =
            {
                SourceAdapterIds = { "eth" },
                TargetAdapterIds = { "usb" },
                SourceWeights = { ["eth"] = 7 },
                Hotspot = new WifiHotspot_InDto { Ssid = "Chez moi", Password = "unmotdepasse", Band = WifiBand.Band5GHz },
            },
            Dispatch = { Port = 3128, AllowLan = true, Strategy = DispatchStrategy.Failover, Username = "bob", Password = "pw" },
        };

        await rig.OpenAsync();

        Assert.Equal(new[] { "eth" }, rig.Catalog.SelectedSources.Select(s => s.Id));
        Assert.Equal(7, rig.Catalog.SelectedSources.Single().WeightValue);
        Assert.Equal(new[] { "usb" }, rig.Catalog.SelectedTargets.Select(t => t.Id));
        Assert.True(rig.Main.Sharing.HotspotEnabled);
        Assert.Equal("Chez moi", rig.Main.Sharing.Ssid);
        Assert.Equal(WifiBand.Band5GHz, rig.Main.Sharing.SelectedBand.Value);
        Assert.Equal(3128m, rig.Main.Dispatch.Port);
        Assert.Equal(DispatchStrategy.Failover, rig.Main.Dispatch.SelectedStrategy.Value);
        Assert.True(rig.Main.Dispatch.UseAuthentication);
        Assert.Equal("bob", rig.Main.Dispatch.Username);
    });

    [Fact]
    public Task Le_journal_affiche_les_messages_les_plus_recents_en_premier() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig();
        var window = await rig.OpenAsync();

        rig.Logs.Add(new LogEntry(DateTimeOffset.Now, LogLevel.Information, "Test", "Partage actif : Wi-Fi → Ethernet 2"));
        rig.Logs.Add(new LogEntry(DateTimeOffset.Now, LogLevel.Warning, "Test", "Liaison Ethernet hors service : aucune réponse aux sondes"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Liaison Ethernet hors service : aucune réponse aux sondes", rig.Main.Log.Entries[0].Message);
        rig.Main.SelectedTabIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Theme(ThemeVariant.Light);
        Capture(window, "05-journal-clair");
        window.Close();
    });

    [Theory]
    [InlineData(0, "0 o/s")]
    [InlineData(1536, "1,5 Ko/s")]
    [InlineData(5_400_000, "5,15 Mo/s")]
    public void Les_debits_sont_formates_en_francais(double bytesPerSecond, string expected)
        => Assert.Equal(expected, NetShareHub.Desktop.Formatting.Rate(bytesPerSecond));

    [Theory]
    [InlineData(866_000_000, "866 Mbit/s")]
    [InlineData(1_000_000_000, "1 Gbit/s")]
    [InlineData(0, "")]
    public void Les_vitesses_de_liaison_sont_formatees(long bits, string expected)
        => Assert.Equal(expected, NetShareHub.Desktop.Formatting.LinkSpeed(bits));

    [Theory]
    [InlineData(0, "0")]
    [InlineData(431, "431")]
    [InlineData(1284, "1\u00a0284")]
    [InlineData(1_250_000, "1\u00a0250\u00a0000")]
    public void Les_entiers_sont_groupes_par_milliers_a_la_francaise(long value, string expected)
        => Assert.Equal(expected, NetShareHub.Desktop.Formatting.Integer(value));

    [Fact]
    public void Le_mot_de_passe_genere_est_valide_pour_le_wifi()
    {
        var password = NetShareHub.Desktop.Formatting.RandomPassword();

        Assert.Equal(12, password.Length);
        Assert.All(password, c => Assert.InRange(c, ' ', '~'));
        Assert.NotEqual(password, NetShareHub.Desktop.Formatting.RandomPassword());
    }
}
