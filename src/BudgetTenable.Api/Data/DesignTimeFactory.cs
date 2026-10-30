using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BudgetTenable.Api.Data;

/// <summary>Utilisée uniquement par `dotnet ef` pour générer les migrations.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<BudgetDbContext>
{
    public BudgetDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
