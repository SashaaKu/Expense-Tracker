using ExpenseTracker.Api.Categorization;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.EnableBanking;
using Microsoft.EntityFrameworkCore;

namespace ExpenseTracker.Api.Endpoints;

public static class TransactionEndpoints
{
    private const int DefaultRangeDays = 90;

    public static void MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/transactions", GetTransactions);
        app.MapPatch("/api/transactions/{id:int}/category", SetCategory);
        app.MapGet("/api/categories", () => Categories.All);
    }

    public sealed record SetCategoryRequest(string? Category);

    private static async Task<IResult> SetCategory(int id, SetCategoryRequest body, AppDbContext db, CancellationToken ct)
    {
        if (!Categories.IsValid(body.Category))
            return Results.BadRequest(new { error = $"Invalid category \"{body.Category}\".", allowed = Categories.All });

        var tx = await db.Transactions.FindAsync([id], ct);
        if (tx is null)
            return Results.NotFound(new { error = $"Transaction {id} not found." });

        tx.Category = body.Category;
        tx.CategoryConfidence = Categories.ConfidenceHigh;
        tx.CategorySource = Categories.SourceManual;
        await db.SaveChangesAsync(ct);
        return Results.Ok(tx);
    }

    private static async Task<IResult> GetTransactions(
        DateOnly? from, DateOnly? to, bool? sync, EnableBankingClient client, AppDbContext db, TimeProvider time,
        CategorizationQueue categorization, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var dateTo = to ?? DateOnly.FromDateTime(now.UtcDateTime);
        var dateFrom = from ?? dateTo.AddDays(-DefaultRangeDays);
        if (dateFrom > dateTo)
            return Results.BadRequest(new { error = "from must be before to" });

        var allAccounts = await db.BankAccounts.Include(a => a.BankSession).ToListAsync(ct);
        // sync=false: read stored rows only, no bank call (bank reads are rate limited), works with an expired session.
        if (sync == false)
            return Results.Ok(await LoadAsync(db, allAccounts.Select(a => a.Uid).ToList(), dateFrom, dateTo, ct));

        // DateTimeOffset is stored as binary, so filter active sessions in memory.
        var accounts = allAccounts
            .Where(a => a.BankSession.ValidUntil > now)
            .ToList();
        if (accounts.Count == 0)
            return Results.Conflict(new { error = "No active bank session. Connect a bank via /api/bank/connect." });

        var added = 0;
        foreach (var account in accounts)
        {
            var fetched = await client.GetTransactionsAsync(account.Uid, dateFrom, dateTo, ct);
            var known = (await db.Transactions
                    .Where(t => t.AccountUid == account.Uid)
                    .Select(t => t.ExternalId)
                    .ToListAsync(ct))
                .ToHashSet();

            foreach (var bankTx in fetched)
            {
                var entity = TransactionMapper.ToEntity(bankTx, account.Uid, now);
                if (known.Add(entity.ExternalId))
                {
                    db.Transactions.Add(entity);
                    added++;
                }
            }
        }
        await db.SaveChangesAsync(ct);
        // Only new rows need categorizing; runs in the background, categories appear once it finishes.
        if (added > 0)
            categorization.Request();

        return Results.Ok(await LoadAsync(db, accounts.Select(a => a.Uid).ToList(), dateFrom, dateTo, ct));
    }

    private static Task<List<Transaction>> LoadAsync(
        AppDbContext db, List<string> uids, DateOnly dateFrom, DateOnly dateTo, CancellationToken ct) =>
        db.Transactions
            .Where(t => uids.Contains(t.AccountUid) && t.BookingDate >= dateFrom && t.BookingDate <= dateTo)
            .OrderByDescending(t => t.BookingDate).ThenByDescending(t => t.Id)
            .ToListAsync(ct);
}
