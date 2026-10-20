using NetShareHub.Domain.Exceptions;
using NetShareHub.Domain.ValueObjects;

namespace NetShareHub.Application.Services;

/// <summary>Réserve un bloc d'adresses privées qui ne chevauche aucun réseau existant.</summary>
public static class SubnetAllocator
{
    private const int FirstSecondOctet = 77;
    private const int SecondOctetAttempts = 60;

    /// <summary>
    /// Alloue un bloc 10.X.Y.0 aligné pouvant contenir <paramref name="count"/> sous-réseaux /24 (arrondi à la puissance de 2 supérieure).
    /// Les cibles reçoivent chacune un /24 du bloc ; le bloc entier sert de préfixe interne au NAT Windows.
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

        var used = inUse.ToList();
        for (var second = FirstSecondOctet; second < FirstSecondOctet + SecondOctetAttempts; second++)
        {
            for (var third = 0; third < 256; third += slots)
            {
                var candidate = new Ipv4Subnet((10u << 24) | ((uint)second << 16) | ((uint)third << 8), prefix);
                if (!used.Any(candidate.Overlaps))
                {
                    return candidate;
                }
            }
        }

        throw new SharingException("Impossible de trouver un bloc d'adresses privées libre pour le partage.");
    }
}
