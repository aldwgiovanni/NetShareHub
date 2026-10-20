using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NetShareHub.Application.Mapping;
using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Entities;

namespace NetShareHub.Desktop.ViewModels;

/// <summary>
/// Liste partagée des adaptateurs et de leur sélection. Les onglets Partage et Dispatch lisent la même sélection :
/// c'est ce qui fait de l'application un seul outil plutôt que deux.
/// </summary>
public sealed partial class AdapterCatalog : ObservableObject
{
    private readonly List<AdapterItemViewModel> _all = new();
    private HashSet<string> _restoredSources = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _restoredTargets = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int> _restoredWeights = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasRestoredSelection;

    [ObservableProperty]
    private bool _showVirtual;

    /// <summary>Vrai pendant un partage ou un Dispatch actif : la sélection et les poids sont figés.</summary>
    [ObservableProperty]
    private bool _isLocked;

    public ObservableCollection<AdapterItemViewModel> Sources { get; } = new();

    public ObservableCollection<AdapterItemViewModel> Targets { get; } = new();

    public ObservableCollection<AdapterItemViewModel> WifiAdapters { get; } = new();

    public IEnumerable<AdapterItemViewModel> SelectedSources => _all.Where(a => a.IsSource && a.CanBeSource);

    public IEnumerable<AdapterItemViewModel> SelectedTargets => _all.Where(a => a.IsTarget && a.CanBeTarget);

    public bool HasSources => Sources.Count > 0;

    public bool HasTargets => Targets.Count > 0;

    /// <summary>Levé quand la sélection ou un poids change (pour enregistrer les préférences).</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>
    /// Levé après une actualisation des adaptateurs : la sélection effective (cochée et disponible) a pu changer.
    /// Ne demande pas d'enregistrement, pour ne pas oublier la sélection d'un adaptateur momentanément absent.
    /// </summary>
    public event EventHandler? AvailabilityChanged;

    /// <summary>Sources à enregistrer : celles cochées, plus celles mémorisées dont l'adaptateur est absent (USB débranché…).</summary>
    public IReadOnlyList<string> RememberedSourceIds => Remembered(_restoredSources, SelectedSources);

    /// <summary>Cibles à enregistrer : celles cochées, plus celles mémorisées dont l'adaptateur est absent.</summary>
    public IReadOnlyList<string> RememberedTargetIds => Remembered(_restoredTargets, SelectedTargets);

    /// <summary>Poids des sources à enregistrer, y compris ceux des adaptateurs absents.</summary>
    public Dictionary<string, int> RememberedSourceWeights
    {
        get
        {
            var present = _all.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
            return RememberedSourceIds.ToDictionary(
                id => id,
                id => present.TryGetValue(id, out var item) ? item.WeightValue : Math.Clamp(_restoredWeights.GetValueOrDefault(id, 1), 1, 10),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Restore(SharingRequest_InDto sharing, DispatchConfig_InDto dispatch)
    {
        // Tolère des préférences partielles (listes absentes ou à trous) : elles ne doivent pas empêcher le démarrage.
        _restoredSources = new HashSet<string>((sharing.SourceAdapterIds ?? new()).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        _restoredTargets = new HashSet<string>((sharing.TargetAdapterIds ?? new()).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        _restoredWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, weight) in sharing.SourceWeights ?? new())
        {
            _restoredWeights[id] = weight;
        }

        foreach (var uplink in (dispatch.Uplinks ?? new()).OfType<DispatchUplink_InDto>())
        {
            if (!string.IsNullOrEmpty(uplink.AdapterId))
            {
                _restoredWeights.TryAdd(uplink.AdapterId, uplink.Weight);
            }
        }

        _hasRestoredSelection = _restoredSources.Count > 0 || _restoredTargets.Count > 0;
    }

    public void Apply(IReadOnlyList<NetworkAdapter> adapters)
    {
        var incoming = adapters.Where(a => !a.IsLoopback).Select(a => a.ToOutDto()).ToList();
        var existing = _all.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var dto in incoming)
        {
            if (existing.TryGetValue(dto.Id, out var item))
            {
                item.Update(dto);
                continue;
            }

            item = new AdapterItemViewModel(dto) { IsLocked = IsLocked };
            if (_hasRestoredSelection)
            {
                item.IsSource = _restoredSources.Contains(dto.Id) && dto.CanBeSource;
                item.IsTarget = _restoredTargets.Contains(dto.Id) && dto.CanBeTarget;
            }
            else
            {
                // Première utilisation : toutes les connexions Internet réelles sont cochées.
                item.IsSource = dto.CanBeSource && !dto.IsVirtual;
                if (item.IsSource)
                {
                    _restoredSources.Add(dto.Id);
                }
            }

            if (_restoredWeights.TryGetValue(dto.Id, out var weight))
            {
                item.Weight = Math.Clamp(weight, 1, 10);
            }

            item.PropertyChanged += OnItemChanged;
            _all.Add(item);
        }

        var incomingIds = incoming.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _all.Where(a => !incomingIds.Contains(a.Id)).ToList())
        {
            gone.PropertyChanged -= OnItemChanged;
            _all.Remove(gone);
        }

        _hasRestoredSelection = _restoredSources.Count > 0 || _restoredTargets.Count > 0;
        Rebuild();
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Met à jour les débits et la santé affichés sur chaque ligne.</summary>
    public void ApplyLive(IReadOnlyList<NetShareHub.Domain.Dtos.Outgoing.UplinkStats_OutDto> stats)
    {
        var byId = stats.ToDictionary(s => s.AdapterId, StringComparer.OrdinalIgnoreCase);
        foreach (var item in _all)
        {
            item.SetLive(byId.GetValueOrDefault(item.Id));
        }
    }

    partial void OnShowVirtualChanged(bool value) => Rebuild();

    partial void OnIsLockedChanged(bool value)
    {
        foreach (var item in _all)
        {
            item.IsLocked = value;
        }
    }

    /// <summary>Nom de l'adaptateur qui porte cette adresse IPv4 (pour libeller les adresses du proxy).</summary>
    public string? NameForAddress(string ipv4)
        => _all.FirstOrDefault(a => a.Adapter.IPv4 == ipv4)?.Name;

    private void Rebuild()
    {
        bool Visible(AdapterItemViewModel a) => ShowVirtual || !a.IsVirtual;

        Sync(Sources, _all.Where(a => a.CanBeSource && Visible(a)));
        Sync(Targets, _all.Where(a => a.CanBeTarget && Visible(a)));
        Sync(WifiAdapters, _all.Where(a => a.CanHostAccessPoint && !a.IsVirtual));

        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(HasTargets));
    }

    // Synchronise en place (sans vider) pour que l'interface ne scintille pas à chaque actualisation.
    private static void Sync(ObservableCollection<AdapterItemViewModel> target, IEnumerable<AdapterItemViewModel> wanted)
    {
        var list = wanted.ToList();
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!list.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (i >= target.Count)
            {
                target.Add(list[i]);
            }
            else if (!ReferenceEquals(target[i], list[i]))
            {
                var current = target.IndexOf(list[i]);
                if (current >= 0)
                {
                    target.Move(current, i);
                }
                else
                {
                    target.Insert(i, list[i]);
                }
            }
        }
    }

    // Ordre de l'interface d'abord, puis les adaptateurs mémorisés mais absents ou momentanément sans Internet.
    private static IReadOnlyList<string> Remembered(HashSet<string> remembered, IEnumerable<AdapterItemViewModel> selected)
        => selected.Select(a => a.Id)
            .Concat(remembered)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not AdapterItemViewModel item)
        {
            return;
        }

        // La sélection mémorisée suit l'utilisateur : un adaptateur rebranché retrouve son dernier état, pas celui du lancement.
        switch (e.PropertyName)
        {
            case nameof(AdapterItemViewModel.IsSource):
                Remember(_restoredSources, item.Id, item.IsSource);
                break;
            case nameof(AdapterItemViewModel.IsTarget):
                Remember(_restoredTargets, item.Id, item.IsTarget);
                break;
            case nameof(AdapterItemViewModel.Weight):
                _restoredWeights[item.Id] = item.WeightValue;
                break;
            default:
                return;
        }

        _hasRestoredSelection = _restoredSources.Count > 0 || _restoredTargets.Count > 0;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Remember(HashSet<string> set, string id, bool selected)
    {
        if (selected)
        {
            set.Add(id);
        }
        else
        {
            set.Remove(id);
        }
    }
}
