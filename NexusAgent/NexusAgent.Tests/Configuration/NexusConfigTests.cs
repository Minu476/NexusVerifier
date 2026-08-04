using NexusAgent.Core.Configuration;

namespace NexusAgent.Tests.Configuration;

/// <summary>
/// Tests that the new env-overridable router knobs (Phase 2 of the v4-flash work)
/// are read correctly, and that unset env keeps the prior hardcoded defaults — so
/// an existing deployment's behavior is unchanged unless it opts in.
/// </summary>
public sealed class NexusConfigTests
{
    [Fact]
    public void Defaults_MatchPriorHardcodedRouterConfig()
    {
        // Unset env → these must equal the old hardcoded values, or existing runs
        // change behavior silently.
        var cfg = WithCleanEnv();
        Assert.Equal(3, cfg.TurnsBeforeEscalation);
        Assert.Equal(6, cfg.TurnsBeforeFlashEscalation);
        Assert.Equal(20, cfg.EpisodesBeforeProEscalation);
        Assert.Equal(0.4, cfg.TempTier1);
        Assert.Equal(0.3, cfg.TempTier2);
        Assert.Equal(0.1, cfg.TempTier3);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_ReadsEscalationKnobs()
    {
        using var _ = WithEnv(new[]
        {
            ("NEXUS_TURNS_BEFORE_ESCALATION", "1"),
            ("NEXUS_TURNS_BEFORE_FLASH_ESCALATION", "2"),
            ("NEXUS_EPISODES_BEFORE_PRO_ESCALATION", "5"),
        });
        var cfg = new NexusConfig();
        cfg.ApplyEnvironmentOverrides();
        Assert.Equal(1, cfg.TurnsBeforeEscalation);
        Assert.Equal(2, cfg.TurnsBeforeFlashEscalation);
        Assert.Equal(5, cfg.EpisodesBeforeProEscalation);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_ReadsTemperatures()
    {
        using var _ = WithEnv(new[]
        {
            ("NEXUS_TEMP_TIER1", "0.6"),
            ("NEXUS_TEMP_TIER2", "0.5"),
            ("NEXUS_TEMP_TIER3", "0.2"),
        });
        var cfg = new NexusConfig();
        cfg.ApplyEnvironmentOverrides();
        Assert.Equal(0.6, cfg.TempTier1);
        Assert.Equal(0.5, cfg.TempTier2);
        Assert.Equal(0.2, cfg.TempTier3);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_GarbageValue_KeepsDefault()
    {
        using var _ = WithEnv(new[] { ("NEXUS_TURNS_BEFORE_ESCALATION", "not-a-number") });
        var cfg = new NexusConfig();
        cfg.ApplyEnvironmentOverrides();
        Assert.Equal(3, cfg.TurnsBeforeEscalation);  // int.TryParse failed → default retained
    }

    // ---- helpers: scope env mutations to the test process without polluting global state ----

    private static NexusConfig WithCleanEnv()
    {
        var cfg = new NexusConfig();
        cfg.ApplyEnvironmentOverrides();
        return cfg;
    }

    private static IDisposable WithEnv((string Key, string Value)[] vars)
    {
        var saved = new List<(string, string?)>();
        foreach (var (k, v) in vars)
        {
            saved.Add((k, Environment.GetEnvironmentVariable(k)));
            Environment.SetEnvironmentVariable(k, v);
        }
        return new EnvRestorer(saved);
    }

    private sealed class EnvRestorer : IDisposable
    {
        private readonly List<(string, string?)> _saved;
        private bool _disposed;
        public EnvRestorer(List<(string, string?)> saved) => _saved = saved;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var (k, v) in _saved)
                Environment.SetEnvironmentVariable(k, v);
        }
    }
}
