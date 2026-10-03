namespace Nordicandia.Simulation;

/// <summary>
/// W04: a deterministic dungeon layout (rooms joined by corridors) generated from a seed. The
/// client assembles its map with the shipped theme kit; the layout supplies the floor grid, the
/// room centres and the spawn anchors the combat instance uses for its packs.
///
/// The client's own maps come from a third-party generator (DungeonGridFlowPolygonDungeon /
/// PathSeeker), so this is a faithful *shape* (rooms + corridors + spawn areas) built on the
/// exported kits, not a port of that plugin.
/// </summary>
public sealed class MapLayout
{
    public int Width { get; }
    public int Height { get; }
    public string Theme { get; }
    public IReadOnlyList<(int X, int Z)> RoomCenters { get; }
    /// <summary>Pack origin anchors (gamedata DungeonMonsterSpawnArea positions, in grid cells).</summary>
    public IReadOnlyList<(int X, int Z)> SpawnAnchors { get; }

    private readonly bool[] floor;

    private MapLayout(int width, int height, bool[] floor, List<(int X, int Z)> rooms, string theme)
    {
        Width = width;
        Height = height;
        this.floor = floor;
        Theme = theme;
        RoomCenters = rooms;
        SpawnAnchors = rooms.ToList();
    }

    public bool IsFloor(int x, int z)
        => x >= 0 && x < Width && z >= 0 && z < Height && floor[z * Width + x];

    /// <summary>Map a grid cell to arena world coordinates (centre of the cell).</summary>
    public (double X, double Z) World(int gx, int gz, double arenaHalf)
    {
        var tile = 2 * arenaHalf / Math.Max(Width, Height);
        return ((gx - Width / 2.0 + 0.5) * tile, (gz - Height / 2.0 + 0.5) * tile);
    }

    /// <summary>World position -> nearest grid cell.</summary>
    public (int X, int Z) Cell(double x, double z, double arenaHalf)
    {
        var tile = 2 * arenaHalf / Math.Max(Width, Height);
        return ((int)Math.Floor(x / tile + Width / 2.0), (int)Math.Floor(z / tile + Height / 2.0));
    }

    private static readonly (int X, int Z)[] Neighbours = { (-1, 0), (1, 0), (0, -1), (0, 1) };

    /// <summary>W02: A* over the floor grid (4-connected). Returns cell centres after the start,
    /// or an empty list when the target is unreachable (e.g. off the floor).</summary>
    public List<(int X, int Z)> FindPath((int X, int Z) from, (int X, int Z) to)
    {
        var result = new List<(int X, int Z)>();
        if (!IsFloor(from.X, from.Z) || !IsFloor(to.X, to.Z)) return result;
        var size = Width * Height;
        var cameFrom = new int[size];
        var gScore = new int[size];
        var fScore = new int[size];
        for (var i = 0; i < size; i++) { cameFrom[i] = -1; gScore[i] = int.MaxValue; fScore[i] = int.MaxValue; }
        int Index(int x, int z) => z * Width + x;
        int H(int x, int z) => Math.Abs(x - to.X) + Math.Abs(z - to.Z);
        var start = Index(from.X, from.Z);
        var goal = Index(to.X, to.Z);
        gScore[start] = 0;
        fScore[start] = H(from.X, from.Z);
        var open = new List<int> { start };
        while (open.Count > 0)
        {
            var bestAt = 0;
            for (var i = 1; i < open.Count; i++)
                if (fScore[open[i]] < fScore[open[bestAt]]) bestAt = i;
            var current = open[bestAt];
            if (current == goal)
            {
                var node = current;
                while (node != start && node >= 0)
                {
                    result.Add((node % Width, node / Width));
                    node = cameFrom[node];
                }
                result.Reverse();
                return result;
            }
            open.RemoveAt(bestAt);
            var cx = current % Width;
            var cz = current / Width;
            foreach (var (dx, dz) in Neighbours)
            {
                var nx = cx + dx;
                var nz = cz + dz;
                if (!IsFloor(nx, nz)) continue;
                var neighbour = Index(nx, nz);
                var tentative = gScore[current] + 1;
                if (tentative >= gScore[neighbour]) continue;
                cameFrom[neighbour] = current;
                gScore[neighbour] = tentative;
                fScore[neighbour] = tentative + H(nx, nz);
                if (!open.Contains(neighbour)) open.Add(neighbour);
            }
        }
        return result;
    }

    /// <summary>Compact ASCII rows ('#' floor, '.' void), used by the client to assemble the kit.</summary>
    public string[] ToRows()
    {
        var rows = new string[Height];
        for (var z = 0; z < Height; z++)
        {
            var chars = new char[Width];
            for (var x = 0; x < Width; x++) chars[x] = IsFloor(x, z) ? '#' : '.';
            rows[z] = new string(chars);
        }
        return rows;
    }

    /// <summary>Generate a room-and-corridor graph. Deterministic for a fixed seed.</summary>
    public static MapLayout Generate(int width, int height, int roomCount, ulong seed, string theme = "dungeon_default")
    {
        width = Math.Max(9, width);
        height = Math.Max(9, height);
        var rng = new CombatRandom(seed);
        var floor = new bool[width * height];
        var rects = new List<(int X0, int Z0, int X1, int Z1)>();
        var rooms = new List<(int X, int Z)>();

        for (var i = 0; i < roomCount; i++)
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var rw = 3 + (int)(rng.NextDouble() * 5);
                var rh = 3 + (int)(rng.NextDouble() * 5);
                var rx = 1 + (int)(rng.NextDouble() * Math.Max(1, width - rw - 2));
                var rz = 1 + (int)(rng.NextDouble() * Math.Max(1, height - rh - 2));
                var rect = (X0: rx, Z0: rz, X1: rx + rw, Z1: rz + rh);
                if (rect.X1 >= width - 1 || rect.Z1 >= height - 1) continue;
                if (rects.Any(r => Overlaps(r, rect, 1))) continue;
                rects.Add(rect);
                rooms.Add(((rect.X0 + rect.X1) / 2, (rect.Z0 + rect.Z1) / 2));
                for (var x = rect.X0; x <= rect.X1; x++)
                    for (var z = rect.Z0; z <= rect.Z1; z++)
                        floor[z * width + x] = true;
                break;
            }
        }
        if (rooms.Count == 0)
        {
            // Guarantee at least one room so a caller always has a spawn anchor.
            for (var x = width / 2 - 2; x <= width / 2 + 2; x++)
                for (var z = height / 2 - 2; z <= height / 2 + 2; z++)
                    floor[z * width + x] = true;
            rooms.Add((width / 2, height / 2));
        }

        for (var i = 1; i < rooms.Count; i++) CarveCorridor(floor, width, rooms[i - 1], rooms[i]);
        return new MapLayout(width, height, floor, rooms, theme);
    }

    private static bool Overlaps((int X0, int Z0, int X1, int Z1) a, (int X0, int Z0, int X1, int Z1) b, int pad)
        => a.X0 - pad <= b.X1 && a.X1 + pad >= b.X0 && a.Z0 - pad <= b.Z1 && a.Z1 + pad >= b.Z0;

    private static void CarveCorridor(bool[] floor, int width, (int X, int Z) from, (int X, int Z) to)
    {
        var x = from.X;
        var z = from.Z;
        while (x != to.X)
        {
            floor[z * width + x] = true;
            x += x < to.X ? 1 : -1;
        }
        while (z != to.Z)
        {
            floor[z * width + x] = true;
            z += z < to.Z ? 1 : -1;
        }
        floor[z * width + x] = true;
    }
}
