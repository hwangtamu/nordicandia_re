namespace Nordicandia.Server.WebApi;

/// <summary>Login payload for the browser client. <c>Identity</c> is an email or username.</summary>
public sealed record WebSessionRequest(string Identity, string Password, bool CreateAccount = false);

public sealed record WebSessionResponse(Guid UserId, string DisplayName, string AuthToken, DateTime ExpiresUtc);

public sealed record WebCharacterSummary(
    Guid CharacterId, string DisplayName, int Class, int Race, int GameMode, double Level, bool Hardcore);

public sealed record WebItem(Guid Id, string Name, int Slot, int DefinitionIntegerId, int Rarity);

/// <summary>Inventory row with resolved equipment stats.</summary>
public sealed record WebItemDetail(
    Guid Id, string Name, int Slot, int Rarity, int EquipSlot, bool Equipped,
    double Offense, double Defense, double Recovery);

public sealed record WebInventory(
    IReadOnlyList<WebItemDetail> Items, double Offense, double Defense, double Recovery);

/// <summary>A drop rolled by combat, resolved into display/testable stats.</summary>
public sealed record LootDropView(
    string Name, int Slot, int Rarity, int Level, double Offense, double Defense, double Recovery);

/// <summary>Authoritative combat snapshot plus the loot generated since the previous call.</summary>
public sealed record WebCombatState(
    Nordicandia.Simulation.CombatSnapshot Combat, IReadOnlyList<LootDropView> Loot);

/// <summary>
/// Compact, authoritative snapshot for the web client. Deliberately not the full internal
/// character blob: only the fields the browser needs to render and to reconcile.
/// </summary>
public sealed record WebSnapshot(
    Guid CharacterId,
    string DisplayName,
    int Class,
    int Race,
    int GameMode,
    double Level,
    double Experience,
    double ExperienceForLevel,
    double ExperienceForNextLevel,
    int Silver,
    int Opals,
    double Offense,
    double Defense,
    double Recovery,
    long MonsterKills,
    IReadOnlyDictionary<int, double> Attributes,
    IReadOnlyList<WebItem> Equipped,
    int InventoryCount,
    IReadOnlyList<int> ActiveSkillIds,
    DateTime ServerTimeUtc);
