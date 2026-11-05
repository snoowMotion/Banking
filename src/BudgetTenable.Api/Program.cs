using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using BudgetTenable.Api.Auth;
using BudgetTenable.Api.Data;
using BudgetTenable.Api.Endpoints;
using BudgetTenable.Api.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = WebRoot.Resolve() });
var cfg = builder.Configuration;

// Variables d'environnement documentées dans le README ; elles complètent la section "Auth".
builder.Services.Configure<AuthOptions>(o =>
{
    cfg.GetSection("Auth").Bind(o);
    o.Mode = cfg["BUDGET_AUTH_MODE"] ?? o.Mode;
    o.Pin = cfg["BUDGET_PIN"] ?? o.Pin;
    o.PinFile = cfg["BUDGET_PIN_FILE"] ?? o.PinFile;
    o.CookieKey = cfg["BUDGET_COOKIE_KEY"] ?? o.CookieKey;
    if (bool.TryParse(cfg["BUDGET_INSECURE_COOKIES"], out var insecure)) o.InsecureCookies = insecure;
});

var dataDir = Path.GetFullPath(cfg["BUDGET_DATA_DIR"] ?? cfg["Data:Directory"] ?? "data");
Directory.CreateDirectory(dataDir);
var paths = new DataPaths(dataDir);
builder.Services.AddSingleton(paths);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IClock, ParisClock>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddDbContext<BudgetDbContext>(o => o.UseSqlite($"Data Source={paths.DatabaseFile}"));
builder.Services.AddScoped<BudgetStore>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString;
});

var permits = Math.Max(1, cfg.GetValue("Auth:LoginPermitPerMinute", 5));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// À n'activer que derrière un reverse proxy qui est la seule porte d'entrée : sinon X-Forwarded-For est falsifiable.
var trustProxy = bool.TryParse(cfg["BUDGET_TRUST_PROXY"], out var tp) && tp;
if (trustProxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
    await BudgetStore.InitializeAsync(scope.ServiceProvider.GetRequiredService<BudgetDbContext>(), scope.ServiceProvider.GetRequiredService<IClock>());

app.UseExceptionHandler(e => e.Run(async ctx =>
{
    // Une requête illisible (JSON invalide, valeur d'énumération inconnue…) est une erreur du client, pas du serveur.
    var error = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var clientError = error is Microsoft.AspNetCore.Http.BadHttpRequestException;
    ctx.Response.StatusCode = clientError ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new { error = clientError ? "Requête invalide." : "Erreur interne." });
}));
if (trustProxy) app.UseForwardedHeaders();
app.UseSecurityHeaders();
app.UseRateLimiter();
app.UseApiAuth();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Les fichiers du frontend sont revalidés à chaque chargement ; le service worker gère le mode hors ligne.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

var api = app.MapGroup("/api");
api.MapAuth();
api.MapBudget();
api.MapTransactions();
api.MapRules();
api.MapImportExport();
api.MapPaste();
api.MapFixed();
api.MapCategories();
api.Map("{**rest}", () => Results.NotFound(new { error = "Introuvable." }));

app.Run();

public partial class Program;

static class WebRoot
{
    /// <summary>wwwroot à côté de l'application (image Docker), sinon le dossier web/ du dépôt (développement, tests).</summary>
    public static string? Resolve()
    {
        var fromEnv = Environment.GetEnvironmentVariable("BUDGET_WEB_ROOT");
        if (!string.IsNullOrEmpty(fromEnv) && Directory.Exists(fromEnv)) return Path.GetFullPath(fromEnv);

        var packaged = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(packaged)) return packaged;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "web");
            if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
        }
        return null;
    }
}
