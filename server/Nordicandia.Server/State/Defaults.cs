using System.Reflection;

namespace Nordicandia.Server.State;

/// <summary>Creates default-initialised DTO instances (with empty collections) for stub responses.</summary>
public static class Defaults
{
    public static T Create<T>()
    {
        if (typeof(T).IsValueType) return default;
        object o;
        try { o = Activator.CreateInstance(typeof(T)); }
        catch { return default; }
        if (o == null) return default;
        foreach (var p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite) continue;
            if (p.GetValue(o) != null) continue;
            var t = p.PropertyType;
            if (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(List<>) || t.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
            {
                try { p.SetValue(o, Activator.CreateInstance(t)); } catch { }
            }
        }
        return (T)o;
    }
}
