using System.Security.Cryptography;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.EnableBanking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ExpenseTracker.Api.Endpoints;

public static class BankEndpoints
{
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    public static void MapBankEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/bank");
        group.MapGet("/aspsps", GetAspsps);
        group.MapGet("/connect", Connect);
        group.MapGet("/callback", Callback);
    }

    private static async Task<IResult> GetAspsps(string? country, EnableBankingClient client, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(country))
            return Results.BadRequest(new { error = "country is required" });

        var aspsps = await client.GetAspspsAsync(country.ToUpperInvariant(), ct);
        return Results.Ok(aspsps.Select(a => new { a.Name, a.Country, a.Logo, a.PsuTypes, a.Beta }));
    }

    private static async Task<IResult> Connect(
        string? name, string? country, EnableBankingClient client, IMemoryCache cache, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(country))
            return Results.BadRequest(new { error = "name and country are required" });

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        cache.Set(StateKey(state), true, StateLifetime);

        var auth = await client.StartAuthAsync(name, country.ToUpperInvariant(), state, ct);
        return Results.Ok(new { auth.Url });
    }

    private static async Task<IResult> Callback(
        string? code, string? state, string? error, string? error_description,
        EnableBankingClient client, IMemoryCache cache, AppDbContext db, TimeProvider time, IConfiguration config,
        CancellationToken ct)
    {
        // With Frontend:Url configured the browser is sent back to the UI; otherwise the result is returned as JSON.
        var frontendUrl = config["Frontend:Url"]?.TrimEnd('/');
        IResult Fail(object body, string message) => string.IsNullOrEmpty(frontendUrl)
            ? Results.BadRequest(body)
            : Results.Redirect($"{frontendUrl}/#connect?error={Uri.EscapeDataString(message)}");

        if (!string.IsNullOrEmpty(error))
            return Fail(new { error, description = error_description }, $"{error}: {error_description}");
        if (string.IsNullOrEmpty(code))
            return Fail(new { error = "code is missing" }, "code is missing");
        if (string.IsNullOrEmpty(state) || !cache.TryGetValue(StateKey(state), out _))
            return Fail(new { error = "invalid or expired state" }, "invalid or expired state");
        cache.Remove(StateKey(state));

        var created = await client.CreateSessionAsync(code, ct);

        var session = await db.BankSessions.Include(s => s.Accounts)
            .FirstOrDefaultAsync(s => s.SessionId == created.SessionId, ct);
        if (session is null)
        {
            session = new BankSession
            {
                SessionId = created.SessionId,
                AspspName = created.Aspsp.Name,
                Country = created.Aspsp.Country,
                CreatedAt = time.GetUtcNow(),
            };
            db.BankSessions.Add(session);
        }
        session.ValidUntil = created.Access.ValidUntil;

        // An account re-authorised under a new session moves to that session.
        var uids = created.Accounts.Select(a => a.Uid).ToList();
        var existing = await db.BankAccounts.Where(a => uids.Contains(a.Uid)).ToDictionaryAsync(a => a.Uid, ct);
        foreach (var a in created.Accounts)
        {
            if (!existing.TryGetValue(a.Uid, out var account))
            {
                account = new BankAccount { Uid = a.Uid };
                db.BankAccounts.Add(account);
            }
            account.BankSession = session;
            account.Iban = a.AccountId?.Iban;
            account.Currency = a.Currency;
            account.Name = a.Name;
        }

        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(frontendUrl))
            return Results.Redirect($"{frontendUrl}/#connect?connected={created.Accounts.Count}");
        return Results.Ok(new
        {
            sessionId = session.SessionId,
            validUntil = session.ValidUntil,
            accounts = created.Accounts.Select(a => new { a.Uid, iban = a.AccountId?.Iban, a.Currency, a.Name }),
        });
    }

    private static string StateKey(string state) => "eb-state:" + state;
}
