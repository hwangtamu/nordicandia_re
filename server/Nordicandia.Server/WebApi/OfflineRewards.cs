namespace Nordicandia.Server.WebApi;

/// <summary>
/// Provisional offline-reward rule for the web slice.
///
/// The native client measures its own away window and posts the result to
/// <c>ClaimCharacterOfflineRewards</c>. The web server computes the reward instead so it is
/// authoritative and idempotent, using the same recovered experience curve: one kill's worth
/// (<see cref="Nordicandia.Simulation.CombatModel.ExperienceReward"/>) per
/// <see cref="SecondsPerKill"/> away, capped at <see cref="MaxSeconds"/> (8h).
/// </summary>
public static class OfflineRewards
{
    public const int MaxSeconds = 8 * 3600;
    public const double SecondsPerKill = 30.0;

    public static (double Experience, int EligibleSeconds) Compute(int level, long awaySeconds)
    {
        var seconds = (int)Math.Clamp(awaySeconds, 0, MaxSeconds);
        var exp = Math.Floor(seconds / SecondsPerKill * Nordicandia.Simulation.CombatModel.ExperienceReward(level));
        return (exp, seconds);
    }
}
