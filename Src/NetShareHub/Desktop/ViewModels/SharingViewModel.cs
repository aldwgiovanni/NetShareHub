using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetShareHub.Application.Contracts.Services;
using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Dtos.Outgoing;
using NetShareHub.Domain.Enums;
using NetShareHub.Domain.Exceptions;

namespace NetShareHub.Desktop.ViewModels;

/// <summary>Onglet Partage : hotspot Wi-Fi et routeur filaire.</summary>
public sealed partial class SharingViewModel : ObservableObject
{
    private readonly IInternetSharingService _sharing;
    private readonly IPrivilegeService _privilege;
    private readonly Action _requestSave;
    private readonly Action _requestElevation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsBusy), nameof(CanStart), nameof(CanStop), nameof(StateText), nameof(IsFaulted), nameof(CanConfigure))]
    private RunState _state = RunState.Stopped;

    [ObservableProperty]
    private string _message = "Prêt. Choisissez où partager votre connexion.";

    [ObservableProperty]
    private bool _hotspotEnabled;

    [ObservableProperty]
    private string _ssid = "NetShareHub";

    [ObservableProperty]
    private string _password = Formatting.RandomPassword();

    [ObservableProperty]
    private bool _showPassword;

    [ObservableProperty]
    private Option<WifiBand> _selectedBand;

    [ObservableProperty]
    private AdapterItemViewModel? _selectedHotspotAdapter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(NeedsElevation))]
    private bool _isElevated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleHotspot))]
    private bool _supportsWifiHotspot;

    [ObservableProperty]
    private bool _supportsTransparentBalancing;

    [ObservableProperty]
    private bool _isLinux;

    [ObservableProperty]
    private bool _balanceSources = true;

    [ObservableProperty]
    private string _capabilityNote = string.Empty;

    public SharingViewModel(
        IInternetSharingService sharing,
        IPrivilegeService privilege,
        AdapterCatalog catalog,
        Action requestSave,
        Action requestElevation)
    {
        _sharing = sharing;
        _privilege = privilege;
        Catalog = catalog;
        _requestSave = requestSave;
        _requestElevation = requestElevation;
        IsElevated = privilege.IsElevated;

        Bands = new[]
        {
            new Option<WifiBand>(WifiBand.Auto, "Automatique"),
            new Option<WifiBand>(WifiBand.Band2_4GHz, "2,4 GHz (portée)"),
            new Option<WifiBand>(WifiBand.Band5GHz, "5 GHz (débit)"),
        };
        _selectedBand = Bands[0];

        sharing.StatusChanged += (_, status) => Dispatcher.UIThread.Post(() => ApplyStatus(status));
        catalog.SelectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedSourceCount));
            OnPropertyChanged(nameof(SelectedTargetCount));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(Summary));
        };
    }

    public AdapterCatalog Catalog { get; }

    public IReadOnlyList<Option<WifiBand>> Bands { get; }

    public ObservableCollection<string> Warnings { get; } = new();

    public bool IsRunning => State == RunState.Running;

    public bool IsBusy => State is RunState.Starting or RunState.Stopping;

    public bool IsFaulted => State == RunState.Faulted;

    public bool NeedsElevation => !IsElevated;

    public bool CanToggleHotspot => SupportsWifiHotspot && CanConfigure;

    public bool CanStop => State == RunState.Running;

    /// <summary>Les destinations et le hotspot ne se modifient que lorsque le partage est arrêté.</summary>
    public bool CanConfigure => State is RunState.Stopped or RunState.Faulted;

    public int SelectedSourceCount => Catalog.SelectedSources.Count();

    public int SelectedTargetCount => Catalog.SelectedTargets.Count() + (HotspotEnabled ? 1 : 0);

    public bool CanStart
        => IsElevated && State is RunState.Stopped or RunState.Faulted && SelectedSourceCount > 0 && SelectedTargetCount > 0;

    public string StateText => State switch
    {
        RunState.Running => "Partage actif",
        RunState.Starting => "Démarrage…",
        RunState.Stopping => "Arrêt…",
        RunState.Faulted => "Erreur",
        _ => "Arrêté",
    };

    public string Summary
    {
        get
        {
            if (SelectedSourceCount == 0)
            {
                return "Cochez au moins une connexion Internet à gauche.";
            }

            if (SelectedTargetCount == 0)
            {
                return "Choisissez au moins une destination : un adaptateur ou le hotspot Wi-Fi.";
            }

            var sources = string.Join(", ", Catalog.SelectedSources.Select(s => s.Name));
            var targets = Catalog.SelectedTargets.Select(t => t.Name).ToList();
            if (HotspotEnabled)
            {
                targets.Add($"hotspot « {Ssid} »");
            }

            return $"{sources}  →  {string.Join(", ", targets)}";
        }
    }

    public void ApplyPlatform(PlatformInfo_OutDto info)
    {
        IsElevated = info.IsElevated;
        SupportsWifiHotspot = info.SupportsWifiHotspot;
        SupportsTransparentBalancing = info.SupportsTransparentBalancing;
        IsLinux = info.Platform == HostPlatform.Linux;
        CapabilityNote = string.Join(" ", info.Notes);
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(NeedsElevation));
    }

    public void Load(AppSettings_OutDto settings)
    {
        HotspotEnabled = settings.HotspotEnabled;
        BalanceSources = settings.Sharing.BalanceSources;
        if (settings.Sharing.Hotspot is { } hotspot)
        {
            Ssid = string.IsNullOrWhiteSpace(hotspot.Ssid) ? Ssid : hotspot.Ssid;
            Password = hotspot.Password.Length >= 8 ? hotspot.Password : Password;
            SelectedBand = Bands.First(b => b.Value == hotspot.Band);
            _pendingHotspotAdapterId = hotspot.AdapterId;
        }

        SelectHotspotAdapter();
    }

    private string? _pendingHotspotAdapterId;

    /// <summary>Choisit la carte Wi-Fi du hotspot (Linux) : celle enregistrée, sinon la première qui n'est pas une source.</summary>
    public void SelectHotspotAdapter()
    {
        var candidates = Catalog.WifiAdapters.ToList();
        SelectedHotspotAdapter =
            candidates.FirstOrDefault(a => a.Id == _pendingHotspotAdapterId && !a.IsSource)
            ?? candidates.FirstOrDefault(a => !a.IsSource)
            ?? candidates.FirstOrDefault();
    }

    public SharingRequest_InDto BuildRequest()
    {
        var request = new SharingRequest_InDto
        {
            SourceAdapterIds = Catalog.SelectedSources.Select(s => s.Id).ToList(),
            TargetAdapterIds = Catalog.SelectedTargets.Select(t => t.Id).ToList(),
            BalanceSources = BalanceSources,
            SourceWeights = Catalog.SelectedSources.ToDictionary(s => s.Id, s => s.WeightValue),
        };

        // Les réglages du hotspot sont toujours conservés, même désactivé, pour les retrouver au prochain lancement.
        request.Hotspot = new WifiHotspot_InDto
        {
            Ssid = Ssid.Trim(),
            Password = Password,
            Band = SelectedBand.Value,
            AdapterId = SelectedHotspotAdapter?.Id,
        };
        return request;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        _requestSave();
        var request = BuildRequest();
        if (!HotspotEnabled)
        {
            request.Hotspot = null;
        }

        try
        {
            await _sharing.StartAsync(request);
        }
        catch (PrivilegeRequiredException)
        {
            Message = "Les droits administrateur sont nécessaires pour partager la connexion.";
        }
        catch (SharingException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            Message = $"Erreur inattendue : {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        try
        {
            await _sharing.StopAsync();
        }
        catch (Exception ex)
        {
            Message = $"Arrêt incomplet : {ex.Message}";
        }
    }

    [RelayCommand]
    private void Elevate() => _requestElevation();

    [RelayCommand]
    private void RegeneratePassword() => Password = Formatting.RandomPassword();

    partial void OnHotspotEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectedTargetCount));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(Summary));
        _requestSave();
    }

    partial void OnSsidChanged(string value)
    {
        OnPropertyChanged(nameof(Summary));
        _requestSave();
    }

    partial void OnPasswordChanged(string value) => _requestSave();

    partial void OnSelectedBandChanged(Option<WifiBand> value) => _requestSave();

    partial void OnBalanceSourcesChanged(bool value) => _requestSave();

    private void ApplyStatus(SharingStatus_OutDto status)
    {
        State = status.State;
        Message = status.State == RunState.Running ? DescribeRunning(status) : status.Message;
        Warnings.Clear();
        foreach (var warning in status.Warnings)
        {
            Warnings.Add(warning);
        }

        OnPropertyChanged(nameof(CanStart));
    }

    private static string DescribeRunning(SharingStatus_OutDto status)
    {
        var targets = status.Targets.ToList();
        if (status.HotspotSsid is not null)
        {
            targets.Add($"hotspot « {status.HotspotSsid} »");
        }

        return $"{string.Join(", ", status.Sources)}  →  {string.Join(", ", targets)}";
    }
}
