using System.Text.Json;

namespace Nordicandia.Server.Auth;

/// <summary>
/// Validates a Steam auth session ticket via the Steamworks Web API. Requires a Web API key
/// with access to appid 1503790 (publisher key); ordinary keys are rejected by Steam for
/// apps they do not control. Returns the SteamID64 on success, null otherwise.
/// </summary>
public static class SteamTicketVerifier
{
    private const int AppId = 1503790;
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://api.steampowered.com/") };

    public static async Task<string> ValidateAsync(string webApiKey, string ticket, CancellationToken ct = default)
    {
        try
        {
            var url = $"ISteamUserAuth/AuthenticateUserTicket/v1/?key={Uri.EscapeDataString(webApiKey)}"
                    + $"&appid={AppId}&ticket={Uri.EscapeDataString(ticket)}";
            using var response = await Http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("response", out var responseElement)) return null;
            if (!responseElement.TryGetProperty("params", out var p)) return null;
            if (!p.TryGetProperty("result", out var result) || !string.Equals(result.GetString(), "OK", StringComparison.Ordinal)) return null;
            return p.TryGetProperty("steamid", out var steamId) ? steamId.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Exchanges a Google Play Games <c>serverAuthCode</c> for tokens and validates the resulting
/// ID token against the configured client id. This only works with an OAuth client whose id
/// and secret belong to the private-server operator (the shipped game client uses the
/// original developer's client, whose secret is not available).
/// </summary>
public static class GoogleTokenVerifier
{
    private static readonly HttpClient Http = new();

    public static async Task<string> ValidateServerAuthCodeAsync(string clientId, string clientSecret, string serverAuthCode, CancellationToken ct = default)
    {
        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = serverAuthCode,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = string.Empty,
            });
            using var response = await Http.PostAsync("https://oauth2.googleapis.com/token", form, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("id_token", out var idToken)) return null;
            return await ValidateIdTokenAsync(clientId, idToken.GetString(), ct);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Validates an ID token's audience, issuer and expiry, returning the subject.</summary>
    public static async Task<string> ValidateIdTokenAsync(string clientId, string idToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(idToken)) return null;
        try
        {
            using var response = await Http.GetAsync("https://oauth2.googleapis.com/tokeninfo?id_token=" + Uri.EscapeDataString(idToken), ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var aud = root.TryGetProperty("aud", out var a) ? a.GetString() : null;
            if (!string.Equals(aud, clientId, StringComparison.Ordinal)) return null;
            var iss = root.TryGetProperty("iss", out var i) ? i.GetString() : null;
            if (iss is not ("accounts.google.com" or "https://accounts.google.com")) return null;
            if (!root.TryGetProperty("exp", out var exp) || !long.TryParse(exp.GetString(), out var expUnix)
                || DateTimeOffset.FromUnixTimeSeconds(expUnix) <= DateTimeOffset.UtcNow) return null;
            return root.TryGetProperty("sub", out var sub) ? sub.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}