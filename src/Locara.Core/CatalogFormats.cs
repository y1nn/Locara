using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Locara.Core;

public sealed class PoFormat : IFormatHandler
{
    private const int ArabicForms = 6;
    private const string ArabicPluralRule =
        "nplurals=6; plural=(n==0?0:n==1?1:n==2?2:n%100>=3&&n%100<=10?3:n%100>=11&&n%100<=99?4:5);";

    private enum FieldKind { None, MsgCtxt, MsgId, MsgIdPlural, MsgStr, MsgStrPlural }

    private sealed class PoBlock
    {
        public bool IsMessage { get; init; }
        public List<string> RawLines { get; } = [];
        public List<string> CommentLines { get; } = [];
        public List<string> IdLines { get; } = [];
        public string MsgCtxt { get; set; } = "";
        public bool HasMsgId { get; set; }
        public string MsgId { get; set; } = "";
        public bool HasPlural { get; set; }
        public string MsgIdPlural { get; set; } = "";
        public string MsgStr { get; set; } = "";
        public Dictionary<int, string> Plurals { get; } = [];
        public bool IsHeader => IsMessage && HasMsgId && MsgId.Length == 0 && MsgCtxt.Length == 0;
        public string? RewrittenText { get; set; }
    }

    public string Name => "po";
    public bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".po" or ".pot";

    private static string Key(string context, string source) => context + "\u001f" + source;
    private static string FormKey(string key, int form) => key + "\u001f" + form;
    private static string FormField(int form) => $"msgstr[{form}]";

    private static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char ch in value)
        {
            sb.Append(ch switch
            {
                '\\' => @"\\",
                '"' => "\\\"",
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                '\a' => @"\a",
                '\b' => @"\b",
                '\f' => @"\f",
                '\v' => @"\v",
                _ => ch.ToString()
            });
        }
        return sb.Append('"').ToString();
    }

    private static string DecodePoString(string fragment)
    {
        string text = fragment.Trim();
        if (text.Length < 2 || text[0] != '"')
            throw new InvalidDataException("Invalid PO string literal.");
        var sb = new StringBuilder(text.Length - 2);
        int i = 1;
        bool closed = false;
        while (i < text.Length)
        {
            char ch = text[i++];
            if (ch == '"')
            {
                closed = true;
                break;
            }
            if (ch != '\\')
            {
                sb.Append(ch);
                continue;
            }
            if (i >= text.Length) throw new InvalidDataException("Unterminated escape in PO string.");
            char esc = text[i++];
            switch (esc)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case '\\': sb.Append('\\'); break;
                case '"': sb.Append('"'); break;
                case '\'': sb.Append('\''); break;
                case 'x':
                {
                    int start = i;
                    while (i < text.Length && i - start < 2 && Uri.IsHexDigit(text[i])) i++;
                    if (i == start) throw new InvalidDataException("Invalid hex escape in PO string.");
                    sb.Append((char)int.Parse(text[start..i], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    break;
                }
                case >= '0' and <= '7':
                {
                    int value = esc - '0';
                    for (int digits = 0; digits < 2 && i < text.Length && text[i] is >= '0' and <= '7'; digits++)
                        value = value * 8 + (text[i++] - '0');
                    sb.Append((char)value);
                    break;
                }
                default:
                    sb.Append(esc);
                    break;
            }
        }
        if (!closed || text[i..].Trim().Length > 0)
            throw new InvalidDataException("Invalid PO string literal.");
        return sb.ToString();
    }

    private static List<PoBlock> Parse(byte[] bytes)
    {
        string content = Encoding.UTF8.GetString(bytes).TrimStart((char)0xFEFF);
        string[] lines = content.Split('\n');
        var blocks = new List<PoBlock>();
        var pendingComments = new List<string>();
        PoBlock? current = null;
        FieldKind field = FieldKind.None;
        int pluralIndex = 0;

        void FinishMessage()
        {
            if (current == null) return;
            blocks.Add(current);
            current = null;
            field = FieldKind.None;
        }

        void FlushComments()
        {
            if (pendingComments.Count == 0) return;
            var raw = new PoBlock { IsMessage = false };
            raw.RawLines.AddRange(pendingComments);
            blocks.Add(raw);
            pendingComments.Clear();
        }

        void StartMessage()
        {
            current = new PoBlock { IsMessage = true };
            if (pendingComments.Count > 0)
            {
                current.CommentLines.AddRange(pendingComments);
                current.RawLines.AddRange(pendingComments);
                pendingComments.Clear();
            }
        }

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string line = lines[lineIndex].TrimEnd('\r');
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                FinishMessage();
                FlushComments();
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                if (current != null) FinishMessage();
                pendingComments.Add(line);
                continue;
            }

            if (TryKeyword(trimmed, "msgctxt", out string rest))
            {
                if (current != null) FinishMessage();
                StartMessage();
                current!.RawLines.Add(line);
                current.IdLines.Add(line);
                current.MsgCtxt += DecodePoString(rest);
                field = FieldKind.MsgCtxt;
                continue;
            }

            if (TryKeyword(trimmed, "msgid_plural", out rest))
            {
                if (current == null) throw new InvalidDataException($"Unexpected msgid_plural at line {lineIndex + 1}.");
                current.RawLines.Add(line);
                current.IdLines.Add(line);
                current.HasPlural = true;
                current.MsgIdPlural += DecodePoString(rest);
                field = FieldKind.MsgIdPlural;
                continue;
            }

            if (TryKeyword(trimmed, "msgid", out rest))
            {
                if (current != null && current.HasMsgId) FinishMessage();
                if (current == null) StartMessage();
                current!.RawLines.Add(line);
                current.IdLines.Add(line);
                current.HasMsgId = true;
                current.MsgId += DecodePoString(rest);
                field = FieldKind.MsgId;
                continue;
            }

            if (TryPluralMsgStr(trimmed, out int idx, out rest))
            {
                if (current == null) throw new InvalidDataException($"Unexpected msgstr[{idx}] at line {lineIndex + 1}.");
                current.RawLines.Add(line);
                pluralIndex = idx;
                current.Plurals[idx] = current.Plurals.GetValueOrDefault(idx, "") + DecodePoString(rest);
                field = FieldKind.MsgStrPlural;
                continue;
            }

            if (TryKeyword(trimmed, "msgstr", out rest))
            {
                if (current == null) throw new InvalidDataException($"Unexpected msgstr at line {lineIndex + 1}.");
                current.RawLines.Add(line);
                current.MsgStr += DecodePoString(rest);
                field = FieldKind.MsgStr;
                continue;
            }

            if (trimmed[0] == '"')
            {
                if (current == null || field == FieldKind.None)
                    throw new InvalidDataException($"Unexpected PO string continuation at line {lineIndex + 1}.");
                current.RawLines.Add(line);
                string decoded = DecodePoString(trimmed);
                switch (field)
                {
                    case FieldKind.MsgCtxt:
                        current.IdLines.Add(line);
                        current.MsgCtxt += decoded;
                        break;
                    case FieldKind.MsgId:
                        current.IdLines.Add(line);
                        current.MsgId += decoded;
                        break;
                    case FieldKind.MsgIdPlural:
                        current.IdLines.Add(line);
                        current.MsgIdPlural += decoded;
                        break;
                    case FieldKind.MsgStr:
                        current.MsgStr += decoded;
                        break;
                    case FieldKind.MsgStrPlural:
                        current.Plurals[pluralIndex] = current.Plurals.GetValueOrDefault(pluralIndex, "") + decoded;
                        break;
                }
                continue;
            }

            throw new InvalidDataException($"Unrecognized PO syntax at line {lineIndex + 1}.");
        }

        FinishMessage();
        FlushComments();
        return blocks;
    }

    private static bool TryKeyword(string trimmed, string keyword, out string rest)
    {
        if (trimmed.StartsWith(keyword, StringComparison.Ordinal)
            && trimmed.Length > keyword.Length
            && (trimmed[keyword.Length] is ' ' or '\t' or '"'))
        {
            rest = trimmed[keyword.Length..];
            return true;
        }
        rest = "";
        return false;
    }

    private static bool TryPluralMsgStr(string trimmed, out int index, out string rest)
    {
        index = 0;
        rest = "";
        if (!trimmed.StartsWith("msgstr[", StringComparison.Ordinal)) return false;
        int close = trimmed.IndexOf(']');
        if (close <= 7) return false;
        if (!int.TryParse(trimmed[7..close], NumberStyles.None, CultureInfo.InvariantCulture, out index)) return false;
        string after = trimmed[(close + 1)..];
        if (after.Length == 0 || after[0] is not (' ' or '\t' or '"')) return false;
        rest = after;
        return true;
    }

    private static string HeaderValue(PoBlock? header, string name)
    {
        if (header == null) return "";
        foreach (string line in header.MsgStr.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            int colon = trimmed.IndexOf(':');
            if (colon > 0 && trimmed[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return trimmed[(colon + 1)..].Trim();
        }
        return "";
    }

    public IReadOnlyList<TextSlot> Extract(byte[] bytes)
    {
        var blocks = Parse(bytes);
        bool import = Formats.IsArabicTarget(HeaderValue(blocks.FirstOrDefault(block => block.IsHeader), "Language"));
        List<TextSlot> slots = [];
        foreach (var block in blocks)
        {
            if (!block.IsMessage || !block.HasMsgId || string.IsNullOrWhiteSpace(block.MsgId)) continue;
            string key = Key(block.MsgCtxt, block.MsgId);
            if (!block.HasPlural)
                slots.Add(new(key, block.MsgId, block.MsgCtxt, import ? block.MsgStr : null));
            else
                for (int form = 0; form < ArabicForms; form++)
                    slots.Add(new(FormKey(key, form), form == 1 ? block.MsgId : block.MsgIdPlural, "Arabic plural form " + form,
                        import ? block.Plurals.GetValueOrDefault(form, "") : null));
        }
        return slots;
    }

    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations) =>
        Apply(bytes, translations, preserveLanguage: false);

    internal byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations, bool preserveLanguage)
    {
        var blocks = Parse(bytes);
        int headerIndex = blocks.FindIndex(block => block.IsHeader);
        PoBlock header = headerIndex >= 0 ? blocks[headerIndex] : CreateEmptyHeader();
        bool keepTargets = preserveLanguage || Formats.IsArabicTarget(HeaderValue(header, "Language"));
        bool hasPlural = false;
        bool matched = false;
        foreach (var block in blocks)
        {
            if (!block.IsMessage || !block.HasMsgId || string.IsNullOrWhiteSpace(block.MsgId)) continue;
            string key = Key(block.MsgCtxt, block.MsgId);
            if (block.HasPlural)
            {
                hasPlural = true;
                bool edited = Enumerable.Range(0, ArabicForms).Any(form => translations.ContainsKey(FormKey(key, form)));
                matched |= edited;
                var targets = Enumerable.Range(0, ArabicForms)
                    .Select(form => translations.GetValueOrDefault(FormKey(key, form))
                        ?? (keepTargets ? block.Plurals.GetValueOrDefault(form, "") : ""))
                    .ToArray();
                bool clearFuzzy = edited && targets.All(target => !string.IsNullOrWhiteSpace(target));
                if (edited || !keepTargets)
                    block.RewrittenText = FormatMessage(block, clearFuzzy,
                        targets.Select((target, form) => FormField(form) + " " + Quote(target)));
            }
            else if (translations.TryGetValue(key, out var target))
            {
                matched = true;
                block.RewrittenText = FormatMessage(block, clearFuzzy: true, ["msgstr " + Quote(target)]);
            }
            else if (!keepTargets)
            {
                block.RewrittenText = FormatMessage(block, clearFuzzy: false, ["msgstr \"\""]);
            }
        }
        if (!matched) return bytes;

        header.RewrittenText = RewriteHeader(header, hasPlural, preserveLanguage);
        if (headerIndex < 0) blocks.Insert(0, header);

        var serialized = blocks.Select(block => block.RewrittenText ?? string.Join('\n', block.RawLines));
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return Encoding.UTF8.GetBytes((bom ? "\uFEFF" : "") + string.Join("\n\n", serialized).TrimEnd() + "\n");
    }

    private static PoBlock CreateEmptyHeader()
    {
        var header = new PoBlock { IsMessage = true, HasMsgId = true, MsgId = "" };
        header.IdLines.Add("msgid \"\"");
        header.RawLines.Add("msgid \"\"");
        header.RawLines.Add("msgstr \"\"");
        return header;
    }

    private static string FormatMessage(PoBlock block, bool clearFuzzy, IEnumerable<string> targetFields)
    {
        var lines = new List<string>();
        foreach (string comment in block.CommentLines)
        {
            if (clearFuzzy && TryStripFuzzy(comment, out string? updated))
            {
                if (updated != null) lines.Add(updated);
            }
            else lines.Add(comment);
        }
        lines.AddRange(block.IdLines);
        lines.AddRange(targetFields);
        return string.Join('\n', lines);
    }

    private static bool TryStripFuzzy(string comment, out string? updated)
    {
        updated = comment;
        string trimmed = comment.TrimStart();
        if (!trimmed.StartsWith("#,", StringComparison.Ordinal)) return false;
        var flags = trimmed[2..].Split(',').Select(flag => flag.Trim()).Where(flag => flag.Length > 0).ToList();
        if (!flags.Remove("fuzzy")) return false;
        updated = flags.Count == 0 ? null : "#, " + string.Join(", ", flags);
        return true;
    }

    private static string RewriteHeader(PoBlock header, bool hasPlural, bool preserveLanguage = false)
    {
        string oldLanguage = HeaderValue(header, "Language");
        var values = header.MsgStr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList();
        string targetLang = preserveLanguage && oldLanguage.Length > 0
            ? oldLanguage
            : (oldLanguage.Length > 0 && Formats.IsArabicTarget(oldLanguage) ? oldLanguage : "ar");
        Set("Language", targetLang);
        Set("Content-Type", "text/plain; charset=UTF-8");
        if (!preserveLanguage && (hasPlural || HeaderValue(header, "Plural-Forms").Length > 0)) Set("Plural-Forms", ArabicPluralRule);
        return FormatMessage(header, clearFuzzy: true, ["msgstr \"\"", .. values.Select(line => Quote(line + "\n"))]);

        void Set(string name, string value)
        {
            string prefix = name + ":";
            int first = values.FindIndex(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            values.RemoveAll(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            values.Insert(first < 0 ? values.Count : first, prefix + " " + value);
        }
    }
}

public sealed class RenpyFormat : IFormatHandler
{
    public string Name => "renpy-template";
    public bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".rpy" or ".rpym";
    private const string Quoted = @"""(?:[^""\\]|\\.)*""";
    private static readonly Regex Header = new(@"^translate[ \t]+(?<language>\w+)[ \t]+(?<block>\w+)[ \t]*:");
    private static readonly Regex Strings = new(@"^[ \t]+(?<command>old|new)[ \t]+(?<text>" + Quoted + @")[ \t]*(?:#.*)?\r?$");
    private static readonly Regex Say = new(@"^[ \t]+(?:(?<speaker>[\w.]+|" + Quoted
        + @")(?:[ \t]+(?:-?[\w.]+|@))*[ \t]+)?(?<text>" + Quoted
        + @")[ \t]*(?:(?:\(|(?:with|id|nointeract)\b)[^\r\n]*)?(?:#.*)?\r?$");
    // Keep the identifiers already stored for statements accepted by the original extractor.
    private static readonly Regex OriginalSay = new(@"^[ \t]+(?:(?<speaker>[\w.]+)(?:[ \t]+[\w.]+)*[ \t]+)?(?<text>"
        + Quoted + @")[ \t]*(?:(?:with|id|nointeract)\b[^#\r\n]*)?(?:#.*)?\r?$");
    private static readonly Regex SourceComment = new(@"^([ \t]+)#[ \t]?(.*)$");
    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
    {
        "voice", "play", "queue", "stop", "show", "scene", "hide", "window", "call", "jump", "return", "new", "old",
        "pass", "pause", "nvl", "camera", "python", "with", "image", "define", "default"
    };

    private static string DecodeString(string literal)
    {
        // The SDK writes old/new as Python literals, which also have bell and vertical-tab escapes.
        string json = Regex.Replace(literal, @"\\.", match => match.Value[1] switch
        {
            'a' => @"\u0007",
            'v' => @"\u000b",
            '\'' => "'",
            _ => match.Value
        });
        return JsonSerializer.Deserialize<string>(json)!;
    }

    private static string DecodeSay(string literal)
    {
        // Ren'Py collapses literal spaces before reading escapes, including escaped spaces.
        string text = Regex.Replace(literal[1..^1], " +", " ");
        return Regex.Replace(text, @"\\(u(?<hex>[0-9a-fA-F]{1,4})|.)", match =>
        {
            var hex = match.Groups["hex"];
            if (hex.Success)
                return ((char)int.Parse(hex.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString();
            return match.Value[1] switch
            {
                '{' => "{{",
                '[' => "[[",
                '%' => "%%",
                'n' => "\n",
                'u' => "",
                char value => value.ToString()
            };
        });
    }

    private static string Encode(string text, bool dialogue)
    {
        var literal = new StringBuilder("\"");
        for (int index = 0; index < text.Length; index++)
        {
            char value = text[index];
            literal.Append(value switch
            {
                '\\' => @"\\",
                '"' => "\\\"",
                '\n' => @"\n",
                ' ' when dialogue && index > 0 && text[index - 1] == ' ' => @"\ ",
                _ when char.IsControl(value) => "\\u" + ((int)value).ToString("x4", CultureInfo.InvariantCulture),
                _ => value.ToString()
            });
        }
        return literal.Append('"').ToString();
    }

    private sealed record Entry(TextSlot Slot, int Line, int Start, int Length, bool Dialogue);

    private static List<Entry> Entries(string[] lines, Action<string>? advise = null)
    {
        List<Entry> result = [];
        string block = "";
        int index = 0;
        int additionalIndex = 0;
        bool otherLanguage = false;
        bool arabicTemplate = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            var header = Header.Match(line);
            if (header.Success)
            {
                string name = header.Groups["block"].Value;
                bool arabic = Formats.IsArabicTarget(header.Groups["language"].Value);
                block = !arabic || name is "python" or "style" or "early" ? "" : name;
                otherLanguage |= !arabic;
                arabicTemplate |= block.Length > 0;
                index = 0;
                additionalIndex = 0;
                continue;
            }
            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) block = "";
            if (block.Length == 0) continue;
            if (block == "strings")
            {
                var old = Strings.Match(line);
                if (!old.Success || old.Groups["command"].Value != "old" || i + 1 == lines.Length) continue;
                var next = Strings.Match(lines[i + 1]);
                if (!next.Success || next.Groups["command"].Value != "new")
                {
                    advise?.Invoke($"تعذّرت قراءة النص المقابل لـ old في قالب Ren’Py عند السطر {i + 1}.");
                    continue;
                }
                string menu = DecodeString(old.Groups["text"].Value);
                var literal = next.Groups["text"];
                result.Add(new(new("string:" + menu, menu, "menu/UI", DecodeString(literal.Value)),
                    i + 1, literal.Index, literal.Length, false));
                continue;
            }

            var dialogue = Say.Match(line);
            string speaker = dialogue.Groups["speaker"].Value;
            if (Commands.Contains(speaker) || line.TrimStart().StartsWith('#')) continue;
            var comment = i > 0 ? SourceComment.Match(lines[i - 1]) : Match.Empty;
            string originalLine = comment.Success ? comment.Groups[1].Value + comment.Groups[2].Value : "";
            var original = Say.Match(originalLine);
            bool hasOriginal = original.Success && !Commands.Contains(original.Groups["speaker"].Value);
            if (!dialogue.Success)
            {
                if (hasOriginal && !string.IsNullOrWhiteSpace(line))
                    advise?.Invoke($"تعذّرت قراءة عبارة الحوار في قالب Ren’Py عند السطر {i + 1}.");
                continue;
            }

            var target = dialogue.Groups["text"];
            string text = DecodeSay(target.Value);
            string? imported = null;
            if (hasOriginal)
            {
                imported = text;
                text = DecodeSay(original.Groups["text"].Value);
            }
            if (text.Length == 0) continue;
            bool existingLocator = OriginalSay.IsMatch(line)
                && (imported is not "" || OriginalSay.IsMatch(originalLine));
            string locator = existingLocator ? $"{block}/{index++}" : $"{block}/say/{additionalIndex++}";
            result.Add(new(new(locator, text, speaker, imported), i, target.Index, target.Length, true));
        }
        if (otherLanguage && !arabicTemplate)
            advise?.Invoke("هذا القالب يخص لغة أخرى. ولّد قالب العربية عبر Generate Translations وافتح game/tl/arabic.");
        return result;
    }

    public IReadOnlyList<TextSlot> Extract(byte[] bytes) => Extract(bytes, null);

    internal IReadOnlyList<TextSlot> Extract(byte[] bytes, Action<string>? advise) =>
        Entries(Encoding.UTF8.GetString(bytes).TrimStart((char)0xFEFF).Split('\n'), advise)
            .Select(entry => entry.Slot).ToList();

    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations)
    {
        string text = Encoding.UTF8.GetString(bytes);
        bool bom = text.Length > 0 && text[0] == (char)0xFEFF;
        string[] lines = (bom ? text[1..] : text).Split('\n');
        foreach (var entry in Entries(lines))
        {
            if (!translations.TryGetValue(entry.Slot.Locator, out var target)) continue;
            string original = lines[entry.Line];
            lines[entry.Line] = original[..entry.Start] + Encode(target, entry.Dialogue) + original[(entry.Start + entry.Length)..];
        }
        return Encoding.UTF8.GetBytes((bom ? "\uFEFF" : "") + string.Join('\n', lines));
    }
}
