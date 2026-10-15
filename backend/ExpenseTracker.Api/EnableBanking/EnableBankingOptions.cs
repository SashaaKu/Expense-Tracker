using System.ComponentModel.DataAnnotations;

namespace ExpenseTracker.Api.EnableBanking;

public sealed class EnableBankingOptions
{
    public const string Section = "EnableBanking";

    [Required] public string ApplicationId { get; set; } = "";
    [Required] public string KeyPath { get; set; } = "";
    [Required, Url] public string BaseUrl { get; set; } = "https://api.enablebanking.com";
    [Required, Url] public string RedirectUrl { get; set; } = "";
}
