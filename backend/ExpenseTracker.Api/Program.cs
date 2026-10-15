using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.EnableBanking;
using ExpenseTracker.Api.Endpoints;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<EnableBankingOptions>()
    .Bind(builder.Configuration.GetSection(EnableBankingOptions.Section))
    .ValidateDataAnnotations()
    .Validate(o => File.Exists(o.KeyPath), "EnableBanking:KeyPath does not point to an existing file")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EnableBankingJwt>();
builder.Services.AddTransient<EnableBankingAuthHandler>();
builder.Services.AddHttpClient<EnableBankingClient>((sp, http) =>
    {
        var baseUrl = sp.GetRequiredService<IOptions<EnableBankingOptions>>().Value.BaseUrl;
        http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    })
    .AddHttpMessageHandler<EnableBankingAuthHandler>();

builder.Services.AddMemoryCache();
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddProblemDetails();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title, detail) = error is EnableBankingException eb
        ? (StatusCodes.Status502BadGateway, "Enable Banking API error", $"{eb.StatusCode}: {eb.Body}")
        : (StatusCodes.Status500InternalServerError, "Unexpected error", (string?)null);

    await Results.Problem(detail, statusCode: status, title: title).ExecuteAsync(context);
}));

app.UseHttpsRedirection();

app.MapBankEndpoints();
app.MapTransactionEndpoints();

app.Run();
