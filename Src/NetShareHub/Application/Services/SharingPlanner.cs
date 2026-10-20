using System.Text;
using NetShareHub.Application.Contracts.Services;
using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Entities;
using NetShareHub.Domain.Enums;
using NetShareHub.Domain.Exceptions;
using NetShareHub.Domain.ValueObjects;

namespace NetShareHub.Application.Services;

/// <summary>
/// Valide une demande de partage et choisit le mécanisme système adapté à chaque cible.
/// Logique pure (aucun accès système) : entièrement testable.
/// </summary>
public sealed class SharingPlanner
{
    /// <param name="occupiedSubnets">Sous-réseaux déjà pris par le système au-delà des adaptateurs (ex. NAT Windows existants).</param>
    public SharingPlan Plan(
        SharingRequest_InDto request,
        IReadOnlyList<NetworkAdapter> adapters,
        BackendCapabilities capabilities,
        IReadOnlyCollection<Ipv4Subnet>? occupiedSubnets = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var byId = adapters.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

        if (capabilities.Platform == HostPlatform.Unsupported)
        {
            throw new SharingException("Système non pris en charge : seuls Windows et Linux sont gérés.");
        }

        // --- Sources -------------------------------------------------------------------------
        var sources = new List<NetworkAdapter>();
        foreach (var id in request.SourceAdapterIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!byId.TryGetValue(id, out var source))
            {
                errors.Add($"Source introuvable (l'adaptateur a disparu) : {id}");
            }
            else if (!source.HasInternetRoute)
            {
                errors.Add($"« {source.Name} » n'a pas de connexion Internet active (adresse IP ou passerelle manquante).");
            }
            else
            {
                sources.Add(source);
            }
        }

        if (request.SourceAdapterIds.Count == 0)
        {
            errors.Add("Choisissez au moins une connexion Internet à partager.");
        }

        // --- Cibles --------------------------------------------------------------------------
        var targets = new List<NetworkAdapter>();
        foreach (var id in request.TargetAdapterIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!byId.TryGetValue(id, out var target))
            {
                errors.Add($"Cible introuvable (l'adaptateur a disparu) : {id}");
                continue;
            }

            if (sources.Any(s => s.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase)) ||
                request.SourceAdapterIds.Contains(target.Id, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"« {target.Name} » ne peut pas être à la fois source et cible.");
            }
            else if (target.IsLoopback)
            {
                errors.Add($"« {target.Name} » est une interface de bouclage.");
            }
            else if (target.Kind == AdapterKind.WiFi)
            {
                errors.Add($"« {target.Name} » est une carte Wi-Fi : activez l'option « Hotspot Wi-Fi » pour la partager.");
            }
            else if (!target.IsUp)
            {
                errors.Add($"« {target.Name} » n'est pas connecté : branchez le câble (ou activez la carte), puis réessayez.");
            }
            else
            {
                if (target.IsVirtual)
                {
                    warnings.Add($"« {target.Name} » est un adaptateur virtuel (VM, VPN, conteneur) : vérifiez que c'est bien voulu.");
                }

                targets.Add(target);
            }
        }

        // --- Hotspot -------------------------------------------------------------------------
        WifiHotspotPlan? hotspot = null;
        if (request.Hotspot is { } hs)
        {
            hotspot = PlanHotspot(hs, byId, sources, capabilities, errors);
        }

        if (targets.Count == 0 && hotspot is null)
        {
            errors.Add("Choisissez au moins une destination : un adaptateur ou un hotspot Wi-Fi.");
        }

        if (errors.Count > 0)
        {
            throw new SharingException(string.Join(Environment.NewLine, errors));
        }

        // --- Sources : poids et source principale ----------------------------------------------
        var weights = sources.ToDictionary(
            s => s.Id,
            s => Math.Clamp(request.SourceWeights.TryGetValue(s.Id, out var w) ? w : 1, 1, 100),
            StringComparer.OrdinalIgnoreCase);

        sources = sources
            .OrderByDescending(s => weights[s.Id])
            .ThenByDescending(s => s.SpeedBitsPerSecond)
            .ToList();

        // --- Mécanismes ------------------------------------------------------------------------
        var plannedTargets = new List<ShareTargetPlan>();
        var natTargets = new List<NetworkAdapter>();

        foreach (var target in targets)
        {
            var mechanism = capabilities.Platform switch
            {
                HostPlatform.Windows when hotspot is null && targets.Count == 1 => SharingMechanism.WindowsIcs,
                HostPlatform.Windows => SharingMechanism.WindowsNat,
                _ when capabilities.NetworkManagerAvailable => SharingMechanism.LinuxNetworkManager,
                _ => SharingMechanism.LinuxNat,
            };

            if (mechanism is SharingMechanism.WindowsNat or SharingMechanism.LinuxNat)
            {
                natTargets.Add(target);
            }

            plannedTargets.Add(new ShareTargetPlan(target, mechanism, null));
        }

        Ipv4Subnet? natBlock = null;
        if (natTargets.Count > 0)
        {
            var inUse = adapters.Select(a => a.Subnet).OfType<Ipv4Subnet>()
                .Concat(occupiedSubnets ?? Array.Empty<Ipv4Subnet>());
            natBlock = SubnetAllocator.AllocateBlock(natTargets.Count, inUse);

            var index = 0;
            for (var i = 0; i < plannedTargets.Count; i++)
            {
                if (plannedTargets[i].Mechanism is SharingMechanism.WindowsNat or SharingMechanism.LinuxNat)
                {
                    plannedTargets[i] = plannedTargets[i] with { Subnet = natBlock.Value.Sub(24, index++) };
                }
            }

            if (capabilities.Platform == HostPlatform.Windows && natTargets.Count > 1)
            {
                warnings.Add("Partage vers plusieurs adaptateurs sous Windows : mode NAT intégré à NetShare Hub (expérimental).");
            }
        }

        // --- Agrégation des sources ------------------------------------------------------------
        var balance = request.BalanceSources && sources.Count > 1;
        if (balance && !capabilities.SupportsTransparentBalancing)
        {
            balance = false;
            warnings.Add(
                $"Plusieurs sources : {sources[0].Name} sert au partage ; les appareils connectés ne sont pas répartis automatiquement " +
                "sous ce système. Utilisez le proxy Dispatch (onglet Dispatch) pour agréger toutes les liaisons.");
        }

        return new SharingPlan(sources, weights, plannedTargets, hotspot, balance, natBlock, warnings);
    }

    private static WifiHotspotPlan? PlanHotspot(
        WifiHotspot_InDto request,
        IReadOnlyDictionary<string, NetworkAdapter> byId,
        IReadOnlyList<NetworkAdapter> sources,
        BackendCapabilities capabilities,
        List<string> errors)
    {
        var ssidBytes = Encoding.UTF8.GetByteCount(request.Ssid ?? string.Empty);
        if (ssidBytes is < 1 or > 32)
        {
            errors.Add("Le nom du réseau Wi-Fi (SSID) doit faire entre 1 et 32 octets.");
        }

        var password = request.Password ?? string.Empty;
        if (password.Length is < 8 or > 63 || password.Any(c => c < 32 || c > 126))
        {
            errors.Add("Le mot de passe Wi-Fi doit contenir de 8 à 63 caractères ASCII imprimables.");
        }

        if (!capabilities.SupportsWifiHotspot)
        {
            errors.Add("Le hotspot Wi-Fi n'est pas disponible sur cette machine : " +
                       (capabilities.Platform == HostPlatform.Linux
                           ? "il faut NetworkManager et une carte Wi-Fi compatible mode point d'accès."
                           : "carte Wi-Fi absente ou pilote incompatible avec le Point d'accès mobile."));
            return null;
        }

        NetworkAdapter? adapter = null;
        if (capabilities.Platform == HostPlatform.Linux)
        {
            if (string.IsNullOrEmpty(request.AdapterId) || !byId.TryGetValue(request.AdapterId, out adapter))
            {
                errors.Add("Choisissez la carte Wi-Fi qui émettra le hotspot.");
                return null;
            }

            if (adapter.Kind != AdapterKind.WiFi)
            {
                errors.Add($"« {adapter.Name} » n'est pas une carte Wi-Fi.");
            }

            if (sources.Any(s => s.Id.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"« {adapter.Name} » est utilisée comme source Internet : elle ne peut pas aussi émettre le hotspot. " +
                           "Utilisez une seconde carte Wi-Fi ou une source filaire.");
            }
        }
        else if (sources.Count > 0 && sources.All(s => s.Kind == AdapterKind.WiFi))
        {
            // Le Point d'accès mobile de Windows sait émettre sur la même radio, mais le débit est partagé.
            // Ce n'est pas une erreur : on laisse passer, l'interface en avertit.
        }

        return new WifiHotspotPlan(
            request.Ssid ?? string.Empty,
            password,
            request.Band,
            adapter,
            capabilities.Platform == HostPlatform.Windows ? SharingMechanism.WindowsMobileHotspot : SharingMechanism.LinuxNetworkManager);
    }
}
