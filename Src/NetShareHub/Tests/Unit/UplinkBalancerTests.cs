using NetShareHub.Application.Services;
using NetShareHub.Domain.Enums;

namespace NetShareHub.Tests.Unit;

public class UplinkBalancerTests
{
    private static UplinkBalancer Make(DispatchStrategy strategy, params (string Id, int Weight, bool Enabled)[] uplinks)
    {
        var balancer = new UplinkBalancer(strategy);
        balancer.SetUplinks(uplinks);
        return balancer;
    }

    [Fact]
    public void WeightedRoundRobin_respecte_les_poids()
    {
        var balancer = Make(DispatchStrategy.WeightedRoundRobin, ("wifi", 3, true), ("eth", 1, true));

        var counts = new Dictionary<string, int> { ["wifi"] = 0, ["eth"] = 0 };
        for (var i = 0; i < 400; i++)
        {
            var id = balancer.Acquire()!;
            counts[id]++;
            balancer.Release(id);
        }

        Assert.Equal(300, counts["wifi"]);
        Assert.Equal(100, counts["eth"]);
    }

    [Fact]
    public void WeightedRoundRobin_evite_les_rafales_sur_une_meme_liaison()
    {
        var balancer = Make(DispatchStrategy.WeightedRoundRobin, ("a", 2, true), ("b", 1, true));

        var sequence = Enumerable.Range(0, 6).Select(_ => balancer.Acquire()!).ToList();

        // Sur 6 tirages 2:1 on attend 4 « a » et 2 « b », jamais 4 « a » d'affilée.
        Assert.Equal(4, sequence.Count(x => x == "a"));
        Assert.DoesNotContain("aaa", string.Concat(sequence));
    }

    [Fact]
    public void Failover_utilise_la_premiere_liaison_saine()
    {
        var balancer = Make(DispatchStrategy.Failover, ("primary", 1, true), ("backup", 1, true));

        Assert.Equal("primary", balancer.Acquire());

        balancer.SetHealth("primary", UplinkHealth.Down);
        Assert.Equal("backup", balancer.Acquire());

        balancer.SetHealth("primary", UplinkHealth.Healthy);
        Assert.Equal("primary", balancer.Acquire());
    }

    [Fact]
    public void LeastConnections_choisit_la_moins_chargee_au_prorata_du_poids()
    {
        var balancer = Make(DispatchStrategy.LeastConnections, ("fast", 4, true), ("slow", 1, true));

        var picks = Enumerable.Range(0, 10).Select(_ => balancer.Acquire()!).ToList();

        // Rien n'est libéré : la liaison de poids 4 doit absorber environ 4 fois plus de connexions.
        Assert.Equal(8, picks.Count(p => p == "fast"));
        Assert.Equal(2, picks.Count(p => p == "slow"));
    }

    [Fact]
    public void Liaisons_desactivees_ou_hors_service_ne_sont_jamais_choisies()
    {
        var balancer = Make(DispatchStrategy.WeightedRoundRobin, ("a", 1, true), ("b", 1, false), ("c", 1, true));
        balancer.SetHealth("c", UplinkHealth.Down);

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal("a", balancer.Acquire());
        }
    }

    [Fact]
    public void Aucune_liaison_disponible_renvoie_null()
    {
        var balancer = Make(DispatchStrategy.WeightedRoundRobin, ("a", 1, true));
        balancer.SetHealth("a", UplinkHealth.Down);

        Assert.Null(balancer.Acquire());
    }

    [Fact]
    public void Exclude_permet_de_basculer_apres_un_echec()
    {
        var balancer = Make(DispatchStrategy.Failover, ("a", 1, true), ("b", 1, true));

        var first = balancer.Acquire();
        var second = balancer.Acquire(exclude: new HashSet<string> { first! });

        Assert.Equal("a", first);
        Assert.Equal("b", second);
        Assert.Null(balancer.Acquire(exclude: new HashSet<string> { "a", "b" }));
    }

    [Fact]
    public void Release_decremente_les_connexions_actives()
    {
        var balancer = Make(DispatchStrategy.LeastConnections, ("a", 1, true));

        balancer.Acquire();
        balancer.Acquire();
        Assert.Equal(2, balancer.GetActive("a"));

        balancer.Release("a");
        balancer.Release("a");
        balancer.Release("a"); // un Release de trop ne doit pas passer sous zéro
        Assert.Equal(0, balancer.GetActive("a"));
    }

    [Fact]
    public void Affinite_garde_la_meme_liaison_pour_une_destination()
    {
        var balancer = new UplinkBalancer(DispatchStrategy.WeightedRoundRobin, TimeSpan.FromMinutes(10));
        balancer.SetUplinks(new[] { ("a", 1, true), ("b", 1, true) });

        var first = balancer.Acquire("example.com");
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(first, balancer.Acquire("example.com"));
        }

        // Sans clé, la répartition reste libre et alterne.
        var free = Enumerable.Range(0, 10).Select(_ => balancer.Acquire()).ToHashSet();
        Assert.Equal(2, free.Count);
    }

    [Fact]
    public void Affinite_expire_apres_la_duree_prevue()
    {
        var now = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        var balancer = new UplinkBalancer(DispatchStrategy.Failover, TimeSpan.FromMinutes(10), () => now);
        balancer.SetUplinks(new[] { ("a", 1, true), ("b", 1, true) });

        Assert.Equal("a", balancer.Acquire("k"));

        // « a » tombe : la destination bascule sur « b » et s'y attache.
        balancer.SetHealth("a", UplinkHealth.Down);
        Assert.Equal("b", balancer.Acquire("k"));

        // « a » revient, mais l'affinité vers « b » tient encore.
        balancer.SetHealth("a", UplinkHealth.Healthy);
        Assert.Equal("b", balancer.Acquire("k"));

        // Une fois l'affinité expirée, la stratégie reprend la main.
        now += TimeSpan.FromMinutes(11);
        Assert.Equal("a", balancer.Acquire("k"));
    }

    [Fact]
    public void Affinite_est_abandonnee_quand_la_liaison_tombe()
    {
        var balancer = new UplinkBalancer(DispatchStrategy.WeightedRoundRobin, TimeSpan.FromMinutes(10));
        balancer.SetUplinks(new[] { ("a", 1, true), ("b", 1, true) });

        var pinned = balancer.Acquire("example.com")!;
        balancer.SetHealth(pinned, UplinkHealth.Down);
        balancer.ForgetSticky(pinned);

        var other = pinned == "a" ? "b" : "a";
        Assert.Equal(other, balancer.Acquire("example.com"));
    }

    [Fact]
    public void Retablissement_d_une_liaison_la_remet_dans_le_tirage()
    {
        var balancer = Make(DispatchStrategy.WeightedRoundRobin, ("a", 1, true), ("b", 1, true));
        balancer.SetHealth("b", UplinkHealth.Down);
        Assert.All(Enumerable.Range(0, 5), _ => Assert.Equal("a", balancer.Acquire()));

        balancer.SetHealth("b", UplinkHealth.Healthy);
        var seen = Enumerable.Range(0, 10).Select(_ => balancer.Acquire()).ToHashSet();
        Assert.Contains("b", seen);
    }
}
