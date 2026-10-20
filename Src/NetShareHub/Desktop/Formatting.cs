using System.Globalization;
using System.Security.Cryptography;
using NetShareHub.Domain.Enums;

namespace NetShareHub.Desktop;

/// <summary>Mise en forme des valeurs pour l'affichage (français).</summary>
public static class Formatting
{
    // Format français explicite : l'application tourne en InvariantGlobalization (pas de dépendance à ICU sous Linux),
    // donc CultureInfo("fr-FR") ne donnerait pas la virgule décimale.
    private static readonly NumberFormatInfo Fr = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = " " };

    public static string Integer(long value) => value.ToString("N0", Fr);

    public static string Bytes(long bytes) => Scale(bytes, new[] { "o", "Ko", "Mo", "Go", "To" });

    public static string Rate(double bytesPerSecond)
        => bytesPerSecond < 1 ? "0 o/s" : Scale(bytesPerSecond, new[] { "o/s", "Ko/s", "Mo/s", "Go/s" });

    public static string LinkSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            return string.Empty;
        }

        return bitsPerSecond >= 1_000_000_000
            ? $"{(bitsPerSecond / 1_000_000_000d).ToString("0.#", Fr)} Gbit/s"
            : $"{(bitsPerSecond / 1_000_000d).ToString("0.#", Fr)} Mbit/s";
    }

    public static string Kind(AdapterKind kind, bool isVirtual)
        => isVirtual
            ? "Virtuel"
            : kind switch
            {
                AdapterKind.WiFi => "Wi-Fi",
                AdapterKind.Ethernet => "Ethernet",
                AdapterKind.Cellular => "Mobile",
                AdapterKind.Bluetooth => "Bluetooth",
                _ => "Autre",
            };

    public static string Health(UplinkHealth health, int? latencyMs) => health switch
    {
        UplinkHealth.Healthy => latencyMs is { } ms ? $"En ligne · {ms} ms" : "En ligne",
        UplinkHealth.Down => "Hors service",
        _ => "En attente",
    };

    public static string Strategy(DispatchStrategy strategy) => strategy switch
    {
        DispatchStrategy.WeightedRoundRobin => "Répartition équilibrée",
        DispatchStrategy.LeastConnections => "Moins chargée d'abord",
        DispatchStrategy.Failover => "Secours (basculement)",
        _ => strategy.ToString(),
    };

    /// <summary>Mot de passe lisible (sans caractères ambigus) pour le hotspot par défaut.</summary>
    public static string RandomPassword(int length = 12)
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzACDEFGHJKLMNPQRTUVWXYZ2346789";
        return string.Create(length, alphabet, (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            }
        });
    }

    private static string Scale(double value, string[] units)
    {
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        var format = index == 0 || value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return $"{value.ToString(format, Fr)} {units[index]}";
    }
}

/// <summary>Élément de liste déroulante : une valeur, un libellé, une aide courte.</summary>
public sealed record Option<T>(T Value, string Label, string Description = "")
{
    public override string ToString() => Label;
}
