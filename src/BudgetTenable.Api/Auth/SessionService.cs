using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace BudgetTenable.Api.Auth;

/// <summary>Vérifie le PIN et émet des jetons de session signés (HMAC-SHA256), sans état côté serveur.</summary>
public sealed class SessionService
{
    public const string CookieName = "bt_session";
    private readonly byte[] _key;
    private readonly byte[] _pinHash;
    private readonly AuthOptions _options;

    public SessionService(IOptions<AuthOptions> options, DataPaths paths)
    {
        _options = options.Value;
        _key = ResolveKey(_options, paths);
        var pin = ResolvePin(_options);
        if (_options.PinEnabled && string.IsNullOrEmpty(pin))
            throw new InvalidOperationException(
                "Aucun PIN configuré. Définis BUDGET_PIN (ou BUDGET_PIN_FILE), ou BUDGET_AUTH_MODE=none derrière un reverse proxy qui authentifie.");
        if (_options.PinEnabled && pin!.Length < 4)
            throw new InvalidOperationException("Le PIN doit comporter au moins 4 caractères.");
        _pinHash = SHA256.HashData(Encoding.UTF8.GetBytes(pin ?? ""));
    }

    public bool Required => _options.PinEnabled;
    public TimeSpan Lifetime => TimeSpan.FromDays(Math.Clamp(_options.SessionDays, 1, 365));
    public bool SecureCookies => !_options.InsecureCookies;

    public bool VerifyPin(string? candidate)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate ?? ""));
        return CryptographicOperations.FixedTimeEquals(hash, _pinHash);
    }

    public string Issue(DateTimeOffset now)
    {
        var payload = $"v1.{now.Add(Lifetime).ToUnixTimeSeconds()}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(8))}";
        return $"{payload}.{Sign(payload)}";
    }

    public bool Validate(string? token, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var i = token.LastIndexOf('.');
        if (i <= 0) return false;
        var payload = token[..i];
        var sig = token[(i + 1)..];
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sig), Encoding.ASCII.GetBytes(Sign(payload)))) return false;
        var parts = payload.Split('.');
        return parts.Length == 3 && parts[0] == "v1" && long.TryParse(parts[1], out var exp) && exp > now.ToUnixTimeSeconds();
    }

    private string Sign(string payload) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    private static string? ResolvePin(AuthOptions o)
    {
        if (!string.IsNullOrEmpty(o.PinFile) && File.Exists(o.PinFile)) return File.ReadAllText(o.PinFile).Trim();
        return o.Pin?.Trim();
    }

    private static byte[] ResolveKey(AuthOptions o, DataPaths paths)
    {
        if (!string.IsNullOrWhiteSpace(o.CookieKey))
        {
            var k = Convert.FromBase64String(o.CookieKey);
            if (k.Length < 32) throw new InvalidOperationException("BUDGET_COOKIE_KEY doit faire 32 octets minimum (base64).");
            return k;
        }
        // Clé générée une fois et conservée à côté de la base (hors base, hors dépôt) : les sessions survivent aux redémarrages.
        var path = Path.Combine(paths.Directory, "cookie.key");
        if (File.Exists(path)) return Convert.FromBase64String(File.ReadAllText(path).Trim());
        var key = RandomNumberGenerator.GetBytes(48);
        File.WriteAllText(path, Convert.ToBase64String(key));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }
}

public sealed record DataPaths(string Directory)
{
    public string DatabaseFile => Path.Combine(Directory, "budget.db");
}
