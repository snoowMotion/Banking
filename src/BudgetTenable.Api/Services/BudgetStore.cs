using System.Text.Json;
using BudgetTenable.Api.Data;
using BudgetTenable.Core.Budget;
using BudgetTenable.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace BudgetTenable.Api.Services;

public sealed class BudgetStore(BudgetDbContext db)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BudgetSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct);
        return row is null ? BudgetDefaults.Settings() : JsonSerializer.Deserialize<BudgetSettings>(row.Json, Json) ?? BudgetDefaults.Settings();
    }

    public async Task SaveSettingsAsync(BudgetSettings s, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(s, Json);
        var row = await db.AppSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (row is null) db.AppSettings.Add(new AppSetting { Id = 1, Json = json });
        else row.Json = json;
        await db.SaveChangesAsync(ct);
    }

    public Task<List<Category>> GetCategoriesAsync(CancellationToken ct = default) =>
        db.Categories.AsNoTracking().OrderBy(c => c.Order).ToListAsync(ct);

    /// <summary>Crée le schéma et les données de départ au premier lancement.</summary>
    public static async Task InitializeAsync(BudgetDbContext db)
    {
        await db.Database.MigrateAsync();

        if (!await db.Categories.AnyAsync()) db.Categories.AddRange(BudgetDefaults.Categories());
        if (!await db.Accounts.AnyAsync())
        {
            db.Accounts.Add(new Account { Label = "Perso", Type = AccountType.Perso });
            db.Accounts.Add(new Account { Label = "Commun", Type = AccountType.Commun });
        }
        if (!await db.AppSettings.AnyAsync())
            db.AppSettings.Add(new AppSetting { Id = 1, Json = JsonSerializer.Serialize(BudgetDefaults.Settings(), Json) });
        await db.SaveChangesAsync();
    }
}
