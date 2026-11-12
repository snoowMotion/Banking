using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BudgetTenable.Core.Banking;

namespace BudgetTenable.Api.Banking;

/// <summary>Erreur renvoyée par l'API Enable Banking. <see cref="Error"/> est le code texte (EXPIRED_SESSION, ASPSP_RATE_LIMIT_EXCEEDED…).</summary>
public sealed class EnableBankingException(HttpStatusCode status, string? error, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public string? Error { get; } = error;
}

/// <summary>
/// En-têtes « PSU » : ils indiquent à la banque que l'utilisateur est présent (synchro lancée à la main),
/// ce qui lève la limite des lectures automatiques. Soit tous ceux qu'exige la banque, soit aucun.
/// </summary>
public sealed record PsuHeaders(IReadOnlyDictionary<string, string> Values)
{
    public bool Covers(IEnumerable<string> required) => required.All(r => Values.Keys.Any(k => string.Equals(k, r, StringComparison.OrdinalIgnoreCase)));
}

public interface IEnableBankingClient
{
    Task<IReadOnlyList<EbAspsp>> GetAspspsAsync(string country, CancellationToken ct);
    Task<EbStartAuthResponse> StartAuthorizationAsync(EbStartAuthRequest request, CancellationToken ct);
    Task<EbSession> AuthorizeSessionAsync(string code, CancellationToken ct);
    Task DeleteSessionAsync(string sessionId, CancellationToken ct);
    Task<EbTransactionsPage> GetTransactionsAsync(string accountUid, DateOnly dateFrom, string strategy, string? continuationKey, PsuHeaders? psu, CancellationToken ct);
}

/// <summary>Client de l'API Enable Banking (https://enablebanking.com/docs/api/reference/).</summary>
public sealed class EnableBankingClient(HttpClient http, EnableBankingAuth auth, TimeProvider time) : IEnableBankingClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<IReadOnlyList<EbAspsp>> GetAspspsAsync(string country, CancellationToken ct)
    {
        var list = await SendAsync<EbAspspList>(HttpMethod.Get, "aspsps?country=" + Uri.EscapeDataString(country) + "&psu_type=personal", null, null, ct);
        return list.Aspsps ?? new List<EbAspsp>();
    }

    public Task<EbStartAuthResponse> StartAuthorizationAsync(EbStartAuthRequest request, CancellationToken ct) =>
        SendAsync<EbStartAuthResponse>(HttpMethod.Post, "auth", request, null, ct);

    public Task<EbSession> AuthorizeSessionAsync(string code, CancellationToken ct) =>
        SendAsync<EbSession>(HttpMethod.Post, "sessions", new EbAuthorizeSessionRequest(code), null, ct);

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct) =>
        await SendAsync<JsonElement>(HttpMethod.Delete, "sessions/" + Uri.EscapeDataString(sessionId), null, null, ct);

    public Task<EbTransactionsPage> GetTransactionsAsync(string accountUid, DateOnly dateFrom, string strategy, string? continuationKey, PsuHeaders? psu, CancellationToken ct)
    {
        // Les paramètres doivent rester identiques d'une page à l'autre ; seule la clé de continuation change.
        var url = "accounts/" + Uri.EscapeDataString(accountUid) + "/transactions?date_from=" + dateFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            + "&strategy=" + Uri.EscapeDataString(strategy);
        if (!string.IsNullOrEmpty(continuationKey)) url += "&continuation_key=" + Uri.EscapeDataString(continuationKey);
        return SendAsync<EbTransactionsPage>(HttpMethod.Get, url, null, psu, ct);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, PsuHeaders? psu, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token(time.GetUtcNow()));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (psu is not null)
            foreach (var (name, value) in psu.Values) req.Headers.TryAddWithoutValidation(name, value);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);

        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.IsSuccessStatusCode)
        {
            try
            {
                return await res.Content.ReadFromJsonAsync<T>(Json, ct)
                    ?? throw new EnableBankingException(res.StatusCode, null, "Réponse vide d'Enable Banking.");
            }
            catch (JsonException) { throw new EnableBankingException(res.StatusCode, null, "Réponse illisible d'Enable Banking."); }
        }

        EbError? error = null;
        try { error = await res.Content.ReadFromJsonAsync<EbError>(Json, ct); }
        catch (Exception e) when (e is JsonException or NotSupportedException) { /* corps non JSON */ }
        // Le message de l'agrégateur n'est pas repris tel quel : il peut contenir des détails de compte.
        throw new EnableBankingException(res.StatusCode, error?.Error, $"Enable Banking a répondu {(int)res.StatusCode}{(error?.Error is null ? "" : " (" + error.Error + ")")}.");
    }
}
