using System.Globalization;
using ExpenseTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ExpenseTracker.Api.Endpoints;

public static class SummaryEndpoints
{
    private const string Currency = "PLN";
    private const string Uncategorized = "Uncategorized";

    public static void MapSummaryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/summary", GetSummary);
    }

    private static async Task<IResult> GetSummary(
        string? month, string? from, string? to, AppDbContext db, CancellationToken ct)
    {
        // Either ?month=yyyy-MM or an inclusive ?from=yyyy-MM-dd&to=yyyy-MM-dd range.
        DateOnly first, last;
        if (!string.IsNullOrEmpty(month))
        {
            if (!TryParseDate(month + "-01", out first))
                return Results.BadRequest(new { error = "month must be in yyyy-MM format, e.g. 2026-10" });
            last = first.AddMonths(1).AddDays(-1);
        }
        else
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
                return Results.BadRequest(new
                {
                    error = "Provide either month (yyyy-MM) or both from and to (yyyy-MM-dd, inclusive).",
                });
            if (!TryParseDate(from, out first) || !TryParseDate(to, out last))
                return Results.BadRequest(new { error = "from and to must be dates in yyyy-MM-dd format, e.g. 2026-10-01" });
            if (first > last)
                return Results.BadRequest(new { error = $"from ({from}) must be on or before to ({to})." });
        }

        // Expenses only: amounts are stored positive, DBIT = money out.
        // Amounts are stored as text in SQLite, so sum in memory.
        var rangeExpenses = db.Transactions
            .Where(t => t.CreditDebit == CreditDebit.Debit && t.BookingDate >= first && t.BookingDate <= last);
        var expenses = await rangeExpenses
            .Where(t => t.Currency == Currency)
            .Select(t => new { t.Category, t.Amount })
            .ToListAsync(ct);
        // Other currencies aren't converted; report how many were left out so totals aren't silently low.
        var skippedNonPln = await rangeExpenses.CountAsync(t => t.Currency != Currency, ct);

        var total = expenses.Sum(e => e.Amount);
        var categories = expenses
            .GroupBy(e => e.Category ?? Uncategorized)
            .Select(g => new
            {
                category = g.Key,
                total = g.Sum(e => e.Amount),
                percent = total == 0 ? 0 : Math.Round(g.Sum(e => e.Amount) / total * 100, 1),
                count = g.Count(),
            })
            .OrderByDescending(c => c.total)
            .ToList();

        return Results.Ok(new
        {
            month, from = first, to = last, currency = Currency, total, count = expenses.Count, skippedNonPln, categories,
        });
    }

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
