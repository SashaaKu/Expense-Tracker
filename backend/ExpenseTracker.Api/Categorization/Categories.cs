namespace ExpenseTracker.Api.Categorization;

/// <summary>The fixed category list, shared by the prompt, the JSON schema, validation and the API.</summary>
public static class Categories
{
    public const string Groceries = "Groceries";
    public const string Restaurants = "Restaurants & Cafes";
    public const string Transport = "Transport";
    public const string Travel = "Travel";
    public const string Housing = "Housing & Utilities";
    public const string Subscriptions = "Subscriptions";
    public const string Health = "Health";
    public const string Sport = "Sport";
    public const string Education = "Education";
    public const string Shopping = "Shopping";
    public const string Clothes = "Clothes";
    public const string Entertainment = "Entertainment";
    public const string Transfers = "Transfers";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All =
    [
        Groceries, Restaurants, Transport, Travel, Housing, Subscriptions, Health,
        Sport, Education, Shopping, Clothes, Entertainment, Transfers, Other,
    ];

    /// <summary>
    /// Claude's answer for incoming money that is not a refund (salary, transfers from people).
    /// Not a category: such rows are stored with Category = null and IsRefund = false.
    /// </summary>
    public const string Income = "Income";

    public const string ConfidenceHigh = "high";
    public const string ConfidenceLow = "low";
    public static readonly IReadOnlyList<string> Confidence = [ConfidenceHigh, ConfidenceLow];

    public const string SourceAi = "ai";
    public const string SourceManual = "manual";
    /// <summary>
    /// Set without Claude: copied from a manual correction of the same merchant (see <see cref="MerchantKey"/>),
    /// or a bank-marked refund matched to an earlier purchase.
    /// </summary>
    public const string SourceRule = "rule";

    public static bool IsValid(string? category) => category is not null && All.Contains(category);
}
