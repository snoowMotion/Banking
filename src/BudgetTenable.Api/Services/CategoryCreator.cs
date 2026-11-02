using System.Text.RegularExpressions;
using BudgetTenable.Api.Data;
using BudgetTenable.Core.Domain;
using BudgetTenable.Core.Rules;
using Microsoft.EntityFrameworkCore;

namespace BudgetTenable.Api.Services;

public static class CategoryCreator
{
    public static string KeyOf(string label) => TextNormalizer.Normalize(label);

    /// <summary>Catégorie existante de même libellé (sans tenir compte des accents ni de la casse).</summary>
    public static Category? FindByLabel(IEnumerable<Category> categories, string label)
    {
        var key = KeyOf(label);
        return categories.FirstOrDefault(c => KeyOf(c.Label) == key);
    }

    /// <summary>
    /// Crée une catégorie sans budget (0 %) : elle n'entame ni l'enveloppe ni l'épargne tant qu'on n'a pas réglé sa part dans l'onglet Budget.
    /// À appeler avec les catégories déjà connues (existantes + créées dans le même lot).
    /// </summary>
    public static Category Create(BudgetDbContext db, ICollection<Category> known, string label, CategoryGroup group)
    {
        var id = Slug(label);
        var unique = id;
        for (var n = 2; known.Any(c => c.Id == unique); n++) unique = $"{id}-{n}";
        var c = new Category
        {
            Id = unique,
            Label = label.Trim(),
            Group = group,
            Order = known.Count == 0 ? 0 : known.Max(x => x.Order) + 1,
        };
        db.Categories.Add(c);
        known.Add(c);
        return c;
    }

    public static string? Validate(string? label) =>
        string.IsNullOrWhiteSpace(label) ? "Le nom de la catégorie est vide."
        : label.Trim().Length > 120 ? "Le nom d'une catégorie fait 120 caractères au plus."
        : null;

    private static string Slug(string label)
    {
        var s = Regex.Replace(TextNormalizer.Normalize(label).ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (s.Length > 30) s = s[..30].TrimEnd('-');
        return s.Length == 0 ? "categorie" : s;
    }
}
