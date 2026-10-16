using System.ComponentModel.DataAnnotations;

namespace ExpenseTracker.Api.Categorization;

public sealed class CategorizationOptions
{
    public const string Section = "Categorization";

    [Required] public string ClaudePath { get; set; } = "claude";
    [Required] public string Model { get; set; } = "sonnet";
    [Range(10, 1800)] public int TimeoutSeconds { get; set; } = 180;
    [Range(1, 200)] public int BatchSize { get; set; } = 50;

    /// <summary>After this many failed batches a transaction is skipped until fixed manually.</summary>
    [Range(1, 20)] public int MaxAttempts { get; set; } = 3;
}
