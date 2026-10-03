namespace Nordicandia.Server.WebApi;

/// <summary>
/// Offline-reward rule, aligned with the recovered client code.
///
/// <c>Game.Calculator.CalculateIdleLevelsGained</c> (Android <c>0x02BA9DE8</c>) computes:
/// <c>totalXp = (seconds/60) * killsPerMinute * xpRateMultiplier * Monster.GetExperience(level)</c>,
/// then converts the total experience to levels with the shared curve. The constants are:
///   * <c>Constants.Offline_Base_Battle_Time_Minutes</c> = 720 (12h cap, ClientVerified)
///   * <c>Constants.Offline_Base_Battle_Xp_Multiplier</c> = 0.15 (ClientVerified)
///   * <c>Constants.Offline_Base_Battle_Xp_Multiplier_Potion</c> = 2.0 (with an active potion)
///   * the loot-side kill count is clamped to 20000
///
/// The per-character rate is recovered from <c>WindowWelcomeBack.Start</c>:
/// <c>killsPerMinute = truncate(Offline_Battle_Efficiency_Multiplier *
/// clamp(1.5 * secondHighestReachedTier, 10, 45))</c>. <c>Offline_Battle_Efficiency_Multiplier</c>
/// (id 565) resolves to <c>Base_Stamina_Multiplier</c> (1.5). The web bridge passes the
/// character's current world tier (attribute 8) as the tier input, defaulting to 1; the client
/// uses <c>Character.GetSecondHighestReachedWorldCheckpoint</c>, whose history the web slice does
/// not track, so the tier input remains an approximation even though the formula is not.
/// </summary>
public static class OfflineRewards
{
    public const int MaxSeconds = 720 * 60;      // Offline_Base_Battle_Time_Minutes = 720
    public const double XpMultiplier = 0.15;      // Offline_Base_Battle_Xp_Multiplier
    public const int MaxKills = 20000;            // caller clamp in GenerateItems
    public const double BaseBattleEfficiency = 1.5; // Base_Stamina_Multiplier fallback

    /// <summary>Recovered per-minute kill rate. <paramref name="tier"/> is the (approximated)
    /// second-highest reached world tier; the 1.5x scaling and 10..45 clamp are ClientVerified.</summary>
    public static double KillsPerMinute(double efficiency, int tier)
    {
        var eff = double.IsFinite(efficiency) && efficiency > 0 ? efficiency : BaseBattleEfficiency;
        var scaled = 1.5 * Math.Max(0, tier);
        return Math.Truncate(eff * Math.Clamp(scaled, 10, 45));
    }

    public static (double Experience, int EligibleSeconds) Compute(
        int level, long awaySeconds, double efficiency = BaseBattleEfficiency, int tier = 1)
    {
        var seconds = (int)Math.Clamp(awaySeconds, 0, MaxSeconds);
        var kills = Math.Min(seconds / 60.0 * KillsPerMinute(efficiency, tier), MaxKills);
        var xpPerKill = Nordicandia.Simulation.CombatModel.ExperienceReward(level);
        var exp = Math.Floor(kills * XpMultiplier * xpPerKill);
        return (exp, seconds);
    }
}
