namespace BudgetTenable.Api.Auth;

public static class Middleware
{
    private static readonly string[] PublicApiPaths = { "/api/auth/login", "/api/auth/logout", "/api/auth/status" };

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self'; style-src 'self'; style-src-attr 'unsafe-inline'; img-src 'self' data:; " +
                "connect-src 'self'; manifest-src 'self'; worker-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            if (ctx.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=31536000";
            if (ctx.Request.Path.StartsWithSegments("/api")) h["Cache-Control"] = "no-store";
            return Task.CompletedTask;
        });
        await next();
    });

    /// <summary>Une seule porte d'entrée : tout /api exige une session valide, sauf la connexion elle-même.</summary>
    public static IApplicationBuilder UseApiAuth(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/api") && !PublicApiPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)))
        {
            var sessions = ctx.RequestServices.GetRequiredService<SessionService>();
            if (sessions.Required)
            {
                var clock = ctx.RequestServices.GetRequiredService<TimeProvider>();
                if (!sessions.Validate(ctx.Request.Cookies[SessionService.CookieName], clock.GetUtcNow()))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Connexion requise." });
                    return;
                }
            }
        }
        await next();
    });
}
