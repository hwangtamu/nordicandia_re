using System.Text.Json;

/// <summary>
/// C04: guards the per-skill behavior specs (tools/web-content/generated/skill_specs.json).
/// The anti-silent-mapping invariant: every one of the 93 skills has an explicit prototype
/// and an explicit behaviorGaps list. Gaps may only shrink by implementing the behavior,
/// never by editing the spec to hide them (the generator recomputes them from data).
/// </summary>
static class SkillSpecTests
{
    public static void Run()
    {
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }

        // Resolve the repo root by walking up from the test binary until the spec file is found.
        string? path = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "web-content", "generated", "skill_specs.json");
            if (File.Exists(candidate)) { path = candidate; break; }
        }
        Check(path is not null, "skill specs: spec file found under the repo root");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var specs = doc.RootElement.GetProperty("specs").EnumerateArray().ToList();
        Check(specs.Count == 93, $"skill specs: all 93 skills have a spec ({specs.Count})");
        var known = new HashSet<string> { "strike", "nova", "chain", "projectile", "rally",
            "mobility", "shield", "leech", "summon", "aura", "might", "warding", "haste", "fortune" };
        var unknown = specs.Where(s => !known.Contains(s.GetProperty("prototype").GetString()!))
            .Select(s => s.GetProperty("name").GetString()).ToList();
        Check(unknown.Count == 0,
            $"skill specs: no skill silently lacks a prototype ({string.Join(",", unknown)})");
        var gapped = specs.Count(s => s.GetProperty("behaviorGaps").GetArrayLength() > 0);
        Check(gapped == 29, $"skill specs: gap inventory is explicit ({gapped} skills with unimplemented attributes)");
        Console.WriteLine($"INFO skill specs: {gapped} skills carry explicit behavior gaps; see docs/web/C04_SKILL_SPECS.md");
    }
}
