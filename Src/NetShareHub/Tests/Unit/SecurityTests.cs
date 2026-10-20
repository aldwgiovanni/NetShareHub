using System.Diagnostics;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using NetShareHub.Application.Contracts.Services;
using NetShareHub.Desktop.Logging;
using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Infrastructure.Repositories;
using NetShareHub.Infrastructure.Services;
using NetShareHub.Infrastructure.Services.Dispatch;
using NetShareHub.Infrastructure.Services.Sharing;
using Microsoft.Extensions.Logging;

namespace NetShareHub.Tests.Unit;

/// <summary>Journal d'annulation rejoué en administrateur : ni commande libre, ni journal déposé par un compte standard.</summary>
public class UndoJournalSecurityTests
{
    [Fact]
    public async Task Un_journal_a_l_ancien_format_avec_une_commande_libre_n_est_jamais_execute()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "j.json");
        File.WriteAllText(path, """[{"FileName":"calc.exe","Arguments":["/c","whoami"],"StdIn":null,"TimeoutSeconds":30,"Description":"x","Script":null}]""");
        var runner = new RecordingRunner();

        var journal = new UndoJournal(path, runner, Logs.For<UndoJournalSecurityTests>(), enforceSecurity: false);
        await journal.UndoAllAsync();

        Assert.Empty(runner.Commands);
        Assert.False(File.Exists(path)); // jamais rejouable : supprimé
    }

    [Theory]
    [InlineData("""{"Kind":"LinuxConnectDevice","Name":"eth0; rm -rf /"}""")]
    [InlineData("""{"Kind":"LinuxConnectDevice","Name":"--help"}""")]
    [InlineData("""{"Kind":"LinuxRestoreSysctl","Name":"kernel.core_pattern","Value":"|/tmp/x"}""")]
    [InlineData("""{"Kind":"LinuxRestoreSysctl","Name":"net.ipv4.ip_forward","Value":"1 kernel.x=2"}""")]
    [InlineData("""{"Kind":"LinuxDeleteConnection","Value":"Wired connection 1"}""")]
    [InlineData("""{"Kind":"LinuxDeleteAddress","Name":"eth0","Value":"10.0.0.1/24 dev lo"}""")]
    [InlineData("""{"Kind":"LinuxDeleteFirewallRule","Name":"eth0","Value":"0.0.0.0/0 -j DROP","Rule":"Masquerade"}""")]
    [InlineData("""{"Kind":"WindowsSetForwarding","Index":-1,"Name":"Ethernet"}""")]
    [InlineData("""{"Kind":"WindowsRestoreInterface","Index":5,"Name":"Ethernet","State":{"Index":5,"Dhcp":false,"Forwarding":false,"AutoMetric":true,"Metric":1,"Addrs":[{"Ip":"1.2.3.4'; calc; '","Prefix":24}],"Gateway":null}}""")]
    [InlineData("""{"Kind":"WindowsRestoreInterface","Index":5,"Name":"Ethernet","State":{"Index":6,"Dhcp":true,"Forwarding":false,"AutoMetric":true,"Metric":1,"Addrs":[],"Gateway":null}}""")]
    public async Task Une_etape_aux_parametres_invalides_est_ignoree(string step)
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "j.json");
        File.WriteAllText(path, "[" + step + "]");
        var runner = new RecordingRunner();

        var journal = new UndoJournal(path, runner, Logs.For<UndoJournalSecurityTests>(), enforceSecurity: false);
        await journal.UndoAllAsync();

        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Une_operation_inconnue_rend_le_journal_illisible()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "j.json");
        File.WriteAllText(path, """[{"Kind":"RunProgram","Name":"calc.exe"}]""");
        var runner = new RecordingRunner();

        var journal = new UndoJournal(path, runner, Logs.For<UndoJournalSecurityTests>(), enforceSecurity: false);
        Assert.False(journal.HasPending);
        await journal.UndoAllAsync();

        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Un_journal_depose_par_un_compte_standard_est_refuse()
    {
        if (Environment.IsPrivilegedProcess)
        {
            return; // en administrateur, le fichier de test appartiendrait justement aux Administrateurs
        }

        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "undo-windows.json");
        File.WriteAllText(path, """[{"Kind":"WindowsDisableIcs"}]""");
        var runner = new RecordingRunner();

        // Contrôles actifs, comme pour le journal système : le fichier appartient à l'utilisateur courant.
        var journal = new UndoJournal(path, runner, Logs.For<UndoJournalSecurityTests>());
        Assert.False(journal.HasPending);
        await journal.UndoAllAsync();
        Assert.Empty(runner.Commands);

        using var stream = File.Exists(path) ? File.OpenRead(path) : null;
        if (stream is not null)
        {
            Assert.False(JournalSecurity.IsTrustedJournal(stream, out var reason));
            Assert.NotNull(reason);
        }
    }

    [Fact]
    public void Le_dossier_du_journal_n_est_accessible_qu_a_SYSTEM_et_aux_administrateurs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var security = JournalSecurity.BuildDirectorySecurity();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

        Assert.True(security.AreAccessRulesProtected); // pas d'héritage de C:\ProgramData (Utilisateurs : création de fichiers)
        Assert.Equal(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), security.GetOwner(typeof(SecurityIdentifier)));
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
        Assert.Contains(rules, r => ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.LocalSystemSid));
        Assert.Contains(rules, r => ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
    }

    [Fact]
    public void Seuls_SYSTEM_et_les_Administrateurs_sont_des_proprietaires_de_confiance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(JournalSecurity.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.True(JournalSecurity.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.False(JournalSecurity.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
        Assert.False(JournalSecurity.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)));
        Assert.False(JournalSecurity.IsTrustedOwner(WindowsIdentity.GetCurrent().User));
        Assert.False(JournalSecurity.IsTrustedOwner(null));
    }

    [Fact]
    public void Un_dossier_de_journal_qui_est_une_jonction_est_refuse()
    {
        using var dir = new TempDirectory();
        var target = Directory.CreateDirectory(Path.Combine(dir.Path, "ailleurs")).FullName;
        var link = Path.Combine(dir.Path, "journal");
        using var linkScope = FileLinks.CreateDirectoryLink(link, target);

        Assert.False(JournalSecurity.TrySecureDirectory(link, out var reason));
        Assert.Contains("lien", reason);
    }
}

/// <summary>Données insérées dans les scripts PowerShell exécutés en administrateur.</summary>
public class PowerShellQuotingTests
{
    [Theory]
    [InlineData("Marie’s PC", "'Marie’’s PC'")]
    [InlineData("‘a’", "'‘‘a’’'")]
    [InlineData("b‚c‛", "'b‚‚c‛‛'")]
    [InlineData("l'été", "'l''été'")]
    public void Quote_double_aussi_les_apostrophes_typographiques(string value, string expected)
        => Assert.Equal(expected, WindowsScripts.Quote(value));

    [Fact]
    public async Task Les_valeurs_piegees_ressortent_a_l_identique_sans_rien_executer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Windows PowerShell 5.1 : la cible réelle de ces scripts
        }

        var payloads = new[]
        {
            "Marie’s PC",
            "x’; Write-Output INJECTED; #",
            "x‘; Write-Output INJECTED; #",
            "x‚; Write-Output INJECTED; #",
            "x‛; Write-Output INJECTED; #",
            "x'; Write-Output INJECTED; #",
            "’’’‘'",
        };
        var script = string.Join('\n', payloads.Select(p => $"Write-Output {WindowsScripts.Quote(p)}"));

        var result = await new CommandRunner(Logs.For<CommandRunner>()).RunAsync(CommandSpec.PowerShell(script, "Essai de guillemets"));

        Assert.True(result.Success, result.StdErr);
        var lines = result.StdOut.Replace("\r", string.Empty).TrimEnd('\n').Split('\n');
        Assert.Equal(payloads, lines);
        Assert.DoesNotContain("INJECTED", lines);
    }
}

/// <summary>En root (pkexec), les écritures dans le profil de l'utilisateur ne doivent pas suivre ses liens.</summary>
public class UserFileSecurityTests
{
    [Fact]
    public async Task Un_fichier_temporaire_restant_est_remplace_et_non_reutilise()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json.tmp"), "reste d'une écriture interrompue, beaucoup plus long que le JSON final ………………………………");
        var repository = new JsonAppSettingsRepository(Logs.For<JsonAppSettingsRepository>(), dir.Path);

        await repository.SaveAsync(new AppSettings_InDto { HotspotEnabled = true, Dispatch = new DispatchConfig_InDto { Port = 1234 } });

        Assert.False(File.Exists(Path.Combine(dir.Path, "settings.json.tmp")));
        var loaded = await repository.GetAsync();
        Assert.True(loaded.HotspotEnabled);
        Assert.Equal(1234, loaded.Dispatch.Port);
    }

    [Fact]
    public async Task Un_dossier_de_preferences_remplace_par_un_lien_n_est_pas_suivi()
    {
        using var dir = new TempDirectory();
        var target = Directory.CreateDirectory(Path.Combine(dir.Path, "dossier-systeme")).FullName;
        var link = Path.Combine(dir.Path, "netsharehub");
        using var linkScope = FileLinks.CreateDirectoryLink(link, target);

        await new JsonAppSettingsRepository(Logs.For<JsonAppSettingsRepository>(), link).SaveAsync(new AppSettings_InDto());

        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public void Un_dossier_de_journaux_remplace_par_un_lien_n_est_pas_suivi()
    {
        using var dir = new TempDirectory();
        var target = Directory.CreateDirectory(Path.Combine(dir.Path, "dossier-systeme")).FullName;
        var link = Path.Combine(dir.Path, "logs");
        using var linkScope = FileLinks.CreateDirectoryLink(link, target);

        new LogStore(link).Add(new LogEntry(DateTimeOffset.Now, LogLevel.Information, "test", "ligne"));

        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public void Le_journal_fichier_s_ajoute_au_fichier_existant()
    {
        using var dir = new TempDirectory();
        var store = new LogStore(dir.Path);

        store.Add(new LogEntry(DateTimeOffset.Now, LogLevel.Information, "test", "première"));
        store.Add(new LogEntry(DateTimeOffset.Now, LogLevel.Warning, "test", "seconde"));

        var lines = File.ReadAllLines(Path.Combine(dir.Path, "netsharehub.log"));
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("première", lines[0]);
        Assert.EndsWith("seconde", lines[1]);
    }

    [Theory]
    [InlineData(0x81A4, 1000u, 1000u, true)]  // fichier ordinaire 0644 de l'utilisateur
    [InlineData(0x81A4, 0u, 1000u, false)]    // fichier de root (lien vers /etc/shadow, lien physique…)
    [InlineData(0xA1FF, 1000u, 1000u, false)] // lien symbolique
    [InlineData(0x41ED, 1000u, 1000u, false)] // dossier
    [InlineData(0x11A4, 1000u, 1000u, false)] // tube nommé
    public void L_instance_root_n_ecrit_que_dans_un_fichier_ordinaire_de_l_utilisateur(int mode, uint owner, uint invoking, bool expected)
        => Assert.Equal(expected, AppPaths.IsAcceptableUserFile(new UnixFileStatus(mode, owner, 0), invoking));
}

public class SingleInstanceLockTests
{
    [Fact]
    public void Une_seconde_instance_n_obtient_pas_le_mutex_tant_que_la_premiere_tourne()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = @"Local\NetShareHub-Test-" + Guid.NewGuid().ToString("N");
        var first = SingleInstanceLock.TryAcquireMutex(name, TimeSpan.Zero);
        Assert.NotNull(first);

        // Un mutex est réentrant pour son propriétaire : la « seconde instance » est simulée par un thread dédié.
        // Test synchrone exprès : avec await/Task.Run, le thread du pool qui possède le mutex pourrait exécuter
        // lui-même la tentative et l'obtenir (réentrance).
        Assert.False(AcquiredOnOtherThread(name));

        first!.Dispose();
        Assert.True(AcquiredOnOtherThread(name));
    }

    private static bool AcquiredOnOtherThread(string name)
    {
        var acquired = false;
        var thread = new Thread(() =>
        {
            using var lockHandle = SingleInstanceLock.TryAcquireMutex(name, TimeSpan.Zero);
            acquired = lockHandle is not null;
        });
        thread.Start();
        thread.Join();
        return acquired;
    }

    [Fact]
    public void Une_seconde_instance_n_obtient_pas_le_verrou_fichier_tant_que_la_premiere_tourne()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "netsharehub.lock");

        using (var first = SingleInstanceLock.TryAcquireFile(path, TimeSpan.Zero))
        {
            Assert.NotNull(first);
            Assert.Null(SingleInstanceLock.TryAcquireFile(path, TimeSpan.Zero));
        }

        using var again = SingleInstanceLock.TryAcquireFile(path, TimeSpan.Zero);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task L_instance_relancee_attend_que_l_ancienne_libere_le_verrou()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "netsharehub.lock");
        var old = SingleInstanceLock.TryAcquireFile(path, TimeSpan.Zero);
        Assert.NotNull(old);

        var waiting = Task.Run(() => SingleInstanceLock.TryAcquireFile(path, TimeSpan.FromSeconds(10)));
        await Task.Delay(300);
        old!.Dispose();

        using var relaunched = await waiting;
        Assert.NotNull(relaunched);
    }
}

/// <summary>Le proxy ouvert au réseau ne doit pas servir de rebond vers les services de la machine.</summary>
public class DispatchDestinationTests
{
    private static readonly IReadOnlySet<IPAddress> Local = new HashSet<IPAddress> { IPAddress.Parse("192.168.1.20") };

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.12.0.9", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("0.1.2.3", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("192.168.1.20", true)]   // adresse de la machine elle-même
    [InlineData("224.0.0.251", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("192.168.1.1", false)]   // la box : un autre appareil du réseau
    [InlineData("93.184.215.14", false)]
    [InlineData("10.0.0.5", false)]
    public void Destinations_interdites_a_un_client_du_reseau(string address, bool forbidden)
        => Assert.Equal(forbidden, DispatchProxyService.IsHostDestination(IPAddress.Parse(address), Local));
}

internal static class FileLinks
{
    /// <summary>
    /// Lien vers un dossier : jonction sous Windows (aucun droit particulier requis), lien symbolique ailleurs.
    /// Le lien est retiré à la libération, avant le dossier temporaire (dont la suppression récursive échouerait sur la jonction).
    /// </summary>
    public static IDisposable CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return new LinkScope(link);
        }

        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(Directory.Exists(link), "création de la jonction impossible");
        return new LinkScope(link);
    }

    private sealed class LinkScope(string link) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                Directory.Delete(link); // supprime le lien lui-même, jamais le contenu de sa cible
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nettoyage au mieux.
            }
        }
    }
}
