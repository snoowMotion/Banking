using BudgetTenable.Core.Domain;

namespace BudgetTenable.Core.Budget;

/// <summary>Jeu de données de départ : valeurs du prototype (§8 de la spec).</summary>
public static class BudgetDefaults
{
    public static BudgetSettings Settings() => new();

    public static List<Category> Categories()
    {
        var order = 0;
        Category Fixed(string id, string label, decimal amount, string? note = null) =>
            new() { Id = id, Label = label, Group = CategoryGroup.Fixe, Amount = amount, Note = note, Order = order++ };
        Category Alloc(string id, CategoryGroup g, string label, decimal pct, decimal today, string? note = null) =>
            new() { Id = id, Label = label, Group = g, Percentage = pct, CurrentAverage = today, Note = note, Order = order++ };
        Category Annual(string id, string label, decimal yearly, string note) =>
            new() { Id = id, Label = label, Group = CategoryGroup.Pot, Amount = yearly, Annual = true, Note = note, Order = order++ };
        Category Ignored(string id, string label) =>
            new() { Id = id, Label = label, Group = CategoryGroup.Ignore, Order = order++ };

        return new List<Category>
        {
            Alloc("courses", CategoryGroup.Variable, "Courses et maison", 47, 810, "Alimentation, hygiène, animaux, dépannage"),
            Alloc("resto", CategoryGroup.Variable, "Restauration et sorties", 7, 160),
            Alloc("carbu", CategoryGroup.Variable, "Carburant et péages", 5, 70),
            Alloc("shop", CategoryGroup.Variable, "Shopping", 10, 272, "Amazon, vêtements, coiffure, divers"),
            Alloc("jeux", CategoryGroup.Variable, "Jeux et loisirs", 4, 102),
            Alloc("divers", CategoryGroup.Variable, "Divers", 3, 58, "Chèque mensuel du commun, à identifier"),
            Alloc("voiture", CategoryGroup.Pot, "Pot voiture", 6, 125, "Entretien, réparations"),
            Alloc("brico", CategoryGroup.Pot, "Pot bricolage et déco", 3, 116),
            Alloc("vac", CategoryGroup.Pot, "Pot vacances et sorties famille", 3, 40),
            Annual("tf", "Taxe foncière", 1432, "Payée une fois par an (septembre)"),
            Annual("auto", "Assurance auto", 396, "Payée une fois par an (octobre)"),
            Fixed("immo", "Crédit immo SOFIAP", 959),
            Fixed("arkea", "Crédit Arkea", 70),
            Fixed("tel", "Télécom (Free box + mobile)", 92),
            Fixed("eau", "Eau + EDF", 62),
            Fixed("assur", "Assurances", 75, "Habitation, juridique, Homeserve, prévoyance"),
            Fixed("abo", "Karapass, Roole-Identicar", 23),
            Fixed("frais", "Frais bancaires", 22),
            Fixed("anth", "Anthropic", 23),
            Fixed("ring", "Ring", 10),
            Fixed("pp", "PayPal récurrents", 60),
            Ignored("rev-partenaire", "Revenu : part de la partenaire"),
            Ignored("rev-caf", "Revenu : CAF"),
            Ignored("rev-salaire", "Revenu : salaire"),
            Ignored("rembt-sante", "Remboursement santé"),
            Ignored("autre-ignore", "À ignorer"),
        };
    }
}
