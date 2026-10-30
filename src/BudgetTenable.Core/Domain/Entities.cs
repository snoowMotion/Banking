namespace BudgetTenable.Core.Domain;

public enum CategoryGroup { Variable, Pot, Fixe, Ignore }
public enum CategorySource { None, Rule, Manual }
public enum TransactionStatus { Booked, Pending }
public enum AccountType { Perso, Commun }
public enum RuleField { Both, Counterparty, Remittance }
public enum RuleDirection { Any, Debit, Credit }

public class Category
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public CategoryGroup Group { get; set; }
    /// <summary>Part de l'enveloppe, en % (postes variables et pots non annuels).</summary>
    public decimal Percentage { get; set; }
    /// <summary>Montant mensuel (charge fixe) ou annuel (provision obligatoire, <see cref="Annual"/>).</summary>
    public decimal Amount { get; set; }
    public bool Annual { get; set; }
    public string? Note { get; set; }
    /// <summary>Dépense moyenne actuelle, pour comparer à la cible dans l'onglet Budget.</summary>
    public decimal CurrentAverage { get; set; }
    public int Order { get; set; }

    public bool IsAllocation => (Group is CategoryGroup.Variable or CategoryGroup.Pot) && !Annual;
}

public class Account
{
    public int Id { get; set; }
    public string? ProviderAccountId { get; set; }
    public string Label { get; set; } = "";
    public AccountType Type { get; set; }
    public string Currency { get; set; } = "EUR";
    /// <summary>IBAN tronqué uniquement (jamais en clair).</summary>
    public string? IbanMasked { get; set; }
}

public class Transaction
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    /// <summary>Nul pour une saisie manuelle.</summary>
    public string? ProviderTransactionId { get; set; }
    public DateOnly BookingDate { get; set; }
    public DateOnly? ValueDate { get; set; }
    /// <summary>Négatif = débit.</summary>
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public TransactionStatus Status { get; set; } = TransactionStatus.Booked;
    public string? Counterparty { get; set; }
    public string? RemittanceInfo { get; set; }
    public string? CategoryId { get; set; }
    public CategorySource CategorySource { get; set; }
    public bool IsInternalTransfer { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool IsManual => ProviderTransactionId is null;
}

public class Rule
{
    public int Id { get; set; }
    public string Pattern { get; set; } = "";
    public bool IsRegex { get; set; }
    public RuleField Field { get; set; } = RuleField.Both;
    /// <summary>Catégorie cible ; nulle si la règle ne fait que marquer un virement interne.</summary>
    public string? CategoryId { get; set; }
    public bool MarksInternalTransfer { get; set; }
    public int Priority { get; set; }
    public RuleDirection Direction { get; set; } = RuleDirection.Any;
    /// <summary>Bornes sur la valeur absolue du montant.</summary>
    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }
    public bool Enabled { get; set; } = true;
}
