using System.ComponentModel;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using NetShareHub.Desktop.Logging;
using NetShareHub.Desktop.ViewModels;
using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Dtos.Outgoing;
using NetShareHub.Domain.Enums;

namespace NetShareHub.Tests.UI;

/// <summary>Défauts relevés lors des tests utilisateur (application réelle, Windows 11, sans droits administrateur).</summary>
public class UiRegressionTests
{
    private static List<string> Record(INotifyPropertyChanged source)
    {
        var names = new List<string>();
        source.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? string.Empty);
        return names;
    }

    [Fact]
    public Task Au_lancement_le_resume_et_le_bouton_demarrer_suivent_les_connexions_cochees() => UiSession.RunAsync(async () =>
    {
        // Hotspot désactivé : rien d'autre que le catalogue ne pouvait rafraîchir le résumé (« Cochez au moins une connexion… »).
        using var rig = new UiRig();
        var sharing = Record(rig.Main.Sharing);
        var dispatch = Record(rig.Main.Dispatch);

        await rig.OpenAsync();

        Assert.Contains(nameof(SharingViewModel.Summary), sharing);
        Assert.Contains(nameof(SharingViewModel.CanStart), sharing);
        Assert.Contains(nameof(DispatchViewModel.CanStart), dispatch);
        Assert.DoesNotContain("Cochez", rig.Main.Sharing.Summary);
    });

    [Fact]
    public Task Debrancher_un_adaptateur_met_a_jour_l_interface_sans_oublier_sa_selection() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig();
        rig.Settings.Current = new AppSettings_OutDto { Sharing = { SourceAdapterIds = { "wifi" }, TargetAdapterIds = { "usb" } } };
        await rig.OpenAsync();
        Assert.True(rig.Main.Sharing.CanStart);
        var sharing = Record(rig.Main.Sharing);

        // La carte USB (seule destination) est débranchée : on ne peut plus démarrer, et l'interface le montre.
        rig.Inventory.Snapshot = UiRig.DefaultAdapters().Where(a => a.Id != "usb").ToList();
        await rig.Main.RefreshAsync();

        Assert.Contains(nameof(SharingViewModel.CanStart), sharing);
        Assert.False(rig.Main.Sharing.CanStart);

        // Aucun enregistrement n'est déclenché par l'actualisation…
        await Task.Delay(1000);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(rig.Settings.Saved);

        // … et si l'utilisateur enregistre autre chose, la carte absente reste mémorisée.
        rig.Main.Sharing.Ssid = "Maison";
        rig.Main.ElevateCommand.Execute(null);
        Assert.Equal(new[] { "usb" }, rig.Settings.Saved!.Sharing.TargetAdapterIds);
        Assert.Equal(new[] { "wifi" }, rig.Settings.Saved.Sharing.SourceAdapterIds);
    });

    [Fact]
    public Task Un_adaptateur_rebranche_retrouve_le_choix_de_l_utilisateur() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig();
        await rig.OpenAsync(); // première utilisation : Wi-Fi et Ethernet cochés automatiquement
        rig.Catalog.Sources.Single(s => s.Id == "eth").IsSource = false;
        rig.Catalog.Sources.Single(s => s.Id == "wifi").Weight = 6;

        rig.Inventory.Snapshot = UiRig.DefaultAdapters().Where(a => a.Id is not "eth" and not "wifi").ToList();
        await rig.Main.RefreshAsync();
        rig.Inventory.Snapshot = UiRig.DefaultAdapters();
        await rig.Main.RefreshAsync();

        Assert.False(rig.Catalog.Sources.Single(s => s.Id == "eth").IsSource);
        Assert.True(rig.Catalog.Sources.Single(s => s.Id == "wifi").IsSource);
        Assert.Equal(6, rig.Catalog.Sources.Single(s => s.Id == "wifi").WeightValue);
    });

    [Fact]
    public Task Des_preferences_partielles_n_empechent_ni_la_restauration_ni_le_suivi_des_adaptateurs() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig();
        rig.Settings.Current = new AppSettings_OutDto
        {
            HotspotEnabled = true,
            Sharing = { SourceAdapterIds = null!, TargetAdapterIds = null!, SourceWeights = null!, Hotspot = new WifiHotspot_InDto { Ssid = "X", Password = null!, Band = (WifiBand)9 } },
            Dispatch = { Strategy = (DispatchStrategy)7, Uplinks = null! },
        };

        await rig.OpenAsync();

        Assert.Equal(1, rig.Sharing.RecoverCalls);
        Assert.Equal(DispatchStrategy.WeightedRoundRobin, rig.Main.Dispatch.SelectedStrategy.Value);
        Assert.Equal(WifiBand.Auto, rig.Main.Sharing.SelectedBand.Value);
        Assert.True(rig.Main.Sharing.Password.Length >= 8);
        Assert.NotEmpty(rig.Catalog.SelectedSources);
        Assert.DoesNotContain(rig.Logger.Entries, e => e.Level >= LogLevel.Error);
    });

    [Fact]
    public Task Un_hotspot_restaure_sur_une_machine_sans_hotspot_ne_bloque_pas_le_partage() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig();
        rig.Sharing.Platform = new PlatformInfo_OutDto { Platform = HostPlatform.Windows, IsElevated = true, SupportsWifiHotspot = false };
        rig.Settings.Current = new AppSettings_OutDto
        {
            HotspotEnabled = true,
            Sharing = { SourceAdapterIds = { "wifi" }, TargetAdapterIds = { "usb" } },
        };

        await rig.OpenAsync();

        Assert.False(rig.Main.Sharing.IsHotspotOn);
        Assert.True(rig.Main.Sharing.CanStart);
        Assert.DoesNotContain("hotspot", rig.Main.Sharing.Summary);
        await rig.Main.Sharing.StartCommand.ExecuteAsync(null);
        Assert.Null(rig.Sharing.LastRequest!.Hotspot);
        await rig.Main.Sharing.StopCommand.ExecuteAsync(null);

        // La préférence n'est pas perdue : une carte Wi-Fi branchée plus tard (nouvelle sonde) réactive le hotspot.
        rig.Sharing.Platform = new PlatformInfo_OutDto { Platform = HostPlatform.Windows, IsElevated = true, SupportsWifiHotspot = true };
        await rig.Main.RefreshAsync();
        Assert.True(rig.Main.Sharing.IsHotspotOn);
    });

    [Fact]
    public Task Linux_le_choix_de_la_carte_du_hotspot_survit_aux_actualisations_et_est_enregistre() => UiSession.RunAsync(async () =>
    {
        var adapters = new[]
        {
            TestAdapters.Make("wlan0", "wlan0", AdapterKind.WiFi, index: 3),
            TestAdapters.Make("wlan1", "wlan1", AdapterKind.WiFi, ip: null, gateway: null, index: 4),
            TestAdapters.Make("wlan2", "wlan2", AdapterKind.WiFi, ip: null, gateway: null, index: 5),
        };
        using var rig = new UiRig(true, adapters);
        rig.Sharing.Platform = new PlatformInfo_OutDto { Platform = HostPlatform.Linux, IsElevated = true, SupportsWifiHotspot = true };
        await rig.OpenAsync();
        Assert.Equal("wlan1", rig.Main.Sharing.SelectedHotspotAdapter!.Id); // choix automatique : première carte libre

        rig.Main.Sharing.SelectedHotspotAdapter = rig.Catalog.WifiAdapters.Single(a => a.Id == "wlan2");
        await rig.Main.RefreshAsync();
        Assert.Equal("wlan2", rig.Main.Sharing.SelectedHotspotAdapter!.Id);

        // Carte choisie débranchée : une autre est utilisée, mais la préférence enregistrée reste la sienne.
        rig.Inventory.Snapshot = adapters.Where(a => a.Id != "wlan2").ToList();
        await rig.Main.RefreshAsync();
        Assert.Equal("wlan1", rig.Main.Sharing.SelectedHotspotAdapter!.Id);
        rig.Main.ElevateCommand.Execute(null);
        Assert.Equal("wlan2", rig.Settings.Saved!.Sharing.Hotspot!.AdapterId);

        rig.Inventory.Snapshot = adapters;
        await rig.Main.RefreshAsync();
        Assert.Equal("wlan2", rig.Main.Sharing.SelectedHotspotAdapter!.Id);
    });

    [Fact]
    public Task Un_ssid_de_plus_de_32_octets_est_signale_avant_le_demarrage() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig();
        await rig.OpenAsync();
        var sharing = rig.Main.Sharing;
        sharing.HotspotEnabled = true;
        sharing.Password = "motdepasse1";

        sharing.Ssid = new string('é', 17); // 17 caractères, mais 34 octets UTF-8
        Assert.True(sharing.HasHotspotError);
        Assert.Contains("34 octets", sharing.HotspotError);
        Assert.False(sharing.CanStart);

        sharing.Ssid = new string('é', 16);
        Assert.False(sharing.HasHotspotError);
        Assert.True(sharing.CanStart);

        sharing.Password = "motdepassé";
        Assert.True(sharing.HasHotspotError);
    });

    [Fact]
    public Task Un_port_vide_ou_invalide_empeche_le_demarrage_du_dispatch() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig();
        await rig.OpenAsync();
        var dispatch = rig.Main.Dispatch;

        dispatch.Port = null;
        Assert.True(dispatch.HasPortError);
        Assert.False(dispatch.CanStart);
        await dispatch.StartCommand.ExecuteAsync(null);
        Assert.Null(rig.Dispatch.LastConfig);
        Assert.Contains("port", dispatch.Message);

        dispatch.Port = 8080;
        Assert.True(dispatch.CanStart);
        await dispatch.StartCommand.ExecuteAsync(null);
        Assert.Equal(8080, rig.Dispatch.LastConfig!.Port);
        await dispatch.StopCommand.ExecuteAsync(null);
    });

    [Fact]
    public Task Une_elevation_annulee_ne_ferme_pas_l_application() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig(elevated: false);
        rig.Privilege.Accept = false;
        await rig.OpenAsync();
        var exits = 0;
        rig.Main.RequestExit = () => exits++;

        await rig.Main.ElevateCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Privilege.RelaunchCalls);
        Assert.Equal(0, exits);
    });

    [Fact]
    public Task Sans_droits_une_session_interrompue_a_restaurer_est_signalee() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig(elevated: false);
        rig.Sharing.Platform = new PlatformInfo_OutDto { Platform = HostPlatform.Windows, IsElevated = false, HasPendingRecovery = true };

        await rig.OpenAsync();

        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("administrateur pour la restaurer"));
    });

    [Fact]
    public Task A_la_fermeture_le_partage_est_arrete_meme_si_le_dispatch_refuse_de_s_arreter() => UiSession.RunAsync(async () =>
    {
        using var rig = new UiRig();
        await rig.OpenAsync();
        await rig.Main.Sharing.StartCommand.ExecuteAsync(null);
        rig.Dispatch.FailOnStop = true;

        await rig.Main.ShutdownAsync();

        Assert.Equal(1, rig.Sharing.StopCalls);
        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Dispatch"));
    });

    [Fact]
    public Task Une_erreur_inattendue_sur_le_thread_d_interface_est_consignee_sans_fermer_l_application() => UiSession.RunAsync(() =>
    {
        var logger = new ListLogger<UiRegressionTests>();
        using (UnhandledExceptionLogger.Install(logger, Dispatcher.UIThread))
        {
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException("boum"));
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("boum"));
        return Task.CompletedTask;
    });
}
