using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed record KioskDevice(string Id, string Name, string TokenHash, long CreatedAt, long ExpiresAt, bool Controls);

/// <summary>Browser credentials are independent of Android keys. Only their hashes are stored on disk.</summary>
public sealed class KioskSessions
{
    public const string CookieName = "__Host-PhoneBox";
    public const long LifetimeMs = 30L * 24 * 60 * 60_000;
    private readonly Lock _lock = new();
    private readonly string _path;
    private List<KioskDevice> _devices;
    private readonly PairingCodes _codes = new();
    private bool _pendingControls;

    public KioskSessions(string directory)
    {
        _path = Path.Combine(directory, "kiosk-devices.json");
        if (!File.Exists(_path)) { _devices = []; return; }
        AppPaths.RestrictToOwner(_path);
        // A corrupt credential file must not silently recreate a trusted device.
        _devices = JsonSerializer.Deserialize<List<KioskDevice>>(File.ReadAllText(_path)) ?? [];
    }

    public (string Code, long ExpiresAt, long Attempt) Start(long now, bool controls)
    {
        lock (_lock)
        {
            _pendingControls = controls;
            return _codes.Start(now);
        }
    }

    public void Cancel(long attempt) { lock (_lock) _codes.Cancel(attempt); }

    public (KioskDevice Device, string Token)? Claim(string? code, string? name, long now)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) return null;
        lock (_lock)
        {
            if (!_codes.Claim(code, now)) return null;
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var device = new KioskDevice(Guid.NewGuid().ToString("N"), name.Trim(), Hash(token), now, now + LifetimeMs, _pendingControls);
            var next = _devices.Where(d => d.ExpiresAt > now).Append(device).ToList();
            Save(next);
            _devices = next;
            return (device, token);
        }
    }

    public KioskDevice? Find(string? token, long now)
    {
        if (token is not { Length: 64 }) return null;
        var hash = Encoding.ASCII.GetBytes(Hash(token));
        lock (_lock)
            return _devices.FirstOrDefault(d => d.ExpiresAt > now &&
                CryptographicOperations.FixedTimeEquals(hash, Encoding.ASCII.GetBytes(d.TokenHash)));
    }

    public IReadOnlyList<object> List(long now)
    {
        lock (_lock)
            return _devices.Where(d => d.ExpiresAt > now)
                .Select(d => (object)new { d.Id, d.Name, d.CreatedAt, d.ExpiresAt, d.Controls }).ToList();
    }

    public bool Revoke(string id)
    {
        lock (_lock)
        {
            var next = _devices.Where(d => d.Id != id).ToList();
            if (next.Count == _devices.Count) return false;
            Save(next);
            _devices = next;
            return true;
        }
    }

    private void Save(List<KioskDevice> devices)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        try
        {
            AppPaths.WriteAllTextPrivate(temporary, JsonSerializer.Serialize(devices));
            File.Move(temporary, _path, overwrite: true);
            AppPaths.RestrictToOwner(_path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
