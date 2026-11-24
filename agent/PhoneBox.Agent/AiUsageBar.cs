using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>Reads ai-usagebar's cache-first report without handling provider credentials.</summary>
internal static class AiUsageBar
{
    internal const int MaxOutputChars = 512 * 1024;
    internal const long StaleAfterMs = 10 * 60_000;

    internal static string? Find(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.IsPathFullyQualified(configuredPath) && File.Exists(configuredPath) ? configuredPath : null;
        return ToolProcess.Find("ai-usagebar")
            ?? Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo", "bin",
                OperatingSystem.IsWindows() ? "ai-usagebar.exe" : "ai-usagebar"));
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    public static async Task<IReadOnlyList<QuotaProvider>> ReadAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppPaths.NeutralCwd,
        };
        start.ArgumentList.Add("usage");
        start.ArgumentList.Add("--json");
        using var process = Process.Start(start) ?? throw new IOException("Could not start ai-usagebar");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var output = ReadBoundedAsync(process.StandardOutput, MaxOutputChars, deadline.Token);
        var error = ReadBoundedAsync(process.StandardError, 16 * 1024, deadline.Token);
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
            if (process.ExitCode != 0) throw new IOException("ai-usagebar could not read usage");
            return Parse(await output);
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("ai-usagebar timed out");
        }
        finally
        {
            deadline.Cancel();
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            try { await Task.WhenAll(output, error); }
            catch (Exception e) when (e is OperationCanceledException or IOException) { }
        }
    }

    internal static async Task<string> ReadBoundedAsync(TextReader reader, int limit, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            if (text.Length + read > limit) throw new IOException("ai-usagebar output exceeded its limit");
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }

    /// <summary>Projects only quota fields; report errors, session details and unknown fields stay local.</summary>
    public static IReadOnlyList<QuotaProvider> Parse(string json, long? nowMs = null)
    {
        if (json.Length > MaxOutputChars) throw new JsonException("ai-usagebar report is too large");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array || (root.TryGetProperty("schema_version", out var version)
                && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1)))
            throw new JsonException("Unsupported ai-usagebar report");
        var now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var providers = new List<QuotaProvider>();
        foreach (var entry in entries.EnumerateArray().Take(128))
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var rawId = Text(entry, "id", 120);
            if (rawId is null) continue;
            var id = NormalizeId(rawId);
            var name = Text(entry, "display_name", 160) ?? Text(entry, "name", 160) ?? CodexBar.DisplayName(id.Split('@')[0]);
            var plan = Text(entry, "plan", 160) ?? name;
            var updated = Date(Text(entry, "fetched_at", 64));
            var limits = new List<QuotaLimit>();
            var balances = new List<string>();
            if (entry.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Array)
            {
                foreach (var metric in metrics.EnumerateArray().Take(32))
                {
                    var label = Text(metric, "label", 120);
                    if (label is null || IsSubRow(metric)) continue;
                    if (Text(metric, "headline", 16) == "value")
                    {
                        if (Text(metric, "value", 120) is { } value) balances.Add(label + ": " + value);
                        continue;
                    }
                    var percent = Number(metric, "percent_precise") ?? Number(metric, "percent");
                    if (percent is null or < 0 or > 10_000) continue;
                    if (Text(metric, "reading", 16) == "left") percent = Math.Max(0, 100 - percent.Value);
                    var window = Number(metric, "window_secs");
                    limits.Add(new QuotaLimit(label, percent.Value, Date(Text(metric, "reset_at", 64)),
                        window is > 0 and <= 31_536_000 ? (long)window.Value : null,
                        percent >= 95 ? "critical" : percent >= 80 ? "warning" : "normal"));
                }
            }
            var failed = Text(entry, "status", 16) == "error"
                || (entry.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined));
            var stale = entry.TryGetProperty("stale", out var staleValue) && staleValue.ValueKind == JsonValueKind.True;
            stale |= updated is null || now - updated > StaleAfterMs || updated > now + 300_000;
            providers.Add(new QuotaProvider(id, name, plan, limits,
                balances.Count > 0 ? string.Join(" · ", balances.Take(4)) : null, updated,
                failed ? "ai-usagebar provider unavailable" : stale ? "ai-usagebar data is stale"
                    : limits.Count == 0 ? "ai-usagebar returned no measured limits" : null,
                Source: "ai-usagebar"));
        }
        return providers.DistinctBy(p => p.Id).ToList();
    }

    /// <summary>
    /// ai-usagebar files rows that sit beneath an overall meter under a group heading: SuperGrok's per-product
    /// slices of its weekly limit ("Breakdown") and Claude's recent sessions ("Sessions"). They are not limits of
    /// their own, so they never become quota rings. Other groups (Antigravity's model families) are real limits.
    /// </summary>
    private static bool IsSubRow(JsonElement metric) => Text(metric, "group", 64) is "Breakdown" or "Sessions";

    internal static IReadOnlyList<QuotaProvider> Snapshot(IReadOnlyList<QuotaProvider> providers, long now) =>
        providers.Select(p => p.Error is null && (p.UpdatedAt is null || now - p.UpdatedAt > StaleAfterMs
            || p.Limits.Any(l => l.ResetsAt is { } reset && reset < now))
                ? p with { Error = "ai-usagebar data is stale" } : p).ToList();

    internal static string NormalizeId(string id)
    {
        var parts = id.Split('@', 2);
        var vendor = parts[0].ToLowerInvariant() switch
        {
            "anthropic" or "anthropic-desktop" => "claude",
            "openai" => "codex",
            var other => other,
        };
        return parts.Length == 2 ? vendor + "@" + parts[1] : vendor;
    }

    private static string? Text(JsonElement item, string field, int max)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(field, out var value)
            || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()?.Trim();
        return text is { Length: > 0 } && text.Length <= max && !text.Any(char.IsControl) ? text : null;
    }

    private static double? Number(JsonElement item, string field) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;

    private static long? Date(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var date) ? date.ToUnixTimeMilliseconds() : null;
}
