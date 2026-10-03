namespace Nordicandia.Server.WebApi;

/// <summary>
/// Offline-reward rule, now aligned with the recovered client code.
///
/// <c>Game.Calculator.CalculateIdleLevelsGained</c> (Android <c>0x02BA9DE8</c>) computes:
/// <c>totalXp = (seconds/60) * killsPerMinute * xpRateMultiplier * Monster.GetExperience(level)</c>,
/// then converts the total experience to levels with the shared curve. The constants are:
///   * <c>Constants.Offline_Base_Battle_Time_Minutes</c> = 720 (12h cap, ClientVerified)
///   * <c>Constants.Offline_Base_Battle_Xp_Multiplier</c> = 0.15 (ClientVerified)
///   * <c>Constants.Offline_Base_Battle_Xp_Multiplier_Potion</c> = 2.0 (with an active potion)
///   * the loot-side kill count is clamped to 20000
/// The client's per-character <c>killsPerMinute</c> comes from a character attribute
/// (<c>WindowWelcomeBack.Start</c> -> <c>(int)(attr * clamp(idle, 8, max))</c>); the exact
/// attribute id was not decoded, so the rate here is Provisional.
/// </summary>
public static class OfflineRewards
{
    public const int MaxSeconds = 720 * 60;      // Offline_Base_Battle_Time_Minutes = 720
    public const double XpMultiplier = 0.15;      // Offline_Base_Battle_Xp_Multiplier
    public const int MaxKills = 20000;            // caller clamp in GenerateItems
    public const double KillsPerMinute = 30.0;    // Provisional: middle of the 8..max client clamp range

    public static (double Experience, int EligibleSeconds) Compute(int level, long awaySeconds)
    {
        var seconds = (int)Math.Clamp(awaySeconds, 0, MaxSeconds);
        var kills = Math.Min(seconds / 60.0 * KillsPerMinute, MaxKills);
        var xpPerKill = Nordicandia.Simulation.CombatModel.ExperienceReward(level);
        var exp = Math.Floor(kills * XpMultiplier * xpPerKill);
        return (exp, seconds);
    }
}
