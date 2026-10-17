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

        // Expenses (DBIT, amounts stored positive) minus refunds (CRDT marked IsRefund), each by its own booking date.
        // Salary and other income are not refunds and don't count. Amounts are stored as text, so sum in memory.
        var inRange = db.Transactions
            .Where(t => (t.CreditDebit == CreditDebit.Debit || t.IsRefund) && t.BookingDate >= first && t.BookingDate <= last);
        var rows = await inRange
            .Where(t => t.Currency == Currency)
            .Select(t => new { t.Category, t.Amount, Refund = t.CreditDebit == CreditDebit.Credit })
            .ToListAsync(ct);
        // Other currencies aren't converted; report how many were left out so totals aren't silently off.
        var skippedNonPln = await inRange.CountAsync(t => t.Currency != Currency, ct);
        // Incoming money not yet checked for refunds: totals may still drop once it is.
        var unclassifiedCredits = await db.Transactions.CountAsync(t =>
            t.CreditDebit == CreditDebit.Credit && t.CategorySource == null &&
            t.BookingDate >= first && t.BookingDate <= last, ct);

        var expenses = rows.Where(r => !r.Refund).Sum(r => r.Amount);
        var refunds = rows.Where(r => r.Refund).Sum(r => r.Amount);
        var total = expenses - refunds;
        var categories = rows
            .GroupBy(r => r.Category ?? Uncategorized)
            .Select(g =>
            {
                var spent = g.Where(r => !r.Refund).Sum(r => r.Amount);
                var refunded = g.Where(r => r.Refund).Sum(r => r.Amount);
                return new
                {
                    category = g.Key,
                    total = spent - refunded,
                    expenses = spent,
                    refunds = refunded,
                    percent = total <= 0 ? 0 : Math.Round((spent - refunded) / total * 100, 1),
                    count = g.Count(r => !r.Refund),
                    refundCount = g.Count(r => r.Refund),
                };
            })
            .OrderByDescending(c => c.total)
            .ToList();

        return Results.Ok(new
        {
            month, from = first, to = last, currency = Currency, total, expenses, refunds,
            count = rows.Count(r => !r.Refund), refundCount = rows.Count(r => r.Refund),
            skippedNonPln, unclassifiedCredits, categories,
        });
    }

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
