using BudgetTenable.Api.Data;
using BudgetTenable.Api.Services;
using BudgetTenable.Core.Budget;
using BudgetTenable.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace BudgetTenable.Api.Endpoints;

/// <summary>
/// Gestion des catégories : créer, renommer, arrêter (archiver) à partir d'une période, réactiver, fusionner, supprimer.
/// Arrêter garde l'historique ; fusionner déplace opérations, règles et correspondances ; supprimer n'est possible que si rien n'y est classé.
/// </summary>
public static class CategoryEndpoints
{
    public static void MapCategories(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/categories");

        g.MapPost("", async (CategoryCreate input, BudgetStore store, BudgetDbContext db, IClock clock, CancellationToken ct) =>
        {
            var settings = await store.GetSettingsAsync(ct);
            var from = input.From ?? Periods.KeyFor(clock.Today, settings.StartDay).ToString();
            var spec = new NewCategorySpec(input.Label ?? "", input.Group ?? CategoryGroup.Variable, input.Annual == true,
                input.Percentage ?? 0, input.Amount ?? 0, input.AccountId, input.Note, from);
            var accounts = (await db.Accounts.Select(a => a.Id).ToListAsync(ct)).ToHashSet();
            if (CategoryCreator.Validate(spec, accounts) is { } err) return BudgetEndpoints.Bad(err);

            var all = await db.Categories.ToListAsync(ct);
            if (CategoryCreator.FindByLabel(all, spec.Label) is not null) return Results.Conflict(new { error = "Une catégorie porte déjà ce nom." });
            var c = CategoryCreator.Create(db, all, spec);

            if (c.Group == CategoryGroup.Pot && input.Opening is { } opening && opening != 0)
            {
                settings.PotOpening[c.Id] = Math.Round(Math.Clamp(opening, -10_000_000m, 10_000_000m), 2);
                await store.SaveSettingsAsync(settings, ct);
            }
            else await db.SaveChangesAsync(ct);
            return Results.Created($"/api/categories/{c.Id}", CategoryDto.From(new CategoryState(c, true, null), true, 0));
        });

        g.MapPut("/{id}", async (string id, CategoryEdit input, BudgetDbContext db, CancellationToken ct) =>
        {
            var c = await db.Categories.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return Results.NotFound();
            if (input.Label is not null)
            {
                var label = input.Label.Trim();
                if (label.Length is 0 or > 120) return BudgetEndpoints.Bad("Le nom d'une catégorie fait entre 1 et 120 caractères.");
                var others = await db.Categories.Where(x => x.Id != id).ToListAsync(ct);
                if (CategoryCreator.FindByLabel(others, label) is not null) return Results.Conflict(new { error = "Une catégorie porte déjà ce nom." });
                c.Label = label;
            }
            if (input.Note is not null) c.Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim()[..Math.Min(input.Note.Trim().Length, 200)];
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Arrête la catégorie à partir d'une période (la période en cours par défaut) : elle disparaît du budget et des listes
        // à partir de là, mais les périodes d'avant la gardent.
        g.MapPost("/{id}/archive", async (string id, ArchiveInput? input, BudgetStore store, BudgetDbContext db, IClock clock, CancellationToken ct) =>
        {
            var timeline = await store.LoadTimelineAsync(ct);
            var settings = await store.GetSettingsAsync(ct);
            if (!TryPeriod(input?.From, settings, clock, out var from)) return BudgetEndpoints.Bad("Période invalide (AAAA-MM attendu).");
            var cat = timeline.AllCategories.FirstOrDefault(c => c.Id == id);
            if (cat is null) return Results.NotFound();
            var state = timeline.StateOf(cat, from);
            if (!state.Active) return Results.Conflict(new { error = "Cette catégorie est déjà arrêtée (ou n'existe pas encore) à cette période." });
            await SetActive(db, cat.Id, from, state.Category, active: false, ct);
            return Results.Ok(new { from = from.ToString() });
        });

        g.MapPost("/{id}/restore", async (string id, ArchiveInput? input, BudgetStore store, BudgetDbContext db, IClock clock, CancellationToken ct) =>
        {
            var timeline = await store.LoadTimelineAsync(ct);
            var settings = await store.GetSettingsAsync(ct);
            if (!TryPeriod(input?.From, settings, clock, out var from)) return BudgetEndpoints.Bad("Période invalide (AAAA-MM attendu).");
            var cat = timeline.AllCategories.FirstOrDefault(c => c.Id == id);
            if (cat is null) return Results.NotFound();
            var state = timeline.StateOf(cat, from);
            if (state.Active) return Results.Conflict(new { error = "Cette catégorie est déjà active à cette période." });
            if (cat.ActiveFrom is not null && PeriodKey.TryParse(cat.ActiveFrom, out var born) && from < born)
                return Results.Conflict(new { error = $"Cette catégorie n'existe qu'à partir de {cat.ActiveFrom}." });
            await SetActive(db, cat.Id, from, state.Category, active: true, ct);
            return Results.Ok(new { from = from.ToString() });
        });

        // Déplace opérations, règles et correspondances bancaires vers une autre catégorie, puis supprime celle-ci.
        g.MapPost("/{id}/merge", async (string id, MergeInput input, BudgetStore store, BudgetDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.IntoId) || input.IntoId == id) return BudgetEndpoints.Bad("Choisis une autre catégorie où fusionner.");
            var source = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);
            var target = await db.Categories.FirstOrDefaultAsync(c => c.Id == input.IntoId, ct);
            if (source is null || target is null) return Results.NotFound();

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var moved = await db.Transactions.Where(t => t.CategoryId == id).ExecuteUpdateAsync(u => u.SetProperty(t => t.CategoryId, input.IntoId), ct);
            var rules = await db.Rules.Where(r => r.CategoryId == id).ExecuteUpdateAsync(u => u.SetProperty(r => r.CategoryId, input.IntoId), ct);
            await db.BankCategoryMappings.Where(m => m.CategoryId == id).ExecuteUpdateAsync(u => u.SetProperty(m => m.CategoryId, input.IntoId), ct);

            // Le solde de départ d'un pot suit le pot dans lequel on le fusionne.
            var settings = await store.GetSettingsAsync(ct);
            if (settings.PotOpening.Remove(id, out var opening) && opening != 0 && target.Group == CategoryGroup.Pot)
                settings.PotOpening[target.Id] = settings.PotOpening.GetValueOrDefault(target.Id) + opening;
            db.Categories.Remove(source); // supprime aussi ses changements datés
            await store.SaveSettingsAsync(settings, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { moved, rules });
        });

        g.MapDelete("/{id}", async (string id, BudgetStore store, BudgetDbContext db, CancellationToken ct) =>
        {
            var cat = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (cat is null) return Results.NotFound();
            var used = await db.Transactions.CountAsync(t => t.CategoryId == id, ct);
            if (used > 0)
                return Results.Conflict(new { error = $"{used} opération{(used > 1 ? "s sont classées" : " est classée")} dans cette catégorie : arrête-la ou fusionne-la plutôt que de la supprimer.", used });
            var settings = await store.GetSettingsAsync(ct);
            settings.PotOpening.Remove(id);
            db.Categories.Remove(cat);
            await store.SaveSettingsAsync(settings, ct);
            return Results.NoContent();
        });
    }

    private static bool TryPeriod(string? text, BudgetSettings settings, IClock clock, out PeriodKey key)
    {
        if (text is null) { key = Periods.KeyFor(clock.Today, settings.StartDay); return true; }
        return PeriodKey.TryParse(text, out key);
    }

    private static async Task SetActive(BudgetDbContext db, string id, PeriodKey from, Category values, bool active, CancellationToken ct)
    {
        var key = from.ToString();
        var term = await db.CategoryTerms.FirstOrDefaultAsync(t => t.CategoryId == id && t.From == key, ct);
        if (term is null)
            db.CategoryTerms.Add(new CategoryTerm { CategoryId = id, From = key, Percentage = values.Percentage, Amount = values.Amount, AccountId = values.AccountId, Active = active });
        else term.Active = active;
        await db.SaveChangesAsync(ct);
    }
}
