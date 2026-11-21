using System.Text.Json;

namespace Locara.Core;

public sealed partial class ApiProvider
{
    public static async Task<IReadOnlyList<string>> ListModels(ProviderOptions options, CancellationToken cancellation)
    {
        string provider = options.Provider.ToLowerInvariant();
        if (provider == "azure") return [];

        string endpoint = ResolveBaseAddress(options).AbsoluteUri.TrimEnd('/') + "/models";
        string queryParameter = provider == "gemini" ? "pageToken" : "after_id";
        var models = new SortedSet<string>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            string url = cursor == null ? endpoint : $"{endpoint}?{queryParameter}={Uri.EscapeDataString(cursor)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            Authenticate(request, options);
            using var response = await SendRequest(request, options, cancellation, inspectPrivateCertificate: true);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await ReadErrorDetail(response, cancellation);
                throw new HttpRequestException($"تعذّر جلب النماذج (HTTP {(int)response.StatusCode}). اكتب اسم النموذج يدوياً إذا كانت خدمة القائمة غير متاحة."
                    + (detail.Length > 0 ? $" ({detail})" : ""),
                    null, response.StatusCode);
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
            var root = json.RootElement;
            string collection = provider == "gemini" ? "models" : "data";
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(collection, out var entries)
                || entries.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("أعاد الخادم قائمة نماذج بتنسيق غير متوقع.");
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (provider == "gemini" && (!entry.TryGetProperty("supportedGenerationMethods", out var methods)
                    || methods.ValueKind != JsonValueKind.Array
                    || !methods.EnumerateArray().Any(method => method.ValueKind == JsonValueKind.String
                        && method.GetString() == "generateContent"))) continue;
                string? id = ReadString(entry, provider == "gemini" ? "name" : "id");
                if (provider == "gemini" && id?.StartsWith("models/", StringComparison.Ordinal) == true)
                    id = id["models/".Length..];
                if (!string.IsNullOrWhiteSpace(id) && !id.Any(char.IsControl)) models.Add(id);
            }

            cursor = provider switch
            {
                "gemini" => ReadString(root, "nextPageToken"),
                "anthropic" when root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True =>
                    ReadString(root, "last_id") ?? throw new InvalidDataException("أعاد الخادم صفحة نماذج بدون معرّف الصفحة التالية."),
                _ => null
            };
            if (cursor != null && (!seenCursors.Add(cursor) || seenCursors.Count > 50))
                throw new InvalidDataException("تعذّر إكمال صفحات قائمة النماذج.");
        } while (!string.IsNullOrEmpty(cursor));

        return models.ToArray();
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
