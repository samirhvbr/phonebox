using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class AiUsageBarTests
{
    private static readonly long Now = DateTimeOffset.Parse("2026-11-21T18:00:00Z").ToUnixTimeMilliseconds();

    [Fact]
    public void PreservesNamedClaudeAccountsAndNormalizesCodex()
    {
        var providers = AiUsageBar.Parse("""
            {"schema_version":1,"entries":[
              {"id":"anthropic@me","display_name":"Claude · me","plan":"Max", "fetched_at":"2026-11-21T18:00:00Z",
               "metrics":[{"label":"Session","percent_precise":24.5,"percent":25,"reset_at":"2026-11-21T20:00:00Z","window_secs":18000}]},
              {"id":"anthropic@b3","display_name":"Claude · b3","fetched_at":"2026-11-21T18:00:00Z",
               "metrics":[{"label":"Weekly","percent":82}]},
              {"id":"openai","display_name":"Codex","fetched_at":"2026-11-21T18:00:00Z","metrics":[{"label":"Weekly","percent":11}]}]}
            """, Now);
        Assert.Equal(["claude@me", "claude@b3", "codex"], providers.Select(p => p.Id));
        Assert.Equal(24.5, providers[0].Limits[0].Percent);
        Assert.Equal(18000, providers[0].Limits[0].WindowSec);
        Assert.Equal(Now + 7_200_000, providers[0].Limits[0].ResetsAt);
        Assert.Equal("warning", providers[1].Limits[0].Severity);
        Assert.All(providers, p => Assert.Equal("ai-usagebar", p.Source));
        Assert.All(providers, p => Assert.Null(p.Error));
    }

    [Fact]
    public void InterpretsRemainingPercentAndRetainsBalanceWithoutInventingQuota()
    {
        var provider = Assert.Single(AiUsageBar.Parse("""
            {"entries":[{"id":"synthetic","fetched_at":"2026-11-21T18:00:00Z","metrics":[
              {"label":"Weekly","percent":60,"reading":"left"},
              {"label":"Credits","headline":"value","value":"$12.40","percent":0}]}]}
            """, Now));
        Assert.Equal(40, Assert.Single(provider.Limits).Percent);
        Assert.Equal("Credits: $12.40", provider.Extra);
    }

    [Fact]
    public void GroupedSubRowsAreNotLimitsButOtherGroupsAre()
    {
        var providers = AiUsageBar.Parse("""
            {"schema_version":1,"entries":[
              {"id":"supergrok","fetched_at":"2026-11-21T18:00:00Z","metrics":[
                {"label":"Weekly usage","percent":8,"window_secs":604800},
                {"label":"xAI API","percent":8,"group":"Breakdown"}]},
              {"id":"anthropic@me","fetched_at":"2026-11-21T18:00:00Z","metrics":[
                {"label":"Weekly (7d)","percent":2},{"label":"phonebox","percent":40,"group":"Sessions"}]},
              {"id":"antigravity","fetched_at":"2026-11-21T18:00:00Z","metrics":[
                {"label":"Gemini Pro","percent":30,"group":"Gemini"}]}]}
            """, Now);
        Assert.Equal("Weekly usage", Assert.Single(providers[0].Limits).Label);
        Assert.Equal("Weekly (7d)", Assert.Single(providers[1].Limits).Label);
        Assert.Equal("Gemini Pro", Assert.Single(providers[2].Limits).Label);
    }

    [Fact]
    public void DropsInvalidMetricsAndKeepsOtherProviders()
    {
        var providers = AiUsageBar.Parse("""
            {"schema_version":1,"entries":[false,{"id":"broken","metrics":[false,
              {"label":"A","percent":"unknown"},{"label":"B","percent":1e400}]},
              {"id":"minimax","fetched_at":"2026-11-21T18:00:00Z","metrics":[{"label":"Plan","percent":0}]}]}
            """, Now);
        Assert.Empty(providers[0].Limits);
        Assert.NotNull(providers[0].Error);
        Assert.Equal(0, Assert.Single(providers[1].Limits).Percent);
        Assert.Null(providers[1].Error);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"schema_version\":\"1\",\"entries\":[]}")]
    [InlineData("{\"schema_version\":2,\"entries\":[]}")]
    [InlineData("{\"entries\":{}}")]
    public void RejectsInvalidReports(string json) => Assert.Throws<JsonException>(() => AiUsageBar.Parse(json, Now));

    [Fact]
    public void ShowsStalenessAndKeepsLastMeasuredValues()
    {
        var provider = Assert.Single(AiUsageBar.Parse("""
            {"entries":[{"id":"openai","fetched_at":"2026-11-21T18:00:00Z","metrics":[{"label":"Weekly","percent":33}]}]}
            """, Now));
        var stale = Assert.Single(AiUsageBar.Snapshot([provider], Now + AiUsageBar.StaleAfterMs + 1));
        Assert.Equal(33, Assert.Single(stale.Limits).Percent);
        Assert.NotNull(stale.Error);
        Assert.Equal(Now, stale.UpdatedAt);
    }

    [Fact]
    public void ProviderDiagnosticsAndUnknownFieldsNeverReachThePhone()
    {
        var provider = Assert.Single(AiUsageBar.Parse("""
            {"entries":[{"id":"openai","error":"Bearer SECRET_TOKEN auth failed","status":"error",
             "access_token":"SECRET_TOKEN","fetched_at":"2026-11-21T18:00:00Z",
             "sections":[{"type":"block","label":"Sessions","body":["PRIVATE_CHAT"]}],"metrics":[]}]}
            """, Now));
        var json = JsonSerializer.Serialize(provider);
        Assert.DoesNotContain("SECRET_TOKEN", json);
        Assert.DoesNotContain("PRIVATE_CHAT", json);
        Assert.Equal("ai-usagebar provider unavailable", provider.Error);
    }

    [Fact]
    public void PublicConfigurationOmitsTheLocalExecutablePath()
    {
        const string path = "/private/local-account/ai-usagebar";
        var config = new AgentConfig { CodexBar = new CodexBarConfig { AiUsageBar = true, AiUsageBarPath = path } };
        var local = JsonSerializer.Serialize(config, ConfigStore.WriteOpts);
        Assert.Contains(path, local);
        var publicConfig = JsonSerializer.SerializeToElement(config.ToPublic(), ConfigStore.WriteOpts);
        var quotaConfig = publicConfig.GetProperty("codexBar");
        Assert.True(quotaConfig.GetProperty("aiUsageBar").GetBoolean());
        Assert.False(quotaConfig.TryGetProperty("aiUsageBarPath", out _));
        Assert.DoesNotContain(path, publicConfig.ToString());
    }

    [Fact]
    public async Task BoundsToolOutput()
    {
        var error = await Assert.ThrowsAsync<IOException>(() => AiUsageBar.ReadBoundedAsync(
            new StringReader(new string('x', 5000)), 4096, CancellationToken.None));
        Assert.Equal("ai-usagebar output exceeded its limit", error.Message);
    }

    [Fact]
    public async Task CliFailuresNeverEchoProviderDiagnostics()
    {
        if (OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(Path.GetTempPath(), "phonebox ai usage " + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var executable = Path.Combine(folder, "fixture");
            File.WriteAllText(executable, "#!/bin/sh\nprintf 'SECRET_TOKEN provider diagnostics' >&2\nexit 1\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var error = await Assert.ThrowsAsync<IOException>(() => AiUsageBar.ReadAsync(executable, CancellationToken.None));
            Assert.Equal("ai-usagebar could not read usage", error.Message);
            Assert.DoesNotContain("SECRET_TOKEN", error.ToString());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task AnEnabledQuotaRuleUsesAiUsageBarWithoutAQuotaWidget()
    {
        var folder = Directory.CreateTempSubdirectory("phonebox-aiusage-rule-").FullName;
        try
        {
            var executable = Path.Combine(folder, "fixture");
            File.WriteAllText(executable, "fixture");
            var path = Path.Combine(folder, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                codexBar = new { aiUsageBar = true, aiUsageBarPath = executable },
                pages = new[] { new { id = "home", widgets = new[] { new { type = "clock", options = new { status = false } } } } },
                rules = new[] { new { id = "quota", when = "quota-above", match = "claude@me", value = 80, then = "notify" } },
            }));
            var store = new ConfigStore(path);
            using var monitor = new QuotaMonitor(store, NullLogger<QuotaMonitor>.Instance);
            monitor.SetStateForTest(claude: new("claude", "Claude", "", [new("Weekly", 99, null, null, "critical")], null, Now, null));
            Assert.DoesNotContain(store.Get().Widgets(true), w => w.Type is "quotas" or "codexbar" or "quotaring");
            Assert.True(monitor.IsAiUsageBarEnabled());
            var reads = 0;
            Assert.True(await monitor.RefreshAiUsageBarIfSelectedAsync(CancellationToken.None, (_, _) =>
            {
                reads++;
                return Task.FromResult<IReadOnlyList<QuotaProvider>>([
                    new("claude@me", "Claude · me", "Max", [new("Weekly", 86, null, null, "warning")],
                        null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, Source: "ai-usagebar"),
                ]);
            }));
            Assert.Equal(1, reads);
            Assert.Equal("claude@me", Assert.Single(monitor.Snapshot()).Id);
            using var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(folder, "push.json"));
            // A quota-only rule reads this monitor and emits a notification; other monitors are unused.
            using var engine = new RuleEngine(store, push, sessions: null!, media: null!, system: null!,
                privacy: null!, calendar: null!, obs: null!, quotas: monitor, github: null!, batteries: null!,
                ping: null!, audio: null!, log: NullLogger<RuleEngine>.Instance);
            engine.Evaluate();
            Assert.Equal(["quota"], engine.Active());
            var notification = Assert.Single(push.Since(0));
            Assert.Equal("notify", notification.Kind);
            Assert.Equal("Claude · me Weekly 86%", notification.Data!.Value.GetProperty("text").GetString());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task AnIdleSelectedSourceDoesNotFallBackToNativePollersOrExposeTheirCachedValues()
    {
        var folder = Directory.CreateTempSubdirectory("phonebox-aiusage-idle-").FullName;
        try
        {
            var path = Path.Combine(folder, "config.json");
            File.WriteAllText(path, """
                {"codexBar":{"aiUsageBar":true,"codexLiveQuota":true},
                 "pages":[{"id":"home","widgets":[{"type":"clock","options":{"status":false}}]}],
                 "rules":[{"when":"quota-above","enabled":false}]}
                """);
            var store = new ConfigStore(path);
            using var monitor = new QuotaMonitor(store, NullLogger<QuotaMonitor>.Instance);
            monitor.SetStateForTest(claude: new("claude", "Claude", "", [], null, Now, null));
            var reads = 0;
            Task<IReadOnlyList<QuotaProvider>> Read(string _, CancellationToken ct)
            {
                reads++;
                return Task.FromResult<IReadOnlyList<QuotaProvider>>([]);
            }
            Assert.True(monitor.IsAiUsageBarConfigured());
            Assert.False(monitor.IsAiUsageBarEnabled());
            Assert.True(await monitor.RefreshAiUsageBarIfSelectedAsync(CancellationToken.None, Read));
            Assert.Equal(0, reads);
            Assert.Empty(monitor.Snapshot());
            store.Get().CodexBar.AiUsageBar = false;
            Assert.False(await monitor.RefreshAiUsageBarIfSelectedAsync(CancellationToken.None, Read));
            Assert.Equal(0, reads);
            Assert.Equal("claude", Assert.Single(monitor.Snapshot()).Id);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task SelectingAiUsageBarSuppressesLiveCodexEvenWhenAWidgetRequestsIt()
    {
        var folder = Directory.CreateTempSubdirectory("phonebox-aiusage-codex-").FullName;
        try
        {
            var path = Path.Combine(folder, "config.json");
            File.WriteAllText(path, """
                {"codexBar":{"aiUsageBar":true,"codexLiveQuota":true,"providers":["codex"]},
                 "pages":[{"id":"home","widgets":[{"type":"quotas"}]}]}
                """);
            var store = new ConfigStore(path);
            using var monitor = new QuotaMonitor(store, NullLogger<QuotaMonitor>.Instance);
            Assert.True(store.Get().ShowCodexQuota());
            Assert.False(monitor.IsCodexLiveEnabled());
            var lookups = 0;
            var outcome = await monitor.RefreshCodexOnceAsync(() => { lookups++; return "fixture"; },
                (_, _) => throw new InvalidOperationException("Native Codex must not be polled"), CancellationToken.None);
            Assert.Equal(QuotaMonitor.CodexLiveOutcome.Skipped, outcome);
            Assert.Equal(0, lookups);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void DisabledRulesAndOtherConditionsDoNotRequestRefresh()
    {
        var folder = Directory.CreateTempSubdirectory("phonebox-aiusage-gate-").FullName;
        try
        {
            var path = Path.Combine(folder, "config.json");
            File.WriteAllText(path, """
                {"codexBar":{"aiUsageBar":false},"pages":[{"id":"home","widgets":[{"type":"clock","options":{"status":false}}]}],
                 "rules":[{"when":"quota-above","enabled":true}]}
                """);
            var store = new ConfigStore(path);
            using var monitor = new QuotaMonitor(store, NullLogger<QuotaMonitor>.Instance);
            Assert.False(monitor.IsAiUsageBarEnabled());
            var config = store.Get();
            config.CodexBar.AiUsageBar = true;
            Assert.True(monitor.IsAiUsageBarEnabled());
            config.Rules[0].Enabled = false;
            Assert.False(monitor.IsAiUsageBarEnabled());
            config.Rules[0].Enabled = true;
            config.Rules[0].When = "cpu-above";
            Assert.False(monitor.IsAiUsageBarEnabled());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClockStatusLinesRefreshNamedAccountsAndDisablingThemStopsDemand(bool inStack)
    {
        var folder = Directory.CreateTempSubdirectory("phonebox-aiusage-clock-").FullName;
        try
        {
            var executable = Path.Combine(folder, "fixture");
            File.WriteAllText(executable, "fixture");
            var path = Path.Combine(folder, "config.json");
            object clock = new { type = "clock", options = new { providers = new[] { "claude@me", "minimax" } } };
            object widget = inStack ? new { type = "stack", options = new { widgets = new[] { clock } } } : clock;
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                codexBar = new { aiUsageBar = true, aiUsageBarPath = executable, providers = new[] { "codex" } },
                pages = new[] { new { id = "home", widgets = new[] { widget } } },
            }));
            var store = new ConfigStore(path);
            using var monitor = new QuotaMonitor(store, NullLogger<QuotaMonitor>.Instance);
            Assert.True(monitor.IsAiUsageBarEnabled());
            var reads = 0;
            Task<IReadOnlyList<QuotaProvider>> Read(string _, CancellationToken ct)
            {
                reads++;
                return Task.FromResult<IReadOnlyList<QuotaProvider>>([
                    new("claude@me", "Claude · me", "Max", [new("Weekly", 35, null, null, "normal")],
                        null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, Source: "ai-usagebar"),
                ]);
            }
            Assert.True(await monitor.RefreshAiUsageBarIfSelectedAsync(CancellationToken.None, Read));
            Assert.Equal(1, reads);
            var providers = monitor.Snapshot();
            Assert.Equal(35, Assert.Single(providers.Single(p => p.Id == "claude@me").Limits).Percent);
            Assert.Equal("Configure MiniMax in ai-usagebar", providers.Single(p => p.Id == "minimax").Error);
            Assert.DoesNotContain(providers, p => p.Id == "codex");

            var savedWidget = store.Get().Pages[0].Widgets![0];
            if (inStack)
                savedWidget.Set("widgets", new[] { new { type = "clock", options = new { status = false, providers = new[] { "minimax" } } } });
            else savedWidget.Set("status", false);
            using var disabled = new QuotaMonitor(store, NullLogger<QuotaMonitor>.Instance);
            Assert.False(disabled.IsAiUsageBarEnabled());
            Assert.True(await disabled.RefreshAiUsageBarIfSelectedAsync(CancellationToken.None, Read));
            Assert.Equal(1, reads);
            Assert.Empty(disabled.Snapshot());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("quotas")]
    [InlineData("codexbar")]
    [InlineData("quotaring")]
    [InlineData("clock")]
    public void EnabledSmartStackQuotaChildrenRemainConsumers(string type)
    {
        var folder = Directory.CreateTempSubdirectory("phonebox-aiusage-stack-").FullName;
        try
        {
            var path = Path.Combine(folder, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                codexBar = new { aiUsageBar = true },
                pages = new[] { new { id = "home", widgets = new[] { new
                {
                    type = "stack", options = new { widgets = new[] { new { type, enabled = true } } },
                } } } },
            }));
            var store = new ConfigStore(path);
            using var monitor = new QuotaMonitor(store, NullLogger<QuotaMonitor>.Instance);
            Assert.True(monitor.IsAiUsageBarEnabled());
            store.Get().Pages[0].Enabled = false;
            Assert.False(monitor.IsAiUsageBarEnabled());
            store.Get().Pages[0].Enabled = true;
            var stack = store.Get().Pages[0].Widgets![0];
            stack.Set("widgets", new[] { new { type, enabled = false } });
            Assert.False(monitor.IsAiUsageBarEnabled());
            stack.Set("widgets", new[] { new { type, enabled = true, active = false } });
            Assert.False(monitor.IsAiUsageBarEnabled());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task MissingCliAndFailedRefreshPreserveSafeProviderState()
    {
        var folder = Path.Combine(Path.GetTempPath(), "phonebox-aiusage-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var executable = Path.Combine(folder, "fixture");
            File.WriteAllText(executable, "fixture");
            var configPath = Path.Combine(folder, "config.json");
            File.WriteAllText(configPath, JsonSerializer.Serialize(new
            {
                codexBar = new { aiUsageBar = true, aiUsageBarPath = executable },
                pages = new[] { new { id = "ai", enabled = true, widgets = new[] {
                    new { id = "quotas", type = "quotas", options = new { providers = new[] { "claude@me", "claude@b3", "minimax" } } } } } },
            }));
            var monitor = new QuotaMonitor(new ConfigStore(configPath), NullLogger<QuotaMonitor>.Instance);
            Assert.True(monitor.IsAiUsageBarEnabled());
            await monitor.RefreshAiUsageBarAsync(CancellationToken.None, (_, _) => Task.FromResult<IReadOnlyList<QuotaProvider>>([
                new("claude@me", "Claude · me", "Max", [new("Weekly", 18, null, null, "normal")], null, Now, null),
                new("claude@b3", "Claude · b3", "Max", [new("Weekly", 72, null, null, "normal")], null, Now, null),
            ]));
            var before = monitor.Snapshot();
            Assert.DoesNotContain(before, p => p.Id == "claude");
            Assert.Equal("Configure MiniMax in ai-usagebar", before.Single(p => p.Id == "minimax").Error);
            await monitor.RefreshAiUsageBarAsync(CancellationToken.None, (_, _) =>
                Task.FromException<IReadOnlyList<QuotaProvider>>(new IOException("SECRET_TOKEN provider failed")));
            var after = monitor.Snapshot();
            Assert.Equal(18, Assert.Single(after.Single(p => p.Id == "claude@me").Limits).Percent);
            Assert.Equal(72, Assert.Single(after.Single(p => p.Id == "claude@b3").Limits).Percent);
            Assert.DoesNotContain("SECRET_TOKEN", JsonSerializer.Serialize(after));
            File.Delete(executable);
            await monitor.RefreshAiUsageBarAsync(CancellationToken.None);
            Assert.All(monitor.Snapshot().Where(p => p.Limits.Count > 0), p => Assert.Equal("ai-usagebar refresh failed", p.Error));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
