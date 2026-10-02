namespace Nordicandia.Server.State;

/// <summary>
/// Character progression rules recovered from the client's <c>Game.Calculator</c>
/// (constants from <c>GameAttributes.Constants.ExpNeededToLevel_*</c>: add 350, mult 20,
/// power 1.7). Experience is cumulative: level L is reached once total experience is at
/// least <c>CalculateXpNeededForSpecifiedLevel(L) = L &lt;= 1 ? 0 : 350 + 20 * L^1.7</c>.
///
/// <c>Calculator.CalculateLevelGains(level, exp)</c> inverts that in closed form,
/// <c>floor(round(((exp - 350) / 20)^(1 / 1.7) - level, 6))</c>, and <c>Character.LevelUp</c>
/// applies the result with no level cap. The server mirrors it exactly; an earlier
/// invented cap of 200 pinned high characters (e.g. 458k XP = level 366) at 200 on every
/// sync/login. The server persists only total experience, so the level is always
/// derived, never stored independently.
///
/// The curve now lives in <see cref="Nordicandia.Simulation.Progression"/> so the
/// headless simulation and the server share one implementation; this type is kept as the
/// stable call site used across the server.
/// </summary>
public static class Progression
{
    public static double ExperienceForLevel(int level)
        => Nordicandia.Simulation.Progression.ExperienceForLevel(level);

    public static int LevelForExperience(double experience)
        => Nordicandia.Simulation.Progression.LevelForExperience(experience);
}
