using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace QoLBar;

// Minimal self-contained localization helper mirroring ECommons.LanguageHelpers:
// same ini format (English==translation, one entry per line, literal \n escapes,
// ?? positional placeholders) and the same .Loc() string extension name.
// QoLBar does not reference ECommons, so this tiny equivalent is shipped instead
// of pulling in the full library just for localization.
public static class Localization
{
    private static readonly Dictionary<string, string> translations = new();

    public static void Init(string directory)
    {
        translations.Clear();
        if (string.IsNullOrEmpty(directory)) return;

        var path = Path.Combine(directory, "LanguageChineseTraditional.ini");
        try
        {
            if (!File.Exists(path)) return;

            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var idx = line.IndexOf("==", StringComparison.Ordinal);
                if (idx <= 0) continue;

                var key = line[..idx].Replace("\\n", "\n");
                var value = line[(idx + 2)..].TrimEnd('\r').Replace("\\n", "\n");
                translations[key] = value;
            }

            DalamudApi.PluginLog?.Information($"Localization: loaded {translations.Count} entries from {path}");
        }
        catch (Exception e)
        {
            DalamudApi.PluginLog?.Error(e, $"Localization: failed to load {path}");
        }
    }

    public static string Loc(this string s) => translations.TryGetValue(s, out var t) ? t : s;

    public static string Loc(this string s, params object[] args)
    {
        var result = s.Loc();
        foreach (var a in args)
        {
            var idx = result.IndexOf("??", StringComparison.Ordinal);
            if (idx < 0) break;
            result = result.Remove(idx, 2).Insert(idx, a?.ToString() ?? "");
        }
        return result;
    }
}
