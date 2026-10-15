using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ExpenseTracker.Api.EnableBanking;

/// <summary>Builds and caches the RS256 JWT used to authenticate against the Enable Banking API.</summary>
public sealed class EnableBankingJwt : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly EnableBankingOptions _options;
    private readonly TimeProvider _time;
    private readonly Lazy<RSA> _rsa;
    private readonly Lock _lock = new();
    private string? _token;
    private DateTimeOffset _expiresAt;

    public EnableBankingJwt(IOptions<EnableBankingOptions> options, TimeProvider time)
    {
        _options = options.Value;
        _time = time;
        _rsa = new Lazy<RSA>(LoadKey);
    }

    public string GetToken()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_token is null || now >= _expiresAt - RefreshMargin)
            {
                _expiresAt = now + Lifetime;
                _token = Create(now, _expiresAt);
            }
            return _token;
        }
    }

    private string Create(DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        var key = new RsaSecurityKey(_rsa.Value) { KeyId = _options.ApplicationId };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "enablebanking.com",
            Audience = "api.enablebanking.com",
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = null,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        };
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(descriptor);
    }

    private RSA LoadKey()
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(_options.KeyPath));
        return rsa;
    }

    public void Dispose()
    {
        if (_rsa.IsValueCreated) _rsa.Value.Dispose();
    }
}
