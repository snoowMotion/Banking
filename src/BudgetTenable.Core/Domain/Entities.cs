namespace BudgetTenable.Core.Domain;

public enum CategoryGroup { Variable, Pot, Fixe, Ignore }
public enum CategorySource { None, Rule, Manual }
public enum TransactionStatus { Booked, Pending }
public enum AccountType { Perso, Commun }
public enum RuleField { Both, Counterparty, Remittance }
public enum RuleDirection { Any, Debit, Credit }
public enum BankConnectionStatus { Active, Expired, Revoked }

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
    /// <summary>Compte sur lequel une charge fixe est prélevée ; nul = premier compte. Sert au marquage « payée » par clic.</summary>
    public int? AccountId { get; set; }
    /// <summary>Première période (yyyy-MM) où la catégorie existe ; nul = depuis toujours. Les valeurs ci-dessus valent dès cette période.</summary>
    public string? ActiveFrom { get; set; }

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

/// <summary>
/// Correspondance retenue entre une catégorie affichée par la banque et une catégorie de l'appli.
/// Une catégorie nulle signifie « ne pas utiliser cette catégorie bancaire ».
/// </summary>
public class BankCategoryMapping
{
    /// <summary>Libellé bancaire normalisé (sans accents, en majuscules).</summary>
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string? CategoryId { get; set; }
}

/// <summary>
/// Changement des paramètres d'une catégorie à partir d'une période : il vaut pour cette période et les suivantes,
/// jusqu'au changement daté suivant. Les périodes d'avant gardent leurs valeurs.
/// </summary>
public class CategoryTerm
{
    public int Id { get; set; }
    public string CategoryId { get; set; } = "";
    /// <summary>Période de départ (yyyy-MM).</summary>
    public string From { get; set; } = "";
    public decimal Percentage { get; set; }
    public decimal Amount { get; set; }
    public int? AccountId { get; set; }
    /// <summary>Faux = catégorie arrêtée (archivée) à partir de cette période.</summary>
    public bool Active { get; set; } = true;
}

/// <summary>Changement des revenus à partir d'une période (même principe que <see cref="CategoryTerm"/>).</summary>
public class IncomeTerm
{
    public int Id { get; set; }
    public string From { get; set; } = "";
    public decimal Net { get; set; }
    public decimal Partner { get; set; }
    public decimal Caf { get; set; }
    public decimal AnnualBonus { get; set; }
}

/// <summary>
/// Connexion à une banque par l'agrégateur (une « session » Enable Banking) : un consentement donné par
/// l'utilisateur sur le site de sa banque, valable jusqu'à <see cref="ValidUntil"/>.
/// </summary>
public class BankConnection
{
    public int Id { get; set; }
    /// <summary>Identifiant de session chez l'agrégateur. Ce n'est pas un secret d'accès : il ne sert qu'avec la clé privée de l'application.</summary>
    public string SessionId { get; set; } = "";
    public string AspspName { get; set; } = "";
    public string AspspCountry { get; set; } = "";
    /// <summary>Fin du consentement (UTC).</summary>
    public DateTime ValidUntil { get; set; }
    public BankConnectionStatus Status { get; set; } = BankConnectionStatus.Active;
    /// <summary>En-têtes « PSU » exigés par la banque pour une synchro lancée par l'utilisateur, séparés par des virgules.</summary>
    public string? RequiredPsuHeaders { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Dernière tentative de synchro (UTC), réussie ou non : sert à espacer les synchros automatiques.</summary>
    public DateTime? LastSyncAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    /// <summary>Message d'erreur de la dernière synchro, sans montant ni numéro de compte.</summary>
    public string? LastError { get; set; }
    public List<BankAccountLink> Accounts { get; set; } = new();
}

/// <summary>Un compte renvoyé par la banque pour une connexion, relié (ou non) à un compte de l'appli.</summary>
public class BankAccountLink
{
    public int Id { get; set; }
    public int ConnectionId { get; set; }
    /// <summary>Identifiant du compte chez l'agrégateur ; il ne vaut que pour cette session.</summary>
    public string ProviderUid { get; set; } = "";
    /// <summary>Empreinte stable du compte, identique d'une session à l'autre : sert à retrouver le compte après une reconnexion.</summary>
    public string IdentificationHash { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>IBAN tronqué uniquement (jamais en clair).</summary>
    public string? IbanMasked { get; set; }
    public string Currency { get; set; } = "EUR";
    /// <summary>Compte de l'appli alimenté par ce compte bancaire ; nul = ne pas synchroniser.</summary>
    public int? AccountId { get; set; }
    /// <summary>Date (Europe/Paris) de la dernière synchro réussie : la suivante repart d'un peu avant.</summary>
    public DateOnly? SyncedThrough { get; set; }
}

/// <summary>Autorisation en cours : le jeton <see cref="State"/> part chez la banque et doit revenir tel quel, une seule fois.</summary>
public class BankAuthState
{
    public string State { get; set; } = "";
    public string AspspName { get; set; } = "";
    public string AspspCountry { get; set; } = "";
    public DateTime ValidUntil { get; set; }
    public string? RequiredPsuHeaders { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Connexion remplacée en cas de succès (bouton « Reconnecter »).</summary>
    public int? ReplacesConnectionId { get; set; }
}
