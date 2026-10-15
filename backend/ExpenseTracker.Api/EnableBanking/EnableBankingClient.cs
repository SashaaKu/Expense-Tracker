using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ExpenseTracker.Api.EnableBanking;

public sealed class EnableBankingException(int statusCode, string body)
    : Exception($"Enable Banking API returned {statusCode}: {body}")
{
    public int StatusCode { get; } = statusCode;
    public string Body { get; } = body;
}

/// <summary>Adds the Enable Banking JWT to every outgoing request.</summary>
public sealed class EnableBankingAuthHandler(EnableBankingJwt jwt) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt.GetToken());
        return base.SendAsync(request, ct);
    }
}

public sealed class EnableBankingClient(HttpClient http, IOptions<EnableBankingOptions> options, TimeProvider time)
{
    private static readonly TimeSpan ConsentValidity = TimeSpan.FromDays(90);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<List<Aspsp>> GetAspspsAsync(string country, CancellationToken ct)
    {
        var response = await SendAsync<AspspsResponse>(
            HttpMethod.Get, $"aspsps?country={Uri.EscapeDataString(country)}", null, ct);
        return response.Aspsps;
    }

    public Task<StartAuthResponse> StartAuthAsync(string name, string country, string state, CancellationToken ct)
    {
        var body = new StartAuthRequest(
            new AuthAccess(time.GetUtcNow() + ConsentValidity),
            new AspspRef(name, country),
            state,
            options.Value.RedirectUrl,
            "personal");
        return SendAsync<StartAuthResponse>(HttpMethod.Post, "auth", body, ct);
    }

    public Task<CreateSessionResponse> CreateSessionAsync(string code, CancellationToken ct) =>
        SendAsync<CreateSessionResponse>(HttpMethod.Post, "sessions", new CreateSessionRequest(code), ct);

    public async Task<List<BankTransaction>> GetTransactionsAsync(
        string accountUid, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var result = new List<BankTransaction>();
        string? continuationKey = null;
        do
        {
            var url = $"accounts/{Uri.EscapeDataString(accountUid)}/transactions" +
                      $"?date_from={from:yyyy-MM-dd}&date_to={to:yyyy-MM-dd}";
            if (!string.IsNullOrEmpty(continuationKey))
                url += $"&continuation_key={Uri.EscapeDataString(continuationKey)}";

            var page = await SendAsync<TransactionsResponse>(HttpMethod.Get, url, null, ct);
            result.AddRange(page.Transactions);
            continuationKey = page.ContinuationKey;
        } while (!string.IsNullOrEmpty(continuationKey));

        return result;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new EnableBankingException((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));

        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new EnableBankingException((int)response.StatusCode, "Empty response body");
    }
}
