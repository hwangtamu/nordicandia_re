using Nordicandia.Simulation;

/// <summary>
/// W04: the deterministic dungeon layout generator (rooms + corridors + spawn anchors).
/// </summary>
static class W04MapLayoutTests
{
    public static void Run()
    {
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }

        var a = MapLayout.Generate(21, 21, 5, 12345UL, "dungeon_grass");
        var b = MapLayout.Generate(21, 21, 5, 12345UL, "dungeon_grass");
        Check(a.ToRows().SequenceEqual(b.ToRows()),
            "W04: the layout is deterministic for a fixed seed");

        Check(a.RoomCenters.Count >= 3, $"W04: the layout has several rooms ({a.RoomCenters.Count})");
        Check(a.SpawnAnchors.Count == a.RoomCenters.Count
            && a.SpawnAnchors.All(s => a.IsFloor(s.X, s.Z)),
            "W04: every spawn anchor sits on a floor tile");
        Check(new[] { a.SpawnAnchors[0], a.SpawnAnchors[^1] }
            .Select(s => a.World(s.X, s.Z, CombatInstance.ArenaHalf))
            .All(w => Math.Abs(w.X) <= CombatInstance.ArenaHalf && Math.Abs(w.Z) <= CombatInstance.ArenaHalf),
            "W04: room anchors map inside the arena bounds");

        var floor = a.ToRows().Sum(r => r.Count(c => c == '#'));
        Check(floor > 40, $"W04: rooms and corridors carve a usable floor ({floor} tiles)");

        var c = MapLayout.Generate(21, 21, 5, 999UL, "dungeon_grass");
        Check(!a.ToRows().SequenceEqual(c.ToRows()), "W04: different seeds produce different layouts");
        Check(a.Theme == "dungeon_grass", "W04: the layout carries its world theme");

        // W02: A* pathfinding between rooms (walls block, corridors connect).
        var from = a.RoomCenters[0];
        var to = a.RoomCenters[^1];
        var path = a.FindPath(from, to);
        Check(path.Count > 0, $"W02: A* finds a path across the dungeon ({path.Count} steps)");
        Check(path.All(p => a.IsFloor(p.X, p.Z)), "W02: every path cell is a floor tile");
        Check(a.FindPath((-1, -1), to).Count == 0, "W02: A* returns no path from a wall cell");
    }
}
