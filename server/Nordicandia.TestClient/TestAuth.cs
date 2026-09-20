using SharedNet.Api;

namespace Nordicandia.TestClient;

/// <summary>
/// Smoke-test accounts. Uses the real email+password login path so the probes work when the
/// server runs in strict mode (unverified provider/device login disabled).
/// </summary>
public static class TestAuth
{
    public const string Password = "test-password-123";

    /// <summary>Registers a brand-new throwaway account and returns its login response.</summary>
    public static Task<LoginResponse> RegisterAndLoginAsync(ILoginServiceApi login, string prefix = null)
        => EnsureAccountAsync(login, $"{prefix ?? "test"}-{Guid.NewGuid():N}@nord.local");

    /// <summary>Registers the account on first use, then logs in on subsequent runs.</summary>
    public static async Task<LoginResponse> EnsureAccountAsync(ILoginServiceApi login, string email)
    {
        var response = await login.LoginWithEmailAsync(new LoginWithEmailRequest
        {
            Email = email,
            Password = Password,
            CreateAccount = true,
            ClientVersion = "1.9.3",
        });
        if (response?.Session?.AuthToken == null)
            throw new InvalidOperationException($"Email login was rejected for {email}");
        return response;
    }
}