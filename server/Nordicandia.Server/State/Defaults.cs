using System.Reflection;

namespace Nordicandia.Server.State;

/// <summary>
/// Creates default-initialised DTO instances for stub responses. Collections are created
/// empty and nested DTOs (game/SharedNet/Game namespaces) are populated recursively so
/// the client does not hit a NullReferenceException walking an empty response graph.
/// Strings and value types keep their CLR defaults on purpose.
/// </summary>
public static class Defaults
{
    private const int MaxDepth = 3;

    public static T Create<T>() => (T)Create(typeof(T), 0);

    private static object Create(Type t, int depth)
    {
        if (t == typeof(string)) return null;
        if (t.IsValueType) return Activator.CreateInstance(t);

        var isCollection = t.IsArray
            || (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(List<>)
                || t.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                || t.GetGenericTypeDefinition() == typeof(HashSet<>)));
        if (isCollection)
        {
            try { return Activator.CreateInstance(t); } catch { return null; }
        }

        // Only recurse into the game's own DTO namespaces, never framework types.
        var ns = t.Namespace ?? string.Empty;
        if (depth > MaxDepth || ns.StartsWith("System") || ns.StartsWith("Microsoft")) return null;

        object o;
        try { o = Activator.CreateInstance(t); }
        catch { return null; }
        if (o == null) return null;

        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite) continue;
            if (p.GetValue(o) != null) continue;
            var pt = p.PropertyType;
            if (pt == typeof(string)) continue;
            if (pt.IsValueType && Nullable.GetUnderlyingType(pt) == null) continue;
            var value = Create(pt, depth + 1);
            if (value == null) continue;
            try { p.SetValue(o, value); } catch { }
        }
        return o;
    }
}
