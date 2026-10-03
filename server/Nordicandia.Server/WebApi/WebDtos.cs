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
    double Offense, double Defense, double Recovery, IReadOnlyList<string> Affixes);

public sealed record WebInventory(
    IReadOnlyList<WebItemDetail> Items, double Offense, double Defense, double Recovery);

/// <summary>A drop rolled by combat, resolved into display/testable stats.</summary>
public sealed record LootDropView(
    string Name, int Slot, int Rarity, int Level, double Offense, double Defense, double Recovery,
    IReadOnlyList<string> Affixes);

/// <summary>Authoritative combat snapshot plus the loot generated since the previous call.</summary>
public sealed record WebCombatState(
    Nordicandia.Simulation.CombatSnapshot Combat, IReadOnlyList<LootDropView> Loot,
    WebMapLayout? Map = null);

/// <summary>W04: the dungeon layout the client assembles with the world theme kit.</summary>
public sealed record WebMapLayout(int Width, int Height, string Theme, IReadOnlyList<string> Rows,
    IReadOnlyList<WebMapAnchor> SpawnAnchors);

public sealed record WebMapAnchor(double X, double Z);

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

/// <summary>Attribute allocation view: remaining points, the allocated values per attribute, and
/// the engine's synthesised totals.</summary>
public sealed record WebAttributes(
    double Available,
    double StrengthAllocated, double DexterityAllocated, double IntelligenceAllocated, double VitalityAllocated,
    double ConstitutionAllocated, double AgilityAllocated, double MindpowerAllocated,
    double Strength, double Dexterity, double Intelligence, double Vitality, double Constitution,
    double Agility, double Mindpower);

/// <summary>Attribute allocation request (pending deltas, matching the client's preview).</summary>
public sealed record WebAllocateAttributesRequest(
    double Strength = 0, double Dexterity = 0, double Intelligence = 0, double Vitality = 0,
    double Constitution = 0, double Agility = 0, double Mindpower = 0);

public sealed record NpcBuyRequest(Guid CatalogItemId, bool UseOpals = false);

public sealed record NpcSetTradeRequest(Guid OfferItemId);

/// <summary>Equipped loadout: up to 6 active skill names and 3 passive (mastery) skill names.</summary>
public sealed record WebLoadoutRequest(List<string> Active, List<string> Passive);

/// <summary>Offline reward preview/claim: the raw away window, the capped eligible seconds,
/// the experience granted, and whether a claim would grant anything.</summary>
public sealed record OfflineView(long AwaySeconds, int EligibleSeconds, double Experience, bool Claimable);

/// <summary>One Aesir blessing: type id, name, whether it is active, remaining seconds and the
/// (Provisional) effect description.</summary>
public sealed record BlessingState(int Type, string Name, bool Active, double SecondsRemaining, string Effect);

/// <summary>A purchasable offering size (cost in opals and ClientVerified duration).</summary>
public sealed record OfferingSize(int Size, string Name, int OpalCost, long DurationSeconds);

/// <summary>Blessings window: active blessings, the offerable sizes and the character's opals.</summary>
public sealed record BlessingsView(IReadOnlyList<BlessingState> Active, IReadOnlyList<OfferingSize> Sizes, int Opals);

/// <summary>Niflheim portal state: whether a run is active, whether the bag holds a portal, the id
/// of that portal, the cleared-run count and the active run's pack goal.</summary>
public sealed record WebPortalState(bool InNiflheim, bool HasPortal, Guid PortalItemId, int RunsCleared, int Packs = 0);

/// <summary>Offering purchase request (type 1..4, size 1..4).</summary>
public sealed record WebOfferingRequest(int Type, int Size);

/// <summary>Portal enter request (the portal item to consume).</summary>
public sealed record WebPortalEnterRequest(Guid ItemId);
