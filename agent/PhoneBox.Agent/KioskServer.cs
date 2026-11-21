using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

/// <summary>The browser has a separate listener, session store and explicit API policy.</summary>
public sealed class KioskServer
{
    private readonly ConfigStore _config;
    private readonly Func<IServiceProvider, object> _state;
    private readonly KioskSessions _sessions;
    private readonly string _configuredHost;
    public KioskCertificates? Certificates { get; }
    public int Port { get; }
    public string Host { get; }
    public bool Active => Certificates is not null;
    public string Url => $"https://{Host}:{Port}/kiosk/";

    public KioskServer(ConfigStore config, string directory, int localPort, Func<IServiceProvider, object> state)
    {
        _config = config;
        _state = state;
        _sessions = new KioskSessions(directory);
        var options = config.Get().Kiosk;
        Port = options.Port;
        _configuredHost = options.Host?.Trim() ?? "";
        Host = string.IsNullOrWhiteSpace(options.Host) ? Environment.MachineName + ".local" : options.Host.Trim();
        if (!options.Enabled) return;
        if (Port == localPort || Port == config.Get().Lan.Port || Port is < 1024 or > 65535)
            throw new ArgumentException("The browser HTTPS port must be separate from settings and Android Wi-Fi.");
        Certificates = new KioskCertificates(directory, Host);
    }

    public object Status() => new
    {
        configured = _config.Get().Kiosk.Enabled, active = Active, url = Url, port = Port,
        urls = Certificates?.Hosts.Where(h => h is not ("localhost" or "127.0.0.1")).Select(h => $"https://{h}:{Port}/kiosk/").ToArray() ?? [],
        fingerprint = Certificates?.Fingerprint,
        restartNeeded = _config.Get().Kiosk.Enabled != Active || Active &&
            (_config.Get().Kiosk.Port != Port || !string.Equals(_config.Get().Kiosk.Host?.Trim() ?? "", _configuredHost, StringComparison.OrdinalIgnoreCase)),
        devices = _sessions.List(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
    };

    public IResult StartPairing(bool controls)
    {
        if (!Active || !_config.Get().Kiosk.Enabled) return Results.BadRequest(new { error = "Enable the browser panel and restart the agent first." });
        var (code, expiresAt, attempt) = _sessions.Start(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), controls);
        var link = Url + "pair.html#code=" + code;
        return Results.Json(new { link, code, expiresAt, attempt, controls, qr = PairingCodes.Svg(link) });
    }

    public void Cancel(long attempt) => _sessions.Cancel(attempt);
    public bool Revoke(string id) => _sessions.Revoke(id);

    internal bool TrustedRequest(HttpRequest request)
    {
        if (!request.IsHttps || request.Host.Port != Port || Certificates is null ||
            !Certificates.Hosts.Contains(request.Host.Host, StringComparer.OrdinalIgnoreCase)) return false;
        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0 && !string.Equals(origin, "https://" + request.Host.Value, StringComparison.OrdinalIgnoreCase)) return false;
        if (request.Headers["Sec-Fetch-Site"].ToString() is "cross-site" or "same-site")
            return request.Method == "GET" && request.Path.StartsWithSegments("/kiosk") && request.Headers["Sec-Fetch-Mode"] == "navigate";
        return request.Method is "GET" or "HEAD" || origin.Length > 0;
    }

    /// <returns>False only for an authenticated, explicitly granted control routed to the existing handler.</returns>
    public async Task<bool> HandleAsync(HttpContext context)
    {
        var request = context.Request;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: https:; connect-src 'self' https:; font-src 'self' data:; frame-src 'self'; frame-ancestors 'self'; base-uri 'none'; form-action 'self'; object-src 'none'";
        if (!_config.Get().Kiosk.Enabled || !TrustedRequest(request)) return Deny(context, 403);

        var path = request.Path.Value ?? "";
        var isGet = request.Method == "GET";
        if (isGet && path is "/" or "/kiosk")
        {
            context.Response.Redirect("/kiosk/");
            return true;
        }
        if (isGet && IsAsset(path))
        {
            await Asset(context, path);
            return true;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (request.Method == "POST" && path == "/kiosk/pair")
        {
            if (request.Headers["X-PhoneBox-Panel"] != "1") return Deny(context, 403);
            context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 1024;
            KioskPairRequest? body;
            try { body = await request.ReadFromJsonAsync<KioskPairRequest>(cancellationToken: context.RequestAborted); }
            catch (Exception e) when (e is JsonException or BadHttpRequestException or IOException or InvalidOperationException)
            { return Deny(context, 400); }
            if (!_config.Get().Kiosk.Enabled) return Deny(context, 403);
            var claim = _sessions.Claim(body?.Code, body?.Name, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (claim is null) return Deny(context, 401);
            context.Response.Cookies.Append(KioskSessions.CookieName, claim.Value.Token, Cookie(claim.Value.Device.ExpiresAt));
            await context.Response.WriteAsJsonAsync(Session(claim.Value.Device), context.RequestAborted);
            return true;
        }

        var device = _sessions.Find(request.Cookies[KioskSessions.CookieName], now);
        if (device is null) return Deny(context, 401);
        if (path == "/kiosk/session" && isGet)
        {
            await context.Response.WriteAsJsonAsync(Session(device), context.RequestAborted);
            return true;
        }
        if (path == "/kiosk/logout" && request.Method == "POST")
        {
            if (request.Headers["X-PhoneBox-Panel"] != "1") return Deny(context, 403);
            _sessions.Revoke(device.Id);
            context.Response.Cookies.Delete(KioskSessions.CookieName, Cookie(device.ExpiresAt));
            await context.Response.WriteAsJsonAsync(new { paired = false }, context.RequestAborted);
            return true;
        }
        if (request.Headers["X-PhoneBox-Panel"] != "1" && path is not ("/api/art" or "/api/procs/icon"))
            return Deny(context, 403);
        if (request.Method == "POST" && device.Controls && AllowedControl(path)) return false;
        if (!isGet) return Deny(context, 403);

        var services = context.RequestServices;
        object result;
        switch (path)
        {
            case "/api/config":
                result = new { version = _config.VersionText, config = PublicConfig(_config.Get(), device.Controls) };
                break;
            case "/api/state":
                result = PublicState(_state(services));
                break;
            case "/api/quotas":
                var quotas = services.GetRequiredService<QuotaMonitor>();
                result = new { providers = quotas.Snapshot(), overview = quotas.Overview() };
                break;
            case "/api/work":
                result = new
                {
                    sessions = services.GetRequiredService<SessionMonitor>().Snapshot().Select(PublicSession),
                    github = services.GetRequiredService<GitHubMonitor>().Snapshot(),
                };
                break;
            case "/api/procs":
                var groups = services.GetRequiredService<ProcessMonitor>().Snapshot();
                result = new
                {
                    total = groups.Sum(g => g.Count), groupCount = groups.Count,
                    groups = groups.Take(40).Select(g => g with { Title = g.Name, Locked = true }),
                };
                break;
            case "/api/procs/icon":
                var icon = services.GetRequiredService<ProcessMonitor>().Icon(request.Query["name"].ToString());
                if (icon is null) return Deny(context, 404);
                context.Response.ContentType = "image/png";
                await context.Response.Body.WriteAsync(icon, context.RequestAborted);
                return true;
            case "/api/art":
                var art = services.GetRequiredService<MediaMonitor>().Art;
                if (art is null) return Deny(context, 404);
                context.Response.ContentType = art.Mime;
                await context.Response.Body.WriteAsync(art.Bytes, context.RequestAborted);
                return true;
            case "/api/push": result = new { version = 0, items = new { } }; break;
            case "/api/approvals": result = new { items = Array.Empty<object>() }; break;
            default: return Deny(context, 403);
        }
        await context.Response.WriteAsJsonAsync(result, context.RequestAborted);
        return true;
    }

    internal static bool AllowedControl(string path) => path is "/api/volume" or "/api/media/playpause" or "/api/media/next" or "/api/media/prev" or "/api/media/seek"
        || path.StartsWith("/api/run/", StringComparison.Ordinal) && path[9..] is { Length: > 0 and <= 100 } id && !id.Contains('/');

    internal static object PublicSession(AgentSession session) => session with
    {
        Name = session.Tool, Project = "", WaitingFor = null, Pid = null, LastTool = null, Title = null,
        Id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(session.Id)))[..16],
    };

    internal static JsonNode PublicState(object state)
    {
        var result = JsonSerializer.SerializeToNode(state, ConfigStore.WriteOpts)!.AsObject();
        result["events"] = new JsonArray();
        result["pushVersion"] = 0;
        return result;
    }

    internal static JsonNode PublicConfig(AgentConfig config, bool controls)
    {
        var result = JsonSerializer.SerializeToNode(config.ToPublic(), ConfigStore.WriteOpts)!.AsObject();
        result["kiosk"] = new JsonObject { ["controls"] = controls };
        foreach (var page in result["pages"]!.AsArray()) FilterWidgets(page!["widgets"]!.AsArray(), controls);
        return result;
    }

    private static readonly HashSet<string> UnsupportedWidgets = ["command", "html", "spectrum", "camera", "app-console", "dictate", "battery", "chargecare", "pcswitch"];
    private static void FilterWidgets(JsonArray widgets, bool controls)
    {
        foreach (var widget in widgets.ToArray())
        {
            var type = widget?["type"]?.GetValue<string>() ?? "";
            if (UnsupportedWidgets.Contains(type)) { widgets.Remove(widget); continue; }
            if (widget?["options"] is JsonObject options)
            {
                options["allowKill"] = false;
                options["control"] = controls;
                if (options["widgets"] is JsonArray children) FilterWidgets(children, controls);
            }
        }
    }

    private static object Session(KioskDevice device) => new { paired = true, device.Name, device.ExpiresAt, device.Controls };
    private static CookieOptions Cookie(long expires) => new()
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/",
        Expires = DateTimeOffset.FromUnixTimeMilliseconds(expires), IsEssential = true,
    };
    private static bool Deny(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return true;
    }

    private static bool IsAsset(string path) => path.StartsWith("/kiosk/", StringComparison.Ordinal) &&
        path is not ("/kiosk/pair" or "/kiosk/session" or "/kiosk/logout");

    private static async Task Asset(HttpContext context, string path)
    {
        string resource;
        if (path.StartsWith("/kiosk/panel/", StringComparison.Ordinal))
            resource = "www/" + (path[13..] is { Length: > 0 } panel ? panel : "index.html");
        else resource = "kiosk/" + (path[7..] is { Length: > 0 } file ? file : "index.html");
        using var stream = AppPaths.Resource(resource);
        if (stream is null) { Deny(context, 404); return; }
        context.Response.ContentType = Path.GetExtension(resource) switch
        {
            ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8", ".json" or ".webmanifest" => "application/manifest+json",
            ".svg" => "image/svg+xml", ".png" => "image/png", ".woff2" => "font/woff2", _ => "application/octet-stream",
        };
        await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private sealed record KioskPairRequest(string? Code, string? Name);
}
