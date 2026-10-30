using BudgetTenable.Core.Budget;
using BudgetTenable.Core.Domain;

namespace BudgetTenable.Api.Endpoints;

public sealed record CategoryDto(string Id, string Label, CategoryGroup Group, decimal Percentage, decimal Amount, bool Annual, string? Note, decimal CurrentAverage, int Order)
{
    public static CategoryDto From(Category c) => new(c.Id, c.Label, c.Group, c.Percentage, c.Amount, c.Annual, c.Note, c.CurrentAverage, c.Order);
}

public sealed record BudgetDto(BudgetSettings Settings, List<CategoryDto> Categories, DateOnly Today, string CurrentPeriod);
public sealed record CategoryUpdate(string Id, string? Label, decimal? Percentage, decimal? Amount);
public sealed record BudgetUpdate(BudgetSettings Settings, List<CategoryUpdate>? Categories);

public sealed record AccountDto(int Id, string Label, AccountType Type, string Currency);

public sealed record TransactionDto(
    int Id, int AccountId, string AccountLabel, DateOnly Date, decimal Amount, string? Counterparty, string? Remittance,
    string? Note, string? CategoryId, string Source, bool IsInternalTransfer, bool IsManual, string Status, string SuggestedPattern)
{
    public static TransactionDto From(Transaction t) => new(
        t.Id, t.AccountId, t.Account?.Label ?? "", t.BookingDate, t.Amount, t.Counterparty, t.RemittanceInfo, t.Note, t.CategoryId,
        t.CategorySource.ToString().ToLowerInvariant(), t.IsInternalTransfer, t.IsManual, t.Status.ToString().ToLowerInvariant(),
        BudgetTenable.Core.Rules.RuleMatcher.SuggestPattern(t));
}

/// <summary>Montant signé comme en banque : négatif = dépense, positif = remboursement.</summary>
public sealed record TransactionInput(DateOnly? Date, decimal? Amount, string? CategoryId, string? Note, int? AccountId, bool? IsInternalTransfer);
public sealed record TransactionList(int Total, List<TransactionDto> Items);

public sealed record RuleDto(
    int Id, string Pattern, bool IsRegex, RuleField Field, string? CategoryId, bool MarksInternalTransfer,
    int Priority, RuleDirection Direction, decimal? MinAmount, decimal? MaxAmount, bool Enabled)
{
    public static RuleDto From(Rule r) => new(r.Id, r.Pattern, r.IsRegex, r.Field, r.CategoryId, r.MarksInternalTransfer, r.Priority, r.Direction, r.MinAmount, r.MaxAmount, r.Enabled);
}

public sealed record RuleInput(
    string? Pattern, bool? IsRegex, RuleField? Field, string? CategoryId, bool? MarksInternalTransfer,
    int? Priority, RuleDirection? Direction, decimal? MinAmount, decimal? MaxAmount, bool? Enabled);

public sealed record RuleCreated(RuleDto Rule, int Applied);
public sealed record ApplyRulesRequest(bool? All);
public sealed record ImportResult(string Format, int Parsed, int Added, int Duplicates, int Categorized, int ToSort);
public sealed record LoginRequest(string? Pin);
