using Grpc.Core;
using MagicOnion;
using SharedNet.Api;
using SharedNet.Constants;
using SharedNet.Dto;
using Nordicandia.Server.Auth;
using Nordicandia.Server.State;

namespace Nordicandia.Server.Services;

public sealed partial class UserLinkedAccountServiceApiImpl
{
    private static readonly bool AllowUnverifiedProviders =
        (Environment.GetEnvironmentVariable("NORD_AUTH_ALLOW_UNVERIFIED_PROVIDERS") ?? "").Trim() is "1" or "true";
    private static readonly string SteamApiKey = (Environment.GetEnvironmentVariable("NORD_STEAM_WEBAPI_KEY") ?? "").Trim();
    private static readonly string GoogleClientId = (Environment.GetEnvironmentVariable("NORD_GOOGLE_CLIENT_ID") ?? "").Trim();
    private static readonly string GoogleClientSecret = (Environment.GetEnvironmentVariable("NORD_GOOGLE_CLIENT_SECRET") ?? "").Trim();

    private Guid Owner => GameStore.Instance.RequireUser(Context.CallContext.RequestHeaders.GetValue("authorization"));

    private UnaryResult<LinkAccountResponse> Link(LoginIdentityProvider platform, string platformUserId, string username, string email)
        => UnaryResult.FromResult(new LinkAccountResponse
        {
            LinkedAccount = GameStore.Instance.LinkAccount(Owner, platform, platformUserId, username, email),
        });

    public UnaryResult<LinkAccountResponse> LinkSteamAccount(LinkSteamAccountRequest req)
    {
        var ticket = req?.SteamTicket ?? string.Empty;
        var steamId = string.Empty;
        if (SteamApiKey.Length > 0 && ticket.Length > 0)
            steamId = SteamTicketVerifier.ValidateAsync(SteamApiKey, ticket).GetAwaiter().GetResult() ?? string.Empty;
        if (steamId.Length == 0)
        {
            if (!AllowUnverifiedProviders)
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Steam ticket could not be verified"));
            // Legacy: the Steam user id is embedded in the ticket but not trivially parseable,
            // so store a stable digest instead.
            steamId = ExtractSteamId(ticket);
        }
        return Link(LoginIdentityProvider.Steam, steamId, "steam:" + steamId, null);
    }

    public UnaryResult<LinkAccountResponse> LinkGoogleAccountAccount(LinkGoogleAccountRequest req)
    {
        var authKey = req?.AuthKey ?? string.Empty;
        var subject = string.Empty;
        if (GoogleClientId.Length > 0 && GoogleClientSecret.Length > 0 && authKey.Length > 0)
            subject = GoogleTokenVerifier
                .ValidateServerAuthCodeAsync(GoogleClientId, GoogleClientSecret, authKey)
                .GetAwaiter().GetResult() ?? string.Empty;
        if (subject.Length == 0)
        {
            if (!AllowUnverifiedProviders)
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Google auth code could not be verified"));
            subject = authKey;
        }
        return Link(LoginIdentityProvider.GooglePlay, subject, null, null);
    }

    public UnaryResult<LinkAccountResponse> LinkGameCenterAccount(LinkGameCenterAccountRequest req)
        => Link(LoginIdentityProvider.GameCenter, req?.PlayerId ?? string.Empty, null, null);

    public UnaryResult<RegisterGameAccountResponse> RegisterGameAccount(RegisterGameAccountRequest req)
    {
        var email = req?.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (email.Length == 0 || string.IsNullOrEmpty(req?.Password))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Email and password are required"));
        if (!string.Equals(req.Password, req.RepeatPassword, StringComparison.Ordinal))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Passwords do not match"));

        var identity = "email:" + email;
        GameStore.Instance.SetCredentialForUser(Owner, identity, req.Password!);
        var account = GameStore.Instance.LinkAccount(Owner, LoginIdentityProvider.Game, identity, null, email);
        return UnaryResult.FromResult(new RegisterGameAccountResponse { LinkedGameAccount = account });
    }

    public UnaryResult<ChangeLinkedAccountPasswordResponse> ChangeLinkedAccountPassword(ChangeLinkedAccountPasswordRequest req)
    {
        var platform = req?.Provider ?? LoginIdentityProvider.Unknown;
        var identity = GameStore.Instance.GetLinkedAccounts(Owner)
            .FirstOrDefault(a => a.Platform == platform)?.PlatformUserId;
        if (string.IsNullOrEmpty(identity) || string.IsNullOrEmpty(req?.NewPassword))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "No linked credential for provider"));
        if (!GameStore.Instance.HasCredential(identity))
            throw new RpcException(new Status(StatusCode.NotFound, "Account has no password"));
        GameStore.Instance.UpdateCredentialPassword(identity, req.NewPassword!);
        return UnaryResult.FromResult(new ChangeLinkedAccountPasswordResponse());
    }

    public UnaryResult<UnlinkAccountResponse> UnlinkSteamAccount(UnlinkSteamAccountRequest req)
    {
        GameStore.Instance.UnlinkAccount(Owner, LoginIdentityProvider.Steam);
        return UnaryResult.FromResult(new UnlinkAccountResponse());
    }

    public UnaryResult<UnlinkAccountResponse> UnlinkGoogleAccount(UnlinkGoogleAccountRequest req)
    {
        GameStore.Instance.UnlinkAccount(Owner, LoginIdentityProvider.GooglePlay);
        return UnaryResult.FromResult(new UnlinkAccountResponse());
    }

    public UnaryResult<UnlinkAccountResponse> UnlinkGameCenterAccount(UnlinkGameCenterAccountRequest req)
    {
        GameStore.Instance.UnlinkAccount(Owner, LoginIdentityProvider.GameCenter);
        return UnaryResult.FromResult(new UnlinkAccountResponse());
    }

    private static string ExtractSteamId(string ticket)
    {
        if (string.IsNullOrEmpty(ticket)) return string.Empty;
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ticket));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }
}