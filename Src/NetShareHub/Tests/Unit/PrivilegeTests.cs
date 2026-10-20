using NetShareHub.Infrastructure.Services;

namespace NetShareHub.Tests.Unit;

public class PrivilegeTests
{
    private static readonly KeyValuePair<string, string>[] Environment =
    {
        new("DISPLAY", ":0"),
        new("XAUTHORITY", "/home/alice/.Xauthority"),
    };

    [Fact]
    public void Avec_le_script_d_elevation_pkexec_lance_le_script_avec_l_environnement_graphique()
    {
        using var dir = new TempDirectory();
        var executable = Path.Combine(dir.Path, "NetShareHub");
        var wrapper = Path.Combine(dir.Path, PrivilegeService.ElevationWrapperName);
        File.WriteAllText(wrapper, "#!/bin/sh");

        var start = PrivilegeService.BuildPkexecStartInfo(executable, Environment);

        Assert.Equal("pkexec", start.FileName);
        Assert.Equal(new[] { wrapper, "DISPLAY=:0", "XAUTHORITY=/home/alice/.Xauthority" }, start.ArgumentList);
    }

    [Fact]
    public void Sans_script_d_elevation_pkexec_passe_par_env_puis_l_executable()
    {
        using var dir = new TempDirectory();
        var executable = Path.Combine(dir.Path, "NetShareHub");

        var start = PrivilegeService.BuildPkexecStartInfo(executable, Environment);

        Assert.Equal(new[] { "env", "DISPLAY=:0", "XAUTHORITY=/home/alice/.Xauthority", executable }, start.ArgumentList);
    }

    [Fact]
    public void Les_valeurs_avec_espaces_restent_un_seul_argument()
    {
        using var dir = new TempDirectory();

        var start = PrivilegeService.BuildPkexecStartInfo(
            Path.Combine(dir.Path, "NetShareHub"),
            new[] { new KeyValuePair<string, string>("XAUTHORITY", "/home/jean dupont/.Xauthority") });

        Assert.Contains("XAUTHORITY=/home/jean dupont/.Xauthority", start.ArgumentList);
    }
}
