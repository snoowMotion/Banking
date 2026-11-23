using BudgetTenable.Api.Data;
using BudgetTenable.Api.Services;
using BudgetTenable.Core.Budget;
using BudgetTenable.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace BudgetTenable.Api.Endpoints;

/// <summary>
/// Budgets exceptionnels d'une période (Noël, un voyage, une réparation…). Une ligne est une catégorie du groupe
/// <see cref="CategoryGroup.Exceptional"/> qui n'existe que pour une période : <c>ActiveFrom</c> = la période, et un changement daté
/// l'arrête dès la suivante (le même mécanisme que « Arrêter », posé d'office). Le montant prévu s'ajoute à côté de l'enveloppe et
/// réduit l'objectif d'épargne du mois ; les opérations qu'on y classe (collage, import, synchro, règle, saisie) réduisent le reste du mois.
/// La suppression passe par <c>DELETE /api/categories/{id}</c>, qui refuse tant qu'une opération y est classée.
/// </summary>
public static class OneOffEndpoints
{
    private const decimal MaxAmount = 10_000_000m;

    public static void MapOneOffs(this IEndpointRouteBuilder api)
    {
        api.MapPost("/periods/{key}/oneoffs", async (string key, OneOffInput? input, BudgetDbContext db, CancellationToken ct) =>
        {
            if (!PeriodKey.TryParse(key, out var k)) return BudgetEndpoints.Bad("Période invalide (AAAA-MM attendu).");
            var label = input?.Label?.Trim();
            if (string.IsNullOrEmpty(label)) return BudgetEndpoints.Bad("Donne un nom à ce budget exceptionnel.");
            if (label.Length > 120) return BudgetEndpoints.Bad("Le nom fait 120 caractères au plus.");
            if (input?.Amount is not { } raw || raw <= 0 || raw > MaxAmount) return BudgetEndpoints.Bad("Saisis un montant prévu positif.");
            var note = input.Note?.Trim();
            if (note is { Length: > 200 }) return BudgetEndpoints.Bad("La note fait 200 caractères au plus.");

            var all = await db.Categories.ToListAsync(ct);
            if (CategoryCreator.FindByLabel(all, label) is not null)
                return Results.Conflict(new { error = "Une catégorie porte déjà ce nom. Précise l'année pour distinguer (ex. « Noël 2026 »)." });

            var amount = Math.Round(raw, 2);
            var spec = new NewCategorySpec(label, CategoryGroup.Exceptional, Amount: amount, Note: note, From: k.ToString());
            var c = CategoryCreator.Create(db, all, spec);
            // Le budget ne vaut que pour cette période : il est arrêté à la suivante.
            db.CategoryTerms.Add(new CategoryTerm { CategoryId = c.Id, From = k.AddMonths(1).ToString(), Amount = amount, Active = false });
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/categories/{c.Id}", new { id = c.Id });
        });

        api.MapPut("/periods/{key}/oneoffs/{id}", async (string key, string id, OneOffEdit? input, BudgetDbContext db, CancellationToken ct) =>
        {
            if (!PeriodKey.TryParse(key, out var k)) return BudgetEndpoints.Bad("Période invalide (AAAA-MM attendu).");
            var cat = await db.Categories.FirstOrDefaultAsync(c => c.Id == id && c.Group == CategoryGroup.Exceptional && c.ActiveFrom == k.ToString(), ct);
            if (cat is null) return Results.NotFound();

            if (input?.Label is { } raw)
            {
                var label = raw.Trim();
                if (label.Length is 0 or > 120) return BudgetEndpoints.Bad("Le nom fait entre 1 et 120 caractères.");
                var others = await db.Categories.Where(c => c.Id != id).ToListAsync(ct);
                if (CategoryCreator.FindByLabel(others, label) is not null)
                    return Results.Conflict(new { error = "Une catégorie porte déjà ce nom. Précise l'année pour distinguer (ex. « Noël 2026 »)." });
                cat.Label = label;
            }
            if (input?.Amount is { } a)
            {
                if (a <= 0 || a > MaxAmount) return BudgetEndpoints.Bad("Saisis un montant prévu positif.");
                cat.Amount = Math.Round(a, 2);
                var stop = await db.CategoryTerms.FirstOrDefaultAsync(t => t.CategoryId == id && t.From == k.AddMonths(1).ToString(), ct);
                if (stop is not null) stop.Amount = cat.Amount;
            }
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }
}
