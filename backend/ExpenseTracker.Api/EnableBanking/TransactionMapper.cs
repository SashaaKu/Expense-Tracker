using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ExpenseTracker.Api.Data;

namespace ExpenseTracker.Api.EnableBanking;

public static class TransactionMapper
{
    public static Transaction ToEntity(BankTransaction t, string accountUid, DateTimeOffset importedAt)
    {
        var isCredit = t.CreditDebitIndicator == "CRDT";
        return new Transaction
        {
            AccountUid = accountUid,
            ExternalId = ExternalId(t),
            BookingDate = BookingDate(t, importedAt),
            ValueDate = t.ValueDate,
            Amount = t.TransactionAmount.Amount,
            Currency = t.TransactionAmount.Currency,
            CreditDebit = t.CreditDebitIndicator,
            Counterparty = (isCredit ? t.Debtor : t.Creditor)?.Name,
            Description = Remittance(t),
            Status = t.Status,
            ImportedAt = importedAt,
        };
    }

    /// <summary>
    /// Stable id for deduplication: entry_reference, else transaction_id,
    /// else a hash of the transaction contents (some banks provide neither).
    /// </summary>
    public static string ExternalId(BankTransaction t)
    {
        if (!string.IsNullOrWhiteSpace(t.EntryReference)) return "ref:" + t.EntryReference;
        if (!string.IsNullOrWhiteSpace(t.TransactionId)) return "id:" + t.TransactionId;

        var raw = string.Join('|',
            t.BookingDate?.ToString("yyyy-MM-dd") ?? t.ValueDate?.ToString("yyyy-MM-dd") ?? "",
            t.TransactionAmount.Amount.ToString(CultureInfo.InvariantCulture),
            t.TransactionAmount.Currency,
            t.CreditDebitIndicator,
            t.Creditor?.Name ?? "",
            t.Debtor?.Name ?? "",
            Remittance(t) ?? "");
        return "hash:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    public static DateOnly BookingDate(BankTransaction t, DateTimeOffset fallback) =>
        t.BookingDate ?? t.ValueDate ?? t.TransactionDate ?? DateOnly.FromDateTime(fallback.UtcDateTime);

    private static string? Remittance(BankTransaction t) =>
        t.RemittanceInformation is { Count: > 0 } info ? string.Join(' ', info) : null;
}
