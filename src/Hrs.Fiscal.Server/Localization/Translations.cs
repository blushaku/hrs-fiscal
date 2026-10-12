using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hrs.Fiscal.Server.Localization;

/// <summary>
/// The interface languages (Doc. 01-06-1967 Art 25.14; UA Art 32.2.9: official languages, Latin script).
/// Albanian is the default; Serbian is in Latin script; English stays available.
/// </summary>
public static class Languages
{
    public const string Albanian = "sq", Serbian = "sr", English = "en";
    /// <summary>Language for users who have not chosen one (config I18n:DefaultLanguage; Albanian unless set).</summary>
    public static string Default { get; set; } = Albanian;
    public const string Cookie = "lang";
    public static readonly (string Code, string Label)[] All = [(Albanian, "Shqip"), (Serbian, "Srpski"), (English, "English")];

    public static string Current(HttpContext context) =>
        context.Request.Cookies[Cookie] is { } c && All.Any(l => l.Code == c) ? c : Default;
}

/// <summary>
/// One language's dictionary: English text → translation. A key may contain placeholders {0}, {1}… for numbers,
/// names or other data inside a sentence; the translation repeats them in its own word order.
/// </summary>
public sealed class Dictionary
{
    private readonly Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    private readonly List<(string Prefix, Regex Pattern, string Template)> _patterns = [];

    public Dictionary(IReadOnlyDictionary<string, string> entries)
    {
        foreach (var (key, value) in entries)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var k = Normalize(key);
            if (!k.Contains("{0}")) { _exact[k] = value; continue; }
            var regex = "^" + Regex.Replace(Regex.Escape(k), @"\\\{(\d)}", m => $"(?<p{m.Groups[1].Value}>.+?)") + "$";
            _patterns.Add((k[..k.IndexOf('{')], new Regex(regex, RegexOptions.CultureInvariant | RegexOptions.Compiled), value));
        }
        // Longer literal prefixes first, so the most specific sentence wins.
        _patterns.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length));
    }

    public int Count => _exact.Count + _patterns.Count;

    public static string Normalize(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>The translation of a whole text, or null when there is none.</summary>
    public string? Translate(string normalized)
    {
        if (_exact.TryGetValue(normalized, out var t)) return t;
        foreach (var (prefix, pattern, template) in _patterns)
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var m = pattern.Match(normalized);
            if (!m.Success) continue;
            return Regex.Replace(template, @"\{(\d)}", p =>
            {
                var value = m.Groups["p" + p.Groups[1].Value].Value;
                return Translate(value) ?? value;    // a placeholder can itself be a known phrase (e.g. a status)
            });
        }
        return null;
    }
}

/// <summary>Loads the dictionaries from i18n/*.json next to the application. Untranslated text is shown in English.</summary>
public sealed class Translations
{
    private readonly ConcurrentDictionary<string, Dictionary> _byLanguage = new();
    private readonly string _folder;
    /// <summary>When set, every visible text without a translation is recorded here (development aid).</summary>
    public ConcurrentDictionary<string, byte>? Missing { get; }

    public Translations(IWebHostEnvironment env, IConfiguration config)
    {
        _folder = Path.Combine(env.ContentRootPath, "i18n");
        if (!Directory.Exists(_folder)) _folder = Path.Combine(AppContext.BaseDirectory, "i18n");
        if (config.GetValue<bool>("I18n:CollectMissing")) Missing = new();
    }

    public Dictionary For(string language) => _byLanguage.GetOrAdd(language, lang =>
    {
        var file = Path.Combine(_folder, lang + ".json");
        var entries = File.Exists(file)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? []
            : [];
        return new Dictionary(entries);
    });

    /// <summary>Translates one text for code (flash messages, PDF titles); returns it unchanged when unknown.</summary>
    public string T(string language, string english)
    {
        if (language == Languages.English) return english;
        var n = Dictionary.Normalize(english);
        var t = For(language).Translate(n);
        if (t is null) Missing?.TryAdd(n, 0);
        return t ?? english;
    }
}
