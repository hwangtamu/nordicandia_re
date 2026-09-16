namespace Nordicandia.Server.State;

/// <summary>
/// Character progression rules recovered from the client's <c>Game.Calculator</c>:
/// <c>CalculateXpNeededForSpecifiedLevel(level) = level &lt;= 1 ? 0 : 350 + 20 * level^1.7</c>
/// and <c>CalculateXpNeededToLevelUp(level) = CalculateXpNeededForSpecifiedLevel(level + 1)</c>.
/// The server persists only total experience, so the level is always derived, never stored
/// independently.
/// </summary>
public static class Progression
{
    public const int MaxLevel = 200;
    private const double Add = 350.0;
    private const double Mult = 20.0;
    private const double Power = 1.7;

    public static double ExperienceForLevel(int level)
        => level <= 1 ? 0.0 : Add + Mult * Math.Pow(level, Power);

    public static int LevelForExperience(double experience)
    {
        if (double.IsNaN(experience) || experience <= 0) return 1;
        var level = 1;
        while (level < MaxLevel && ExperienceForLevel(level + 1) <= experience) level++;
        return level;
    }
}
