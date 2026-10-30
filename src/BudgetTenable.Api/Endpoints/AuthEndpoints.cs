using BudgetTenable.Api.Auth;

namespace BudgetTenable.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/auth");

        g.MapPost("/login", async (LoginRequest req, SessionService sessions, TimeProvider time, HttpContext ctx) =>
        {
            if (!sessions.Required) return Results.Ok(new { authenticated = true });
            if (!sessions.VerifyPin(req.Pin))
            {
                await Task.Delay(400); // ralentit l'essai de codes en plus de la limitation de débit
                return Results.Json(new { error = "PIN incorrect." }, statusCode: StatusCodes.Status401Unauthorized);
            }
            ctx.Response.Cookies.Append(SessionService.CookieName, sessions.Issue(time.GetUtcNow()), new CookieOptions
            {
                HttpOnly = true,
                Secure = sessions.SecureCookies,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                MaxAge = sessions.Lifetime,
                IsEssential = true,
            });
            return Results.Ok(new { authenticated = true });
        }).RequireRateLimiting("login");

        g.MapPost("/logout", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(SessionService.CookieName, new CookieOptions { Path = "/" });
            return Results.NoContent();
        });

        g.MapGet("/status", (SessionService sessions, TimeProvider time, HttpContext ctx) => Results.Ok(new
        {
            required = sessions.Required,
            authenticated = !sessions.Required || sessions.Validate(ctx.Request.Cookies[SessionService.CookieName], time.GetUtcNow()),
        }));
    }
}
