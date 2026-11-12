using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BudgetTenable.Api.Banking;

/// <summary>
/// Signe les jetons d'accès à l'API Enable Banking : JWT RS256, <c>kid</c> = identifiant de l'application,
/// <c>iss</c> = enablebanking.com, <c>aud</c> = api.enablebanking.com, durée de vie d'une heure (24 h au plus sont admises).
/// La clé privée ne quitte jamais ce service et n'est jamais journalisée.
/// </summary>
public sealed class EnableBankingAuth : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private readonly RSA? _rsa;
    private readonly string _appId;
    private readonly object _lock = new();
    private string? _token;
    private DateTimeOffset _tokenExpiry;

    public EnableBankingAuth(IOptions<EnableBankingOptions> options)
    {
        var o = options.Value;
        _appId = o.AppId?.Trim() ?? "";
        if (!o.Configured) return;

        var pem = o.PrivateKey;
        if (string.IsNullOrWhiteSpace(pem) && !string.IsNullOrWhiteSpace(o.PrivateKeyFile))
        {
            if (!File.Exists(o.PrivateKeyFile))
                throw new InvalidOperationException($"Synchro bancaire : le fichier de clé privée « {o.PrivateKeyFile} » (BUDGET_EB_KEY_FILE) est introuvable.");
            pem = File.ReadAllText(o.PrivateKeyFile);
        }
        if (string.IsNullOrWhiteSpace(pem))
            throw new InvalidOperationException("Synchro bancaire : BUDGET_EB_APP_ID est défini mais pas la clé privée (BUDGET_EB_KEY_FILE ou BUDGET_EB_KEY).");
        if (!Uri.TryCreate(o.RedirectUrl, UriKind.Absolute, out var redirect) || (redirect.Scheme != Uri.UriSchemeHttps && !redirect.IsLoopback))
            throw new InvalidOperationException("Synchro bancaire : BUDGET_EB_REDIRECT_URL doit être une URL HTTPS complète, par exemple https://budget.example.fr/api/bank/callback.");

        _rsa = RSA.Create();
        try { _rsa.ImportFromPem(pem); }
        catch (Exception e) when (e is ArgumentException or CryptographicException)
        {
            _rsa.Dispose();
            throw new InvalidOperationException("Synchro bancaire : la clé privée n'est pas une clé RSA au format PEM lisible.");
        }
    }

    public bool Configured => _rsa is not null;

    /// <summary>Jeton valide, réutilisé tant qu'il lui reste plus de cinq minutes.</summary>
    public string Token(DateTimeOffset now)
    {
        if (_rsa is null) throw new InvalidOperationException("La synchro bancaire n'est pas configurée.");
        lock (_lock)
        {
            if (_token is not null && now >= _tokenExpiry - Lifetime && now < _tokenExpiry - Margin) return _token;
            var header = JsonSerializer.Serialize(new { typ = "JWT", alg = "RS256", kid = _appId });
            var payload = JsonSerializer.Serialize(new
            {
                iss = "enablebanking.com",
                aud = "api.enablebanking.com",
                iat = now.ToUnixTimeSeconds(),
                exp = now.Add(Lifetime).ToUnixTimeSeconds(),
            });
            var signingInput = B64(Encoding.UTF8.GetBytes(header)) + "." + B64(Encoding.UTF8.GetBytes(payload));
            var signature = _rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            _token = signingInput + "." + B64(signature);
            _tokenExpiry = now.Add(Lifetime);
            return _token;
        }
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _rsa?.Dispose();
}
