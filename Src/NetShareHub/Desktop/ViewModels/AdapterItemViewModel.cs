using CommunityToolkit.Mvvm.ComponentModel;
using NetShareHub.Domain.Dtos.Outgoing;
using NetShareHub.Domain.Enums;

namespace NetShareHub.Desktop.ViewModels;

/// <summary>Un adaptateur réseau dans l'interface : identité, sélection (source / cible), poids et débits en direct.</summary>
public sealed partial class AdapterItemViewModel : ObservableObject
{
    [ObservableProperty]
    private NetworkAdapter_OutDto _adapter;

    /// <summary>Cochée : cette connexion est partagée / utilisée par le Dispatch.</summary>
    [ObservableProperty]
    private bool _isSource;

    /// <summary>Cochée : cet adaptateur reçoit la connexion partagée.</summary>
    [ObservableProperty]
    private bool _isTarget;

    /// <summary>Poids de répartition (1 à 10).</summary>
    [ObservableProperty]
    private double _weight = 1;

    [ObservableProperty]
    private UplinkHealth _health = UplinkHealth.Unknown;

    [ObservableProperty]
    private string _liveText = string.Empty;

    [ObservableProperty]
    private int _activeConnections;

    /// <summary>Vrai pendant qu'un partage ou le Dispatch tourne : la sélection n'est alors plus modifiable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(CanSelectTarget))]
    private bool _isLocked;

    public AdapterItemViewModel(NetworkAdapter_OutDto adapter)
    {
        _adapter = adapter;
    }

    public string Id => Adapter.Id;

    public string Name => Adapter.Name;

    public bool IsUp => Adapter.IsUp;

    public bool IsVirtual => Adapter.IsVirtual;

    public bool CanBeSource => Adapter.CanBeSource;

    public bool CanBeTarget => Adapter.CanBeTarget;

    public bool CanHostAccessPoint => Adapter.CanHostAccessPoint;

    public string KindText => Formatting.Kind(Adapter.Kind, Adapter.IsVirtual);

    public string Address
        => string.IsNullOrEmpty(Adapter.IPv4) ? "Pas d'adresse IP" : Adapter.IPv4;

    /// <summary>Ligne de détails : passerelle, vitesse de liaison.</summary>
    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Adapter.Gateway))
            {
                parts.Add($"passerelle {Adapter.Gateway}");
            }

            var speed = Formatting.LinkSpeed(Adapter.SpeedBitsPerSecond);
            if (speed.Length > 0)
            {
                parts.Add(speed);
            }

            return string.Join(" · ", parts);
        }
    }

    public bool HasDetails => Details.Length > 0;

    public bool CanEdit => !IsLocked;

    /// <summary>Un adaptateur débranché ne peut pas recevoir de partage.</summary>
    public bool CanSelectTarget => !IsLocked && IsUp;

    public string TargetStatus
        => Adapter.IsUp ? "Connecté" : "Non connecté (câble débranché ?)";

    public string WeightText => $"Poids {(int)Weight}";

    public bool IsHealthy => Health == UplinkHealth.Healthy;

    public bool IsDown => Health == UplinkHealth.Down;

    public int WeightValue => Math.Clamp((int)Math.Round(Weight), 1, 10);

    public void Update(NetworkAdapter_OutDto adapter)
    {
        Adapter = adapter;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(IsUp));
        OnPropertyChanged(nameof(CanSelectTarget));
        OnPropertyChanged(nameof(IsVirtual));
        OnPropertyChanged(nameof(CanBeSource));
        OnPropertyChanged(nameof(CanBeTarget));
        OnPropertyChanged(nameof(CanHostAccessPoint));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(HasDetails));
        OnPropertyChanged(nameof(TargetStatus));
    }

    public void SetLive(UplinkStats_OutDto? stats)
    {
        if (stats is null)
        {
            Health = UplinkHealth.Unknown;
            LiveText = string.Empty;
            ActiveConnections = 0;
            return;
        }

        Health = stats.Health;
        ActiveConnections = stats.ActiveConnections;
        LiveText = $"↓ {Formatting.Rate(stats.ReceiveRateBytesPerSecond)}   ↑ {Formatting.Rate(stats.SendRateBytesPerSecond)}";
    }

    partial void OnIsSourceChanged(bool value)
    {
        // Une carte ne peut pas être à la fois la source et la destination d'un partage.
        if (value && IsTarget)
        {
            IsTarget = false;
        }
    }

    partial void OnIsTargetChanged(bool value)
    {
        if (value && IsSource)
        {
            IsSource = false;
        }
    }

    partial void OnWeightChanged(double value) => OnPropertyChanged(nameof(WeightText));

    partial void OnHealthChanged(UplinkHealth value)
    {
        OnPropertyChanged(nameof(IsHealthy));
        OnPropertyChanged(nameof(IsDown));
    }
}
