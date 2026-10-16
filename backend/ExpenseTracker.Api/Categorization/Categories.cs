namespace ExpenseTracker.Api.Categorization;

/// <summary>The fixed category list, shared by the prompt, the JSON schema, validation and the API.</summary>
public static class Categories
{
    public const string Groceries = "Groceries";
    public const string Restaurants = "Restaurants & Cafes";
    public const string Transport = "Transport";
    public const string Housing = "Housing & Utilities";
    public const string Subscriptions = "Subscriptions";
    public const string Health = "Health";
    public const string Shopping = "Shopping";
    public const string Entertainment = "Entertainment";
    public const string Transfers = "Transfers";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All =
    [
        Groceries, Restaurants, Transport, Housing, Subscriptions,
        Health, Shopping, Entertainment, Transfers, Other,
    ];

    public const string ConfidenceHigh = "high";
    public const string ConfidenceLow = "low";
    public static readonly IReadOnlyList<string> Confidence = [ConfidenceHigh, ConfidenceLow];

    public const string SourceAi = "ai";
    public const string SourceManual = "manual";

    public static bool IsValid(string? category) => category is not null && All.Contains(category);
}
