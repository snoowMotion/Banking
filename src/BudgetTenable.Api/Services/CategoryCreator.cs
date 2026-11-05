using System.Text.RegularExpressions;
using BudgetTenable.Api.Data;
using BudgetTenable.Core.Budget;
using BudgetTenable.Core.Domain;
using BudgetTenable.Core.Rules;

namespace BudgetTenable.Api.Services;

/// <summary>Ce qu'il faut pour créer une catégorie ; <see cref="From"/> est la première période où elle existe.</summary>
public sealed record NewCategorySpec(
    string Label, CategoryGroup Group, bool Annual = false, decimal Percentage = 0, decimal Amount = 0, int? AccountId = null, string? Note = null, string? From = null);

public static class CategoryCreator
{
    public static string KeyOf(string label) => TextNormalizer.Normalize(label);

    /// <summary>Catégorie existante de même libellé (sans tenir compte des accents ni de la casse).</summary>
    public static Category? FindByLabel(IEnumerable<Category> categories, string label)
    {
        var key = KeyOf(label);
        return categories.FirstOrDefault(c => KeyOf(c.Label) == key);
    }

    /// <summary>Message d'erreur si la demande est incohérente, sinon nul.</summary>
    public static string? Validate(NewCategorySpec s, ISet<int> accountIds)
    {
        if (string.IsNullOrWhiteSpace(s.Label)) return "Le nom de la catégorie est vide.";
        if (s.Label.Trim().Length > 120) return "Le nom d'une catégorie fait 120 caractères au plus.";
        if (s.Note is { Length: > 200 }) return "La note fait 200 caractères au plus.";
        if (s.From is not null && !PeriodKey.TryParse(s.From, out _)) return "Période de départ invalide (AAAA-MM attendu).";
        if (s.Annual && s.Group != CategoryGroup.Pot) return "Seule une provision (pot) peut être annuelle.";
        if (s.AccountId is { } a && !accountIds.Contains(a)) return "Compte inconnu.";
        switch (s.Group)
        {
            case CategoryGroup.Variable or CategoryGroup.Pot when !s.Annual:
                if (s.Percentage is < 0 or > 100) return "La part de l'enveloppe est comprise entre 0 et 100 %.";
                break;
            case CategoryGroup.Pot:
                if (s.Amount is <= 0 or > 10_000_000m) return "Le montant annuel d'une provision doit être positif.";
                break;
            case CategoryGroup.Fixe:
                if (s.Amount is < 0 or > 10_000_000m) return "Le montant d'une charge fixe doit être positif.";
                break;
        }
        return null;
    }

    /// <summary>
    /// Crée une catégorie. Une variable ou un pot sans part (0 %) n'entame ni l'enveloppe ni l'épargne tant que sa part n'est pas réglée.
    /// <paramref name="known"/> : catégories déjà connues (existantes et créées dans le même lot), pour garantir un identifiant unique.
    /// </summary>
    public static Category Create(BudgetDbContext db, ICollection<Category> known, NewCategorySpec s)
    {
        var id = Slug(s.Label);
        var unique = id;
        for (var n = 2; known.Any(c => c.Id == unique); n++) unique = $"{id}-{n}";
        var c = new Category
        {
            Id = unique,
            Label = s.Label.Trim(),
            Group = s.Group,
            Annual = s.Annual,
            Percentage = s.Group is CategoryGroup.Variable or CategoryGroup.Pot && !s.Annual ? s.Percentage : 0,
            Amount = s.Group == CategoryGroup.Fixe || s.Annual ? Math.Round(s.Amount, 2) : 0,
            AccountId = s.Group == CategoryGroup.Fixe ? s.AccountId : null,
            Note = string.IsNullOrWhiteSpace(s.Note) ? null : s.Note.Trim(),
            ActiveFrom = s.From,
            Order = known.Count == 0 ? 0 : known.Max(x => x.Order) + 1,
        };
        db.Categories.Add(c);
        known.Add(c);
        return c;
    }

    private static string Slug(string label)
    {
        var s = Regex.Replace(TextNormalizer.Normalize(label).ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (s.Length > 30) s = s[..30].TrimEnd('-');
        return s.Length == 0 ? "categorie" : s;
    }
}
