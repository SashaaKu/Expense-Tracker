using System.Globalization;
using System.Text;
using System.Text.Json;
using ExpenseTracker.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExpenseTracker.Api.Categorization;

/// <summary>
/// Result for one transaction. Expense: Category set, IsRefund false. Credit: a refund (IsRefund, Category may be
/// null when unknown) or income (IsRefund false, Category null).
/// </summary>
public sealed record CategoryAssignment(int Id, string? Category, string Confidence, bool IsRefund = false);

/// <summary>A classification the user set by hand, used as a rule for the same merchant and as a prompt example.</summary>
public sealed record Correction(string CreditDebit, string? Counterparty, string? Description, string? Category, bool IsRefund)
{
    public string? Key => MerchantKey.For(Counterparty, Description);
}

public sealed record BatchValidation(List<CategoryAssignment> Valid, List<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Classifies transactions: expenses get a category; incoming money is a refund (with the purchase's category)
/// or income. Uses manual corrections, bank refund markers and, for the rest, the Claude Code CLI.
/// </summary>
public sealed class CategorizationService(
    AppDbContext db, ClaudeCli claude, IOptions<CategorizationOptions> options, ILogger<CategorizationService> log)
{
    // A bank-marked refund is matched to purchases of the same amount from this many days before it.
    private const int RefundMatchDays = 90;

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
                        category = new { type = "string", @enum = Categories.All.Append(Categories.Income) },
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
    /// Classifies all unclassified transactions (CategorySource == null). Returns how many were classified.
    /// 1. Merchants the user corrected by hand get the same result directly, without Claude.
    /// 2. Credits the bank marked as returned payments are refunds; category from a same-amount earlier purchase.
    /// 3. The rest (below the attempt limit) go to Claude, with the recent corrections as examples.
    /// Throws a permanent <see cref="ClaudeCliException"/> (CLI missing, not logged in) to stop the run.
    /// </summary>
    public async Task<int> CategorizePendingAsync(CancellationToken ct)
    {
        var corrections = await LoadCorrectionsAsync(ct);
        var done = await ApplyCorrectionRulesAsync(corrections, ct);
        done += await ApplyBankRefundsAsync(ct);

        var maxAttempts = options.Value.MaxAttempts;
        // Snapshot only; rows may get a manual category while Claude is working, so saves re-check (see Save*).
        var pending = await db.Transactions.AsNoTracking()
            .Where(t => t.CategorySource == null && t.CategorizationAttempts < maxAttempts)
            .OrderBy(t => t.Id)
            .ToListAsync(ct);
        if (pending.Count == 0) return done;

        // Newest correction per merchant and direction; corrections without a usable key say nothing about a
        // merchant, and a refund of unknown category teaches nothing about categories.
        var examples = corrections
            .Where(c => c.Key is not null && !(c.IsRefund && c.Category is null))
            .DistinctBy(c => (c.CreditDebit, c.Key))
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

    /// <summary>Manual classifications, newest first (by transaction id).</summary>
    private Task<List<Correction>> LoadCorrectionsAsync(CancellationToken ct) =>
        db.Transactions.AsNoTracking()
            .Where(t => t.CategorySource == Categories.SourceManual)
            .OrderByDescending(t => t.Id)
            .Select(t => new Correction(t.CreditDebit, t.Counterparty, t.Description, t.Category, t.IsRefund))
            .ToListAsync(ct);

    /// <summary>
    /// Gives unclassified transactions the result the user chose for the same merchant in the same direction.
    /// Applies regardless of the attempt limit (no Claude call). Merchants whose corrections disagree are left to Claude.
    /// </summary>
    private async Task<int> ApplyCorrectionRulesAsync(List<Correction> corrections, CancellationToken ct)
    {
        var rules = corrections
            .Where(c => c.Key is not null)
            .GroupBy(c => (c.CreditDebit, Key: c.Key!))
            .Where(g => g.Select(c => (c.Category, c.IsRefund)).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First());
        if (rules.Count == 0) return 0;

        var unclassified = await db.Transactions.AsNoTracking()
            .Where(t => t.CategorySource == null)
            .Select(t => new { t.Id, t.CreditDebit, t.Counterparty, t.Description })
            .ToListAsync(ct);
        var matches = new List<CategoryAssignment>();
        foreach (var t in unclassified)
        {
            var key = MerchantKey.For(t.Counterparty, t.Description);
            if (key is not null && rules.TryGetValue((t.CreditDebit, key), out var rule))
                matches.Add(new CategoryAssignment(t.Id, rule.Category, Categories.ConfidenceHigh, rule.IsRefund));
        }

        var saved = await SaveAssignmentsAsync(matches, Categories.SourceRule, ct);
        if (saved > 0)
            log.LogInformation("Categorized {Count} transactions from earlier manual corrections", saved);
        return saved;
    }

    /// <summary>
    /// Credits the bank marked as returned payments are refunds. Their description has no merchant, so the category
    /// comes from earlier purchases with the same amount and currency; if those disagree or there are none, the
    /// category stays null for the user to pick. Low confidence either way, so the UI asks to check it.
    /// </summary>
    private async Task<int> ApplyBankRefundsAsync(CancellationToken ct)
    {
        var refunds = (await db.Transactions.AsNoTracking()
                .Where(t => t.CategorySource == null && t.CreditDebit == CreditDebit.Credit)
                .Select(t => new { t.Id, t.Description, t.Amount, t.Currency, t.BookingDate })
                .ToListAsync(ct))
            .Where(t => MerchantKey.IsPaymentReturn(t.Description))
            .ToList();
        if (refunds.Count == 0) return 0;

        // Amounts are stored as text in SQLite, so match them in memory.
        var since = refunds.Min(r => r.BookingDate).AddDays(-RefundMatchDays);
        var purchases = await db.Transactions.AsNoTracking()
            .Where(t => t.CreditDebit == CreditDebit.Debit && t.Category != null && t.BookingDate >= since)
            .Select(t => new { t.Amount, t.Currency, t.BookingDate, t.Category })
            .ToListAsync(ct);

        var assignments = refunds.Select(r =>
        {
            var categories = purchases
                .Where(p => p.Amount == r.Amount && p.Currency == r.Currency &&
                            p.BookingDate <= r.BookingDate && p.BookingDate >= r.BookingDate.AddDays(-RefundMatchDays))
                .Select(p => p.Category)
                .Distinct()
                .ToList();
            var category = categories.Count == 1 ? categories[0] : null;
            return new CategoryAssignment(r.Id, category, Categories.ConfidenceLow, IsRefund: true);
        }).ToList();

        var saved = await SaveAssignmentsAsync(assignments, Categories.SourceRule, ct);
        if (saved > 0)
            log.LogInformation("Marked {Count} bank refunds ({Matched} matched to a purchase)",
                saved, assignments.Count(a => a.Category is not null));
        return saved;
    }

    // Conditional updates: a row classified manually in the meantime is left alone.
    private async Task<int> SaveAssignmentsAsync(List<CategoryAssignment> assignments, string source, CancellationToken ct)
    {
        var saved = 0;
        foreach (var group in assignments.GroupBy(a => (a.Category, a.Confidence, a.IsRefund)))
        {
            var ids = group.Select(a => a.Id).ToList();
            var (category, confidence, isRefund) = group.Key;
            saved += await db.Transactions
                .Where(t => ids.Contains(t.Id) && t.CategorySource == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Category, category)
                    .SetProperty(t => t.CategoryConfidence, confidence)
                    .SetProperty(t => t.IsRefund, isRefund)
                    .SetProperty(t => t.CategorySource, source), ct);
        }
        return saved;
    }

    private async Task CountFailedAttemptsAsync(List<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        await db.Transactions
            .Where(t => ids.Contains(t.Id) && t.CategorySource == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategorizationAttempts, t => t.CategorizationAttempts + 1), ct);
    }

    private async Task<List<CategoryAssignment>> CategorizeBatchAsync(
        Transaction[] batch, List<Correction> examples, CancellationToken ct)
    {
        var ids = batch.Select(t => t.Id).ToHashSet();
        var creditIds = batch.Where(t => t.CreditDebit == CreditDebit.Credit).Select(t => t.Id).ToHashSet();
        var prompt = BuildPrompt(batch, examples, previousErrors: null);

        var first = await TryRunAsync(prompt, ids, creditIds, ct);
        if (first.IsValid) return first.Valid;

        log.LogWarning("Batch {First}-{Last} invalid, retrying: {Errors}",
            batch[0].Id, batch[^1].Id, string.Join("; ", first.Errors));

        var second = await TryRunAsync(BuildPrompt(batch, examples, first.Errors), ids, creditIds, ct);
        if (!second.IsValid)
        {
            var missing = ids.Except(second.Valid.Select(v => v.Id)).Order();
            log.LogError("Batch {First}-{Last} still invalid after retry; leaving uncategorized: [{Missing}]. Errors: {Errors}",
                batch[0].Id, batch[^1].Id, string.Join(",", missing), string.Join("; ", second.Errors));
        }
        return second.Valid;
    }

    private async Task<BatchValidation> TryRunAsync(
        string prompt, IReadOnlySet<int> ids, IReadOnlySet<int> creditIds, CancellationToken ct)
    {
        try
        {
            return Validate(await claude.RunAsync(prompt, Schema, ct), ids, creditIds);
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
    /// category and confidence from the allowed lists, "Income" only for incoming money (<paramref name="creditIds"/>).
    /// Valid items are kept even if others fail. For a credit, a category means a refund and "Income" means not a refund.
    /// </summary>
    public static BatchValidation Validate(JsonElement output, IReadOnlySet<int> expectedIds, IReadOnlySet<int> creditIds)
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
            var isCredit = creditIds.Contains(id);
            if (category == Categories.Income && !isCredit)
            {
                errors.Add($"id {id}: \"{Categories.Income}\" is only for incoming money (direction \"in\"); pick a category.");
                continue;
            }
            if (category != Categories.Income && !Categories.IsValid(category))
            {
                errors.Add($"id {id}: invalid category \"{category}\".");
                continue;
            }
            if (confidence is null || !Categories.Confidence.Contains(confidence))
            {
                errors.Add($"id {id}: invalid confidence \"{confidence}\" (use high or low).");
                continue;
            }
            valid.Add(category == Categories.Income
                ? new CategoryAssignment(id, null, confidence, IsRefund: false)
                : new CategoryAssignment(id, category, confidence, IsRefund: isCredit));
        }

        var missing = expectedIds.Except(seen).Order().ToList();
        if (missing.Count > 0)
            errors.Add($"Missing ids: {string.Join(", ", missing)}. Return exactly one item per input transaction.");

        return new BatchValidation(valid, errors);
    }

    private static string Direction(string creditDebit) => creditDebit == CreditDebit.Credit ? "in" : "out";

    private static string BuildPrompt(Transaction[] batch, List<Correction> examples, List<string>? previousErrors)
    {
        var input = batch.Select(t => new
        {
            id = t.Id,
            direction = Direction(t.CreditDebit),
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
            sb.AppendLine("The account owner corrected these by hand. They override the general rules above:");
            sb.AppendLine("use the same answer for the same merchant, and treat them as strong hints for similar merchants.");
            foreach (var e in examples)
                sb.AppendLine(JsonSerializer.Serialize(new
                {
                    direction = Direction(e.CreditDebit),
                    counterparty = e.Counterparty,
                    description = e.Description,
                    category = e.IsRefund || e.CreditDebit == CreditDebit.Debit ? e.Category : Categories.Income,
                }, Json));
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
        You categorize transactions from a personal Polish bank account (PKO Bank Polski).
        Descriptions are often in Polish and may contain card terminal names, cities, BLIK references or shortened merchant names.
        Each transaction has a direction: "out" = money spent, "in" = money received.

        For direction "out", assign exactly one category from this fixed list. Use the names exactly as written:

        - Groceries: supermarkets, grocery and convenience stores, bakeries, butchers, markets.
          e.g. Biedronka, Lidl, Żabka, Carrefour, Auchan, Kaufland, Dino, Netto, Stokrotka, Lewiatan, Frisco.
        - Restaurants & Cafes: restaurants, cafes, bars, fast food, food delivery.
          e.g. McDonald's, KFC, Starbucks, Costa, Pyszne.pl, Glovo, Wolt, Uber Eats.
        - Transport: local public transport, taxis and ride-hailing, trains, fuel, parking, car service, tolls. Not flights.
          e.g. ZTM, MPK, PKP Intercity, Koleo, Jakdojade, Bolt, Uber (rides), FreeNow, Orlen, BP, Shell, Circle K, mPay parking.
        - Travel: flights, hotels and other accommodation, booking sites, travel agencies, travel insurance.
          e.g. Ryanair, Wizz Air, LOT, easyJet, Booking.com, Airbnb, Hotels.com, Expedia, Itaka, Rainbow, TUI.
        - Housing & Utilities: rent, housing fees, electricity, gas, water, heating, internet, mobile and landline bills, home insurance.
          e.g. czynsz, wspólnota/spółdzielnia, PGE, Tauron, Enea, PGNiG, Veolia, Orange, Play, Plus, T-Mobile, UPC, Vectra.
        - Subscriptions: recurring digital services.
          e.g. Netflix, Spotify, YouTube Premium, Disney+, HBO Max, Apple.com/bill, Google Play subscriptions, iCloud, ChatGPT, Claude.
        - Health: pharmacies, doctors, dentists, clinics, lab tests, opticians, health insurance.
          e.g. apteka, Super-Pharm, Gemini apteka, Lux Med, Medicover, Enel-Med, Diagnostyka.
        - Sport: gyms and fitness clubs, sports clubs and classes, swimming pools, courts, sports equipment, race entry fees.
          e.g. CityFit, Zdrofit, Calypso, McFit, Multisport, Decathlon, Go Sport, squash/padel courts, basen.
        - Education: courses, schools and universities, tuition, language lessons, books and e-books, e-learning, exam fees.
          e.g. Udemy, Coursera, Duolingo, Babbel, Empik (books), Świat Książki, Legimi, szkoła językowa, czesne.
        - Shopping: electronics, household goods, furniture, cosmetics, online marketplaces, gifts. Not clothing.
          e.g. Allegro, Amazon, Rossmann, Hebe, IKEA, Media Markt, RTV Euro AGD, Pepco, Action, Castorama, Leroy Merlin.
        - Clothes: clothing, shoes and fashion accessories, online fashion stores.
          e.g. H&M, Zara, Reserved, Zalando, CCC, Sinsay, Bershka, Pull&Bear, Mohito, Cropp, House, Deichmann, TK Maxx, Vinted.
        - Entertainment: cinema, concerts, events, museums, games, hobbies.
          e.g. Cinema City, Multikino, Helios, eBilet, Ticketmaster, Steam, PlayStation Store.
        - Transfers: money sent to people or to own accounts, BLIK phone transfers, savings, ATM cash withdrawals, currency exchange, loan or credit card repayments.
          Typical wording: "przelew", "przelew na telefon", "BLIK P2P", "wypłata z bankomatu", "spłata", "przelew własny".
        - Other: anything that does not clearly fit above, fees and taxes (opłata, prowizja, US / urząd skarbowy), or when you cannot tell.

        For direction "in", decide whether the money is a REFUND from a merchant or company for an earlier purchase
        (returned goods, cancelled booking or ticket, chargeback, shop cashback):
        - If it is a refund, answer the category the original purchase would have, from the list above.
        - Otherwise answer "Income": salary and wages, transfers from private persons (also friends paying back or
          "zwrot" from a private person), own-account transfers, interest, tax returns, social benefits.

        Rules:
        1. Use ONLY the category names above, or "Income" for direction "in". Never invent a category or rename one.
        2. Never answer "Income" for direction "out".
        3. If you are not reasonably sure, use "Other" (for "in": "Income") with confidence "low". Do not guess.
        4. confidence "high" = merchant or purpose is clear; "low" = an educated guess or ambiguous.
        5. Return exactly one item per input transaction, using the same "id". Do not skip, duplicate or add ids.
        6. Base the decision on counterparty and description; the amount is only a weak hint.

        Examples:
        Input:  {"id":101,"direction":"out","amount":87.45,"currency":"PLN","counterparty":"BIEDRONKA 4123","description":"Płatność kartą BIEDRONKA 4123 WARSZAWA"}
        Output: {"id":101,"category":"Groceries","confidence":"high"}

        Input:  {"id":102,"direction":"out","amount":24.00,"currency":"PLN","counterparty":"BOLT.EU","description":"BOLT.EU/O/2510151234 Tallinn"}
        Output: {"id":102,"category":"Transport","confidence":"high"}

        Input:  {"id":103,"direction":"out","amount":200.00,"currency":"PLN","counterparty":"Jan Kowalski","description":"Przelew na telefon BLIK"}
        Output: {"id":103,"category":"Transfers","confidence":"high"}

        Input:  {"id":104,"direction":"out","amount":59.99,"currency":"PLN","counterparty":"PAYU*ABCD1234","description":"Płatność internetowa PAYU*ABCD1234"}
        Output: {"id":104,"category":"Other","confidence":"low"}

        Input:  {"id":105,"direction":"out","amount":249.00,"currency":"PLN","counterparty":null,"description":"BerlinZalando PaymentsDE CARD-PAYMENT"}
        Output: {"id":105,"category":"Clothes","confidence":"high"}

        Input:  {"id":106,"direction":"out","amount":139.00,"currency":"PLN","counterparty":null,"description":"POZNANCITYFIT POZNANPL CARD-PAYMENT"}
        Output: {"id":106,"category":"Sport","confidence":"high"}

        Input:  {"id":107,"direction":"out","amount":412.30,"currency":"PLN","counterparty":null,"description":"DUBLINRYANAIR LTDIE CARD-PAYMENT"}
        Output: {"id":107,"category":"Travel","confidence":"high"}

        Input:  {"id":108,"direction":"out","amount":54.99,"currency":"PLN","counterparty":null,"description":"SAN FRANCISCOUDEMY: ONLINE COURSESUS CARD-PAYMENT"}
        Output: {"id":108,"category":"Education","confidence":"high"}

        Input:  {"id":109,"direction":"in","amount":6500.00,"currency":"PLN","counterparty":"ACME SP. Z O.O.","description":"Wynagrodzenie za 9/2026 TRANSFER-IN"}
        Output: {"id":109,"category":"Income","confidence":"high"}

        Input:  {"id":110,"direction":"in","amount":50.00,"currency":"PLN","counterparty":"ANNA NOWAK","description":"ZWROT ZA BILETY MOBILE-PAYMENT-C2C"}
        Output: {"id":110,"category":"Income","confidence":"high"}

        Input:  {"id":111,"direction":"in","amount":820.00,"currency":"PLN","counterparty":"BOOKING.COM B.V.","description":"Refund reservation 4512339871 TRANSFER-IN"}
        Output: {"id":111,"category":"Travel","confidence":"high"}

        Respond with {"items":[...]} containing one object per transaction.
        """;
}
