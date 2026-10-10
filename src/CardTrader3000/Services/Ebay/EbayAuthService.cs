using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CardTrader3000.Data;
using CardTrader3000.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CardTrader3000.Services.Ebay;

/// <summary>
/// eBay OAuth (authorization code grant). The user signs in on eBay's consent page; eBay redirects
/// to /ebay/callback with a code, which is exchanged for an access token (2 hours) and a refresh
/// token (about 18 months). Both are encrypted with Data Protection and stored in <see cref="EbaySettings"/>.
/// </summary>
public sealed class EbayAuthService(
    IDbContextFactory<AppDbContext> dbFactory,
    IHttpClientFactory httpFactory,
    IDataProtectionProvider protectionProvider,
    IOptions<EbayOptions> options,
    ILogger<EbayAuthService> logger)
{
    public const string HttpClientName = "ebay-auth";

    private readonly EbayOptions _opt = options.Value;
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("CardTrader3000.EbayTokens.v1");
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // Application tokens (client credentials) by scope: used for public data like price research.
    private readonly ConcurrentDictionary<string, (string Token, DateTime ExpiresUtc)> _appTokens = new();

    // Pending sign-in "state" values (CSRF protection), valid for 15 minutes.
    private readonly ConcurrentDictionary<string, DateTime> _pendingStates = new();

    public EbayEnvironment Environment => _opt.Environment;

    /// <summary>The eBay consent page URL. Navigate the browser here (full page load).</summary>
    public string BuildConsentUrl()
    {
        if (!_opt.IsConfigured)
            throw new InvalidOperationException("eBay isn't configured. Set Ebay:ClientId, Ebay:ClientSecret and Ebay:RuName (see README).");

        foreach (var (key, created) in _pendingStates)
            if (created < DateTime.UtcNow.AddMinutes(-15)) _pendingStates.TryRemove(key, out _);

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _pendingStates[state] = DateTime.UtcNow;

        var query = new Dictionary<string, string>
        {
            ["client_id"] = _opt.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = _opt.RuName,
            ["scope"] = string.Join(' ', EbayOptions.Scopes),
            ["state"] = state
        };
        return _opt.AuthorizeUrl + "?" + string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>Handles the redirect back from eBay. Returns null on success, or an error message.</summary>
    public async Task<string?> HandleCallbackAsync(string? code, string? state, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(state) || !_pendingStates.TryRemove(state, out _))
            return "The sign-in link expired or didn't come from this app. Please try Connect again.";

        if (string.IsNullOrEmpty(code))
            return "eBay didn't return an authorization code (sign-in was declined or cancelled).";

        try
        {
            var token = await RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = _opt.RuName
            }, ct);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var settings = await GetOrCreateSettingsAsync(db, ct);
            var now = DateTime.UtcNow;

            settings.AccessTokenProtected = _protector.Protect(token.AccessToken);
            settings.AccessTokenExpiresUtc = now.AddSeconds(token.ExpiresIn - 60);
            settings.RefreshTokenProtected = token.RefreshToken is null ? settings.RefreshTokenProtected : _protector.Protect(token.RefreshToken);
            settings.RefreshTokenExpiresUtc = token.RefreshTokenExpiresIn > 0 ? now.AddSeconds(token.RefreshTokenExpiresIn) : settings.RefreshTokenExpiresUtc;
            settings.ConnectedUtc = now;
            settings.UpdatedUtc = now;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Connected to eBay {Environment}", _opt.Environment);
            return null;
        }
        catch (EbayApiException ex)
        {
            logger.LogError(ex, "eBay token exchange failed");
            return ex.Message;
        }
    }

    /// <summary>A valid user access token, refreshed when it's about to expire.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var settings = await db.EbaySettings.FirstOrDefaultAsync(s => s.Environment == _opt.Environment, ct);

            if (settings?.RefreshTokenProtected is null)
                throw new EbayApiException("Not connected to eBay. Open eBay Settings and click Connect.");

            if (settings.AccessTokenProtected is not null && settings.AccessTokenExpiresUtc > DateTime.UtcNow)
                return _protector.Unprotect(settings.AccessTokenProtected);

            if (settings.RefreshTokenExpiresUtc is { } exp && exp <= DateTime.UtcNow)
                throw new EbayApiException("Your eBay sign-in has expired. Open eBay Settings and click Connect again.");

            var token = await RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _protector.Unprotect(settings.RefreshTokenProtected),
                ["scope"] = string.Join(' ', EbayOptions.Scopes)
            }, ct);

            settings.AccessTokenProtected = _protector.Protect(token.AccessToken);
            settings.AccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(token.ExpiresIn - 60);
            settings.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            return token.AccessToken;
        }
        catch (CryptographicException)
        {
            throw new EbayApiException("Stored eBay tokens can't be decrypted (the app's data-protection keys changed). Click Connect again.");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.EbaySettings.FirstOrDefaultAsync(s => s.Environment == _opt.Environment, ct);
        if (settings is null) return;

        settings.AccessTokenProtected = null;
        settings.AccessTokenExpiresUtc = null;
        settings.RefreshTokenProtected = null;
        settings.RefreshTokenExpiresUtc = null;
        settings.ConnectedUtc = null;
        settings.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The settings row for the configured environment, created with defaults if missing.</summary>
    public async Task<EbaySettings> GetOrCreateSettingsAsync(AppDbContext db, CancellationToken ct = default)
    {
        var settings = await db.EbaySettings.FirstOrDefaultAsync(s => s.Environment == _opt.Environment, ct);
        if (settings is not null) return settings;

        settings = new EbaySettings { Environment = _opt.Environment, UpdatedUtc = DateTime.UtcNow };
        db.EbaySettings.Add(settings);
        await db.SaveChangesAsync(ct);
        return settings;
    }

    /// <summary>
    /// An application access token (client credentials grant) for public APIs such as Browse.
    /// Needs only the app keys, not a signed-in user. Cached until shortly before it expires.
    /// </summary>
    public async Task<string> GetApplicationTokenAsync(string scope, CancellationToken ct = default)
    {
        if (!_opt.IsConfigured)
            throw new EbayApiException("eBay isn't configured. Set Ebay:ClientId, Ebay:ClientSecret and Ebay:RuName (see README).");

        if (_appTokens.TryGetValue(scope, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
            return cached.Token;

        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = scope
        }, ct);

        _appTokens[scope] = (token.AccessToken, DateTime.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 120)));
        return token.AccessToken;
    }

    private async Task<TokenResponse> RequestTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, _opt.ApiBaseUrl + "identity/v1/oauth2/token")
        {
            Content = new FormUrlEncodedContent(form)
        };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_opt.ClientId}:{_opt.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new EbayApiException($"eBay sign-in failed ({(int)response.StatusCode}): {EbayApiException.Describe(body)}", (int)response.StatusCode);

        return System.Text.Json.JsonSerializer.Deserialize<TokenResponse>(body)
               ?? throw new EbayApiException("eBay returned an empty token response.");
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("refresh_token_expires_in")] public int RefreshTokenExpiresIn { get; set; }
    }
}
