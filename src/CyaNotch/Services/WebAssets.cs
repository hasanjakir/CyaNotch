using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace CyaNotch.Services;

/// <summary>Phone web pages, embedded in the exe.</summary>
public static class WebAssets
{
    private static readonly Dictionary<string, string> Cache = new();

    public static string Load(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var s)) return s;
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"CyaNotch.Web.{name}");
            if (stream == null) return "<h1>Missing page</h1>";
            using var reader = new StreamReader(stream, Encoding.UTF8);
            s = reader.ReadToEnd();
            Cache[name] = s;
            return s;
        }
    }
}
