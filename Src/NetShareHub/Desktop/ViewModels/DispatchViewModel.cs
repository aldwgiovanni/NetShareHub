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

/// <summary>Une ligne du tableau de bord Dispatch (une liaison montante).</summary>
public sealed class UplinkRowViewModel
{
    public UplinkRowViewModel(UplinkStats_OutDto stats)
    {
        Name = stats.Name;
        Address = stats.IPv4;
        HealthText = Formatting.Health(stats.Health, stats.LatencyMs);
        IsHealthy = stats.Health == UplinkHealth.Healthy;
        IsDown = stats.Health == UplinkHealth.Down;
        Weight = $"×{stats.Weight}";
        Active = stats.ActiveConnections.ToString();
        Total = Formatting.Integer(stats.TotalConnections);
        Failed = stats.FailedConnections.ToString();
        HasFailures = stats.FailedConnections > 0;
        Download = Formatting.Rate(stats.ReceiveRateBytesPerSecond);
        Upload = Formatting.Rate(stats.SendRateBytesPerSecond);
        TotalDownload = Formatting.Bytes(stats.BytesReceived);
        TotalUpload = Formatting.Bytes(stats.BytesSent);
    }

    public string Name { get; }

    public string Address { get; }

    public string HealthText { get; }

    public bool IsHealthy { get; }

    public bool IsDown { get; }

    public string Weight { get; }

    public string Active { get; }

    public string Total { get; }

    public string Failed { get; }

    public bool HasFailures { get; }

    public string Download { get; }

    public string Upload { get; }

    public string TotalDownload { get; }

    public string TotalUpload { get; }
}

/// <summary>Adresse à saisir côté appareil, avec le protocole.</summary>
public sealed record ClientEndpoint(string Label, string Value);

/// <summary>Onglet Dispatch : proxy qui agrège plusieurs connexions Internet.</summary>
public sealed partial class DispatchViewModel : ObservableObject
{
    private readonly IDispatchService _dispatch;
    private readonly Action _requestSave;
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(CanStart), nameof(CanEdit), nameof(StateText), nameof(IsFaulted))]
    private RunState _state = RunState.Stopped;

    [ObservableProperty]
    private string _message = "Le Dispatch répartit chaque connexion Internet de vos appareils entre vos différentes liaisons.";

    [ObservableProperty]
    private decimal? _port = 1080;

    [ObservableProperty]
    private bool _allowLan;

    [ObservableProperty]
    private Option<DispatchStrategy> _selectedStrategy;

    [ObservableProperty]
    private bool _stickyDestinations = true;

    [ObservableProperty]
    private decimal? _stickyMinutes = 10;

    [ObservableProperty]
    private bool _healthProbe = true;

    [ObservableProperty]
    private bool _useAuthentication;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private int _activeConnections;

    public DispatchViewModel(IDispatchService dispatch, AdapterCatalog catalog, Action requestSave)
    {
        _dispatch = dispatch;
        Catalog = catalog;
        _requestSave = requestSave;

        Strategies = new[]
        {
            new Option<DispatchStrategy>(
                DispatchStrategy.WeightedRoundRobin,
                Formatting.Strategy(DispatchStrategy.WeightedRoundRobin),
                "Chaque nouvelle connexion va tour à tour vers chaque liaison, selon son poids. À privilégier pour cumuler les débits."),
            new Option<DispatchStrategy>(
                DispatchStrategy.LeastConnections,
                Formatting.Strategy(DispatchStrategy.LeastConnections),
                "La liaison qui a le moins de connexions ouvertes (rapporté à son poids) prend la suivante."),
            new Option<DispatchStrategy>(
                DispatchStrategy.Failover,
                Formatting.Strategy(DispatchStrategy.Failover),
                "Une seule liaison est utilisée (la première cochée) ; les autres ne servent qu'en cas de panne."),
        };
        _selectedStrategy = Strategies[0];

        catalog.SelectionChanged += (_, _) => OnPropertyChanged(nameof(CanStart));
        catalog.AvailabilityChanged += (_, _) => OnPropertyChanged(nameof(CanStart));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public AdapterCatalog Catalog { get; }

    public IReadOnlyList<Option<DispatchStrategy>> Strategies { get; }

    public ObservableCollection<UplinkRowViewModel> Uplinks { get; } = new();

    public ObservableCollection<ClientEndpoint> Endpoints { get; } = new();

    public bool IsRunning => State == RunState.Running;

    public bool IsFaulted => State == RunState.Faulted;

    public bool CanEdit => State is RunState.Stopped or RunState.Faulted;

    public bool CanStart => CanEdit && Catalog.SelectedSources.Any() && !HasPortError;

    /// <summary>Port vide, non numérique ou hors plage : signalé au lieu de démarrer en silence sur le port par défaut.</summary>
    public string PortError => Port is { } port && port == decimal.Truncate(port) && port is >= 1 and <= 65535
        ? string.Empty
        : "Indiquez un port entre 1 et 65535.";

    public bool HasPortError => PortError.Length > 0;

    public string StateText => State switch
    {
        RunState.Running => "Dispatch actif",
        RunState.Starting => "Démarrage…",
        RunState.Stopping => "Arrêt…",
        RunState.Faulted => "Erreur",
        _ => "Arrêté",
    };

    public string StrategyHelp => SelectedStrategy.Description;

    public string ScopeText => AllowLan
        ? "Les autres appareils du réseau (ou du hotspot) peuvent utiliser ce proxy."
        : "Seul cet ordinateur peut utiliser ce proxy.";

    public bool ShowOpenProxyWarning => AllowLan && !UseAuthentication;

    public void Load(AppSettings_OutDto settings)
    {
        var d = settings.Dispatch;
        Port = d.Port;
        AllowLan = d.AllowLan;
        SelectedStrategy = Strategies.FirstOrDefault(s => s.Value == d.Strategy) ?? Strategies[0];
        StickyDestinations = d.StickyDestinations;
        StickyMinutes = d.StickyMinutes;
        HealthProbe = d.HealthProbe;
        UseAuthentication = !string.IsNullOrEmpty(d.Username);
        Username = d.Username ?? string.Empty;
        Password = d.Password ?? string.Empty;
    }

    public DispatchConfig_InDto BuildConfig() => new()
    {
        AllowLan = AllowLan,
        Port = HasPortError ? new DispatchConfig_InDto().Port : (int)Port!.Value,
        Strategy = SelectedStrategy.Value,
        StickyDestinations = StickyDestinations,
        StickyMinutes = (int)(StickyMinutes ?? 10),
        HealthProbe = HealthProbe,
        Username = UseAuthentication ? Username.Trim() : null,
        Password = UseAuthentication ? Password : null,
        Uplinks = Catalog.SelectedSources
            .Select(s => new DispatchUplink_InDto { AdapterId = s.Id, Weight = s.WeightValue, Enabled = true })
            .ToList(),
    };

    // Pendant une commande Démarrer / Arrêter, l'état affiché est géré par la commande et non par le minuteur.
    private bool _busy;

    [RelayCommand]
    private async Task StartAsync()
    {
        if (HasPortError)
        {
            Message = PortError;
            return;
        }

        _requestSave();
        _busy = true;
        State = RunState.Starting;
        try
        {
            await _dispatch.StartAsync(BuildConfig());
            _busy = false;
            Refresh();
        }
        catch (DispatchException ex)
        {
            Message = ex.Message;
            State = RunState.Faulted;
        }
        catch (Exception ex)
        {
            Message = $"Erreur inattendue : {ex.Message}";
            State = RunState.Faulted;
        }
        finally
        {
            _busy = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        _busy = true;
        State = RunState.Stopping;
        try
        {
            await _dispatch.StopAsync();
        }
        finally
        {
            _busy = false;
            State = RunState.Stopped;
            Message = "Dispatch arrêté.";
            Refresh();
        }
    }

    public async Task StopQuietlyAsync()
    {
        _timer.Stop();
        await _dispatch.StopAsync();
    }

    internal void Refresh()
    {
        var status = _dispatch.GetStatus();

        if (!_busy)
        {
            // Après un échec, on garde l'état « Erreur » (et son message) jusqu'au prochain démarrage.
            var keepFault = State == RunState.Faulted && status.State != RunState.Running;
            if (!keepFault)
            {
                State = status.State;
            }

            if (status.State == RunState.Running)
            {
                Message = $"{Formatting.Strategy(status.Strategy)} sur {status.ListenEndpoint}";
            }
        }

        ActiveConnections = status.ActiveConnections;
        Catalog.ApplyLive(status.State == RunState.Running ? status.Uplinks : Array.Empty<UplinkStats_OutDto>());

        SyncRows(status);
        SyncEndpoints(status);
    }

    private void SyncRows(DispatchStatus_OutDto status)
    {
        var rows = status.State == RunState.Running ? status.Uplinks.Select(u => new UplinkRowViewModel(u)).ToList() : new();
        // Remplacement ligne à ligne : conserve le défilement et évite de vider la liste à chaque seconde.
        for (var i = 0; i < rows.Count; i++)
        {
            if (i < Uplinks.Count)
            {
                Uplinks[i] = rows[i];
            }
            else
            {
                Uplinks.Add(rows[i]);
            }
        }

        while (Uplinks.Count > rows.Count)
        {
            Uplinks.RemoveAt(Uplinks.Count - 1);
        }
    }

    private void SyncEndpoints(DispatchStatus_OutDto status)
    {
        // Une ligne par adresse : le même port accepte SOCKS5 et HTTP.
        var wanted = status.State == RunState.Running
            ? status.ClientEndpoints.Select(e => new ClientEndpoint(LabelFor(e), e)).ToList()
            : new List<ClientEndpoint>();

        if (Endpoints.SequenceEqual(wanted))
        {
            return;
        }

        Endpoints.Clear();
        foreach (var endpoint in wanted)
        {
            Endpoints.Add(endpoint);
        }
    }

    private string LabelFor(string endpoint)
    {
        var address = endpoint[..endpoint.LastIndexOf(':')];
        return address == "127.0.0.1" ? "Cet ordinateur" : Catalog.NameForAddress(address) ?? "Réseau local";
    }

    partial void OnSelectedStrategyChanged(Option<DispatchStrategy> value)
    {
        OnPropertyChanged(nameof(StrategyHelp));
        _requestSave();
    }

    partial void OnAllowLanChanged(bool value)
    {
        OnPropertyChanged(nameof(ScopeText));
        OnPropertyChanged(nameof(ShowOpenProxyWarning));
        _requestSave();
    }

    partial void OnUseAuthenticationChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowOpenProxyWarning));
        _requestSave();
    }

    partial void OnPortChanged(decimal? value)
    {
        OnPropertyChanged(nameof(PortError));
        OnPropertyChanged(nameof(HasPortError));
        OnPropertyChanged(nameof(CanStart));
        _requestSave();
    }

    partial void OnStickyDestinationsChanged(bool value) => _requestSave();

    partial void OnStickyMinutesChanged(decimal? value) => _requestSave();

    partial void OnHealthProbeChanged(bool value) => _requestSave();

    partial void OnUsernameChanged(string value) => _requestSave();

    partial void OnPasswordChanged(string value) => _requestSave();
}
