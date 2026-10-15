namespace ExpenseTracker.Api.EnableBanking;

// Shapes of the Enable Banking API (snake_case on the wire, see EnableBankingClient.Json).

public sealed record AspspsResponse(List<Aspsp> Aspsps);

public sealed record Aspsp(
    string Name,
    string Country,
    string? Logo,
    List<string>? PsuTypes,
    bool? Beta,
    long? MaximumConsentValidity);

public sealed record AspspRef(string Name, string Country);

public sealed record AuthAccess(DateTimeOffset ValidUntil);

public sealed record StartAuthRequest(
    AuthAccess Access,
    AspspRef Aspsp,
    string State,
    string RedirectUrl,
    string PsuType);

public sealed record StartAuthResponse(string Url, string AuthorizationId);

public sealed record CreateSessionRequest(string Code);

public sealed record AccountIdentification(string? Iban);

public sealed record SessionAccount(
    string Uid,
    AccountIdentification? AccountId,
    string? Currency,
    string? Name);

public sealed record SessionAccess(DateTimeOffset ValidUntil);

public sealed record CreateSessionResponse(
    string SessionId,
    List<SessionAccount> Accounts,
    AspspRef Aspsp,
    SessionAccess Access);

public sealed record AmountType(string Currency, decimal Amount);

public sealed record PartyIdentification(string? Name);

public sealed record BankTransaction(
    string? EntryReference,
    string? TransactionId,
    AmountType TransactionAmount,
    PartyIdentification? Creditor,
    PartyIdentification? Debtor,
    string CreditDebitIndicator,
    string? Status,
    DateOnly? BookingDate,
    DateOnly? ValueDate,
    DateOnly? TransactionDate,
    List<string>? RemittanceInformation);

public sealed record TransactionsResponse(List<BankTransaction> Transactions, string? ContinuationKey);
