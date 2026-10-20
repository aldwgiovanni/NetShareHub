using NetShareHub.Domain.Exceptions;
using NetShareHub.Domain.ValueObjects;

namespace NetShareHub.Application.Services;

/// <summary>Réserve un bloc d'adresses privées qui ne chevauche aucun réseau existant.</summary>
public static class SubnetAllocator
{
    private const int FirstSecondOctet = 77;
    private const int SecondOctetAttempts = 60;

    // Réseau du partage ICS et du Point d'accès mobile de Windows : jamais pour le NAT intégré, même libre au moment du plan.
    private static readonly Ipv4Subnet WindowsSharingSubnet = Ipv4Subnet.Parse("192.168.137.0/24");

    /// <summary>
    /// Alloue un bloc X.Y.Z.0 aligné pouvant contenir <paramref name="count"/> sous-réseaux /24 (arrondi à la puissance de 2 supérieure).
    /// Les cibles reçoivent chacune un /24 du bloc ; le bloc entier sert de préfixe interne au NAT Windows.
    /// On cherche d'abord dans 10.77–136.x.x, puis dans 172.16.0.0/12 et 192.168.0.0/16 : un réseau d'entreprise ou un VPN
    /// en 10.0.0.0/8 ne doit pas rendre le partage impossible.
    /// </summary>
    public static Ipv4Subnet AllocateBlock(int count, IEnumerable<Ipv4Subnet> inUse)
    {
        if (count is < 1 or > 16)
        {
            throw new SharingException("Le nombre de cibles NAT doit être compris entre 1 et 16.");
        }

        var slots = 1;
        var prefix = 24;
        while (slots < count)
        {
            slots *= 2;
            prefix--;
        }

        var used = inUse.Append(WindowsSharingSubnet).ToList();
        foreach (var candidate in Candidates(slots, prefix))
        {
            if (!used.Any(candidate.Overlaps))
            {
                return candidate;
            }
        }

        throw new SharingException("Impossible de trouver un bloc d'adresses privées libre pour le partage.");
    }

    private static IEnumerable<Ipv4Subnet> Candidates(int slots, int prefix)
    {
        static uint Address(uint first, int second, int third) => (first << 24) | ((uint)second << 16) | ((uint)third << 8);

        var seconds = Enumerable.Range(FirstSecondOctet, SecondOctetAttempts).Select(s => (First: 10u, Second: s))
            .Concat(Enumerable.Range(16, 16).Select(s => (First: 172u, Second: s)))
            .Append((First: 192u, Second: 168));

        foreach (var (first, second) in seconds)
        {
            for (var third = 0; third < 256; third += slots)
            {
                yield return new Ipv4Subnet(Address(first, second, third), prefix);
            }
        }
    }
}
