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

    private static async Task<IResult> GetSummary(string? month, AppDbContext db, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                out var first))
            return Results.BadRequest(new { error = "month is required in yyyy-MM format, e.g. 2026-10" });
        var last = first.AddMonths(1).AddDays(-1);

        // Expenses only: amounts are stored positive, DBIT = money out.
        // Amounts are stored as text in SQLite, so sum in memory.
        var monthExpenses = db.Transactions
            .Where(t => t.CreditDebit == CreditDebit.Debit && t.BookingDate >= first && t.BookingDate <= last);
        var expenses = await monthExpenses
            .Where(t => t.Currency == Currency)
            .Select(t => new { t.Category, t.Amount })
            .ToListAsync(ct);
        // Other currencies aren't converted; report how many were left out so totals aren't silently low.
        var skippedNonPln = await monthExpenses.CountAsync(t => t.Currency != Currency, ct);

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

        return Results.Ok(new { month, currency = Currency, total, count = expenses.Count, skippedNonPln, categories });
    }
}
