using BudgetTenable.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace BudgetTenable.Api.Data;

/// <summary>Réglages du budget, stockés en un seul document JSON (une ligne).</summary>
public class AppSetting
{
    public int Id { get; set; }
    public string Json { get; set; } = "{}";
}

public class BudgetDbContext(DbContextOptions<BudgetDbContext> options) : DbContext(options)
{
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Rule> Rules => Set<Rule>();
    public DbSet<BankCategoryMapping> BankCategoryMappings => Set<BankCategoryMapping>();
    public DbSet<CategoryTerm> CategoryTerms => Set<CategoryTerm>();
    public DbSet<IncomeTerm> IncomeTerms => Set<IncomeTerm>();
    public DbSet<BankConnection> BankConnections => Set<BankConnection>();
    public DbSet<BankAccountLink> BankAccountLinks => Set<BankAccountLink>();
    public DbSet<BankAuthState> BankAuthStates => Set<BankAuthState>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppSetting>(e => e.Property(x => x.Id).ValueGeneratedNever());

        b.Entity<Category>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(40);
            e.Property(x => x.Label).HasMaxLength(120).IsRequired();
            e.Property(x => x.Group).HasConversion<string>().HasMaxLength(12);
            e.Ignore(x => x.IsAllocation);
            e.Property(x => x.ActiveFrom).HasMaxLength(7);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<CategoryTerm>(e =>
        {
            e.Property(x => x.CategoryId).HasMaxLength(40).IsRequired();
            e.Property(x => x.From).HasMaxLength(7).IsRequired();
            e.HasIndex(x => new { x.CategoryId, x.From }).IsUnique();
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<IncomeTerm>(e =>
        {
            e.Property(x => x.From).HasMaxLength(7).IsRequired();
            e.HasIndex(x => x.From).IsUnique();
        });

        b.Entity<Account>(e =>
        {
            e.Property(x => x.Label).HasMaxLength(80).IsRequired();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasIndex(x => x.ProviderAccountId);
        });

        b.Entity<Transaction>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.CategorySource).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.ProviderTransactionId).HasMaxLength(200);
            e.Property(x => x.Counterparty).HasMaxLength(300);
            e.Property(x => x.RemittanceInfo).HasMaxLength(600);
            e.Property(x => x.Note).HasMaxLength(120);
            e.Property(x => x.CategoryId).HasMaxLength(40);
            e.Ignore(x => x.IsManual);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            // Clé d'unicité anti-doublons : un identifiant fournisseur n'apparaît qu'une fois par compte.
            e.HasIndex(x => new { x.AccountId, x.ProviderTransactionId }).IsUnique();
            e.HasIndex(x => x.BookingDate);
            e.HasIndex(x => new { x.CategoryId, x.IsInternalTransfer });
        });

        b.Entity<BankCategoryMapping>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(120);
            e.Property(x => x.Label).HasMaxLength(120).IsRequired();
            e.Property(x => x.CategoryId).HasMaxLength(40);
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<BankConnection>(e =>
        {
            e.Property(x => x.SessionId).HasMaxLength(80).IsRequired();
            e.Property(x => x.AspspName).HasMaxLength(120).IsRequired();
            e.Property(x => x.AspspCountry).HasMaxLength(2).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.RequiredPsuHeaders).HasMaxLength(400);
            e.Property(x => x.LastError).HasMaxLength(300);
            e.HasMany(x => x.Accounts).WithOne().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<BankAccountLink>(e =>
        {
            e.Property(x => x.ProviderUid).HasMaxLength(80).IsRequired();
            e.Property(x => x.IdentificationHash).HasMaxLength(300).IsRequired();
            e.Property(x => x.Label).HasMaxLength(120).IsRequired();
            e.Property(x => x.IbanMasked).HasMaxLength(40);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<BankAuthState>(e =>
        {
            e.HasKey(x => x.State);
            e.Property(x => x.State).HasMaxLength(64);
            e.Property(x => x.AspspName).HasMaxLength(120).IsRequired();
            e.Property(x => x.AspspCountry).HasMaxLength(2).IsRequired();
            e.Property(x => x.RequiredPsuHeaders).HasMaxLength(400);
        });

        b.Entity<Rule>(e =>
        {
            e.Property(x => x.Pattern).HasMaxLength(200).IsRequired();
            e.Property(x => x.Field).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Direction).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.CategoryId).HasMaxLength(40);
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
