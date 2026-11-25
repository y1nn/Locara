using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Locara.Core;

public static class Tokens
{
    private const string Common = @"\[(?:/?[a-z][a-z0-9_.:!=#-]*|[A-Z0-9_]{2,}(?:[=:#.-][^\s\]\r\n]+)?|\d+)\]|\{[^{}\r\n]+\}|%%|%(?:\([^)]+\))?[\d.$+#-]*(?:hh?|ll?|z)?[sdifuxXgeEc]|%[1-9]\d*|\\[A-Za-z]+(?:\[[^\]]*\])?|</?[A-Za-z][^>]*>";
    private static readonly Regex Standard = new(Common, RegexOptions.Compiled);
    private static readonly Regex RpgMaker = new(Common + @"|\\[.$|!><^{}\\]", RegexOptions.Compiled);
    private static readonly Regex PairedMarkup = new(
        @"<(?<closing>/)?(?<name>a|b|i|u|s|color|size|font|link|em|strong|pc|mrk|nobr|mark|style|align|alpha|cspace|sup|sub)(?=[\s=>/])[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static Regex Pattern(string engine) => engine == "rpgmaker" ? RpgMaker : Standard;
    public static List<string> Find(string text, string engine = "") =>
        Pattern(engine).Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal).ToList();

    public static string Validate(string source, string target, string engine = "")
    {
        var expected = Find(source, engine);
        var extra = Find(target, engine);
        var missing = expected.Where(token => !extra.Remove(token)).ToList();
        var issues = new List<string>();
        if (missing.Count > 0) issues.Add("رموز مفقودة: " + string.Join(" ", missing));
        if (extra.Count > 0) issues.Add("رموز زائدة: " + string.Join(" ", extra));
        if (FirstMisnestedTag(source) == null && FirstMisnestedTag(target) is { } badTag)
            issues.Add("ترتيب إغلاق وسم غير صحيح: " + badTag);
        return string.Join(" · ", issues);
    }

    internal static bool ValidTagNesting(string source, string target) =>
        FirstMisnestedTag(source) != null || FirstMisnestedTag(target) == null;

    private static string? FirstMisnestedTag(string text)
    {
        var open = new Stack<string>();
        foreach (Match tag in PairedMarkup.Matches(text))
        {
            if (tag.Value.EndsWith("/>", StringComparison.Ordinal)) continue;
            string name = tag.Groups["name"].Value;
            if (!tag.Groups["closing"].Success) open.Push(name);
            else if (!open.TryPop(out var expected) || !name.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return tag.Value;
        }
        return open.TryPeek(out var remaining) ? "<" + remaining + ">" : null;
    }
    internal static string ReplaceTokens(string text, Func<string, string> replacer, string engine = "") =>
        Pattern(engine).Replace(text, match => replacer(match.Value));

    internal static MatchCollection MatchTokens(string text, string engine = "") =>
        Pattern(engine).Matches(text);

    public static (string Text,Dictionary<string,string> Map) Mask(string text, string engine = "")
    {
        string prefix = "__LOCARA_";
        while (text.Contains(prefix, StringComparison.Ordinal)) prefix += "_";
        var map = new Dictionary<string, string>();
        string masked = Pattern(engine).Replace(text, match =>
        {
            string key = prefix + map.Count + "__";
            map[key] = match.Value;
            return key;
        });
        return (masked, map);
    }
    public static string Restore(string target,Dictionary<string,string> map){foreach(var pair in map)target=target.Replace(pair.Key,pair.Value,StringComparison.Ordinal);return target;}
}
public sealed record TokenUsage(long InputTokens = 0, long OutputTokens = 0, long TotalTokens = 0)
{
    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens, a.TotalTokens + b.TotalTokens);
}
public sealed record ChatTurn(string Role, string Content);
public sealed record StructuredResponse(string Content, TokenUsage Tokens);
public record ProviderOptions(string Provider,string Model,string ApiKey,string? BaseUrl=null,string? Region=null,int BatchSize=15,int MaxCharacters=50000,int MaxRequests=100,int? MaxUnits=null,string? CertificateSha256=null,bool JsonMode=false);
public record TranslationProgress(int Completed,int Total,string Message,TokenUsage? Tokens=null);
public record TranslationJob(string Id, string Label, string SourceScope, string Package, string Connection,
    Dictionary<string, long> Rows);
public record TranslationUsage(int Characters, int Requests, long InputTokens = 0, long OutputTokens = 0, long TotalTokens = 0);
public record TranslationSuggestion(Unit Original, string Target, string Warning);

public interface ITranslationProvider
{
    Task<Dictionary<string,string>> Translate(IReadOnlyList<Unit> units,IReadOnlyDictionary<string,string> glossary,CancellationToken cancellation);
}

public sealed partial class ApiProvider : ITranslationProvider
{
    private static readonly HttpClient Client=new(new HttpClientHandler { AllowAutoRedirect = false }){Timeout=TimeSpan.FromSeconds(180)};
    private readonly ProviderOptions options;
    private readonly string engine;
    public Uri Endpoint { get; }
    public TokenUsage LastTokens { get; private set; } = new();

    public ApiProvider(ProviderOptions options, string engine = "")
    {
        this.options = options;
        this.engine = engine;
        Endpoint = ResolveEndpoint(options);
    }

    public static Uri ResolveEndpoint(ProviderOptions options)
    {
        string provider = options.Provider.ToLowerInvariant();
        if (provider != "azure" && string.IsNullOrWhiteSpace(options.Model)) throw new ArgumentException("اختر نموذج الترجمة.");
        var address = ResolveBaseAddress(options);
        string route = provider switch
        {
            "gemini" => "/models/" + Uri.EscapeDataString(options.Model) + ":generateContent",
            "anthropic" => "/messages",
            "azure" => "/translate?api-version=3.0&to=ar",
            "deepseek" or "openai-compatible" => "/chat/completions",
            _ => throw new ArgumentException("Unknown translation provider.")
        };
        return new Uri(address.AbsoluteUri.TrimEnd('/') + route);
    }

    public static Uri ResolveBaseAddress(ProviderOptions options)
    {
        string? defaultBase = options.Provider.ToLowerInvariant() switch
        {
            "gemini" => "https://generativelanguage.googleapis.com/v1beta",
            "anthropic" => "https://api.anthropic.com/v1",
            "azure" => "https://api.cognitive.microsofttranslator.com",
            "deepseek" => "https://api.deepseek.com/v1",
            "openai-compatible" => null,
            _ => throw new ArgumentException("Unknown translation provider.")
        };
        if (!Uri.TryCreate(options.BaseUrl ?? defaultBase, UriKind.Absolute, out var address)
            || !(address.Scheme == "https" || (address.Scheme == "http" && address.IsLoopback))
            || address.UserInfo.Length + address.Fragment.Length + address.Query.Length != 0)
            throw new ArgumentException("أدخل عنوان API صالحاً مع HTTPS أو خادماً محلياً مع HTTP.");
        if (options.ApiKey.Any(char.IsControl) || options.Region?.Any(char.IsControl) == true)
            throw new ArgumentException("Invalid API header value.");
        CertificatePin(options, address);
        return address;
    }

    public async Task<Dictionary<string,string>> Translate(IReadOnlyList<Unit> units,IReadOnlyDictionary<string,string> glossary,CancellationToken cancellation)
    {
        var masked=units.Select(u=>(Unit:u,Mask:Tokens.Mask(u.Source, engine))).ToList();
        bool compactIds = units.Any(u => u.Id.Length > 16);
        var wireToReal = new Dictionary<string, string>(units.Count, StringComparer.Ordinal);
        var realToWire = new Dictionary<string, string>(units.Count, StringComparer.Ordinal);
        for (int i = 0; i < units.Count; i++)
        {
            string wireId = compactIds ? (i + 1).ToString() : units[i].Id;
            wireToReal[wireId] = units[i].Id;
            realToWire[units[i].Id] = wireId;
        }
        string prompt="Translate game text to Arabic. Preserve each __LOCARA_n__ token exactly. Use context and terminology. Return only a flat JSON object like {\"<supplied id>\":\"<Arabic translation>\"}, using every exact input id once. Do not copy the input envelope or its context fields.\n"+JsonSerializer.Serialize(new{glossary,entries=masked.Select(x=>new{id=realToWire[x.Unit.Id],text=x.Mask.Text,context=x.Unit.Context})});
        string provider=options.Provider.ToLowerInvariant();
        bool jsonObject = provider == "deepseek" || (provider == "openai-compatible" && options.JsonMode);
        object body = provider switch
        {
            "gemini" => new { contents = new[] { new { parts = new[] { new { text = prompt } } } }, generationConfig = new { responseMimeType = "application/json" } },
            "anthropic" => new { model = options.Model, max_tokens = 8192, messages = new[] { new { role = "user", content = prompt } } },
            "azure" => masked.Select(x => new { Text = x.Mask.Text }).ToArray(),
            _ when jsonObject => new { model = options.Model, messages = new[] { new { role = "user", content = prompt } }, temperature = 0.2, response_format = new { type = "json_object" } },
            _ => new { model = options.Model, messages = new[] { new { role = "user", content = prompt } }, temperature = 0.2 }
        };
        using var doc = await Send(body, cancellation);
        LastTokens = ExtractUsage(doc.RootElement);
        Dictionary<string,string> result;
        if(provider=="azure")
        {
            var rows=doc.RootElement.EnumerateArray().ToList();if(rows.Count!=units.Count)throw new InvalidDataException("Provider result count differs.");
            result=units.Select((u,i)=>(u.Id,Text:rows[i].GetProperty("translations")[0].GetProperty("text").GetString()!)).ToDictionary(x=>x.Id,x=>x.Text);
        }
        else
        {
            string content = ExtractMessage(doc.RootElement);
            var blankUnitIds = masked.Where(m => string.IsNullOrWhiteSpace(m.Unit.Source))
                .Select(m => m.Unit.Id).ToHashSet(StringComparer.Ordinal);
            var allowBlank = wireToReal.Where(kv => blankUnitIds.Contains(kv.Value))
                .Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
            var byWire = ReadTranslations(content, wireToReal.Keys, allowBlank.Count > 0 ? allowBlank : null);
            result = byWire.ToDictionary(pair => wireToReal[pair.Key], pair => pair.Value, StringComparer.Ordinal);
        }
        foreach(var item in masked){string t=Tokens.Restore(result[item.Unit.Id],item.Mask.Map);if(string.IsNullOrWhiteSpace(t) && !string.IsNullOrWhiteSpace(item.Unit.Source))throw new InvalidDataException("Empty translation.");result[item.Unit.Id]=string.IsNullOrWhiteSpace(item.Unit.Source)?item.Unit.Source:t;}
        return result;
    }

    internal static readonly JsonDocumentOptions LenientJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly HashSet<string> WrapperKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "entries", "translations", "results", "items", "reviews", "data", "output"
    };

    internal static Dictionary<string, string> ReadTranslations(string content, IEnumerable<string> ids,
        IReadOnlySet<string>? allowBlankIds = null)
    {
        var parsed = ReadTranslationsCore(content, ids, allowBlankIds, allowPartial: false, out _);
        return parsed;
    }

    internal static Dictionary<string, string> ReadTranslationsPartial(string content, IEnumerable<string> ids,
        out List<string> missingIds, IReadOnlySet<string>? allowBlankIds = null)
    {
        return ReadTranslationsCore(content, ids, allowBlankIds, allowPartial: true, out missingIds);
    }

    internal static Dictionary<string, string> ReadTranslationsAndTermsPartial(string content, IEnumerable<string> ids,
        out List<string> missingIds, out Dictionary<string, string> newTerms, IReadOnlySet<string>? allowBlankIds = null)
    {
        var idList = ids as IReadOnlyCollection<string> ?? ids.ToList();
        var expectedSet = idList.ToHashSet(StringComparer.Ordinal);
        newTerms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string effectiveContent = content;
        try
        {
            using var doc = JsonDocument.Parse(CleanJsonPayload(content), LenientJson);
            var root = doc.RootElement;
            // Unwrap outer single-key wrapper (e.g. {"data": {"translations": {...}, "newTerms": {...}}}) when not an expected ID
            for (int depth = 0; depth < 2 && root.ValueKind == JsonValueKind.Object; depth++)
            {
                var props = root.EnumerateObject().ToArray();
                if (props.Length == 1 && !expectedSet.Contains(props[0].Name) && WrapperKeys.Contains(props[0].Name)
                    && props[0].Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    root = props[0].Value;
                    continue;
                }
                break;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                bool rootHasDirectExpectedIds = root.EnumerateObject().Any(p => expectedSet.Contains(p.Name));
                JsonElement? transNode = null;
                foreach (var prop in root.EnumerateObject())
                {
                    if (expectedSet.Contains(prop.Name)) continue;

                    if (!rootHasDirectExpectedIds && WrapperKeys.Contains(prop.Name)
                        && prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        transNode ??= prop.Value;
                    }
                    else if ((prop.Name.Equals("newTerms", StringComparison.OrdinalIgnoreCase)
                              || prop.Name.Equals("glossary", StringComparison.OrdinalIgnoreCase)
                              || prop.Name.Equals("terms", StringComparison.OrdinalIgnoreCase))
                             && prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var termProp in prop.Value.EnumerateObject())
                        {
                            if (termProp.Value.ValueKind != JsonValueKind.String) continue;
                            string k = termProp.Name.Trim();
                            string v = (termProp.Value.GetString() ?? "").Trim();
                            if (k.Length >= 2 && k.Length <= 80 && v.Length >= 1 && v.Length <= 100
                                && !k.Contains("__LOCARA_", StringComparison.Ordinal)
                                && !v.Contains("__LOCARA_", StringComparison.Ordinal))
                            {
                                newTerms[k] = v;
                            }
                        }
                    }
                }
                if (transNode.HasValue)
                    effectiveContent = transNode.Value.GetRawText();
                else if (rootHasDirectExpectedIds)
                    effectiveContent = root.GetRawText();
            }
        }
        catch (JsonException)
        {
            // Fall through to ReadTranslationsCore which emits the standard Arabic error
        }

        return ReadTranslationsCore(effectiveContent, idList, allowBlankIds, allowPartial: true, out missingIds);
    }

    private static Dictionary<string, string> ReadTranslationsCore(string content, IEnumerable<string> ids,
        IReadOnlySet<string>? allowBlankIds, bool allowPartial, out List<string> missingIds)
    {
        const string invalid = "أعاد النموذج دفعة بصيغة غير صالحة. يلزم نص ترجمة واحد لكل معرّف مطلوب. لم تُحفظ نتائج هذه الدفعة.";
        var remaining = ids.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        static string? ExtractTextFromValue(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind != JsonValueKind.Object) return null;
            foreach (string key in new[] { "translation", "target", "value", "ar", "arabic", "text" })
            {
                foreach (var prop in value.EnumerateObject())
                {
                    if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                        return prop.Value.GetString();
                }
            }
            return null;
        }

        static string? ReadIdElement(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            _ => null
        };

        void Add(string? id, JsonElement value)
        {
            if (id == null)
            {
                if (!allowPartial) throw new InvalidDataException(invalid);
                return;
            }
            if (!remaining.Contains(id))
            {
                if (result.ContainsKey(id) || !allowPartial)
                    throw new InvalidDataException(invalid);
                return; // Ignore extra commentary/unexpected key in partial salvage mode
            }
            string? text = ExtractTextFromValue(value);
            if (text == null)
            {
                if (!allowPartial) throw new InvalidDataException(invalid);
                return;
            }
            if (string.IsNullOrWhiteSpace(text) && allowBlankIds?.Contains(id) != true)
            {
                if (!allowPartial) throw new InvalidDataException(invalid);
                return;
            }
            remaining.Remove(id);
            result.Add(id, text);
        }

        try
        {
            using var document = JsonDocument.Parse(CleanJsonPayload(content), LenientJson);
            var payload = UnwrapSingleContainer(document.RootElement);

            if (payload.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in payload.EnumerateObject()) Add(property.Name, property.Value);
            }
            else if (payload.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in payload.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        if (!allowPartial) throw new InvalidDataException(invalid);
                        continue;
                    }
                    var fields = entry.EnumerateObject().ToArray();
                    var identifiers = fields.Where(field => field.Name.Equals("id", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (identifiers.Length != 1)
                    {
                        if (!allowPartial) throw new InvalidDataException(invalid);
                        continue;
                    }
                    string? entryId = ReadIdElement(identifiers[0].Value);
                    JsonProperty? targetProp = fields.Cast<JsonProperty?>().FirstOrDefault(f =>
                        f!.Value.Name.Equals("translation", StringComparison.OrdinalIgnoreCase)
                        || f.Value.Name.Equals("target", StringComparison.OrdinalIgnoreCase)
                        || f.Value.Name.Equals("value", StringComparison.OrdinalIgnoreCase)
                        || f.Value.Name.Equals("ar", StringComparison.OrdinalIgnoreCase)
                        || f.Value.Name.Equals("arabic", StringComparison.OrdinalIgnoreCase));
                    targetProp ??= fields.Cast<JsonProperty?>().FirstOrDefault(f =>
                        f!.Value.Name.Equals("text", StringComparison.OrdinalIgnoreCase));
                    if (entryId == null || targetProp == null)
                    {
                        if (!allowPartial) throw new InvalidDataException(invalid);
                        continue;
                    }
                    Add(entryId, targetProp.Value.Value);
                }
            }
            else throw new InvalidDataException(invalid);
        }
        catch (JsonException error) { throw new InvalidDataException(invalid, error); }

        missingIds = remaining.ToList();
        if (!allowPartial && remaining.Count > 0)
            throw new InvalidDataException($"{invalid} (المفقود: {remaining.Count})");
        if (allowPartial && result.Count == 0 && remaining.Count > 0)
            throw new InvalidDataException(invalid);
        return result;
    }

    internal static JsonElement UnwrapSingleContainer(JsonElement payload)
    {
        for (int depth = 0; depth < 2 && payload.ValueKind == JsonValueKind.Object; depth++)
        {
            var props = payload.EnumerateObject().ToArray();
            if (props.Length == 1 && WrapperKeys.Contains(props[0].Name)
                && props[0].Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                payload = props[0].Value;
                continue;
            }
            break;
        }
        return payload;
    }

    internal async Task<string> RequestStructured(string instructions, string data, CancellationToken cancellation) =>
        (await RequestStructuredWithUsage(instructions, data, cancellation)).Content;

    internal Task<StructuredResponse> RequestStructuredWithUsage(string instructions, string data, CancellationToken cancellation) =>
        RequestChatWithUsage(instructions, null, data, cancellation);

    internal async Task<StructuredResponse> RequestChatWithUsage(string instructions, IReadOnlyList<ChatTurn>? history,
        string data, CancellationToken cancellation)
    {
        string provider = options.Provider.ToLowerInvariant();
        if (provider == "azure")
            throw new NotSupportedException("اختر مزوّداً يدعم تعليمات النظام لفريق الترجمة.");

        var normalizedTurns = NormalizeAlternatingHistory(history);
        bool jsonObject = provider == "deepseek" || (provider == "openai-compatible" && options.JsonMode);

        object body;
        if (provider == "gemini")
        {
            var contents = new List<object>(normalizedTurns.Count + 1);
            foreach (var turn in normalizedTurns)
            {
                string gRole = turn.Role == "assistant" ? "model" : "user";
                contents.Add(new { role = gRole, parts = new[] { new { text = turn.Content } } });
            }
            contents.Add(new { role = "user", parts = new[] { new { text = data } } });
            body = new
            {
                systemInstruction = new { parts = new[] { new { text = instructions } } },
                contents,
                generationConfig = new { responseMimeType = "application/json" }
            };
        }
        else if (provider == "anthropic")
        {
            var messages = new List<object>(normalizedTurns.Count + 1);
            foreach (var turn in normalizedTurns)
                messages.Add(new { role = turn.Role, content = turn.Content });
            messages.Add(new { role = "user", content = data });
            body = new { model = options.Model, max_tokens = 8192, system = instructions, messages };
        }
        else
        {
            var messages = new List<object>(normalizedTurns.Count + 2)
            {
                new { role = "system", content = instructions }
            };
            foreach (var turn in normalizedTurns)
                messages.Add(new { role = turn.Role, content = turn.Content });
            messages.Add(new { role = "user", content = data });
            body = jsonObject
                ? new { model = options.Model, messages, response_format = new { type = "json_object" } }
                : new { model = options.Model, messages };
        }

        using var response = await Send(body, cancellation);
        var tokens = ExtractUsage(response.RootElement);
        LastTokens = tokens;
        return new(ExtractMessage(response.RootElement), tokens);
    }

    private static List<ChatTurn> NormalizeAlternatingHistory(IReadOnlyList<ChatTurn>? history)
    {
        if (history == null || history.Count == 0) return [];
        var clean = new List<ChatTurn>(history.Count);
        string? pendingUser = null;
        foreach (var turn in history)
        {
            if (turn == null || string.IsNullOrWhiteSpace(turn.Content)) continue;
            string rawRole = turn.Role ?? "";
            bool isAssistant = rawRole.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                || rawRole.Equals("model", StringComparison.OrdinalIgnoreCase);
            if (!isAssistant)
            {
                // Latest user turn replaces any previous unpaired user turn
                pendingUser = turn.Content;
            }
            else if (pendingUser != null)
            {
                clean.Add(new ChatTurn("user", pendingUser));
                clean.Add(new ChatTurn("assistant", turn.Content));
                pendingUser = null;
            }
        }
        return clean;
    }

    internal TokenUsage ExtractUsage(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object) return new();
        static long ReadLong(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out long val) ? val : 0;

        string provider = options.Provider.ToLowerInvariant();
        if (provider == "gemini")
        {
            if (!response.TryGetProperty("usageMetadata", out var meta) || meta.ValueKind != JsonValueKind.Object) return new();
            long input = ReadLong(meta, "promptTokenCount");
            long output = ReadLong(meta, "candidatesTokenCount");
            long total = ReadLong(meta, "totalTokenCount");
            if (total == 0) total = input + output + ReadLong(meta, "thoughtsTokenCount");
            return new(input, output, total);
        }
        if (provider == "anthropic")
        {
            if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return new();
            long input = ReadLong(usage, "input_tokens");
            long output = ReadLong(usage, "output_tokens");
            return new(input, output, input + output);
        }
        if (response.TryGetProperty("usage", out var openai) && openai.ValueKind == JsonValueKind.Object)
        {
            long input = ReadLong(openai, "prompt_tokens");
            if (input == 0) input = ReadLong(openai, "input_tokens");
            long output = ReadLong(openai, "completion_tokens");
            if (output == 0) output = ReadLong(openai, "output_tokens");
            long total = ReadLong(openai, "total_tokens");
            if (total == 0) total = input + output;
            return new(input, output, total);
        }
        return new();
    }

    internal static string CleanJsonPayload(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        // Strip <think>...</think> reasoning blocks emitted by reasoning models (DeepSeek-R1, Qwen, local OpenAI servers)
        string stripped = Regex.Replace(text, @"<think\b[^>]*>[\s\S]*?</think>", "", RegexOptions.IgnoreCase).Trim();
        if (stripped.Length == 0) stripped = text.Trim();

        // Check fenced ```json ... ``` blocks anywhere inside prose (prefer last valid block)
        var fenceMatches = Regex.Matches(stripped, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        for (int i = fenceMatches.Count - 1; i >= 0; i--)
        {
            string fenced = fenceMatches[i].Groups[1].Value.Trim();
            if (TryNormalizeJsonCandidate(fenced, out string normalized))
                return normalized;
        }

        string trimmed = Regex.Replace(stripped, @"^```(?:json)?\s*|\s*```$", "", RegexOptions.IgnoreCase).Trim();
        if (trimmed.Length == 0) return trimmed;
        if (TryNormalizeJsonCandidate(trimmed, out string direct))
            return direct;

        int objStart = trimmed.IndexOf('{');
        int arrStart = trimmed.IndexOf('[');
        int start = (objStart, arrStart) switch
        {
            ( >= 0, >= 0) => Math.Min(objStart, arrStart),
            ( >= 0, _) => objStart,
            (_, >= 0) => arrStart,
            _ => -1
        };
        if (start < 0) return trimmed;

        char close = trimmed[start] == '{' ? '}' : ']';
        int end = trimmed.LastIndexOf(close);
        if (end > start)
        {
            string candidate = trimmed[start..(end + 1)];
            if (TryNormalizeJsonCandidate(candidate, out string normalized))
                return normalized;
            if (TryMergeConcatenatedObjects(candidate, out string merged))
                return merged;
        }

        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int index = start; index < trimmed.Length; index++)
        {
            char ch = trimmed[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
            }
            else if (ch == '"') inString = true;
            else if (ch is '{' or '[') depth++;
            else if (ch is '}' or ']' && --depth == 0)
            {
                string slice = trimmed[start..(index + 1)];
                if (TryNormalizeJsonCandidate(slice, out string normalized))
                    return normalized;
                break;
            }
        }
        return trimmed;
    }

    private static bool TryNormalizeJsonCandidate(string candidate, out string normalized)
    {
        normalized = candidate;
        if (candidate.Length < 2 || !((candidate[0] == '{' && candidate[^1] == '}') || (candidate[0] == '[' && candidate[^1] == ']')))
            return false;
        if (IsValidJson(candidate, strict: true))
        {
            normalized = candidate;
            return true;
        }
        if (IsValidJson(candidate, strict: false))
        {
            using var doc = JsonDocument.Parse(candidate, LenientJson);
            normalized = doc.RootElement.GetRawText();
            return true;
        }
        string repaired = TryRepairUnescapedJsonQuotes(candidate);
        if (repaired != candidate && IsValidJson(repaired, strict: false))
        {
            using var doc = JsonDocument.Parse(repaired, LenientJson);
            normalized = doc.RootElement.GetRawText();
            return true;
        }
        return false;
    }

    private static bool IsValidJson(string text, bool strict)
    {
        try
        {
            using var _ = strict ? JsonDocument.Parse(text) : JsonDocument.Parse(text, LenientJson);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryMergeConcatenatedObjects(string text, out string merged)
    {
        merged = "";
        var objects = new List<string>();
        int pos = 0;
        while (pos < text.Length)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
            if (pos >= text.Length) break;
            if (text[pos] != '{') return false;
            int start = pos;
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            int foundEnd = -1;
            for (int i = start; i < text.Length; i++)
            {
                char ch = text[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (ch == '\\') escaped = true;
                    else if (ch == '"') inString = false;
                }
                else if (ch == '"') inString = true;
                else if (ch == '{') depth++;
                else if (ch == '}' && --depth == 0)
                {
                    foundEnd = i;
                    break;
                }
            }
            if (foundEnd < 0) return false;
            string slice = text[start..(foundEnd + 1)];
            if (!TryNormalizeJsonCandidate(slice, out string normObj)) return false;
            objects.Add(normObj);
            pos = foundEnd + 1;
        }
        if (objects.Count < 2) return false;
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            foreach (string objText in objects)
            {
                using var doc = JsonDocument.Parse(objText, LenientJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
                foreach (var prop in doc.RootElement.EnumerateObject())
                    prop.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        merged = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        return true;
    }

    private static string TryRepairUnescapedJsonQuotes(string json)
    {
        var sb = new System.Text.StringBuilder(json.Length + 16);
        bool inString = false;
        bool escaped = false;

        int NextNonWs(int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
            return index;
        }

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (!inString)
            {
                if (c == '"') inString = true;
                sb.Append(c);
                continue;
            }
            if (escaped)
            {
                escaped = false;
                sb.Append(c);
                continue;
            }
            if (c == '\\')
            {
                escaped = true;
                sb.Append(c);
                continue;
            }
            if (c == '"')
            {
                int nextIdx = NextNonWs(i + 1);
                bool validTerminator = false;
                if (nextIdx >= json.Length)
                {
                    validTerminator = true;
                }
                else
                {
                    char next = json[nextIdx];
                    if (next is ':' or '}' or ']')
                    {
                        validTerminator = true;
                    }
                    else if (next == ',')
                    {
                        int afterComma = NextNonWs(nextIdx + 1);
                        if (afterComma < json.Length && json[afterComma] is '"' or '{' or '[' or '}' or ']' or '-' or (>= '0' and <= '9') or 't' or 'f' or 'n')
                            validTerminator = true;
                    }
                }
                if (validTerminator)
                {
                    inString = false;
                    sb.Append('"');
                }
                else
                {
                    sb.Append("\\\"");
                }
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string? ExtractGeminiText(JsonElement response)
    {
        if (!response.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
        {
            string reason = response.TryGetProperty("promptFeedback", out var fb) && fb.ValueKind == JsonValueKind.Object
                && fb.TryGetProperty("blockReason", out var br) && br.ValueKind == JsonValueKind.String
                ? br.GetString() ?? "" : "";
            throw new InvalidDataException(reason.Length > 0
                ? $"حظر مزوّد Gemini الاستجابة ({reason})."
                : "أعاد مزوّد Gemini استجابة بلا نتائج (candidates فارغة).");
        }
        var first = candidates[0];
        string finishReason = first.TryGetProperty("finishReason", out var fr) && fr.ValueKind == JsonValueKind.String
            ? fr.GetString() ?? "" : "";
        if (!first.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(finishReason.Length > 0
                ? $"أنهى مزوّد Gemini الاستجابة بدون نص ({finishReason})."
                : "استجابة مزوّد Gemini لا تحتوي نصاً قابلاً للقراءة.");
        }

        var answer = new System.Text.StringBuilder();
        var fallback = new System.Text.StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (!part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) continue;
            string? value = text.GetString();
            if (string.IsNullOrEmpty(value)) continue;
            fallback.Append(value);
            bool isThought = part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True;
            if (!isThought) answer.Append(value);
        }
        if (answer.Length > 0) return answer.ToString();
        if (fallback.Length > 0) return fallback.ToString();
        if (finishReason.Length > 0)
            throw new InvalidDataException($"أنهى مزوّد Gemini الاستجابة بنص فارغ ({finishReason}).");
        return null;
    }

    private static string? ExtractAnthropicText(JsonElement response)
    {
        if (!response.TryGetProperty("content", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("استجابة Anthropic لا تحتوي محتوى نصياً.");
        var answer = new System.Text.StringBuilder();
        foreach (var block in blocks.EnumerateArray())
        {
            if ((!block.TryGetProperty("type", out var type) || type.GetString() == "text")
                && block.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
                answer.Append(value.GetString());
        }
        if (answer.Length > 0) return answer.ToString();
        string stopReason = response.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String
            ? sr.GetString() ?? "" : "";
        if (stopReason.Length > 0)
            throw new InvalidDataException($"أنهى مزوّد Anthropic الاستجابة بدون نص ({stopReason}).");
        return null;
    }

    private static string? ExtractOpenAiText(JsonElement response)
    {
        if (!response.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new InvalidDataException("أعاد المزوّد استجابة بلا اختيارات (choices فارغة).");
        var first = choices[0];
        string finishReason = first.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String
            ? fr.GetString() ?? "" : "";
        if (first.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                string? value = content.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            if (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(refusal.GetString()))
                throw new InvalidDataException($"رفض النموذج معالجة الدفعة ({refusal.GetString()}).");
        }
        if (finishReason.Length > 0)
            throw new InvalidDataException($"أنهى النموذج الاستجابة بدون نص ({finishReason}).");
        return null;
    }

    private string ExtractMessage(JsonElement response)
    {
        string? text = options.Provider.ToLowerInvariant() switch
        {
            "gemini" => ExtractGeminiText(response),
            "anthropic" => ExtractAnthropicText(response),
            _ => ExtractOpenAiText(response)
        };
        return CleanJsonPayload(text ?? throw new InvalidDataException("استجابة المزوّد فارغة."));
    }

    internal static async Task<string> ReadErrorDetail(HttpResponseMessage response, CancellationToken cancellation)
    {
        try
        {
            string raw = await response.Content.ReadAsStringAsync(cancellation);
            if (string.IsNullOrWhiteSpace(raw)) return "";
            if (raw.Length > 4096) raw = raw[..4096];
            string extracted = raw;
            try
            {
                using var json = JsonDocument.Parse(raw);
                var root = json.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("error", out var error))
                    {
                        if (error.ValueKind == JsonValueKind.Object
                            && error.TryGetProperty("message", out var nested) && nested.ValueKind == JsonValueKind.String)
                            extracted = nested.GetString() ?? raw;
                        else if (error.ValueKind == JsonValueKind.String)
                            extracted = error.GetString() ?? raw;
                    }
                    else if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                        extracted = message.GetString() ?? raw;
                    else if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                        extracted = detail.GetString() ?? raw;
                }
            }
            catch (JsonException) { }
            string normalized = Regex.Replace(extracted, @"[\r\n\t\p{Cc}]+", " ").Trim();
            return normalized.Length > 200 ? normalized[..200].TrimEnd() + "…" : normalized;
        }
        catch (Exception error) when (error is IOException or HttpRequestException or NotSupportedException)
        {
            return "";
        }
    }

    private async Task<JsonDocument> Send(object body, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(body) };
        Authenticate(request, options);
        using var response=await SendRequest(request, options, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await ReadErrorDetail(response, cancellation);
            throw new HttpRequestException(
                "Provider returned HTTP " + (int)response.StatusCode + ". Inspect quota, credentials and model. Retry this batch explicitly."
                + (detail.Length > 0 ? " (" + detail + ")" : ""),
                null, response.StatusCode);
        }
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
    }

    private static void Authenticate(HttpRequestMessage request, ProviderOptions options)
    {
        string provider = options.Provider.ToLowerInvariant();
        if(provider=="gemini")request.Headers.Add("x-goog-api-key",options.ApiKey);
        else if(provider=="anthropic"){request.Headers.Add("x-api-key",options.ApiKey);request.Headers.Add("anthropic-version","2023-06-01");}
        else if(provider=="azure"){request.Headers.Add("Ocp-Apim-Subscription-Key",options.ApiKey);if(!string.IsNullOrWhiteSpace(options.Region))request.Headers.Add("Ocp-Apim-Subscription-Region",options.Region);}
        else if(options.ApiKey.Length>0)request.Headers.Authorization=new("Bearer",options.ApiKey);
    }
}

public sealed class TranslationRunner(ProjectStore store)
{
    private sealed record ScanState(string Source, string ScannedSource, string Scope, string Package,
        string Version, string Engine, string ScannedEngine);

    private ScanState ReadScanState()
    {
        var settings = store.Query("""
            SELECT key,value FROM settings
            WHERE key IN ('source','scanned_source','source_scope','scanned_package','scan_version','engine','scanned_engine')
            """, row => (Key: row.GetString(0), Value: row.GetString(1))).ToDictionary(row => row.Key, row => row.Value);
        string Get(string key) => settings.GetValueOrDefault(key) ?? "";
        return new(Get("source"), Get("scanned_source"), Get("source_scope"), Get("scanned_package"),
            Get("scan_version"), Get("engine"), Get("scanned_engine"));
    }

    public (int Units, int Characters) Estimate() => store.Query(
        "SELECT COUNT(*),COALESCE(SUM(length(source)),0) FROM units WHERE active=1 AND status='pending'",
        row => (row.GetInt32(0), row.GetInt32(1)))[0];

    private string JobSetting => "translation-job:" + store.Get("source_scope");
    public TranslationJob? LastJob => store.Get(JobSetting) is { Length: > 0 } saved
        ? JsonSerializer.Deserialize<TranslationJob>(saved) : null;

    internal static string Connection(ProviderOptions options) => JsonSerializer.Serialize(new
    {
        provider = options.Provider.ToLowerInvariant(), options.Model, endpoint = ApiProvider.ResolveEndpoint(options).AbsoluteUri,
        options.Region
    });

    public TranslationJob Capture(string label, IEnumerable<Unit> selection, ProviderOptions options) =>
        Capture(label, selection.Where(unit => unit.Status == "pending").DistinctBy(unit => unit.Id)
            .ToDictionary(unit => unit.Id, unit => unit.Revision, StringComparer.Ordinal), options);

    public TranslationJob Capture(string label, Dictionary<string, long> selection, ProviderOptions options)
    {
        var scan = ReadScanState();
        if (scan.ScannedSource.Length == 0 || scan.ScannedSource != scan.Source)
            throw new InvalidOperationException("افحص المصدر قبل إرسال النصوص.");
        return new(Guid.NewGuid().ToString("N"), label, scan.Scope, scan.Package, Connection(options), selection);
    }

    public (int Units, int Characters) Estimate(TranslationJob job, int? maximumUnits = null)
    {
        var rows = Pending(JsonSerializer.Serialize(job.Rows), maximumUnits ?? -1);
        return (rows.Count, rows.Sum(unit => unit.Source.Length));
    }

    public bool HasPending(TranslationJob job) => store.Query("""
        SELECT EXISTS(
            SELECT 1 FROM json_each($selection) AS selected CROSS JOIN units
            WHERE units.id=selected.key AND units.revision=selected.value AND active=1 AND status='pending'
        )
        """, row => row.GetBoolean(0), ("$selection", JsonSerializer.Serialize(job.Rows))).Single();

    public TranslationUsage Usage(TranslationJob job) => store.Get("translation-usage:" + job.Id) is { Length: > 0 } saved
        ? JsonSerializer.Deserialize<TranslationUsage>(saved)! : new(0, 0);

    private List<Unit> Pending(string? selection, int limit) => selection == null
        ? store.Query("""
            SELECT id,path,locator,source,target,context,status,warning,revision,imported
            FROM units WHERE active=1 AND status='pending' ORDER BY path,locator LIMIT $n
            """, ProjectStore.ReadUnit, ("$n", limit))
        : store.Query("""
            SELECT units.id,units.path,locator,source,target,context,status,warning,revision,imported
            FROM json_each($selection) AS selected CROSS JOIN units
            WHERE units.id=selected.key AND units.revision=selected.value
            AND active=1 AND status='pending' ORDER BY units.path,locator LIMIT $n
            """, ProjectStore.ReadUnit, ("$selection", selection), ("$n", limit));

    // The caller holds the exclusive project request lock. Remaining reservations belong to an interrupted run.
    internal void RecoverAttempts() => store.Execute("UPDATE attempts SET state='acknowledged' WHERE state='inflight'");

    internal static bool CanRetry(Exception error) => error is InvalidDataException or JsonException or TaskCanceledException
        or KeyNotFoundException or IOException
        || (error is HttpRequestException http && (
            http.StatusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests
            || (int?)http.StatusCode >= 500
            || (http.StatusCode == null && http.HttpRequestError != HttpRequestError.SecureConnectionError)));

    private long RecordAttempt(string id, string provider)
    {
        store.Execute("INSERT INTO attempts(unit_id,provider,state) VALUES($i,$p,'inflight')",
            ("$i", id), ("$p", provider));
        return store.Query("SELECT last_insert_rowid()", row => row.GetInt64(0)).Single();
    }

    internal FileStream LockRequests()
    {
        string path = Path.Combine(store.DirectoryPath, "translation.lock");
        Identity.RequireNoLinks(path);
        try { return File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11)
        {
            throw new InvalidOperationException("توجد عملية ترجمة نشطة لهذا المشروع.", error);
        }
    }

    public async Task Run(ProviderOptions options, IProgress<TranslationProgress>? progress = null,
        CancellationToken cancellation = default, ITranslationProvider? provider = null, TranslationJob? job = null)
    {
        if (options.BatchSize < 1 || options.MaxRequests < 1 || options.MaxCharacters < 1 || options.MaxUnits is <= 0)
            throw new ArgumentException("Positive batch and budget limits required.");
        using var requestLock = LockRequests();
        var scan = ReadScanState();
        if (scan.ScannedSource.Length == 0 || scan.ScannedSource != scan.Source)
            throw new InvalidOperationException("افحص المصدر قبل إرسال النصوص.");
        if (job != null && (job.SourceScope != scan.Scope || job.Package != scan.Package || job.Connection != Connection(options)))
            throw new InvalidOperationException("استأنف على المصدر والحزمة والمزوّد والنموذج المستخدمة عند بدء الدفعة.");
        provider ??= new ApiProvider(options, scan.ScannedEngine);

        RecoverAttempts();
        int failures = 0;
        int done = 0, characters = 0, requests = 0;
        var sessionTokens = new TokenUsage();
        string? selection = job == null ? null : JsonSerializer.Serialize(job.Rows);
        int total = Math.Min(job == null ? Estimate().Units : Estimate(job).Units, options.MaxUnits ?? int.MaxValue);
        bool registerJob = job != null;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            int remaining = Math.Min(options.BatchSize, (options.MaxUnits ?? int.MaxValue) - done);
            if (remaining <= 0) return;
            List<Unit> batch;
            Dictionary<string, long> attempts = [];
            int reused;
            bool budgetExceeded = false;
            store.Execute("BEGIN IMMEDIATE");
            try
            {
                if (ReadScanState() != scan)
                    throw new InvalidOperationException("تغيّر المصدر أو الفحص أثناء الترجمة. ابدأ دفعة جديدة للمصدر المحدد.");
                if (job != null)
                {
                    var used = Usage(job);
                    characters = used.Characters;
                    requests = used.Requests;
                    sessionTokens = new TokenUsage(used.InputTokens, used.OutputTokens, used.TotalTokens);
                    if (registerJob) store.Set(JobSetting, JsonSerializer.Serialize(job));
                }

                batch = Pending(selection, remaining);
                int selected = batch.Count;
                if (selected > 0) ReuseApprovedTranslations(batch);
                reused = selected - batch.Count;

                if (batch.Count > 0)
                {
                    int available = options.MaxCharacters - characters;
                    int count = 0;
                    foreach (var row in batch)
                    {
                        if (row.Source.Length > available) break;
                        available -= row.Source.Length;
                        count++;
                    }
                    budgetExceeded = count == 0 || requests >= options.MaxRequests;
                    if (!budgetExceeded)
                    {
                        batch = batch.GetRange(0, count);
                        characters = options.MaxCharacters - available;
                        requests++;
                        foreach (var unit in batch) attempts[unit.Id] = RecordAttempt(unit.Id, options.Provider);
                        if (job != null)
                            store.Set("translation-usage:" + job.Id, JsonSerializer.Serialize(
                                new TranslationUsage(characters, requests, sessionTokens.InputTokens, sessionTokens.OutputTokens, sessionTokens.TotalTokens)));
                    }
                }
                store.Execute("COMMIT");
                registerJob = false;
            }
            catch { store.Execute("ROLLBACK"); throw; }

            if (budgetExceeded) throw new InvalidOperationException("بلغ التشغيل حدّ الأحرف أو الطلبات. حُفظت الدفعات المكتملة ويمكن استئناف النطاق ضمن حدّ أكبر.");
            done += reused;
            if (batch.Count == 0)
            {
                if (reused == 0) return;
                progress?.Report(new(done, total, "Saved translations from project memory", sessionTokens));
                continue;
            }
            Dictionary<string,string> translated;
            try { translated = await provider.Translate(batch, store.Glossary(), cancellation); }
            catch (Exception error) when (!cancellation.IsCancellationRequested && CanRetry(error) && failures < 2)
            {
                failures++;
                RecoverAttempts();
                progress?.Report(new(done, total, "إعادة محاولة الدفعة", sessionTokens));
                await Task.Delay(TimeSpan.FromSeconds(failures), cancellation);
                continue;
            }
            TokenUsage batchTokens = new();
            if (provider is ApiProvider api)
            {
                batchTokens = api.LastTokens;
                sessionTokens += batchTokens;
            }
            store.RecordUsage(options.Provider, options.Model, batchTokens, batch.Sum(u => u.Source.Length), 1);
            failures = 0;
            cancellation.ThrowIfCancellationRequested();
            store.Execute("BEGIN IMMEDIATE");
            bool scanChanged;
            try
            {
                scanChanged = ReadScanState() != scan;
                foreach (var unit in batch)
                {
                    var current = scanChanged ? null : store.UnitsById(unit.Id);
                    string state = "discarded";
                    if (current is { Status: "pending" } && current.Revision == unit.Revision)
                    {
                        unit.Target = translated[unit.Id];
                        store.Save(unit, false, true);
                        state = "saved";
                    }
                    store.Execute("UPDATE attempts SET state=$s WHERE id=$i AND state='inflight'",
                        ("$s", state), ("$i", attempts[unit.Id]));
                }
                if (job != null)
                    store.Set("translation-usage:" + job.Id, JsonSerializer.Serialize(
                        new TranslationUsage(characters, requests, sessionTokens.InputTokens, sessionTokens.OutputTokens, sessionTokens.TotalTokens)));
                store.Execute("COMMIT");
            }
            catch { store.Execute("ROLLBACK"); throw; }
            if (scanChanged)
                throw new InvalidOperationException("تغيّر المصدر أو الفحص أثناء الترجمة. ابدأ دفعة جديدة للمصدر المحدد.");
            done += batch.Count;
            progress?.Report(new(done, total, "Saved translation batch", sessionTokens));
        }
    }

    public async Task<TranslationSuggestion> Suggest(string id, ProviderOptions options, CancellationToken cancellation = default,
        ITranslationProvider? provider = null)
    {
        using var requestLock = LockRequests();
        var scan = ReadScanState();
        if (scan.ScannedSource.Length == 0 || scan.ScannedSource != scan.Source)
            throw new InvalidOperationException("افحص المصدر قبل إرسال النص.");
        provider ??= new ApiProvider(options, scan.ScannedEngine);
        Unit original;
        long attempt;
        cancellation.ThrowIfCancellationRequested();
        store.Execute("BEGIN IMMEDIATE");
        try
        {
            if (ReadScanState() != scan) throw new InvalidOperationException("تغيّر المصدر. أعد المحاولة.");
            RecoverAttempts();
            original = store.FindUnit(id);
            if (original.Source.Length > options.MaxCharacters || options.MaxRequests < 1)
                throw new InvalidOperationException("حدّ التشغيل أصغر من حجم السطر.");
            attempt = RecordAttempt(id, options.Provider);
            store.Execute("COMMIT");
        }
        catch { store.Execute("ROLLBACK"); throw; }
        var answer = await provider.Translate([original], store.Glossary(), cancellation);
        if (provider is ApiProvider api)
            store.RecordUsage(options.Provider, options.Model, api.LastTokens, original.Source.Length, 1);
        else
            store.RecordUsage(options.Provider, options.Model, new TokenUsage(), original.Source.Length, 1);
        store.Execute("UPDATE attempts SET state='suggested' WHERE id=$i AND state='inflight'", ("$i", attempt));
        string target = answer[id];
        return new(original, target, Tokens.Validate(original.Source, target, scan.ScannedEngine));
    }

    internal static string? FindApprovedTranslation(ProjectStore store, string source, string context) =>
        store.Query("""
            SELECT target FROM units
            WHERE active=1 AND status='approved' AND warning='' AND source=$s AND context=$c
            ORDER BY path, locator LIMIT 1
            """, row => row.GetString(0), ("$s", source), ("$c", context)).FirstOrDefault();

    private void ReuseApprovedTranslations(List<Unit> batch)
    {
        foreach (var unit in batch.ToList())
        {
            string? target = FindApprovedTranslation(store, unit.Source, unit.Context);
            if (target == null) continue;
            unit.Target = target;
            store.Save(unit, false, true);
            batch.Remove(unit);
        }
    }

    public void AcknowledgeAttempts()
    {
        using var requestLock = LockRequests();
        store.Execute("UPDATE attempts SET state='acknowledged' WHERE state='inflight'");
    }
}
