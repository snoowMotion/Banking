using BudgetTenable.Api.Data;
using BudgetTenable.Core.Domain;
using BudgetTenable.Core.Rules;
using Microsoft.EntityFrameworkCore;

namespace BudgetTenable.Api.Endpoints;

public static class RuleEndpoints
{
    public static void MapRules(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/rules");

        g.MapGet("", async (BudgetDbContext db, CancellationToken ct) =>
            (await db.Rules.AsNoTracking().OrderByDescending(r => r.Priority).ThenBy(r => r.Id).ToListAsync(ct)).Select(RuleDto.From).ToList());

        g.MapPost("", async (RuleInput input, bool? apply, BudgetDbContext db, CancellationToken ct) =>
        {
            var rule = new Rule();
            if (await Fill(rule, input, db, ct) is { } error) return error;
            db.Rules.Add(rule);
            await db.SaveChangesAsync(ct);
            // Une nouvelle règle classe tout de suite ce qui attend dans la boîte « À trier ».
            var applied = apply == false ? 0 : await ApplyRules(db, all: false, ct);
            return Results.Created($"/api/rules/{rule.Id}", new RuleCreated(RuleDto.From(rule), applied));
        });

        g.MapPut("/{id:int}", async (int id, RuleInput input, BudgetDbContext db, CancellationToken ct) =>
        {
            var rule = await db.Rules.FindAsync(new object[] { id }, ct);
            if (rule is null) return Results.NotFound();
            if (await Fill(rule, input, db, ct) is { } error) return error;
            await db.SaveChangesAsync(ct);
            return Results.Ok(RuleDto.From(rule));
        });

        g.MapDelete("/{id:int}", async (int id, BudgetDbContext db, CancellationToken ct) =>
        {
            var rule = await db.Rules.FindAsync(new object[] { id }, ct);
            if (rule is null) return Results.NotFound();
            db.Rules.Remove(rule);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapPost("/apply", async (ApplyRulesRequest? req, BudgetDbContext db, CancellationToken ct) =>
            Results.Ok(new { applied = await ApplyRules(db, req?.All == true, ct) }));
    }

    /// <summary>
    /// Applique les règles. Par défaut, seulement aux opérations non classées ; avec <paramref name="all"/>,
    /// aussi à celles déjà classées par une règle (jamais aux classements manuels).
    /// </summary>
    public static async Task<int> ApplyRules(BudgetDbContext db, bool all, CancellationToken ct)
    {
        var rules = RuleMatcher.Ordered(await db.Rules.AsNoTracking().ToListAsync(ct));
        var targets = await db.Transactions
            .Where(t => t.CategorySource == CategorySource.None || (all && t.CategorySource == CategorySource.Rule))
            .ToListAsync(ct);

        var applied = 0;
        foreach (var t in targets)
        {
            if (t.CategorySource == CategorySource.Rule)
            {
                t.CategoryId = null;
                t.IsInternalTransfer = false;
                t.CategorySource = CategorySource.None;
            }
            if (RuleMatcher.Apply(t, rules)) applied++;
        }
        await db.SaveChangesAsync(ct);
        return applied;
    }

    private static async Task<IResult?> Fill(Rule rule, RuleInput i, BudgetDbContext db, CancellationToken ct)
    {
        if (i.Pattern is not null) rule.Pattern = i.Pattern.Trim();
        if (i.IsRegex is { } rx) rule.IsRegex = rx;
        if (i.Field is { } f) rule.Field = f;
        if (i.Direction is { } d) rule.Direction = d;
        if (i.Priority is { } p) rule.Priority = Math.Clamp(p, -1000, 1000);
        if (i.Enabled is { } en) rule.Enabled = en;
        rule.MarksInternalTransfer = i.MarksInternalTransfer ?? rule.MarksInternalTransfer;
        rule.CategoryId = rule.MarksInternalTransfer ? null : (i.CategoryId ?? rule.CategoryId);
        rule.MinAmount = i.MinAmount is { } mn && mn >= 0 ? mn : null;
        rule.MaxAmount = i.MaxAmount is { } mx && mx >= 0 ? mx : null;

        if (RuleMatcher.ValidatePattern(rule.Pattern, rule.IsRegex) is { } err) return BudgetEndpoints.Bad(err);
        if (rule.Pattern.Length > 200) return BudgetEndpoints.Bad("Le motif est trop long (200 caractères).");
        if (!rule.MarksInternalTransfer)
        {
            if (rule.CategoryId is null) return BudgetEndpoints.Bad("Choisis une catégorie, ou marque la règle comme virement interne.");
            if (!await db.Categories.AnyAsync(c => c.Id == rule.CategoryId, ct)) return BudgetEndpoints.Bad("Catégorie inconnue.");
        }
        if (rule.MinAmount is { } a && rule.MaxAmount is { } b && a > b) return BudgetEndpoints.Bad("Le montant minimum dépasse le maximum.");
        return null;
    }
}
