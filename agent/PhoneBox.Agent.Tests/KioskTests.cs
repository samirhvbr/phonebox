using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using PhoneBox.Agent;
using Xunit;

public sealed class KioskTests
{
    private const long Now = 1_800_000_000_000;
    private const int Port = 18867;

    [Fact]
    public void BrowserHttpsIsOptInAndRejectsEitherExistingListenerPort()
    {
        using var fixture = new Fixture(enabled: false);
        Assert.False(fixture.Server.Active);
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.pfx"));
        Assert.Equal(StatusCodes.Status400BadRequest,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(fixture.Server.StartPairing(false)).StatusCode);

        fixture.Configure(enabled: true, port: 18865);
        Assert.Throws<ArgumentException>(() => fixture.CreateServer());
        fixture.Configure(enabled: true, port: 18866);
        Assert.Throws<ArgumentException>(() => fixture.CreateServer());
    }

    [Fact]
    public void DevicesUseIndependentHashedCredentialsAndRevocationSurvivesRestart()
    {
        using var directory = new TemporaryDirectory();
        var sessions = new KioskSessions(directory.Path);
        var first = Pair(sessions, "Safari", Now, controls: false);
        var second = Pair(sessions, "Home Screen", Now, controls: true);
        Assert.NotEqual(first.Token, second.Token);
        Assert.Matches("^[A-F0-9]{64}$", first.Token);
        Assert.NotEqual(first.Token, first.Device.TokenHash);
        var persisted = File.ReadAllText(Path.Combine(directory.Path, "kiosk-devices.json"));
        Assert.DoesNotContain(first.Token, persisted);
        Assert.DoesNotContain(second.Token, persisted);
        Assert.DoesNotContain("TokenHash", JsonSerializer.Serialize(sessions.List(Now)));

        sessions = new KioskSessions(directory.Path);
        Assert.False(sessions.Find(first.Token, Now)!.Controls);
        Assert.True(sessions.Find(second.Token, Now)!.Controls);
        Assert.True(sessions.Revoke(first.Device.Id));
        Assert.False(sessions.Revoke(first.Device.Id));
        sessions = new KioskSessions(directory.Path);
        Assert.Null(sessions.Find(first.Token, Now));
        Assert.NotNull(sessions.Find(second.Token, Now));
        Assert.Single(sessions.List(Now));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(directory.Path, "kiosk-devices.json")));
    }

    [Fact]
    public void ExpiredDevicesCannotAuthenticateAndArePrunedOnTheNextClaim()
    {
        using var directory = new TemporaryDirectory();
        var sessions = new KioskSessions(directory.Path);
        var old = Pair(sessions, "Expired", Now);
        Assert.Equal(Now + KioskSessions.LifetimeMs, old.Device.ExpiresAt);
        Assert.NotNull(sessions.Find(old.Token, old.Device.ExpiresAt - 1));
        Assert.Null(sessions.Find(old.Token, old.Device.ExpiresAt));
        Assert.Empty(sessions.List(old.Device.ExpiresAt));
        Assert.Null(sessions.Find(null, Now));
        Assert.Null(sessions.Find("not-a-token", Now));
        Assert.Null(sessions.Find(new string('f', 64), Now));

        var current = Pair(sessions, "Current", old.Device.ExpiresAt);
        var restarted = new KioskSessions(directory.Path);
        Assert.Null(restarted.Find(old.Token, old.Device.ExpiresAt));
        Assert.NotNull(restarted.Find(current.Token, old.Device.ExpiresAt));
        Assert.DoesNotContain(old.Device.Id, File.ReadAllText(Path.Combine(directory.Path, "kiosk-devices.json")));
    }

    [Fact]
    public async Task AnInvitationHasOneWinnerEvenWhenClaimsArriveTogether()
    {
        using var directory = new TemporaryDirectory();
        var sessions = new KioskSessions(directory.Path);
        var (expired, expires, _) = sessions.Start(Now, false);
        Assert.Null(sessions.Claim(expired, "Expired", expires + 1));
        var (cancelled, _, attempt) = sessions.Start(Now, false);
        sessions.Cancel(attempt);
        Assert.Null(sessions.Claim(cancelled, "Cancelled", Now));
        var (replaced, _, older) = sessions.Start(Now, false);
        var (code, _, _) = sessions.Start(Now, true);
        sessions.Cancel(older);
        Assert.Null(sessions.Claim(replaced, "Replaced", Now));

        var claims = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(i => Task.Run(() => sessions.Claim(code, "Browser " + i, Now))));
        var winner = Assert.Single(claims, c => c is not null)!.Value;
        Assert.True(winner.Device.Controls);
        Assert.Single(sessions.List(Now));
        Assert.Null(sessions.Claim(code, "Replay", Now));
        Assert.NotNull(new KioskSessions(directory.Path).Find(winner.Token, Now));
    }

    [Fact]
    public void InvalidBrowserNamesDoNotConsumeTheInvitation()
    {
        using var directory = new TemporaryDirectory();
        var sessions = new KioskSessions(directory.Path);
        var (code, _, _) = sessions.Start(Now, false);
        Assert.Null(sessions.Claim(code, null, Now));
        Assert.Null(sessions.Claim(code, "   ", Now));
        Assert.Null(sessions.Claim(code, new string('a', 81), Now));
        Assert.Equal("Safari", sessions.Claim(code, " Safari ", Now)!.Value.Device.Name);
    }

    [Theory]
    [InlineData("https", "phonebox.test:18867", "GET", "/api/state", "", "", "", true)]
    [InlineData("https", "PHONEBOX.TEST:18867", "POST", "/api/volume", "https://phonebox.test:18867", "same-origin", "cors", true)]
    [InlineData("https", "127.0.0.1:18867", "GET", "/api/state", "", "", "", true)]
    [InlineData("http", "phonebox.test:18867", "GET", "/api/state", "", "", "", false)]
    [InlineData("https", "evil.test:18867", "GET", "/api/state", "", "", "", false)]
    [InlineData("https", "phonebox.test:18865", "GET", "/api/state", "", "", "", false)]
    [InlineData("https", "phonebox.test", "GET", "/api/state", "", "", "", false)]
    [InlineData("https", "phonebox.test:18867", "GET", "/api/state", "https://evil.test", "", "", false)]
    [InlineData("https", "phonebox.test:18867", "GET", "/api/state", "null", "", "", false)]
    [InlineData("https", "phonebox.test:18867", "POST", "/kiosk/pair", "", "", "", false)]
    [InlineData("https", "phonebox.test:18867", "GET", "/api/state", "", "cross-site", "navigate", false)]
    [InlineData("https", "phonebox.test:18867", "GET", "/api/state", "", "same-site", "cors", false)]
    [InlineData("https", "phonebox.test:18867", "GET", "/kiosk/", "", "cross-site", "navigate", true)]
    [InlineData("https", "phonebox.test:18867", "GET", "/kiosk/", "", "cross-site", "no-cors", false)]
    public void RequestsRequireHttpsAnExpectedHostAndSameOrigin(
        string scheme, string host, string method, string path, string origin, string site, string mode, bool trusted)
    {
        using var fixture = new Fixture();
        var context = fixture.Context(method, path);
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        context.Request.Headers.Origin = origin;
        context.Request.Headers["Sec-Fetch-Site"] = site;
        context.Request.Headers["Sec-Fetch-Mode"] = mode;
        Assert.Equal(trusted, fixture.Server.TrustedRequest(context.Request));
    }

    [Fact]
    public async Task PairingRequiresThePanelHeaderAndSetsASecureHttpOnlyHostCookie()
    {
        using var fixture = new Fixture();
        var code = fixture.StartPairing(controls: false);
        var rejected = fixture.PairRequest(code, header: false);
        Assert.True(await fixture.Server.HandleAsync(rejected));
        Assert.Equal(403, rejected.Response.StatusCode);
        var accepted = fixture.PairRequest(code);
        Assert.True(await fixture.Server.HandleAsync(accepted));
        Assert.Equal(200, accepted.Response.StatusCode);
        var cookie = accepted.Response.Headers.SetCookie.ToString();
        Assert.StartsWith(KioskSessions.CookieName + "=", cookie);
        Assert.Contains("; path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", fixture.Body(accepted), StringComparison.OrdinalIgnoreCase);
        var replay = fixture.PairRequest(code);
        Assert.True(await fixture.Server.HandleAsync(replay));
        Assert.Equal(401, replay.Response.StatusCode);
    }

    [Fact]
    public async Task PairingThatOutlivesDisablingThePanelIsRefusedWithoutConsumingItsCode()
    {
        using var fixture = new Fixture();
        var code = fixture.StartPairing(controls: true);
        var context = fixture.PairRequest(code);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Request.Body = new GatedMemoryStream(Encoding.UTF8.GetBytes($$"""{"code":"{{code}}","name":"Safari"}"""), entered, release.Task);
        var pending = fixture.Server.HandleAsync(context);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Configure(enabled: false);
        }
        finally { release.TrySetResult(); }
        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(403, context.Response.StatusCode);
        Assert.Empty(new KioskSessions(fixture.Directory).List(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        fixture.Configure(enabled: true);
        var retry = fixture.PairRequest(code);
        Assert.True(await fixture.Server.HandleAsync(retry));
        Assert.Equal(200, retry.Response.StatusCode);
    }

    [Fact]
    public async Task TheBareKioskAddressRedirectsBeforePairingIncludingCrossSiteNavigation()
    {
        using var fixture = new Fixture();
        foreach (var path in new[] { "/", "/kiosk" })
        {
            var context = fixture.Context("GET", path, header: false);
            context.Request.Headers.Remove("Origin");
            if (path == "/kiosk")
            {
                context.Request.Headers["Sec-Fetch-Site"] = "cross-site";
                context.Request.Headers["Sec-Fetch-Mode"] = "navigate";
            }
            Assert.True(await fixture.Server.HandleAsync(context));
            Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
            Assert.Equal("/kiosk/", context.Response.Headers.Location.ToString());
            Assert.Equal(0, context.Response.Headers.SetCookie.Count);
        }
    }

    [Fact]
    public async Task DenialsHaveAnExplicitTextContentTypeForBrowserNavigation()
    {
        using var fixture = new Fixture();
        var unauthorized = fixture.Context("GET", "/api/config", header: false);
        var forbidden = fixture.Context("GET", "/kiosk/", header: false);
        forbidden.Request.Host = new HostString("untrusted.test", Port);
        var malformed = fixture.PairRequest("unused");
        malformed.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{invalid-json"));
        var missing = fixture.Context("GET", "/kiosk/missing.html", header: false);
        foreach (var (context, status) in new[]
        {
            (unauthorized, StatusCodes.Status401Unauthorized),
            (forbidden, StatusCodes.Status403Forbidden),
            (malformed, StatusCodes.Status400BadRequest),
            (missing, StatusCodes.Status404NotFound),
        })
        {
            Assert.True(await fixture.Server.HandleAsync(context));
            Assert.Equal(status, context.Response.StatusCode);
            Assert.Equal("text/plain; charset=utf-8", context.Response.ContentType);
        }
    }

    [Fact]
    public async Task ApiReadsRequireBothAPairedCookieAndThePanelHeader()
    {
        using var fixture = new Fixture();
        var unpaired = fixture.Context("GET", "/api/config");
        Assert.True(await fixture.Server.HandleAsync(unpaired));
        Assert.Equal(401, unpaired.Response.StatusCode);
        var token = await fixture.PairBrowserAsync(false);
        var missingHeader = fixture.Context("GET", "/api/config", token, header: false);
        Assert.True(await fixture.Server.HandleAsync(missingHeader));
        Assert.Equal(403, missingHeader.Response.StatusCode);
        var accepted = fixture.Context("GET", "/api/config", token);
        Assert.True(await fixture.Server.HandleAsync(accepted));
        Assert.Equal(200, accepted.Response.StatusCode);
        Assert.Equal("no-store", accepted.Response.Headers.CacheControl.ToString());
        Assert.Equal("nosniff", accepted.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Contains("frame-ancestors 'self'", accepted.Response.Headers.ContentSecurityPolicy.ToString());
        Assert.False(JsonNode.Parse(fixture.Body(accepted))!["config"]!["kiosk"]!["controls"]!.GetValue<bool>());
    }

    [Fact]
    public async Task EvenAControlDeviceCannotReachAdministrationApprovalsOrCommandOutput()
    {
        using var fixture = new Fixture();
        var token = await fixture.PairBrowserAsync(true);
        foreach (var (method, path) in new[]
        {
            ("GET", "/api/settings"), ("POST", "/api/settings"), ("GET", "/api/kiosk"),
            ("POST", "/api/kiosk/pair/start"), ("DELETE", "/api/kiosk/devices/device"),
            ("POST", "/api/restart"), ("POST", "/api/approvals/approve"),
            ("POST", "/api/opencode/permission"), ("POST", "/api/procs/kill"),
            ("GET", "/api/widget/page/secret"), ("GET", "/api/command/secret"),
            ("GET", "/api/feed"), ("POST", "/api/controls/command/terminal"),
        })
        {
            var denied = fixture.Context(method, path, token);
            Assert.True(await fixture.Server.HandleAsync(denied), method + " " + path);
            Assert.Equal(403, denied.Response.StatusCode);
        }
        var approvals = fixture.Context("GET", "/api/approvals", token);
        Assert.True(await fixture.Server.HandleAsync(approvals));
        Assert.Empty(JsonNode.Parse(fixture.Body(approvals))!["items"]!.AsArray());
    }

    [Fact]
    public async Task OnlyExplicitControlsWithThePanelHeaderReachNativeHandlers()
    {
        using var fixture = new Fixture();
        var readOnly = await fixture.PairBrowserAsync(false);
        var control = await fixture.PairBrowserAsync(true);
        foreach (var path in new[] { "/api/volume", "/api/media/playpause", "/api/media/next", "/api/media/prev", "/api/media/seek", "/api/run/safe-id" })
        {
            var denied = fixture.Context("POST", path, readOnly);
            Assert.True(await fixture.Server.HandleAsync(denied));
            Assert.Equal(403, denied.Response.StatusCode);
            denied = fixture.Context("POST", path, control, header: false);
            Assert.True(await fixture.Server.HandleAsync(denied));
            Assert.Equal(403, denied.Response.StatusCode);
            Assert.False(await fixture.Server.HandleAsync(fixture.Context("POST", path, control)));
            var get = fixture.Context("GET", path, control);
            Assert.True(await fixture.Server.HandleAsync(get));
            Assert.Equal(403, get.Response.StatusCode);
        }
        Assert.False(KioskServer.AllowedControl("/api/run/"));
        Assert.False(KioskServer.AllowedControl("/api/run/one/two"));
        Assert.False(KioskServer.AllowedControl("/api/run/" + new string('a', 101)));
        Assert.False(KioskServer.AllowedControl("/api/media/arbitrary"));
    }

    [Fact]
    public async Task LogoutRevokesOnlyItsBrowserAndDisablingThePanelImmediatelyRefusesExistingCookies()
    {
        using var fixture = new Fixture();
        var first = await fixture.PairBrowserAsync(false);
        var second = await fixture.PairBrowserAsync(false);
        var logout = fixture.Context("POST", "/kiosk/logout", first);
        Assert.True(await fixture.Server.HandleAsync(logout));
        Assert.Contains("expires=Thu, 01 Jan 1970", logout.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
        var revoked = fixture.Context("GET", "/kiosk/session", first);
        Assert.True(await fixture.Server.HandleAsync(revoked));
        Assert.Equal(401, revoked.Response.StatusCode);
        var remaining = fixture.Context("GET", "/kiosk/session", second);
        Assert.True(await fixture.Server.HandleAsync(remaining));
        Assert.Equal(200, remaining.Response.StatusCode);
        fixture.Configure(enabled: false);
        var disabled = fixture.Context("GET", "/api/config", second);
        Assert.True(await fixture.Server.HandleAsync(disabled));
        Assert.Equal(403, disabled.Response.StatusCode);
    }

    [Fact]
    public void PublicStateRemovesPushEventsAndPublicSessionsRemoveTitlesAndProjectDetails()
    {
        var state = KioskServer.PublicState(new
        {
            ts = 123, pc = new { cpu = 42 }, pushVersion = 9,
            events = new[] { new { type = "approval", text = "private conversation" } },
        });
        Assert.Empty(state["events"]!.AsArray());
        Assert.Equal(0, state["pushVersion"]!.GetValue<int>());
        Assert.Equal(42, state["pc"]!["cpu"]!.GetValue<int>());
        Assert.DoesNotContain("private conversation", state.ToJsonString());
        var original = new AgentSession("private-session-id", "claude", "Private conversation", "/secret/project", "waiting",
            "permission prompt", Now, Now, Pid: 123, Model: "model", LastTool: "secret command", Title: "Private title");
        var session = Assert.IsType<AgentSession>(KioskServer.PublicSession(original));
        Assert.Equal("claude", session.Name);
        Assert.Equal("", session.Project);
        Assert.Null(session.WaitingFor);
        Assert.Null(session.Pid);
        Assert.Null(session.LastTool);
        Assert.Null(session.Title);
        Assert.Matches("^[A-F0-9]{16}$", session.Id);
        Assert.NotEqual(original.Id, session.Id);
        Assert.Equal(session.Id, Assert.IsType<AgentSession>(KioskServer.PublicSession(original)).Id);
        Assert.Equal("model", session.Model);
        Assert.Equal("Private title", original.Title);
    }

    [Fact]
    public void PublicConfigRemovesCommandsAndUnsupportedStackChildrenWithoutMutatingSettings()
    {
        var config = JsonSerializer.Deserialize<AgentConfig>("""
        {
          "lan":{"token":"secret-lan-token"}, "kiosk":{"enabled":true,"host":"private-host"},
          "shortcuts":[{"id":"shortcut","label":"Run","cmd":"secret-shortcut","args":"secret-args","cwd":"/private"}],
          "pages":[{"id":"page","widgets":[
            {"id":"command","type":"command","options":{"command":"secret-script"}},
            {"id":"html","type":"html","options":{"html":"secret-html"}},
            {"id":"processes","type":"processes","options":{"allowKill":true,"control":true}},
            {"id":"stack","type":"stack","options":{"widgets":[
              {"id":"nested-command","type":"command","options":{"command":"secret-nested-script"}},
              {"id":"nested-camera","type":"camera","options":{}},
              {"id":"nested-html","type":"html","options":{"html":"secret-nested-html"}},
              {"id":"nested-procs","type":"processes","options":{"allowKill":true,"control":true}},
              {"id":"nested-clock","type":"clock","options":{}}
            ]}}
          ]}]
        }
        """, ConfigStore.ReadOpts)!;
        var serializedOriginal = JsonSerializer.Serialize(config, ConfigStore.WriteOpts);
        foreach (var controls in new[] { false, true })
        {
            var result = KioskServer.PublicConfig(config, controls);
            var json = result.ToJsonString();
            Assert.DoesNotContain("secret-", json);
            Assert.DoesNotContain("private-host", json);
            Assert.DoesNotContain("/private", json);
            var widgets = result["pages"]![0]!["widgets"]!.AsArray();
            Assert.Equal(new[] { "processes", "stack" }, widgets.Select(w => w!["type"]!.GetValue<string>()));
            var processOptions = widgets[0]!["options"]!;
            Assert.False(processOptions["allowKill"]!.GetValue<bool>());
            Assert.Equal(controls, processOptions["control"]!.GetValue<bool>());
            var children = widgets[1]!["options"]!["widgets"]!.AsArray();
            Assert.Equal(new[] { "processes", "clock" }, children.Select(w => w!["type"]!.GetValue<string>()));
            Assert.False(children[0]!["options"]!["allowKill"]!.GetValue<bool>());
            Assert.Equal(controls, children[0]!["options"]!["control"]!.GetValue<bool>());
        }
        Assert.Equal(serializedOriginal, JsonSerializer.Serialize(config, ConfigStore.WriteOpts));
    }

    [Fact]
    public void TlsCertificateHasExpectedSansAndTheXmlProfileContainsOnlyThePublicAuthority()
    {
        using var directory = new TemporaryDirectory();
        var certificates = new KioskCertificates(directory.Path, "phonebox.test");
        using var authority = certificates.Authority;
        using var server = certificates.Server;
        Assert.True(authority.HasPrivateKey);
        Assert.True(server.HasPrivateKey);
        foreach (var host in new[] { "phonebox.test", "localhost", "127.0.0.1" })
            Assert.True(server.MatchesHostname(host, allowWildcards: false, allowCommonName: false), host);
        Assert.True(TrustedBy(server, authority));
        var profile = certificates.Profile();
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", Encoding.UTF8.GetString(profile));
        using var reader = XmlReader.Create(new MemoryStream(profile), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        var document = XDocument.Load(reader);
        Assert.Equal("plist", document.Root!.Name.LocalName);
        var data = Assert.Single(document.Descendants("data"));
        var exported = Convert.FromBase64String(data.Value);
        Assert.Equal(authority.RawData, exported);
        using var publicCertificate = X509CertificateLoader.LoadCertificate(exported);
        Assert.False(publicCertificate.HasPrivateKey);
        Assert.Contains(document.Descendants("string"), element => element.Value == "com.apple.security.root");
        var reloaded = new KioskCertificates(directory.Path, "phonebox.test");
        using var reloadedCa = reloaded.Authority;
        using var reloadedServer = reloaded.Server;
        Assert.Equal(certificates.Fingerprint, reloaded.Fingerprint);
        Assert.Equal(server.Thumbprint, reloadedServer.Thumbprint);
        if (!OperatingSystem.IsWindows())
            foreach (var file in Directory.GetFiles(directory.Path, "*.pfx"))
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Fact]
    public void RegeneratingTheAuthorityAlsoReissuesAnExistingServerCertificate()
    {
        using var directory = new TemporaryDirectory();
        var original = new KioskCertificates(directory.Path, "phonebox.test");
        using var oldAuthority = original.Authority;
        using var oldServer = original.Server;
        File.Delete(Path.Combine(directory.Path, "kiosk-ca.pfx"));
        var renewed = new KioskCertificates(directory.Path, "phonebox.test");
        using var authority = renewed.Authority;
        using var server = renewed.Server;
        Assert.NotEqual(original.Fingerprint, renewed.Fingerprint);
        Assert.True(TrustedBy(server, authority));
        Assert.NotEqual(oldServer.Thumbprint, server.Thumbprint);
    }

    private static bool TrustedBy(X509Certificate2 server, X509Certificate2 authority)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        return chain.Build(server);
    }

    private static (KioskDevice Device, string Token) Pair(KioskSessions sessions, string name, long now, bool controls = false)
    {
        var (code, _, _) = sessions.Start(now, controls);
        return sessions.Claim(code, name, now)!.Value;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        private readonly ServiceProvider _services = new ServiceCollection().AddLogging().BuildServiceProvider();
        public string Directory => _directory.Path;
        public ConfigStore Config { get; }
        public KioskServer Server { get; }

        public Fixture(bool enabled = true)
        {
            var path = Path.Combine(Directory, "config.json");
            File.WriteAllText(path, "{}");
            Config = new ConfigStore(path);
            Configure(enabled);
            Server = CreateServer();
        }

        public void Configure(bool enabled, int port = Port) => Assert.Null(Config.Save(JsonSerializer.Serialize(new
        {
            lan = new { enabled = false, port = 18866 },
            kiosk = new { enabled, port, host = "phonebox.test" },
        })));

        public KioskServer CreateServer() => new(Config, Directory, 18865, _ => new { events = Array.Empty<object>(), pushVersion = 0 });

        public DefaultHttpContext Context(string method, string path, string? token = null, bool header = true)
        {
            var context = new DefaultHttpContext { RequestServices = _services };
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("phonebox.test", Port);
            context.Request.Method = method;
            context.Request.Path = path;
            context.Request.Headers.Origin = "https://phonebox.test:" + Port;
            context.Request.Headers["Sec-Fetch-Site"] = "same-origin";
            if (header) context.Request.Headers["X-PhoneBox-Panel"] = "1";
            if (token is not null) context.Request.Headers.Cookie = KioskSessions.CookieName + "=" + token;
            context.Response.Body = new MemoryStream();
            context.Features.Set<IHttpMaxRequestBodySizeFeature>(new BodyLimit());
            return context;
        }

        public DefaultHttpContext PairRequest(string code, bool header = true)
        {
            var context = Context("POST", "/kiosk/pair", header: header);
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { code, name = "Safari" })));
            return context;
        }

        public string StartPairing(bool controls)
        {
            var result = Assert.IsAssignableFrom<IValueHttpResult>(Server.StartPairing(controls));
            return JsonSerializer.SerializeToElement(result.Value).GetProperty("code").GetString()!;
        }

        public async Task<string> PairBrowserAsync(bool controls)
        {
            var context = PairRequest(StartPairing(controls));
            Assert.True(await Server.HandleAsync(context));
            Assert.Equal(200, context.Response.StatusCode);
            var cookie = context.Response.Headers.SetCookie.ToString().Split(';')[0];
            Assert.StartsWith(KioskSessions.CookieName + "=", cookie);
            return cookie[(KioskSessions.CookieName.Length + 1)..];
        }

        public string Body(DefaultHttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

        public void Dispose()
        {
            Server.Certificates?.Server.Dispose();
            Server.Certificates?.Authority.Dispose();
            _services.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class BodyLimit : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Directory.CreateTempSubdirectory("phonebox-kiosk-").FullName;
        public void Dispose() => System.IO.Directory.Delete(Path, recursive: true);
    }

    private sealed class GatedMemoryStream(byte[] data, TaskCompletionSource entered, Task release) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await release.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
