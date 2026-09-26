using Grpc.Core;
using MagicOnion;
using MagicOnion.Server;
using SharedNet.Api;
using SharedNet.Dto;
using Nordicandia.Server.Auth;
using Nordicandia.Server.State;

namespace Nordicandia.Server.Services;

/// <summary>
/// Account authentication. Email/username logins are verified against a PBKDF2 password
/// credential. Provider logins (Steam/Google) are verified when the corresponding credential
/// is configured; otherwise they are rejected unless <c>NORD_AUTH_ALLOW_UNVERIFIED_PROVIDERS=1</c>
/// (the legacy trust-the-device behaviour, for local development only).
/// </summary>
public sealed class LoginService : ServiceBase<ILoginServiceApi>, ILoginServiceApi
{
    private static readonly bool AllowUnverifiedProviders =
        Env("NORD_AUTH_ALLOW_UNVERIFIED_PROVIDERS") is "1" or "true";
    private static readonly bool AllowRegistration =
        Env("NORD_ALLOW_REGISTRATION") is not ("0" or "false");
    // Optional login aliasing for private servers: lets one login identity land on
    // another identity's user row, so e.g. a desktop Steam login can share the same
    // characters/currency/items as a mobile device login without any client changes.
    // Format: "steam:76561198000000000=device:abc123;steam:999=device:xyz" (the left
    // side is "<kind>:<identity>" as computed below, the right side is the target
    // user key used for GetOrCreateUser). The real platform identity is still
    // recorded in LinkedAccounts on the resolved user.
    private static readonly Dictionary<string, string> LoginAliases = ParseAliases(Env("NORD_LOGIN_ALIAS"));

    private static Dictionary<string, string> ParseAliases(string raw)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in (raw ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var from = pair[..eq].Trim();
            var to = pair[(eq + 1)..].Trim();
            if (from.Length > 0 && to.Length > 0) map[from] = to;
        }
        return map;
    }
    private static readonly string SteamApiKey = Env("NORD_STEAM_WEBAPI_KEY");
    private static readonly string GoogleClientId = Env("NORD_GOOGLE_CLIENT_ID");
    private static readonly string GoogleClientSecret = Env("NORD_GOOGLE_CLIENT_SECRET");

    private static string Env(string name) => Environment.GetEnvironmentVariable(name)?.Trim() ?? string.Empty;

    private static UnaryResult<LoginResponse> Rejected() => UnaryResult.FromResult(new LoginResponse());

    public UnaryResult<LoginResponse> LoginWithStandaloneDeviceIdAsync(LoginWithStandaloneDeviceIdRequest req)
        => AllowUnverifiedProviders
            ? Login(req?.DeviceId, "device", SharedNet.Constants.LoginIdentityProvider.StandaloneDevice)
            : Rejected();

    public UnaryResult<LoginResponse> LoginWithSteamAsync(LoginWithSteamRequest req)
    {
        if (SteamApiKey.Length > 0 && !string.IsNullOrEmpty(req?.SteamTicket))
        {
            var steamId = SteamTicketVerifier.ValidateAsync(SteamApiKey, req.SteamTicket!).GetAwaiter().GetResult();
            return steamId != null ? Login(steamId, "steam", SharedNet.Constants.LoginIdentityProvider.Steam) : Rejected();
        }
        return AllowUnverifiedProviders
            ? Login(req?.DeviceId ?? req?.SteamTicket, "steam", SharedNet.Constants.LoginIdentityProvider.Steam)
            : Rejected();
    }

    public UnaryResult<LoginResponse> LoginWithGooglePlayAsync(LoginWithGooglePlayRequest req)
    {
        if (GoogleClientId.Length > 0 && GoogleClientSecret.Length > 0 && !string.IsNullOrEmpty(req?.ServerAuthCode))
        {
            var subject = GoogleTokenVerifier
                .ValidateServerAuthCodeAsync(GoogleClientId, GoogleClientSecret, req.ServerAuthCode!)
                .GetAwaiter().GetResult();
            return subject != null ? Login(subject, "google", SharedNet.Constants.LoginIdentityProvider.GooglePlay) : Rejected();
        }
        return AllowUnverifiedProviders
            ? Login(req?.DeviceId ?? req?.ServerAuthCode, "google", SharedNet.Constants.LoginIdentityProvider.GooglePlay)
            : Rejected();
    }

    public UnaryResult<LoginResponse> LoginWithGameCenterAsync(LoginWithGameCenterRequest req)
        => AllowUnverifiedProviders
            ? Login(req?.PlayerId ?? req?.DeviceId, "gc", SharedNet.Constants.LoginIdentityProvider.GameCenter)
            : Rejected();

    public UnaryResult<LoginResponse> LoginWithEmailAsync(LoginWithEmailRequest req)
    {
        var email = req?.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (email.Length == 0 || string.IsNullOrEmpty(req?.Password)) return Rejected();
        var identity = "email:" + email;
        if (req.CreateAccount == true && !GameStore.Instance.HasCredential(identity))
        {
            if (!AllowRegistration) return Rejected();
            var created = GameStore.Instance.RegisterCredential(identity, req.Password!, email);
            GameStore.Instance.LinkAccount(created.UserId, SharedNet.Constants.LoginIdentityProvider.Game, identity, null, email);
            return UnaryResult.FromResult(BuildLogin(created));
        }
        var user = GameStore.Instance.VerifyCredential(identity, req.Password!);
        if (user == null) return Rejected();
        GameStore.Instance.LinkAccount(user.UserId, SharedNet.Constants.LoginIdentityProvider.Game, identity, null, email);
        return UnaryResult.FromResult(BuildLogin(user));
    }

    public UnaryResult<LoginResponse> LoginWithUsernameAsync(LoginWithUsernameRequest req)
    {
        var username = req?.Username?.Trim() ?? string.Empty;
        if (username.Length == 0 || string.IsNullOrEmpty(req?.Password)) return Rejected();
        var identity = "username:" + username.ToLowerInvariant();
        if (req.CreateAccount == true && !GameStore.Instance.HasCredential(identity))
        {
            if (!AllowRegistration) return Rejected();
            var created = GameStore.Instance.RegisterCredential(identity, req.Password!, username);
            GameStore.Instance.LinkAccount(created.UserId, SharedNet.Constants.LoginIdentityProvider.Game, identity, username, null);
            return UnaryResult.FromResult(BuildLogin(created));
        }
        var user = GameStore.Instance.VerifyCredential(identity, req.Password!);
        if (user == null) return Rejected();
        GameStore.Instance.LinkAccount(user.UserId, SharedNet.Constants.LoginIdentityProvider.Game, identity, username, null);
        return UnaryResult.FromResult(BuildLogin(user));
    }

    public UnaryResult<LoginResponse> LoginWithAdminAsync(LoginWithAdminRequest req)
        // The client carries no admin secret, so this is only available in compat mode.
        => AllowUnverifiedProviders ? Login(req?.UserId.ToString(), "admin") : Rejected();

    public UnaryResult<LoginResponse> LoginWithRefreshTokenAsync(LoginWithRefreshAuthTokenRequest req)
    {
        var user = GameStore.Instance.FindUserByRefreshToken(req?.RefreshToken);
        if (user == null) return Rejected();
        return UnaryResult.FromResult(BuildLogin(user));
    }

    private static UnaryResult<LoginResponse> Login(string identity, string kind,
        SharedNet.Constants.LoginIdentityProvider? platform = null)
    {
        if (string.IsNullOrEmpty(identity)) identity = "guest-" + Guid.NewGuid().ToString("N");
        var loginKey = kind + ":" + identity;
        var userKey = LoginAliases.TryGetValue(loginKey, out var target) ? target : loginKey;
        var user = GameStore.Instance.GetOrCreateUser(userKey);
        Console.WriteLine($"[Login] {loginKey} -> user {user.UserId} ({userKey})");
        // The client checks GetUserAccountData.LinkedAccounts after login and shows the
        // account as "unregistered" when it is empty, so link the identity that was used.
        if (platform is { } p)
            GameStore.Instance.LinkAccount(user.UserId, p, loginKey, loginKey,
                kind is "email" or "username" ? identity : null);
        return UnaryResult.FromResult(BuildLogin(user));
    }

    internal static LoginResponse BuildLogin(UserDto user) => new LoginResponse
    {
        User = user,
        Session = GameStore.Instance.CreateSession(user.UserId),
        ServerTime = DateTime.UtcNow,
    };

    public UnaryResult<GetNonceResponse> GetNonce(GetNonceRequest req)
        => UnaryResult.FromResult(new GetNonceResponse { Nonce = Guid.NewGuid().ToString("N") });

    public UnaryResult<GetUserAccountDataResponse> GetUserAccountData(GetUserAccountDataRequest req)
    {
        var owner = GameStore.Instance.RequireUser(Context.CallContext.RequestHeaders.GetValue("authorization"));
        var response = Defaults.Create<GetUserAccountDataResponse>();
        response.AccountData = GameStore.Instance.GetAccountData(owner);
        response.LinkedAccounts = new UserLinkedAccountListDto { LinkedAccounts = GameStore.Instance.GetLinkedAccounts(owner) };
        return UnaryResult.FromResult(response);
    }

    public UnaryResult<KeepAliveResponse> KeepAlive(KeepAliveRequest req)
        => UnaryResult.FromResult(new KeepAliveResponse { ServerTime = DateTime.UtcNow });

    public UnaryResult<SendAnalyticsResponse> SendAnalytics(SendAnalyticsRequest req)
        => UnaryResult.FromResult(new SendAnalyticsResponse());

    public UnaryResult<ForgotPasswordResponse> ForgotPassword(ForgotPasswordRequest req)
        => UnaryResult.FromResult(new ForgotPasswordResponse());

    /// <summary>
    /// Password change for the account's own email identity. The real service mails a
    /// one-time code via ForgotPassword; a private server has no mail path, so this
    /// instead requires an authenticated caller whose account owns the credential —
    /// which cannot be abused by a third party and needs no out-of-band delivery.
    /// </summary>
    public UnaryResult<ChangeEmailPasswordResponse> ChangeEmailPassword(ChangeEmailPasswordRequest req)
    {
        var email = (req?.Email ?? string.Empty).Trim().ToLowerInvariant();
        var owner = GameStore.Instance.RequireUser(Context.CallContext.RequestHeaders.GetValue("authorization"));
        var identity = "email:" + email;
        if (email.Length == 0 || string.IsNullOrEmpty(req?.NewPassword) || req.NewPassword != req.NewPasswordRepeat
            || !GameStore.Instance.CredentialBelongsTo(identity, owner)
            || !GameStore.Instance.UpdateCredentialPassword(identity, req.NewPassword))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Password change was rejected"));

        return UnaryResult.FromResult(new ChangeEmailPasswordResponse());
    }
}