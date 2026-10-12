using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Hrs.Fiscal.Server.Localization;

/// <summary>
/// Translates the visible text of a rendered page: text between tags and the attributes a user reads (placeholder,
/// title, aria-label, alt, data-confirm, and the value of buttons). Data is left alone: script, style, textarea, pre,
/// code, elements with class "mono" (numbers, codes, NUIKF) and anything inside translate="no".
/// </summary>
public static partial class HtmlTranslator
{
    private static readonly HashSet<string> Void = ["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"];
    private static readonly HashSet<string> Raw = ["script", "style", "textarea", "pre", "code"];
    private static readonly string[] TextAttributes = ["placeholder", "title", "aria-label", "alt", "data-confirm",
        "data-t-nothing", "data-t-saving", "data-t-empty", "data-t-newpass", "data-t-yes", "data-t-no", "data-t-allowed", "data-t-notallowed"];

    [GeneratedRegex(@"\s(?<name>[a-zA-Z_:][-a-zA-Z0-9_:.]*)=""(?<value>[^""]*)""")]
    private static partial Regex AttributeRegex();
    [GeneratedRegex(@"^<\s*/?\s*(?<tag>[a-zA-Z][a-zA-Z0-9-]*)")]
    private static partial Regex TagNameRegex();

    public static string Translate(string html, Func<string, string?> translate)
    {
        var sb = new StringBuilder(html.Length + html.Length / 8);
        var stack = new Stack<(string Tag, bool NoTranslate)>();
        var i = 0;
        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0) lt = html.Length;
            if (lt > i)
            {
                var text = html[i..lt];
                sb.Append(stack.Count > 0 && stack.Peek().NoTranslate ? text : TranslateText(text, translate));
            }
            if (lt >= html.Length) break;

            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                var endComment = html.IndexOf("-->", lt, StringComparison.Ordinal);
                endComment = endComment < 0 ? html.Length : endComment + 3;
                sb.Append(html, lt, endComment - lt);
                i = endComment;
                continue;
            }
            var gt = TagEnd(html, lt);
            var tag = html[lt..gt];
            var m = TagNameRegex().Match(tag);
            if (!m.Success) { sb.Append(tag); i = gt; continue; }
            var name = m.Groups["tag"].Value.ToLowerInvariant();
            var closing = tag.Length > 1 && tag.TrimStart('<').TrimStart().StartsWith('/');

            if (closing)
            {
                while (stack.Count > 0) { if (stack.Pop().Tag == name) break; }
                sb.Append(tag);
                i = gt;
                continue;
            }

            var parentNo = stack.Count > 0 && stack.Peek().NoTranslate;
            var no = parentNo || Raw.Contains(name) || tag.Contains("translate=\"no\"") || HasClass(tag, "mono");
            sb.Append(parentNo ? tag : TranslateAttributes(tag, name, translate));
            i = gt;

            if (Raw.Contains(name))
            {
                // Copy the raw content up to the matching end tag untouched.
                var end = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                if (end < 0) end = html.Length;
                sb.Append(html, i, end - i);
                i = end;
                continue;
            }
            if (!Void.Contains(name) && !tag.EndsWith("/>")) stack.Push((name, no));
        }
        return sb.ToString();
    }

    /// <summary>Escapes only what HTML requires (letters such as ë, ç, š stay as they are).</summary>
    private static string Encode(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static int TagEnd(string html, int lt)
    {
        var inQuote = false;
        for (var j = lt + 1; j < html.Length; j++)
        {
            if (html[j] == '"') inQuote = !inQuote;
            else if (html[j] == '>' && !inQuote) return j + 1;
        }
        return html.Length;
    }

    private static bool HasClass(string tag, string cls)
    {
        var m = Regex.Match(tag, @"\sclass=""([^""]*)""");
        return m.Success && m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(cls);
    }

    private static string TranslateText(string raw, Func<string, string?> translate)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var decoded = WebUtility.HtmlDecode(raw);
        var normalized = Dictionary.Normalize(decoded);
        if (normalized.Length == 0 || !normalized.Any(char.IsLetter)) return raw;
        var t = translate(normalized);
        if (t is null) return raw;
        var lead = raw.Length - raw.TrimStart().Length;
        var trail = raw.Length - raw.TrimEnd().Length;
        return raw[..lead] + Encode(t) + raw[(raw.Length - trail)..];
    }

    private static string TranslateAttributes(string tag, string name, Func<string, string?> translate)
    {
        var isButtonValue = name == "button" || (name == "input" && Regex.IsMatch(tag, @"\stype=""(submit|button|reset)"""));
        return AttributeRegex().Replace(tag, m =>
        {
            var attr = m.Groups["name"].Value.ToLowerInvariant();
            if (!TextAttributes.Contains(attr) && !(attr == "value" && isButtonValue)) return m.Value;
            var value = Dictionary.Normalize(WebUtility.HtmlDecode(m.Groups["value"].Value));
            if (value.Length == 0 || !value.Any(char.IsLetter)) return m.Value;
            var t = translate(value);
            return t is null ? m.Value : $" {m.Groups["name"].Value}=\"{Encode(t)}\"";
        });
    }
}

/// <summary>Translates HTML pages into the user's language (cookie "lang"; Albanian by default).</summary>
public sealed class TranslationMiddleware(RequestDelegate next, Translations translations)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var language = Languages.Current(context);
        context.Items["lang"] = language;
        if (language == Languages.English || HttpMethods.IsHead(context.Request.Method))
        {
            await next(context);
            return;
        }

        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = original;
        }

        buffer.Position = 0;
        var type = context.Response.ContentType ?? "";
        if (!type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            await buffer.CopyToAsync(original);
            return;
        }
        var html = Encoding.UTF8.GetString(buffer.ToArray());
        var dictionary = translations.For(language);
        var translated = HtmlTranslator.Translate(html, text =>
        {
            var t = dictionary.Translate(text);
            if (t is null) translations.Missing?.TryAdd(text, 0);
            return t;
        });
        translated = translated.Replace("<html lang=\"en\">", $"<html lang=\"{language}\">");
        var bytes = Encoding.UTF8.GetBytes(translated);
        context.Response.ContentLength = bytes.Length;
        await original.WriteAsync(bytes);
    }
}
