using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NetShareHub.Application.Contracts.Persistence;
using NetShareHub.Application.Contracts.Services;
using NetShareHub.Desktop.Logging;
using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Enums;

namespace NetShareHub.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly INetworkInventoryService _inventory;
    private readonly IInternetSharingService _sharing;
    private readonly IPrivilegeService _privilege;
    private readonly IAppSettingsRepository _settings;
    private readonly ILogger<MainViewModel> _logger;
    private CancellationTokenSource? _saveCts;
    private bool _loading = true;

    [ObservableProperty]
    private string _platformText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsElevation))]
    private bool _isElevated;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private int _selectedTabIndex;

    public MainViewModel(
        INetworkInventoryService inventory,
        IInternetSharingService sharing,
        IDispatchService dispatch,
        IPrivilegeService privilege,
        IAppSettingsRepository settings,
        AdapterCatalog catalog,
        LogStore logStore,
        ILogger<MainViewModel> logger)
    {
        _inventory = inventory;
        _sharing = sharing;
        _privilege = privilege;
        _settings = settings;
        _logger = logger;
        Catalog = catalog;
        IsElevated = privilege.IsElevated;

        Sharing = new SharingViewModel(sharing, privilege, catalog, ScheduleSave, Elevate);
        Dispatch = new DispatchViewModel(dispatch, catalog, ScheduleSave);
        Log = new LogViewModel(logStore);

        catalog.SelectionChanged += (_, _) => ScheduleSave();
        Sharing.PropertyChanged += (_, e) => UpdateLock(e.PropertyName);
        Dispatch.PropertyChanged += (_, e) => UpdateLock(e.PropertyName);
        catalog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AdapterCatalog.ShowVirtual))
            {
                Sharing.SelectHotspotAdapter();
            }
        };
    }

    public AdapterCatalog Catalog { get; }

    public SharingViewModel Sharing { get; }

    public DispatchViewModel Dispatch { get; }

    public LogViewModel Log { get; }

    public bool NeedsElevation => !IsElevated;

    // La sélection des connexions est figée tant qu'un partage ou le Dispatch tourne (elle ne serait pas prise en compte).
    private void UpdateLock(string? property)
    {
        if (property != "State")
        {
            return;
        }

        static bool Active(RunState state) => state is RunState.Running or RunState.Starting or RunState.Stopping;
        Catalog.IsLocked = Active(Sharing.State) || Active(Dispatch.State);
    }

    /// <summary>Renseigné par l'application : ferme la fenêtre (utilisé après le relancement en administrateur).</summary>
    public Action? RequestExit { get; set; }

    public async Task InitializeAsync()
    {
        try
        {
            var settings = await _settings.GetAsync();
            Catalog.Restore(settings.Sharing, settings.Dispatch);
            Catalog.Apply(await _inventory.GetAdaptersAsync());

            var info = await _sharing.GetPlatformInfoAsync();
            IsElevated = info.IsElevated;
            PlatformText = DescribePlatform(info.Platform, info.OsDescription);
            Sharing.ApplyPlatform(info);
            Sharing.Load(settings);
            Dispatch.Load(settings);

            _inventory.AdaptersChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = RefreshAsync());

            if (IsElevated && await _sharing.RecoverAsync())
            {
                _logger.LogWarning("Une session de partage précédente a été interrompue : la configuration réseau a été restaurée.");
            }

            if (!IsElevated && info.Platform != HostPlatform.Unsupported)
            {
                _logger.LogInformation("Mode limité : le Dispatch fonctionne, le partage de connexion demande les droits administrateur.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialisation incomplète");
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            Catalog.Apply(await _inventory.GetAdaptersAsync());
            Sharing.SelectHotspotAdapter();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Actualisation impossible");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private void Elevate()
    {
        SaveNow();
        if (_privilege.TryRelaunchElevated())
        {
            RequestExit?.Invoke();
        }
        else
        {
            _logger.LogWarning("Le relancement en administrateur a été annulé ou n'est pas possible.");
        }
    }

    /// <summary>Arrête proprement le Dispatch et le partage (la configuration réseau est restaurée).</summary>
    public async Task ShutdownAsync()
    {
        SaveNow();
        try
        {
            await Dispatch.StopQuietlyAsync();
            await _sharing.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Arrêt incomplet");
        }
    }

    private AppSettings_InDto BuildSettings() => new()
    {
        Sharing = Sharing.BuildRequest(),
        HotspotEnabled = Sharing.HotspotEnabled,
        Dispatch = Dispatch.BuildConfig(),
    };

    private void ScheduleSave()
    {
        if (_loading)
        {
            return;
        }

        _saveCts?.Cancel();
        var cts = _saveCts = new CancellationTokenSource();
        _ = SaveLaterAsync(cts.Token);
    }

    private async Task SaveLaterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(700, cancellationToken);
            await _settings.SaveAsync(BuildSettings(), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Une modification plus récente a pris le relais.
        }
    }

    private void SaveNow()
    {
        if (_loading)
        {
            return;
        }

        _saveCts?.Cancel();
        var settings = BuildSettings();

        // Synchrone (utilisé juste avant de quitter) : exécuté hors du thread d'interface pour éviter tout interblocage.
        Task.Run(() => _settings.SaveAsync(settings)).GetAwaiter().GetResult();
    }

    private static string DescribePlatform(HostPlatform platform, string description) => platform switch
    {
        HostPlatform.Windows => "Windows",
        HostPlatform.Linux => "Linux",
        _ => description,
    };
}
