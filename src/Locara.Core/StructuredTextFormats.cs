using System.Globalization;
using System.Text;

namespace Locara.Core;

/// <summary>
/// Lossless, comment-preserving TOML localization &amp; data string extractor/rewriter.
/// Supports [table], [[array_of_tables]], inline arrays/tables, basic/literal/multiline strings,
/// and normalized JSON-pointer-style locators compatible with FieldSemantics and SchemaClusterer.
/// </summary>
public sealed class TomlFormat : IFormatHandler
{
    public static TomlFormat Instance { get; } = new();
    public string Name => "toml";

    public bool Supports(string path) =>
        Path.GetExtension(path).Equals(".toml", StringComparison.OrdinalIgnoreCase);

    private enum TomlQuoteKind { Basic, Literal, MultilineBasic, MultilineLiteral }

    private sealed record TomlStringSpan(
        string Locator,
        string Value,
        int RawStart,
        int RawEnd,
        TomlQuoteKind Kind);

    public IReadOnlyList<TextSlot> Extract(byte[] bytes)
    {
        var (text, _, _) = TextEncodingHelper.DecodeText(bytes);
        return ParseStringSpans(text)
            .Where(s => !string.IsNullOrWhiteSpace(s.Value))
            .Select(s => new TextSlot(s.Locator, s.Value, s.Locator))
            .ToList();
    }

    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations)
    {
        if (translations.Count == 0) return bytes;
        var (text, encoding, preamble) = TextEncodingHelper.DecodeText(bytes);
        var spans = ParseStringSpans(text);
        var replacements = spans
            .Where(s => translations.TryGetValue(s.Locator, out string? target) && target != s.Value)
            .OrderByDescending(s => s.RawStart)
            .ToList();
        if (replacements.Count == 0) return bytes;

        var sb = new StringBuilder(text);
        foreach (var span in replacements)
        {
            string target = translations[span.Locator];
            string encoded = EncodeTomlString(target, span.Kind);
            sb.Remove(span.RawStart, span.RawEnd - span.RawStart);
            sb.Insert(span.RawStart, encoded);
        }
        return TextEncodingHelper.EncodeText(sb.ToString(), encoding, preamble);
    }

    private static List<TomlStringSpan> ParseStringSpans(string text)
    {
        var results = new List<TomlStringSpan>();
        var seenLocators = new HashSet<string>(StringComparer.Ordinal);
        var arrayTableCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        string currentPrefix = "";
        int pos = 0;
        int len = text.Length;

        while (pos < len)
        {
            SkipWhitespaceAndComments(text, ref pos);
            if (pos >= len) break;

            if (text[pos] == '\r' || text[pos] == '\n')
            {
                pos++;
                continue;
            }

            // Table or Array-of-Tables header
            if (text[pos] == '[')
            {
                bool isArrayTable = pos + 1 < len && text[pos + 1] == '[';
                pos += isArrayTable ? 2 : 1;
                var segs = ReadKeyPath(text, ref pos, stopAtBracket: true);
                if (isArrayTable)
                {
                    if (pos + 1 >= len || text[pos] != ']' || text[pos + 1] != ']')
                        throw new InvalidDataException("ترويسة مصفوفة جداول TOML غير مغلقة.");
                    pos += 2;
                    string basePath = BuildPointer(segs);
                    int idx = arrayTableCounts.TryGetValue(basePath, out int prev) ? prev + 1 : 0;
                    arrayTableCounts[basePath] = idx;
                    // Reset nested array-table counters beneath this table element
                    foreach (string k in arrayTableCounts.Keys.Where(k => k.StartsWith(basePath + "/", StringComparison.Ordinal)).ToList())
                        arrayTableCounts.Remove(k);
                    currentPrefix = $"{basePath}/{idx}";
                }
                else
                {
                    if (pos >= len || text[pos] != ']')
                        throw new InvalidDataException("ترويسة جدول TOML غير مغلقة.");
                    pos++;
                    var resolved = new List<string>(segs.Count);
                    string running = "";
                    foreach (string seg in segs)
                    {
                        running += "/" + EscapePointer(seg);
                        resolved.Add(EscapePointer(seg));
                        if (arrayTableCounts.TryGetValue(running, out int activeIdx))
                        {
                            resolved.Add(activeIdx.ToString(CultureInfo.InvariantCulture));
                            running += "/" + activeIdx.ToString(CultureInfo.InvariantCulture);
                        }
                    }
                    currentPrefix = resolved.Count == 0 ? "" : "/" + string.Join('/', resolved);
                }
                SkipRestOfLine(text, ref pos);
                continue;
            }

            // Key = Value
            var keySegs = ReadKeyPath(text, ref pos, stopAtBracket: false);
            SkipInlineSpace(text, ref pos);
            if (keySegs.Count == 0 || pos >= len || text[pos] != '=')
                throw new InvalidDataException("بنية مفتاح/قيمة TOML غير صالحة.");
            pos++; // skip '='
            SkipInlineSpace(text, ref pos);

            string fullLocator = currentPrefix + BuildPointer(keySegs);
            ParseValue(text, ref pos, fullLocator, results, seenLocators);
            SkipRestOfLine(text, ref pos);
        }

        return results;
    }

    private static void ParseValue(string text, ref int pos, string locator, List<TomlStringSpan> results, HashSet<string> seen)
    {
        SkipInlineSpace(text, ref pos);
        if (pos >= text.Length) return;
        char c = text[pos];

        if (c == '"' || c == '\'')
        {
            var span = ReadTomlString(text, ref pos, locator);
            if (!seen.Add(locator))
                throw new InvalidDataException($"مفتاح TOML مكرر: {locator}");
            results.Add(span);
            return;
        }

        if (c == '[')
        {
            pos++; // '['
            int elemIdx = 0;
            while (pos < text.Length)
            {
                SkipWhitespaceAndComments(text, ref pos, allowNewlines: true);
                if (pos >= text.Length) throw new InvalidDataException("مصفوفة TOML غير مغلقة.");
                if (text[pos] == ']') { pos++; break; }
                ParseValue(text, ref pos, $"{locator}/{elemIdx}", results, seen);
                elemIdx++;
                SkipWhitespaceAndComments(text, ref pos, allowNewlines: true);
                if (pos < text.Length && text[pos] == ',') pos++;
                else if (pos < text.Length && text[pos] == ']') { pos++; break; }
            }
            return;
        }

        if (c == '{')
        {
            pos++; // '{'
            while (pos < text.Length)
            {
                SkipWhitespaceAndComments(text, ref pos, allowNewlines: true);
                if (pos >= text.Length) throw new InvalidDataException("جدول TOML السطري غير مغلق.");
                if (text[pos] == '}') { pos++; break; }
                var subKeys = ReadKeyPath(text, ref pos, stopAtBracket: false);
                SkipInlineSpace(text, ref pos);
                if (subKeys.Count == 0 || pos >= text.Length || text[pos] != '=')
                    throw new InvalidDataException("مفتاح جدول TOML السطري غير صالح.");
                pos++;
                ParseValue(text, ref pos, locator + BuildPointer(subKeys), results, seen);
                SkipWhitespaceAndComments(text, ref pos, allowNewlines: true);
                if (pos < text.Length && text[pos] == ',') pos++;
                else if (pos < text.Length && text[pos] == '}') { pos++; break; }
            }
            return;
        }

        // Scalar non-string (number, boolean, datetime)
        while (pos < text.Length && text[pos] is not ('\r' or '\n' or '#' or ',' or ']' or '}'))
            pos++;
    }

    private static TomlStringSpan ReadTomlString(string text, ref int pos, string locator)
    {
        int start = pos;
        char quote = text[pos];
        bool isTriple = pos + 2 < text.Length && text[pos + 1] == quote && text[pos + 2] == quote;

        if (quote == '\'' && !isTriple)
        {
            pos++;
            int contentStart = pos;
            while (pos < text.Length && text[pos] != '\'' && text[pos] != '\n' && text[pos] != '\r')
                pos++;
            if (pos >= text.Length || text[pos] != '\'')
                throw new InvalidDataException("نص TOML حرفي غير مغلق.");
            string val = text[contentStart..pos];
            pos++;
            return new TomlStringSpan(locator, val, start, pos, TomlQuoteKind.Literal);
        }

        if (quote == '\'' && isTriple)
        {
            pos += 3;
            if (pos < text.Length && text[pos] == '\r') pos++;
            if (pos < text.Length && text[pos] == '\n') pos++;
            int contentStart = pos;
            while (pos + 2 < text.Length && !(text[pos] == '\'' && text[pos + 1] == '\'' && text[pos + 2] == '\''))
                pos++;
            if (pos + 2 >= text.Length)
                throw new InvalidDataException("نص TOML متعدد الأسطر غير مغلق.");
            int extraQuotes = 0;
            while (pos + 3 + extraQuotes < text.Length && text[pos + 3 + extraQuotes] == '\'' && extraQuotes < 2)
                extraQuotes++;
            string val = text.Substring(contentStart, (pos + extraQuotes) - contentStart);
            pos += 3 + extraQuotes;
            return new TomlStringSpan(locator, val, start, pos, TomlQuoteKind.MultilineLiteral);
        }

        if (!isTriple)
        {
            pos++;
            var sb = new StringBuilder();
            while (pos < text.Length && text[pos] != '"' && text[pos] != '\n' && text[pos] != '\r')
            {
                if (text[pos] == '\\')
                {
                    pos++;
                    sb.Append(ReadTomlEscape(text, ref pos));
                }
                else
                {
                    sb.Append(text[pos++]);
                }
            }
            if (pos >= text.Length || text[pos] != '"')
                throw new InvalidDataException("نص TOML مزدوج غير مغلق.");
            pos++;
            return new TomlStringSpan(locator, sb.ToString(), start, pos, TomlQuoteKind.Basic);
        }
        else
        {
            pos += 3;
            if (pos < text.Length && text[pos] == '\r') pos++;
            if (pos < text.Length && text[pos] == '\n') pos++;
            var sb = new StringBuilder();
            while (pos < text.Length)
            {
                if (pos + 2 < text.Length && text[pos] == '"' && text[pos + 1] == '"' && text[pos + 2] == '"')
                {
                    int extra = 0;
                    while (pos + 3 + extra < text.Length && text[pos + 3 + extra] == '"' && extra < 2)
                        extra++;
                    for (int q = 0; q < extra; q++) sb.Append('"');
                    pos += 3 + extra;
                    return new TomlStringSpan(locator, sb.ToString(), start, pos, TomlQuoteKind.MultilineBasic);
                }
                if (text[pos] == '\\')
                {
                    pos++;
                    // Line-ending backslash trims trailing whitespace & newlines
                    int peek = pos;
                    while (peek < text.Length && text[peek] is ' ' or '\t') peek++;
                    if (peek < text.Length && text[peek] is '\r' or '\n')
                    {
                        pos = peek;
                        while (pos < text.Length && text[pos] is ' ' or '\t' or '\r' or '\n') pos++;
                        continue;
                    }
                    sb.Append(ReadTomlEscape(text, ref pos));
                }
                else
                {
                    sb.Append(text[pos++]);
                }
            }
            throw new InvalidDataException("نص TOML ثلاثي الاقتباس غير مغلق.");
        }
    }

    private static string ReadTomlEscape(string text, ref int pos)
    {
        if (pos >= text.Length) throw new InvalidDataException("تسلسل هروب TOML مقطوع.");
        char esc = text[pos++];
        return esc switch
        {
            'b' => "\b",
            't' => "\t",
            'n' => "\n",
            'f' => "\f",
            'r' => "\r",
            '"' => "\"",
            '\\' => "\\",
            'u' => ReadUnicodeScalar(text, ref pos, 4),
            'U' => ReadUnicodeScalar(text, ref pos, 8),
            _ => throw new InvalidDataException($"رمز هروب TOML غير صالح: \\{esc}")
        };
    }

    private static string ReadUnicodeScalar(string text, ref int pos, int hexDigits)
    {
        if (pos + hexDigits > text.Length || !int.TryParse(text.AsSpan(pos, hexDigits), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
            throw new InvalidDataException("قيمة Unicode في TOML غير صالحة.");
        pos += hexDigits;
        return char.ConvertFromUtf32(cp);
    }

    private static string EncodeTomlString(string value, TomlQuoteKind preferredKind)
    {
        if (preferredKind == TomlQuoteKind.Literal && !value.Contains('\'') && !value.Contains('\n') && !value.Contains('\r'))
            return "'" + value + "'";

        var sb = new StringBuilder(value.Length + 4);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c)) sb.Append($"\\u{(int)c:X4}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static List<string> ReadKeyPath(string text, ref int pos, bool stopAtBracket)
    {
        var parts = new List<string>();
        while (pos < text.Length)
        {
            SkipInlineSpace(text, ref pos);
            if (pos >= text.Length) break;
            char c = text[pos];
            if ((stopAtBracket && c == ']') || (!stopAtBracket && c == '=')) break;

            if (c == '"' || c == '\'')
            {
                var span = ReadTomlString(text, ref pos, "");
                parts.Add(span.Value);
            }
            else
            {
                int start = pos;
                while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] is '_' or '-'))
                    pos++;
                if (pos == start) break;
                parts.Add(text[start..pos]);
            }

            SkipInlineSpace(text, ref pos);
            if (pos < text.Length && text[pos] == '.') pos++;
            else break;
        }
        return parts;
    }

    private static string EscapePointer(string seg) => seg.Replace("~", "~0").Replace("/", "~1");
    private static string BuildPointer(IReadOnlyList<string> segs) =>
        segs.Count == 0 ? "" : "/" + string.Join('/', segs.Select(EscapePointer));

    private static void SkipInlineSpace(string text, ref int pos)
    {
        while (pos < text.Length && text[pos] is ' ' or '\t') pos++;
    }

    private static void SkipRestOfLine(string text, ref int pos)
    {
        SkipInlineSpace(text, ref pos);
        if (pos < text.Length && text[pos] == '#')
        {
            while (pos < text.Length && text[pos] is not ('\r' or '\n')) pos++;
        }
        if (pos < text.Length && text[pos] == '\r') pos++;
        if (pos < text.Length && text[pos] == '\n') pos++;
    }

    private static void SkipWhitespaceAndComments(string text, ref int pos, bool allowNewlines = true)
    {
        while (pos < text.Length)
        {
            char c = text[pos];
            if (c is ' ' or '\t' || (allowNewlines && c is '\r' or '\n')) { pos++; continue; }
            if (c == '#')
            {
                while (pos < text.Length && text[pos] is not ('\r' or '\n')) pos++;
                continue;
            }
            break;
        }
    }
}

/// <summary>
/// Apple / Cocoa / Unity / cross-platform .strings ("key" = "value";) localization reader and writer.
/// Preserves UTF-8, UTF-16LE, and UTF-16BE encodings + BOMs, comments, and exact file layout.
/// </summary>
public sealed class StringsFormat : IFormatHandler
{
    public static StringsFormat Instance { get; } = new();
    public string Name => "strings";

    public bool Supports(string path) =>
        Path.GetExtension(path).Equals(".strings", StringComparison.OrdinalIgnoreCase);

    private sealed record StringsEntry(string Locator, string Value, string Context, int ValueStart, int ValueEnd);

    public IReadOnlyList<TextSlot> Extract(byte[] bytes)
    {
        var (text, _, _) = TextEncodingHelper.DecodeText(bytes);
        return ParseEntries(text)
            .Where(e => !string.IsNullOrWhiteSpace(e.Value))
            .Select(e => new TextSlot(e.Locator, e.Value, e.Context))
            .ToList();
    }

    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations)
    {
        if (translations.Count == 0) return bytes;
        var (text, encoding, preamble) = TextEncodingHelper.DecodeText(bytes);
        var entries = ParseEntries(text);
        var updates = entries
            .Where(e => translations.TryGetValue(e.Locator, out string? target) && target != e.Value)
            .OrderByDescending(e => e.ValueStart)
            .ToList();
        if (updates.Count == 0) return bytes;

        var sb = new StringBuilder(text);
        foreach (var e in updates)
        {
            string escaped = EncodeStringsLiteral(translations[e.Locator]);
            sb.Remove(e.ValueStart, e.ValueEnd - e.ValueStart);
            sb.Insert(e.ValueStart, escaped);
        }
        return TextEncodingHelper.EncodeText(sb.ToString(), encoding, preamble);
    }

    private static List<StringsEntry> ParseEntries(string text)
    {
        var result = new List<StringsEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string pendingComment = "";
        int pos = 0;
        int len = text.Length;

        while (pos < len)
        {
            while (pos < len && char.IsWhiteSpace(text[pos]))
            {
                if (text[pos] == '\n' && pos + 1 < len && text[pos + 1] is '\r' or '\n')
                    pendingComment = "";
                pos++;
            }
            if (pos >= len) break;

            if (pos + 1 < len && text[pos] == '/' && text[pos + 1] == '*')
            {
                int cStart = pos + 2;
                int cEnd = text.IndexOf("*/", cStart, StringComparison.Ordinal);
                if (cEnd < 0) throw new InvalidDataException("تعليق /* غير مغلق في ملف .strings.");
                pendingComment = text[cStart..cEnd].Trim();
                pos = cEnd + 2;
                continue;
            }
            if (pos + 1 < len && text[pos] == '/' && text[pos + 1] == '/')
            {
                int cStart = pos + 2;
                while (pos < len && text[pos] is not ('\r' or '\n')) pos++;
                string lineComment = text[cStart..pos].Trim();
                pendingComment = pendingComment.Length == 0 ? lineComment : pendingComment + " " + lineComment;
                continue;
            }

            string key;
            if (text[pos] == '"')
            {
                key = ReadQuotedStringsValue(text, ref pos);
            }
            else
            {
                int kStart = pos;
                while (pos < len && (char.IsLetterOrDigit(text[pos]) || text[pos] is '_' or '.' or '-')) pos++;
                if (pos == kStart) throw new InvalidDataException("مفتاح غير صالح في ملف .strings.");
                key = text[kStart..pos];
            }

            SkipWsAndComments(text, ref pos);
            if (pos >= len || text[pos] != '=')
                throw new InvalidDataException($"علامة '=' مفقودة بعد المفتاح {key} في ملف .strings.");
            pos++;
            SkipWsAndComments(text, ref pos);

            if (pos >= len || text[pos] != '"')
                throw new InvalidDataException($"قيمة نصية مفقودة للمفتاح {key} في ملف .strings.");
            int valStart = pos;
            string val = ReadQuotedStringsValue(text, ref pos);
            int valEnd = pos;

            SkipWsAndComments(text, ref pos);
            if (pos < len && text[pos] == ';') pos++;

            string locator = "key:" + key;
            if (!seen.Add(locator))
                throw new InvalidDataException($"مفتاح .strings مكرر: {key}");
            string ctx = pendingComment.Length > 0 ? $"{key} — {pendingComment}" : key;
            result.Add(new StringsEntry(locator, val, ctx, valStart, valEnd));
            pendingComment = "";
        }
        return result;
    }

    private static void SkipWsAndComments(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            if (char.IsWhiteSpace(text[pos])) { pos++; continue; }
            if (pos + 1 < text.Length && text[pos] == '/' && text[pos + 1] == '*')
            {
                int end = text.IndexOf("*/", pos + 2, StringComparison.Ordinal);
                pos = end < 0 ? text.Length : end + 2;
                continue;
            }
            if (pos + 1 < text.Length && text[pos] == '/' && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] is not ('\r' or '\n')) pos++;
                continue;
            }
            break;
        }
    }

    private static string ReadQuotedStringsValue(string text, ref int pos)
    {
        pos++; // '"'
        var sb = new StringBuilder();
        while (pos < text.Length)
        {
            char c = text[pos++];
            if (c == '"') return sb.ToString();
            if (c == '\\')
            {
                if (pos >= text.Length) throw new InvalidDataException("تسلسل هروب مقطوع في .strings.");
                char esc = text[pos++];
                switch (esc)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '"': sb.Append('"'); break;
                    case '\'': sb.Append('\''); break;
                    case '\\': sb.Append('\\'); break;
                    case 'u':
                    case 'U':
                        if (pos + 4 <= text.Length && int.TryParse(text.AsSpan(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int u))
                        {
                            sb.Append((char)u);
                            pos += 4;
                        }
                        else sb.Append(esc);
                        break;
                    default: sb.Append(esc); break;
                }
            }
            else sb.Append(c);
        }
        throw new InvalidDataException("نص مقتبس غير مغلق في ملف .strings.");
    }

    private static string EncodeStringsLiteral(string value)
    {
        var sb = new StringBuilder(value.Length + 4);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}

/// <summary>
/// Safe lexer-based Lua localization table extractor and span-exact rewriter.
/// Extracts pure string literals from table constructors and variable assignments while
/// ignoring comments, bracketed table keys (["key"]), and function call arguments (require/print).
/// </summary>
public sealed class LuaFormat : IFormatHandler
{
    public static LuaFormat Instance { get; } = new();
    public string Name => "lua";

    public bool Supports(string path) =>
        Path.GetExtension(path).Equals(".lua", StringComparison.OrdinalIgnoreCase);

    private enum LuaTokenKind { Ident, String, Number, Symbol }
    private sealed record LuaToken(LuaTokenKind Kind, string Text, int Start, int End);
    private sealed record LuaStringSlot(string Locator, string Value, int Start, int End);

    public IReadOnlyList<TextSlot> Extract(byte[] bytes)
    {
        var (text, _, _) = TextEncodingHelper.DecodeText(bytes);
        return ExtractSpans(text)
            .Where(s => !string.IsNullOrWhiteSpace(s.Value))
            .Select(s => new TextSlot(s.Locator, s.Value, s.Locator))
            .ToList();
    }

    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations)
    {
        if (translations.Count == 0) return bytes;
        var (text, encoding, preamble) = TextEncodingHelper.DecodeText(bytes);
        var spans = ExtractSpans(text);
        var updates = spans
            .Where(s => translations.TryGetValue(s.Locator, out string? target) && target != s.Value)
            .OrderByDescending(s => s.Start)
            .ToList();
        if (updates.Count == 0) return bytes;

        var sb = new StringBuilder(text);
        foreach (var s in updates)
        {
            string encoded = EncodeLuaString(translations[s.Locator]);
            sb.Remove(s.Start, s.End - s.Start);
            sb.Insert(s.Start, encoded);
        }
        return TextEncodingHelper.EncodeText(sb.ToString(), encoding, preamble);
    }

    private static readonly HashSet<string> LuaKeywords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function",
        "goto", "if", "in", "local", "nil", "not", "or", "repeat", "return",
        "then", "true", "until", "while"
    };

    private static List<LuaStringSlot> ExtractSpans(string text)
    {
        var tokens = Tokenize(text);
        var slots = new List<LuaStringSlot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;

        while (i < tokens.Count)
        {
            // Anonymous return { ... } localization table at module tail
            if (tokens[i].Kind == LuaTokenKind.Ident && tokens[i].Text == "return"
                && i + 1 < tokens.Count && tokens[i + 1] is { Kind: LuaTokenKind.Symbol, Text: "{" })
            {
                i++;
                ParseTableConstructor(tokens, ref i, "", slots, seen);
                continue;
            }

            // Skip function call expressions `ident(...)` or `ident "..."` or `ident { ... }`
            if (tokens[i].Kind == LuaTokenKind.Ident && !LuaKeywords.Contains(tokens[i].Text))
            {
                int save = i;
                var lhs = TryReadLhsPath(tokens, ref i);
                if (lhs != null && i < tokens.Count && tokens[i] is { Kind: LuaTokenKind.Symbol, Text: "=" })
                {
                    i++; // '='
                    ParseAssignedRhs(tokens, ref i, lhs, slots, seen);
                    continue;
                }
                i = save + 1;
                continue;
            }

            // Skip parenthesized groups so function call arguments are never extracted
            if (tokens[i] is { Kind: LuaTokenKind.Symbol, Text: "(" })
            {
                SkipBalanced(tokens, ref i, "(", ")");
                continue;
            }

            i++;
        }

        return slots;
    }

    private static string? TryReadLhsPath(List<LuaToken> tokens, ref int i)
    {
        if (i >= tokens.Count || tokens[i].Kind != LuaTokenKind.Ident || LuaKeywords.Contains(tokens[i].Text))
            return null;
        var segs = new List<string> { EscapeSeg(tokens[i].Text) };
        i++;
        while (i < tokens.Count)
        {
            if (tokens[i] is { Kind: LuaTokenKind.Symbol, Text: "." } && i + 1 < tokens.Count && tokens[i + 1].Kind == LuaTokenKind.Ident)
            {
                segs.Add(EscapeSeg(tokens[i + 1].Text));
                i += 2;
            }
            else if (tokens[i] is { Kind: LuaTokenKind.Symbol, Text: "[" }
                && i + 2 < tokens.Count
                && tokens[i + 1].Kind is LuaTokenKind.String or LuaTokenKind.Number
                && tokens[i + 2] is { Kind: LuaTokenKind.Symbol, Text: "]" })
            {
                segs.Add(EscapeSeg(tokens[i + 1].Text));
                i += 3;
            }
            else break;
        }
        return "/" + string.Join('/', segs);
    }

    private static void ParseAssignedRhs(List<LuaToken> tokens, ref int i, string locator, List<LuaStringSlot> slots, HashSet<string> seen)
    {
        if (i >= tokens.Count) return;
        if (tokens[i] is { Kind: LuaTokenKind.Symbol, Text: "{" })
        {
            ParseTableConstructor(tokens, ref i, locator, slots, seen);
            return;
        }
        if (tokens[i].Kind == LuaTokenKind.String)
        {
            // Only extract pure standalone string literals (not string concatenations `..` or comparisons)
            bool hasBinaryTail = i + 1 < tokens.Count && tokens[i + 1] is { Kind: LuaTokenKind.Symbol, Text: ".." or "==" or "~=" };
            if (!hasBinaryTail && seen.Add(locator))
                slots.Add(new LuaStringSlot(locator, tokens[i].Text, tokens[i].Start, tokens[i].End));
            i++;
        }
    }

    private static void ParseTableConstructor(List<LuaToken> tokens, ref int i, string prefix, List<LuaStringSlot> slots, HashSet<string> seen)
    {
        i++; // skip '{'
        int arrayIndex = 0;
        while (i < tokens.Count)
        {
            var tok = tokens[i];
            if (tok is { Kind: LuaTokenKind.Symbol, Text: "}" }) { i++; break; }
            if (tok is { Kind: LuaTokenKind.Symbol, Text: "," or ";" }) { i++; continue; }

            // 1. Bracketed key: ["key"] = value  or  [1] = value
            if (tok is { Kind: LuaTokenKind.Symbol, Text: "[" })
            {
                if (i + 3 < tokens.Count
                    && tokens[i + 1].Kind is LuaTokenKind.String or LuaTokenKind.Number
                    && tokens[i + 2] is { Kind: LuaTokenKind.Symbol, Text: "]" }
                    && tokens[i + 3] is { Kind: LuaTokenKind.Symbol, Text: "=" })
                {
                    string keySeg = EscapeSeg(tokens[i + 1].Text);
                    i += 4;
                    ParseAssignedRhs(tokens, ref i, $"{prefix}/{keySeg}", slots, seen);
                    SkipFieldExprRemainder(tokens, ref i);
                    continue;
                }
                SkipBalanced(tokens, ref i, "[", "]");
                continue;
            }

            // 2. Identifier key: key = value
            if (tok.Kind == LuaTokenKind.Ident && !LuaKeywords.Contains(tok.Text)
                && i + 1 < tokens.Count && tokens[i + 1] is { Kind: LuaTokenKind.Symbol, Text: "=" })
            {
                string keySeg = EscapeSeg(tok.Text);
                i += 2;
                ParseAssignedRhs(tokens, ref i, $"{prefix}/{keySeg}", slots, seen);
                SkipFieldExprRemainder(tokens, ref i);
                continue;
            }

            // 3. Positional table element (string or nested table)
            string elemLocator = $"{prefix}/{arrayIndex}";
            arrayIndex++;
            if (tok is { Kind: LuaTokenKind.Symbol, Text: "{" })
            {
                ParseTableConstructor(tokens, ref i, elemLocator, slots, seen);
            }
            else if (tok.Kind == LuaTokenKind.String)
            {
                bool isCallOrConcat = i + 1 < tokens.Count && tokens[i + 1] is { Kind: LuaTokenKind.Symbol, Text: ".." or "==" or "~=" };
                if (!isCallOrConcat && seen.Add(elemLocator))
                    slots.Add(new LuaStringSlot(elemLocator, tok.Text, tok.Start, tok.End));
                i++;
            }
            SkipFieldExprRemainder(tokens, ref i);
        }
    }

    private static void SkipFieldExprRemainder(List<LuaToken> tokens, ref int i)
    {
        int braceDepth = 0, parenDepth = 0, bracketDepth = 0;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Kind == LuaTokenKind.Symbol)
            {
                if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0 && t.Text is "," or ";" or "}")
                    break;
                if (t.Text == "{") braceDepth++;
                else if (t.Text == "}") braceDepth--;
                else if (t.Text == "(") parenDepth++;
                else if (t.Text == ")") parenDepth = Math.Max(0, parenDepth - 1);
                else if (t.Text == "[") bracketDepth++;
                else if (t.Text == "]") bracketDepth = Math.Max(0, bracketDepth - 1);
            }
            i++;
        }
    }

    private static void SkipBalanced(List<LuaToken> tokens, ref int i, string open, string close)
    {
        int depth = 0;
        while (i < tokens.Count)
        {
            if (tokens[i] is { Kind: LuaTokenKind.Symbol } sym)
            {
                if (sym.Text == open) depth++;
                else if (sym.Text == close)
                {
                    depth--;
                    if (depth <= 0) { i++; return; }
                }
            }
            i++;
        }
    }

    private static string EscapeSeg(string s) => s.Replace("~", "~0").Replace("/", "~1");

    private static List<LuaToken> Tokenize(string text)
    {
        var tokens = new List<LuaToken>();
        int pos = 0;
        int len = text.Length;

        while (pos < len)
        {
            char c = text[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }

            // Comments: --[[ ... ]] or -- ...
            if (c == '-' && pos + 1 < len && text[pos + 1] == '-')
            {
                pos += 2;
                if (pos < len && text[pos] == '[' && TryMatchLongBracket(text, pos, out int eqCount))
                {
                    SkipLongBracketContent(text, ref pos, eqCount);
                }
                else
                {
                    while (pos < len && text[pos] is not ('\r' or '\n')) pos++;
                }
                continue;
            }

            // Long bracket string: [[ ... ]] or [=[ ... ]=]
            if (c == '[' && TryMatchLongBracket(text, pos, out int strEq))
            {
                int start = pos;
                string val = ReadLongBracketString(text, ref pos, strEq);
                tokens.Add(new LuaToken(LuaTokenKind.String, val, start, pos));
                continue;
            }

            // Quoted string: "..." or '...'
            if (c is '"' or '\'')
            {
                int start = pos;
                string val = ReadQuotedLuaString(text, ref pos);
                tokens.Add(new LuaToken(LuaTokenKind.String, val, start, pos));
                continue;
            }

            // Identifier
            if (char.IsLetter(c) || c == '_')
            {
                int start = pos++;
                while (pos < len && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_')) pos++;
                tokens.Add(new LuaToken(LuaTokenKind.Ident, text[start..pos], start, pos));
                continue;
            }

            // Number
            if (char.IsDigit(c))
            {
                int start = pos++;
                while (pos < len && (char.IsLetterOrDigit(text[pos]) || text[pos] is '.' or '_')) pos++;
                tokens.Add(new LuaToken(LuaTokenKind.Number, text[start..pos], start, pos));
                continue;
            }

            // Multi-char operators (`..`, `==`, `~=`, `<=`, `>=`)
            if (pos + 1 < len)
            {
                string two = text.Substring(pos, 2);
                if (two is ".." or "==" or "~=" or "<=" or ">=")
                {
                    tokens.Add(new LuaToken(LuaTokenKind.Symbol, two, pos, pos + 2));
                    pos += 2;
                    continue;
                }
            }

            tokens.Add(new LuaToken(LuaTokenKind.Symbol, c.ToString(), pos, pos + 1));
            pos++;
        }

        return tokens;
    }

    private static bool TryMatchLongBracket(string text, int pos, out int eqCount)
    {
        eqCount = 0;
        if (pos >= text.Length || text[pos] != '[') return false;
        int p = pos + 1;
        while (p < text.Length && text[p] == '=') { eqCount++; p++; }
        return p < text.Length && text[p] == '[';
    }

    private static void SkipLongBracketContent(string text, ref int pos, int eqCount)
    {
        _ = ReadLongBracketString(text, ref pos, eqCount);
    }

    private static string ReadLongBracketString(string text, ref int pos, int eqCount)
    {
        pos += 2 + eqCount; // skip `[==[`
        if (pos < text.Length && text[pos] == '\r') pos++;
        if (pos < text.Length && text[pos] == '\n') pos++;
        string closer = "]" + new string('=', eqCount) + "]";
        int closeIdx = text.IndexOf(closer, pos, StringComparison.Ordinal);
        if (closeIdx < 0)
            throw new InvalidDataException("نص Lua طويل [[...]] غير مغلق.");
        string content = text[pos..closeIdx];
        pos = closeIdx + closer.Length;
        return content;
    }

    private static string ReadQuotedLuaString(string text, ref int pos)
    {
        char quote = text[pos++];
        var sb = new StringBuilder();
        while (pos < text.Length)
        {
            char c = text[pos++];
            if (c == quote) return sb.ToString();
            if (c is '\r' or '\n') throw new InvalidDataException("نص Lua غير مغلق قبل نهاية السطر.");
            if (c == '\\')
            {
                if (pos >= text.Length) throw new InvalidDataException("هروب Lua مقطوع.");
                char esc = text[pos++];
                switch (esc)
                {
                    case 'a': sb.Append('\a'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'v': sb.Append('\v'); break;
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case '\'': sb.Append('\''); break;
                    case '\r':
                        if (pos < text.Length && text[pos] == '\n') pos++;
                        sb.Append('\n');
                        break;
                    case '\n': sb.Append('\n'); break;
                    case 'z':
                        while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
                        break;
                    case 'x':
                        if (pos + 2 <= text.Length && int.TryParse(text.AsSpan(pos, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hx))
                        {
                            sb.Append((char)hx);
                            pos += 2;
                        }
                        break;
                    case 'u':
                        if (pos < text.Length && text[pos] == '{')
                        {
                            int braceEnd = text.IndexOf('}', pos + 1);
                            if (braceEnd > pos + 1 && int.TryParse(text.AsSpan(pos + 1, braceEnd - pos - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
                            {
                                sb.Append(char.ConvertFromUtf32(cp));
                                pos = braceEnd + 1;
                            }
                        }
                        break;
                    default:
                        if (char.IsDigit(esc))
                        {
                            int dec = esc - '0';
                            for (int d = 0; d < 2 && pos < text.Length && char.IsDigit(text[pos]); d++)
                                dec = dec * 10 + (text[pos++] - '0');
                            sb.Append((char)Math.Clamp(dec, 0, 255));
                        }
                        else sb.Append(esc);
                        break;
                }
            }
            else sb.Append(c);
        }
        throw new InvalidDataException("نص Lua مقتبس غير مغلق.");
    }

    private static string EncodeLuaString(string value)
    {
        var sb = new StringBuilder(value.Length + 4);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c)) sb.Append($"\\{(int)c:D3}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}

internal static class TextEncodingHelper
{
    public static (string Text, Encoding Encoding, byte[] Preamble) DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(false), [0xEF, 0xBB, 0xBF]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(bigEndian: false, byteOrderMark: false), [0xFF, 0xFE]);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(bigEndian: true, byteOrderMark: false), [0xFE, 0xFF]);
        return (Encoding.UTF8.GetString(bytes), new UTF8Encoding(false), []);
    }

    public static byte[] EncodeText(string text, Encoding encoding, byte[] preamble)
    {
        byte[] body = encoding.GetBytes(text);
        if (preamble.Length == 0) return body;
        byte[] combined = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, combined, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, combined, preamble.Length, body.Length);
        return combined;
    }
}

/// <summary>
/// Lossless plain-text localization handler for .txt files located inside explicit localization
/// directories (Languages/, Localisation Files/, Localization/, Locales/, Translations/, Lang/, i18n/).
/// Auto-detects key=value (quoted or unquoted with [Section] and // # ; comments), tab-separated
/// key\tvalue, or line-by-line plain text layouts while preserving BOM, comments, and exact line endings.
/// </summary>
public sealed class LocalizationTextFormat : IFormatHandler
{
    public static LocalizationTextFormat Instance { get; } = new();
    public string Name => "loc-txt";

    private static readonly HashSet<string> ExcludedDocNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "readme", "license", "licenses", "credits", "changelog", "changes", "notes", "notice",
        "third-party", "third_party", "thirdparty", "eula", "privacy", "about", "install",
        "todo", "authors", "contributing", "copying", "patents", "steam_appid", "version"
    };

    private enum LayoutKind { KeyValue, TabSeparated, LineByLine }

    private sealed record LinePart(string Content, string NewLine);

    private sealed record EntrySpan(
        int LineIndex,
        string Locator,
        string Source,
        string Context,
        string Prefix,
        bool IsQuoted,
        string Suffix);

    public bool Supports(string path)
    {
        if (!path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return false;
        if (XUnityTranslationFormat.Instance.Supports(path)) return false;
        if (!ProjectService.IsExplicitLocalizationDirectory(path)) return false;
        string stem = Path.GetFileNameWithoutExtension(path).Trim();
        if (stem.Length == 0) return false;
        string normalizedStem = stem.Trim('_').Trim('-');
        if (ExcludedDocNames.Contains(normalizedStem)) return false;
        if (normalizedStem.StartsWith("readme", StringComparison.OrdinalIgnoreCase)
            || normalizedStem.StartsWith("license", StringComparison.OrdinalIgnoreCase)
            || normalizedStem.StartsWith("changelog", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public IReadOnlyList<TextSlot> Extract(byte[] bytes)
    {
        if (bytes.Length == 0 || LooksBinary(bytes)) return [];
        var (text, _, _) = TextEncodingHelper.DecodeText(bytes);
        var lines = SplitLines(text);
        var (_, entries) = ParseEntries(lines);
        return entries.Select(e => new TextSlot(e.Locator, e.Source, e.Context)).ToList();
    }

    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations)
    {
        if (bytes.Length == 0 || translations.Count == 0 || LooksBinary(bytes)) return bytes;
        var (text, encoding, preamble) = TextEncodingHelper.DecodeText(bytes);
        var lines = SplitLines(text);
        var (layout, entries) = ParseEntries(lines);
        if (entries.Count == 0) return bytes;

        var replacementsByLine = new Dictionary<int, string>();
        foreach (var entry in entries)
        {
            if (!translations.TryGetValue(entry.Locator, out string? target)) continue;
            if (target == entry.Source) continue;
            string encoded = layout switch
            {
                LayoutKind.KeyValue when entry.IsQuoted => entry.Prefix + EncodeQuoted(target) + entry.Suffix,
                LayoutKind.KeyValue => entry.Prefix + EscapeSingleLine(target) + entry.Suffix,
                LayoutKind.TabSeparated => entry.Prefix + EscapeSingleLine(target) + entry.Suffix,
                _ => SanitizeLineByLine(target)
            };
            replacementsByLine[entry.LineIndex] = encoded;
        }
        if (replacementsByLine.Count == 0) return bytes;

        var sb = new StringBuilder(text.Length + 64);
        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append(replacementsByLine.TryGetValue(i, out string? updated) ? updated : lines[i].Content);
            sb.Append(lines[i].NewLine);
        }
        return TextEncodingHelper.EncodeText(sb.ToString(), encoding, preamble);
    }

    private static bool LooksBinary(byte[] bytes)
    {
        bool hasUtf16Bom = bytes.Length >= 2
            && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF));
        if (hasUtf16Bom) return false;
        int check = Math.Min(bytes.Length, 4096);
        int badControl = 0;
        for (int i = 0; i < check; i++)
        {
            byte b = bytes[i];
            if (b == 0) return true;
            if (b < 0x20 && b is not ((byte)'\t' or (byte)'\r' or (byte)'\n' or 0x0C))
                badControl++;
        }
        return badControl * 50 > check;
    }

    private static List<LinePart> SplitLines(string text)
    {
        var lines = new List<LinePart>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    lines.Add(new(text[start..i], "\r\n"));
                    i++;
                    start = i + 1;
                }
                else
                {
                    lines.Add(new(text[start..i], "\r"));
                    start = i + 1;
                }
            }
            else if (text[i] == '\n')
            {
                lines.Add(new(text[start..i], "\n"));
                start = i + 1;
            }
        }
        if (start < text.Length) lines.Add(new(text[start..], ""));
        return lines;
    }

    private static bool IsCommentLine(string trimmed) =>
        trimmed.StartsWith("//", StringComparison.Ordinal)
        || trimmed.StartsWith('#')
        || trimmed.StartsWith(';');

    private static bool IsSectionHeader(string trimmed, out string section)
    {
        section = "";
        if (trimmed.Length >= 3 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            section = trimmed[1..^1].Trim();
            return section.Length > 0 && !section.Contains('[');
        }
        return false;
    }

    private static bool TrySplitKeyValue(string line, out string key, out string prefix, out string rawValue)
    {
        key = prefix = rawValue = "";
        int eq = line.IndexOf('=');
        if (eq <= 0) return false;
        string left = line[..eq];
        string trimmedKey = left.Trim();
        if (trimmedKey.Length == 0 || trimmedKey.Length > 120) return false;
        foreach (char c in trimmedKey)
        {
            if (!(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':' or '/' or '$'))
                return false;
        }
        int valStart = eq + 1;
        while (valStart < line.Length && line[valStart] is ' ' or '\t') valStart++;
        key = trimmedKey;
        prefix = line[..valStart];
        rawValue = line[valStart..];
        return rawValue.Length > 0;
    }

    private static bool TryParseQuotedValue(string rawValue, out string decoded, out string suffix)
    {
        decoded = suffix = "";
        if (rawValue.Length < 2 || rawValue[0] != '"') return false;
        var sb = new StringBuilder(rawValue.Length);
        for (int i = 1; i < rawValue.Length; i++)
        {
            char c = rawValue[i];
            if (c == '\\' && i + 1 < rawValue.Length)
            {
                char next = rawValue[++i];
                sb.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\',
                    _ => next
                });
            }
            else if (c == '"')
            {
                decoded = sb.ToString();
                suffix = rawValue[(i + 1)..];
                return true;
            }
            else
            {
                sb.Append(c);
            }
        }
        return false;
    }

    private static LayoutKind DetectLayout(List<LinePart> lines)
    {
        int candidates = 0, kvCount = 0, tabCount = 0;
        foreach (var part in lines)
        {
            string trimmed = part.Content.Trim();
            if (trimmed.Length == 0 || IsCommentLine(trimmed) || IsSectionHeader(trimmed, out _))
                continue;
            candidates++;
            if (TrySplitKeyValue(part.Content, out _, out _, out _))
                kvCount++;
            else
            {
                int tab = part.Content.IndexOf('\t');
                if (tab > 0 && !string.IsNullOrWhiteSpace(part.Content[..tab]) && !string.IsNullOrWhiteSpace(part.Content[(tab + 1)..]))
                    tabCount++;
            }
        }
        if (candidates == 0) return LayoutKind.LineByLine;
        if (kvCount >= 1 && kvCount * 2 >= candidates) return LayoutKind.KeyValue;
        if (tabCount >= 1 && tabCount * 2 >= candidates) return LayoutKind.TabSeparated;
        return LayoutKind.LineByLine;
    }

    private static (LayoutKind Layout, List<EntrySpan> Entries) ParseEntries(List<LinePart> lines)
    {
        var layout = DetectLayout(lines);
        var entries = new List<EntrySpan>();
        var usedLocators = new HashSet<string>(StringComparer.Ordinal);
        string section = "";
        string lastComment = "";

        string MakeUniqueLocator(string baseLocator, int lineIndex)
        {
            string loc = baseLocator;
            if (!usedLocators.Add(loc))
            {
                loc = $"{baseLocator}#{lineIndex + 1}";
                usedLocators.Add(loc);
            }
            return loc;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            string raw = lines[i].Content;
            string trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                lastComment = "";
                continue;
            }
            if (IsCommentLine(trimmed))
            {
                string commentText = trimmed.TrimStart('/', '#', ';').Trim();
                if (commentText.Length > 0) lastComment = commentText;
                continue;
            }

            if (layout == LayoutKind.KeyValue)
            {
                if (IsSectionHeader(trimmed, out string sec))
                {
                    section = sec;
                    lastComment = "";
                    continue;
                }
                if (!TrySplitKeyValue(raw, out string key, out string prefix, out string rawValue))
                {
                    lastComment = "";
                    continue;
                }
                bool isQuoted = TryParseQuotedValue(rawValue, out string decoded, out string suffix);
                string value = isQuoted ? decoded : rawValue.TrimEnd();
                if (!isQuoted) suffix = rawValue[value.Length..];
                if (string.IsNullOrWhiteSpace(value))
                {
                    lastComment = "";
                    continue;
                }
                string baseLoc = section.Length > 0 ? $"[{section}]{key}" : key;
                string locator = MakeUniqueLocator(baseLoc, i);
                string context = lastComment.Length > 0 ? lastComment : baseLoc;
                entries.Add(new(i, locator, value, context, prefix, isQuoted, suffix));
                lastComment = "";
            }
            else if (layout == LayoutKind.TabSeparated)
            {
                int tab = raw.IndexOf('\t');
                if (tab <= 0) { lastComment = ""; continue; }
                string key = raw[..tab].Trim();
                string valRaw = raw[(tab + 1)..];
                string value = valRaw.TrimEnd();
                if (key.Length == 0 || string.IsNullOrWhiteSpace(value)) { lastComment = ""; continue; }
                string suffix = valRaw[value.Length..];
                string locator = MakeUniqueLocator(key, i);
                string context = lastComment.Length > 0 ? lastComment : key;
                entries.Add(new(i, locator, value, context, raw[..(tab + 1)], false, suffix));
                lastComment = "";
            }
            else
            {
                string locator = $"line:{i + 1}";
                entries.Add(new(i, locator, raw, $"Line {i + 1}", "", false, ""));
                lastComment = "";
            }
        }

        return (layout, entries);
    }

    private static string EncodeQuoted(string value)
    {
        var sb = new StringBuilder(value.Length + 4);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string EscapeSingleLine(string value) =>
        value.Replace("\r\n", "\\n").Replace("\r", "\\n").Replace("\n", "\\n");

    private static string SanitizeLineByLine(string value) =>
        value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
}
