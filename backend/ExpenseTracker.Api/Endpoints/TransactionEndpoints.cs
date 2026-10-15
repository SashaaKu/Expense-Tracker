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
    }

    private static async Task<IResult> GetTransactions(
        DateOnly? from, DateOnly? to, EnableBankingClient client, AppDbContext db, TimeProvider time,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var dateTo = to ?? DateOnly.FromDateTime(now.UtcDateTime);
        var dateFrom = from ?? dateTo.AddDays(-DefaultRangeDays);
        if (dateFrom > dateTo)
            return Results.BadRequest(new { error = "from must be before to" });

        // DateTimeOffset is stored as binary, so filter active sessions in memory.
        var accounts = (await db.BankAccounts.Include(a => a.BankSession).ToListAsync(ct))
            .Where(a => a.BankSession.ValidUntil > now)
            .ToList();
        if (accounts.Count == 0)
            return Results.Conflict(new { error = "No active bank session. Connect a bank via /api/bank/connect." });

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
                    db.Transactions.Add(entity);
            }
        }
        await db.SaveChangesAsync(ct);

        var uids = accounts.Select(a => a.Uid).ToList();
        var result = await db.Transactions
            .Where(t => uids.Contains(t.AccountUid) && t.BookingDate >= dateFrom && t.BookingDate <= dateTo)
            .OrderByDescending(t => t.BookingDate).ThenByDescending(t => t.Id)
            .ToListAsync(ct);
        return Results.Ok(result);
    }
}
