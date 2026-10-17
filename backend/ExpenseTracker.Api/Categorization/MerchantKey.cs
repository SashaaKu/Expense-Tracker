using System.Text.RegularExpressions;

namespace ExpenseTracker.Api.Categorization;

/// <summary>
/// Normalizes a transaction's merchant text so repeat payments to the same merchant get the same key,
/// e.g. "POZNANZABKA Z4822 K.1PL CARD-PAYMENT" -> "POZNANZABKA Z K PL".
/// </summary>
public static partial class MerchantKey
{
    // Shorter keys carry too little to identify a merchant.
    private const int MinLetters = 3;

    // PKO appends the payment type as the last token: CARD-PAYMENT, MOBILE-PAYMENT-POS-NO-CARD-TX-CODE, TRANSFER, ...
    [GeneratedRegex(@"\s+(?:[A-Z0-9]+(?:-[A-Z0-9]+)+|TRANSFER)\s*$")]
    private static partial Regex TypeCode();

    // Refund type codes: CARD-PAYMENT-RETURN, MOBILE-PAYMENT-POS-RETURN.
    [GeneratedRegex(@"\s+[A-Z0-9]+(?:-[A-Z0-9]+)*-RETURN\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReturnTypeCode();

    /// <summary>True when the bank marked the transaction as a returned payment (a refund).</summary>
    public static bool IsPaymentReturn(string? description) =>
        description is not null && ReturnTypeCode().IsMatch(description);

    // Digits (store numbers, operation ids, dates) and punctuation vary between payments to the same merchant.
    [GeneratedRegex(@"[^\p{L}]+")]
    private static partial Regex NonLetters();

    /// <summary>
    /// Key from the counterparty, or from the description when there is none.
    /// Null when nothing merchant-like is left (e.g. BLIK payments that are only an id and a type code).
    /// </summary>
    public static string? For(string? counterparty, string? description)
    {
        var source = string.IsNullOrWhiteSpace(counterparty) ? description : counterparty;
        if (string.IsNullOrWhiteSpace(source)) return null;

        var text = TypeCode().Replace(source.ToUpperInvariant(), "");
        text = NonLetters().Replace(text, " ").Trim();
        return text.Count(char.IsLetter) >= MinLetters ? text : null;
    }
}
