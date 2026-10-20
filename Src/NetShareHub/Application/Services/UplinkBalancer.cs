using NetShareHub.Domain.Enums;

namespace NetShareHub.Application.Services;

/// <summary>
/// Choisit la liaison montante de chaque nouvelle connexion. Logique pure et thread-safe :
/// le proxy appelle <see cref="Acquire"/> avant de se connecter et <see cref="Release"/> quand la connexion se termine ou échoue.
/// </summary>
public sealed class UplinkBalancer
{
    private const int MaxStickyEntries = 4096;

    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, StickyEntry> _sticky = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan? _stickyFor;

    public UplinkBalancer(DispatchStrategy strategy, TimeSpan? stickyFor = null, Func<DateTimeOffset>? clock = null)
    {
        Strategy = strategy;
        _stickyFor = stickyFor;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public DispatchStrategy Strategy { get; }

    /// <summary>Définit les liaisons, par ordre de priorité (l'ordre compte pour le mode Failover).</summary>
    public void SetUplinks(IEnumerable<(string AdapterId, int Weight, bool Enabled)> uplinks)
    {
        lock (_gate)
        {
            var previous = _entries.ToDictionary(e => e.AdapterId, StringComparer.OrdinalIgnoreCase);
            _entries.Clear();
            foreach (var (id, weight, enabled) in uplinks)
            {
                var entry = previous.TryGetValue(id, out var old) ? old : new Entry(id);
                entry.Weight = Math.Max(1, weight);
                entry.Enabled = enabled;
                _entries.Add(entry);
            }

            _sticky.Clear();
        }
    }

    public void SetHealth(string adapterId, UplinkHealth health)
    {
        lock (_gate)
        {
            var entry = Find(adapterId);
            if (entry is not null)
            {
                entry.Health = health;
            }
        }
    }

    public UplinkHealth GetHealth(string adapterId)
    {
        lock (_gate)
        {
            return Find(adapterId)?.Health ?? UplinkHealth.Unknown;
        }
    }

    public int GetActive(string adapterId)
    {
        lock (_gate)
        {
            return Find(adapterId)?.Active ?? 0;
        }
    }

    /// <summary>
    /// Réserve une liaison pour une nouvelle connexion.
    /// </summary>
    /// <param name="stickyKey">Destination (ex. nom d'hôte) : si l'affinité est activée, elle reste sur la même liaison.</param>
    /// <param name="exclude">Liaisons déjà essayées pour cette connexion.</param>
    /// <returns>Identifiant de l'adaptateur, ou <c>null</c> si aucune liaison n'est utilisable.</returns>
    public string? Acquire(string? stickyKey = null, IReadOnlySet<string>? exclude = null)
    {
        lock (_gate)
        {
            var candidates = _entries
                .Where(e => e.Enabled && e.Health != UplinkHealth.Down && (exclude is null || !exclude.Contains(e.AdapterId)))
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            var now = _clock();
            Entry? chosen = null;

            if (_stickyFor is { } span && stickyKey is not null && _sticky.TryGetValue(stickyKey, out var sticky))
            {
                if (sticky.Expires > now)
                {
                    chosen = candidates.FirstOrDefault(c => c.AdapterId.Equals(sticky.AdapterId, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    _sticky.Remove(stickyKey);
                }
            }

            chosen ??= Strategy switch
            {
                DispatchStrategy.Failover => candidates[0],
                DispatchStrategy.LeastConnections => PickLeastLoaded(candidates),
                _ => PickSmoothWeighted(candidates),
            };

            if (_stickyFor is { } keep && stickyKey is not null)
            {
                if (_sticky.Count >= MaxStickyEntries)
                {
                    foreach (var key in _sticky.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList())
                    {
                        _sticky.Remove(key);
                    }

                    if (_sticky.Count >= MaxStickyEntries)
                    {
                        _sticky.Clear();
                    }
                }

                _sticky[stickyKey] = new StickyEntry(chosen.AdapterId, now + keep);
            }

            chosen.Active++;
            return chosen.AdapterId;
        }
    }

    /// <summary>Libère la réservation faite par <see cref="Acquire"/>.</summary>
    public void Release(string adapterId)
    {
        lock (_gate)
        {
            var entry = Find(adapterId);
            if (entry is { Active: > 0 })
            {
                entry.Active--;
            }
        }
    }

    /// <summary>Oublie l'affinité de destinations associées à une liaison tombée.</summary>
    public void ForgetSticky(string adapterId)
    {
        lock (_gate)
        {
            foreach (var key in _sticky.Where(kv => kv.Value.AdapterId.Equals(adapterId, StringComparison.OrdinalIgnoreCase))
                         .Select(kv => kv.Key).ToList())
            {
                _sticky.Remove(key);
            }
        }
    }

    private static Entry PickLeastLoaded(List<Entry> candidates)
    {
        var best = candidates[0];
        var bestScore = (best.Active + 1.0) / best.Weight;
        foreach (var candidate in candidates.Skip(1))
        {
            var score = (candidate.Active + 1.0) / candidate.Weight;
            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    // « Smooth weighted round-robin » (nginx) : répartition proportionnelle aux poids, sans rafales sur une même liaison.
    private static Entry PickSmoothWeighted(List<Entry> candidates)
    {
        var total = 0;
        Entry? best = null;
        foreach (var candidate in candidates)
        {
            candidate.Current += candidate.Weight;
            total += candidate.Weight;
            if (best is null || candidate.Current > best.Current)
            {
                best = candidate;
            }
        }

        best!.Current -= total;
        return best;
    }

    private Entry? Find(string adapterId)
        => _entries.FirstOrDefault(e => e.AdapterId.Equals(adapterId, StringComparison.OrdinalIgnoreCase));

    private sealed class Entry(string adapterId)
    {
        public string AdapterId { get; } = adapterId;

        public int Weight { get; set; } = 1;

        public bool Enabled { get; set; } = true;

        public UplinkHealth Health { get; set; } = UplinkHealth.Unknown;

        public int Active { get; set; }

        public int Current { get; set; }
    }

    private sealed record StickyEntry(string AdapterId, DateTimeOffset Expires);
}
