using System.Globalization;
using System.Text;
using System.Text.Json;
using ExpenseTracker.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExpenseTracker.Api.Categorization;

public sealed record CategoryAssignment(int Id, string Category, string Confidence);

/// <summary>A category the user set by hand, used as a rule for the same merchant and as a prompt example.</summary>
public sealed record Correction(string? Counterparty, string? Description, string Category)
{
    public string? Key => MerchantKey.For(Counterparty, Description);
}

public sealed record BatchValidation(List<CategoryAssignment> Valid, List<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Assigns categories to uncategorized expenses in batches using the Claude Code CLI.</summary>
public sealed class CategorizationService(
    AppDbContext db, ClaudeCli claude, IOptions<CategorizationOptions> options, ILogger<CategorizationService> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static readonly string Schema = JsonSerializer.Serialize(new
    {
        type = "object",
        properties = new
        {
            items = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        id = new { type = "integer" },
                        category = new { type = "string", @enum = Categories.All },
                        confidence = new { type = "string", @enum = Categories.Confidence },
                    },
                    required = new[] { "id", "category", "confidence" },
                    additionalProperties = false,
                },
            },
        },
        required = new[] { "items" },
        additionalProperties = false,
    });

    /// <summary>
    /// Categorizes uncategorized expenses. Returns how many were categorized.
    /// 1. Merchants the user corrected by hand get that category directly, without Claude.
    /// 2. The rest (below the attempt limit) go to Claude, with the recent corrections as examples.
    /// Throws a permanent <see cref="ClaudeCliException"/> (CLI missing, not logged in) to stop the run.
    /// </summary>
    public async Task<int> CategorizePendingAsync(CancellationToken ct)
    {
        var corrections = await LoadCorrectionsAsync(ct);
        var done = await ApplyCorrectionRulesAsync(corrections, ct);

        var maxAttempts = options.Value.MaxAttempts;
        // Snapshot only; rows may get a manual category while Claude is working, so saves re-check (see Save*).
        var pending = await db.Transactions.AsNoTracking()
            .Where(t => t.Category == null && t.CreditDebit == CreditDebit.Debit &&
                        t.CategorizationAttempts < maxAttempts)
            .OrderBy(t => t.Id)
            .ToListAsync(ct);
        if (pending.Count == 0) return done;

        // Newest correction per merchant; corrections without a usable key say nothing about a merchant.
        var examples = corrections.Where(c => c.Key is not null).DistinctBy(c => c.Key)
            .Take(options.Value.MaxCorrectionExamples).ToList();

        log.LogInformation("Categorizing {Count} transactions with Claude ({Examples} correction examples)",
            pending.Count, examples.Count);
        var byClaude = 0;
        foreach (var batch in pending.Chunk(options.Value.BatchSize))
        {
            var assignments = await CategorizeBatchAsync(batch, examples, ct);
            byClaude += await SaveAssignmentsAsync(assignments, Categories.SourceAi, ct);

            var failedIds = batch.Select(t => t.Id).Except(assignments.Select(a => a.Id)).ToList();
            await CountFailedAttemptsAsync(failedIds, ct);
        }

        log.LogInformation("Categorized {Done} of {Count} transactions with Claude", byClaude, pending.Count);
        return done + byClaude;
    }

    /// <summary>Manual corrections, newest first (by transaction id).</summary>
    private Task<List<Correction>> LoadCorrectionsAsync(CancellationToken ct) =>
        db.Transactions.AsNoTracking()
            .Where(t => t.CategorySource == Categories.SourceManual && t.Category != null)
            .OrderByDescending(t => t.Id)
            .Select(t => new Correction(t.Counterparty, t.Description, t.Category!))
            .ToListAsync(ct);

    /// <summary>
    /// Gives uncategorized expenses the category the user chose for the same merchant. Applies regardless of
    /// the attempt limit (no Claude call). Merchants whose corrections disagree are left to Claude.
    /// </summary>
    private async Task<int> ApplyCorrectionRulesAsync(List<Correction> corrections, CancellationToken ct)
    {
        var rules = corrections
            .Where(c => c.Key is not null)
            .GroupBy(c => c.Key!)
            .Where(g => g.Select(c => c.Category).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Category);
        if (rules.Count == 0) return 0;

        var uncategorized = await db.Transactions.AsNoTracking()
            .Where(t => t.Category == null && t.CreditDebit == CreditDebit.Debit)
            .Select(t => new { t.Id, t.Counterparty, t.Description })
            .ToListAsync(ct);
        var matches = uncategorized
            .Select(t => (t.Id, Key: MerchantKey.For(t.Counterparty, t.Description)))
            .Where(t => t.Key is not null && rules.ContainsKey(t.Key))
            .Select(t => new CategoryAssignment(t.Id, rules[t.Key!], Categories.ConfidenceHigh))
            .ToList();

        var saved = await SaveAssignmentsAsync(matches, Categories.SourceRule, ct);
        if (saved > 0)
            log.LogInformation("Categorized {Count} transactions from earlier manual corrections", saved);
        return saved;
    }

    // Conditional updates: a row categorized manually in the meantime is left alone.
    private async Task<int> SaveAssignmentsAsync(List<CategoryAssignment> assignments, string source, CancellationToken ct)
    {
        var saved = 0;
        foreach (var group in assignments.GroupBy(a => (a.Category, a.Confidence)))
        {
            var ids = group.Select(a => a.Id).ToList();
            var (category, confidence) = group.Key;
            saved += await db.Transactions
                .Where(t => ids.Contains(t.Id) && t.Category == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Category, category)
                    .SetProperty(t => t.CategoryConfidence, confidence)
                    .SetProperty(t => t.CategorySource, source), ct);
        }
        return saved;
    }

    private async Task CountFailedAttemptsAsync(List<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        await db.Transactions
            .Where(t => ids.Contains(t.Id) && t.Category == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategorizationAttempts, t => t.CategorizationAttempts + 1), ct);
    }

    private async Task<List<CategoryAssignment>> CategorizeBatchAsync(
        Transaction[] batch, List<Correction> examples, CancellationToken ct)
    {
        var ids = batch.Select(t => t.Id).ToHashSet();
        var prompt = BuildPrompt(batch, examples, previousErrors: null);

        var first = await TryRunAsync(prompt, ids, ct);
        if (first.IsValid) return first.Valid;

        log.LogWarning("Batch {First}-{Last} invalid, retrying: {Errors}",
            batch[0].Id, batch[^1].Id, string.Join("; ", first.Errors));

        var second = await TryRunAsync(BuildPrompt(batch, examples, first.Errors), ids, ct);
        if (!second.IsValid)
        {
            var missing = ids.Except(second.Valid.Select(v => v.Id)).Order();
            log.LogError("Batch {First}-{Last} still invalid after retry; leaving uncategorized: [{Missing}]. Errors: {Errors}",
                batch[0].Id, batch[^1].Id, string.Join(",", missing), string.Join("; ", second.Errors));
        }
        return second.Valid;
    }

    private async Task<BatchValidation> TryRunAsync(string prompt, IReadOnlySet<int> ids, CancellationToken ct)
    {
        try
        {
            return Validate(await claude.RunAsync(prompt, Schema, ct), ids);
        }
        catch (ClaudeCliException ex) when (!ex.IsPermanent)
        {
            // Crash, timeout or garbled output: a failed attempt, handled by the normal retry.
            return new BatchValidation([], [$"Response could not be used: {ex.Message}"]);
        }
        catch (JsonException ex)
        {
            return new BatchValidation([], [$"Response could not be used: {ex.Message}"]);
        }
    }

    /// <summary>
    /// Checks the model output against the batch: every id exactly once, only known ids,
    /// category and confidence from the allowed lists. Valid items are kept even if others fail.
    /// </summary>
    public static BatchValidation Validate(JsonElement output, IReadOnlySet<int> expectedIds)
    {
        var valid = new List<CategoryAssignment>();
        var errors = new List<string>();

        if (output.ValueKind != JsonValueKind.Object ||
            !output.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return new BatchValidation(valid, ["Response must be an object with an \"items\" array."]);

        var seen = new HashSet<int>();
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            var at = $"items[{index++}]";
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id))
            {
                errors.Add($"{at}: missing or non-integer id.");
                continue;
            }
            if (!expectedIds.Contains(id)) { errors.Add($"{at}: unknown id {id} (not in this batch)."); continue; }
            if (!seen.Add(id)) { errors.Add($"{at}: duplicate id {id}."); continue; }

            var category = item.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var confidence = item.TryGetProperty("confidence", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            if (!Categories.IsValid(category))
            {
                errors.Add($"id {id}: invalid category \"{category}\".");
                continue;
            }
            if (confidence is null || !Categories.Confidence.Contains(confidence))
            {
                errors.Add($"id {id}: invalid confidence \"{confidence}\" (use high or low).");
                continue;
            }
            valid.Add(new CategoryAssignment(id, category!, confidence));
        }

        var missing = expectedIds.Except(seen).Order().ToList();
        if (missing.Count > 0)
            errors.Add($"Missing ids: {string.Join(", ", missing)}. Return exactly one item per input transaction.");

        return new BatchValidation(valid, errors);
    }

    private static string BuildPrompt(Transaction[] batch, List<Correction> examples, List<string>? previousErrors)
    {
        var input = batch.Select(t => new
        {
            id = t.Id,
            date = t.BookingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            amount = t.Amount,
            currency = t.Currency,
            counterparty = t.Counterparty,
            description = t.Description,
        });

        var sb = new StringBuilder(PromptHeader);
        if (examples.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("The account owner corrected these categories by hand. They override the general rules above:");
            sb.AppendLine("use the same category for the same merchant, and treat them as strong hints for similar merchants.");
            foreach (var e in examples)
                sb.AppendLine(JsonSerializer.Serialize(
                    new { counterparty = e.Counterparty, description = e.Description, category = e.Category }, Json));
        }
        if (previousErrors is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("YOUR PREVIOUS ANSWER FOR THIS BATCH WAS REJECTED. Fix these problems:");
            foreach (var e in previousErrors) sb.AppendLine("- " + e);
        }
        sb.AppendLine();
        sb.AppendLine("Transactions to categorize:");
        sb.AppendLine(JsonSerializer.Serialize(input, Json));
        return sb.ToString();
    }

    private const string PromptHeader =
        """
        You categorize outgoing payments (expenses) from a personal Polish bank account (PKO Bank Polski).
        Descriptions are often in Polish and may contain card terminal names, cities, BLIK references or shortened merchant names.

        Assign each transaction exactly one category from this fixed list. Use the names exactly as written:

        - Groceries: supermarkets, grocery and convenience stores, bakeries, butchers, markets.
          e.g. Biedronka, Lidl, Żabka, Carrefour, Auchan, Kaufland, Dino, Netto, Stokrotka, Lewiatan, Frisco.
        - Restaurants & Cafes: restaurants, cafes, bars, fast food, food delivery.
          e.g. McDonald's, KFC, Starbucks, Costa, Pyszne.pl, Glovo, Wolt, Uber Eats.
        - Transport: public transport, taxis and ride-hailing, trains, flights, fuel, parking, car service, tolls.
          e.g. ZTM, MPK, PKP Intercity, Koleo, Jakdojade, Bolt, Uber (rides), FreeNow, Orlen, BP, Shell, Circle K, mPay parking.
        - Housing & Utilities: rent, housing fees, electricity, gas, water, heating, internet, mobile and landline bills, home insurance.
          e.g. czynsz, wspólnota/spółdzielnia, PGE, Tauron, Enea, PGNiG, Veolia, Orange, Play, Plus, T-Mobile, UPC, Vectra.
        - Subscriptions: recurring digital services and memberships.
          e.g. Netflix, Spotify, YouTube Premium, Disney+, HBO Max, Apple.com/bill, Google Play subscriptions, iCloud, ChatGPT, Claude, Multisport.
        - Health: pharmacies, doctors, dentists, clinics, lab tests, opticians, health insurance.
          e.g. apteka, Super-Pharm, Gemini apteka, Lux Med, Medicover, Enel-Med, Diagnostyka.
        - Shopping: clothing, electronics, household goods, cosmetics, online marketplaces, gifts.
          e.g. Allegro, Amazon, Zalando, H&M, Zara, Reserved, Rossmann, Hebe, IKEA, Media Markt, RTV Euro AGD, Empik, Decathlon, Castorama, Leroy Merlin.
        - Entertainment: cinema, concerts, events, games, sports tickets, hobbies, travel leisure (hotels, Booking.com).
          e.g. Cinema City, Multikino, Helios, eBilet, Ticketmaster, Steam, PlayStation Store, Booking.com.
        - Transfers: money sent to people or to own accounts, BLIK phone transfers, savings, ATM cash withdrawals, currency exchange, loan or credit card repayments.
          Typical wording: "przelew", "przelew na telefon", "BLIK P2P", "wypłata z bankomatu", "spłata", "przelew własny".
        - Other: anything that does not clearly fit above, fees and taxes (opłata, prowizja, US / urząd skarbowy), or when you cannot tell.

        Rules:
        1. Use ONLY the category names above. Never invent a category or rename one.
        2. If you are not reasonably sure, use "Other" with confidence "low". Do not guess.
        3. confidence "high" = merchant or purpose is clear; "low" = an educated guess or ambiguous.
        4. Return exactly one item per input transaction, using the same "id". Do not skip, duplicate or add ids.
        5. Base the decision on counterparty and description; the amount is only a weak hint.

        Examples:
        Input:  {"id":101,"amount":87.45,"currency":"PLN","counterparty":"BIEDRONKA 4123","description":"Płatność kartą BIEDRONKA 4123 WARSZAWA"}
        Output: {"id":101,"category":"Groceries","confidence":"high"}

        Input:  {"id":102,"amount":24.00,"currency":"PLN","counterparty":"BOLT.EU","description":"BOLT.EU/O/2510151234 Tallinn"}
        Output: {"id":102,"category":"Transport","confidence":"high"}

        Input:  {"id":103,"amount":200.00,"currency":"PLN","counterparty":"Jan Kowalski","description":"Przelew na telefon BLIK"}
        Output: {"id":103,"category":"Transfers","confidence":"high"}

        Input:  {"id":104,"amount":59.99,"currency":"PLN","counterparty":"PAYU*ABCD1234","description":"Płatność internetowa PAYU*ABCD1234"}
        Output: {"id":104,"category":"Other","confidence":"low"}

        Respond with {"items":[...]} containing one object per transaction.
        """;
}
