namespace ExpenseTracker.Api.Data;

public sealed class BankSession
{
    public int Id { get; set; }
    public required string SessionId { get; set; }
    public required string AspspName { get; set; }
    public required string Country { get; set; }
    public DateTimeOffset ValidUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<BankAccount> Accounts { get; set; } = [];
}

public sealed class BankAccount
{
    public int Id { get; set; }
    public required string Uid { get; set; }
    public int BankSessionId { get; set; }
    public BankSession BankSession { get; set; } = null!;
    public string? Iban { get; set; }
    public string? Currency { get; set; }
    public string? Name { get; set; }
}

public sealed class Transaction
{
    public int Id { get; set; }
    public required string AccountUid { get; set; }
    public required string ExternalId { get; set; }
    public DateOnly BookingDate { get; set; }
    public DateOnly? ValueDate { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public required string CreditDebit { get; set; }
    public string? Counterparty { get; set; }
    public string? Description { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}
