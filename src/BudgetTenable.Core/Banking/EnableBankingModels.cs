namespace BudgetTenable.Core.Banking;

// Formes JSON de l'API Enable Banking (https://enablebanking.com/docs/api/reference/), en snake_case côté fil.
// Seuls les champs utilisés sont déclarés ; tout est facultatif, les banques ne renseignant pas toutes les mêmes champs.

public sealed record EbAmount(string? Currency, string? Amount);
public sealed record EbParty(string? Name);
public sealed record EbBankTransactionCode(string? Description, string? Code, string? SubCode);

public sealed record EbTransaction(
    string? EntryReference,
    EbAmount? TransactionAmount,
    EbParty? Creditor,
    EbParty? Debtor,
    EbBankTransactionCode? BankTransactionCode,
    string? CreditDebitIndicator,
    string? Status,
    string? BookingDate,
    string? ValueDate,
    string? TransactionDate,
    List<string>? RemittanceInformation,
    string? Note);

public sealed record EbTransactionsPage(List<EbTransaction>? Transactions, string? ContinuationKey);

public sealed record EbAspspRef(string Name, string Country);

public sealed record EbAspsp(
    string? Name,
    string? Country,
    List<string>? PsuTypes,
    long? MaximumConsentValidity,
    bool? Beta,
    List<string>? RequiredPsuHeaders);

public sealed record EbAspspList(List<EbAspsp>? Aspsps);

public sealed record EbAccess(string ValidUntil, bool? Balances = null, bool? Transactions = null);

public sealed record EbStartAuthRequest(EbAccess Access, EbAspspRef Aspsp, string State, string RedirectUrl, string? PsuType, string? Language);
public sealed record EbStartAuthResponse(string? Url, string? AuthorizationId);

public sealed record EbAuthorizeSessionRequest(string Code);

public sealed record EbAccountId(string? Iban);

public sealed record EbAccount(
    EbAccountId? AccountId,
    string? Name,
    string? Details,
    string? Product,
    string? CashAccountType,
    string? Currency,
    string? Uid,
    string? IdentificationHash);

public sealed record EbSessionAccess(string? ValidUntil);
public sealed record EbSession(string? SessionId, List<EbAccount>? Accounts, EbAspspRef? Aspsp, EbSessionAccess? Access);

public sealed record EbError(string? Message, int? Code, string? Error);
