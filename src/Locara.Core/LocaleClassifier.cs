namespace Locara.Core;

/// <summary>
/// Central locale &amp; localization-directory intelligence used across resource discovery,
/// secondary-language lazy skipping, Gettext template detection, and file catalog classification.
/// </summary>
internal static class LocaleClassifier
{
    private static readonly HashSet<string> AmbiguousShortLocaleTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "no", "it", "he", "hi"
    };

    private static readonly HashSet<string> ExplicitLocalizationDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "localization", "localisation", "localization files", "localisation files",
        "locales", "locale", "lang", "language", "languages", "language files",
        "translations", "translation", "i18n", "l10n", "dialogues", "dialogs", "subtitles", "captions", "tl"
    };

    private static readonly HashSet<string> LocalizationContextFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "localization", "localisation", "localization files", "localisation files",
        "locales", "locale", "lang", "language", "languages", "language files",
        "translations", "translation", "i18n", "l10n", "strings", "dialogues", "dialogs", "subtitles", "captions", "tl", "text"
    };

    private static readonly string[] LocalizationFileStems =
    [
        "strings_", "strings-", "locale_", "locale-", "lang_", "lang-",
        "loc_", "loc-", "i18n_", "i18n-", "l10n_", "l10n-", "subtitles_", "dialogue_", "dialog_"
    ];

    private static readonly HashSet<string> DedicatedLocalizationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".po", ".pot", ".locres", ".translation", ".xlf", ".xliff", ".strings"
    };

    private static readonly HashSet<string> KnownLocaleCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "eng", "english", "englishus", "englishuk", "en-us", "en-gb", "en_us", "en_gb",
        "de", "deu", "ger", "german", "de-de", "de_de",
        "fr", "fra", "fre", "french", "fr-fr", "fr_fr",
        "es", "esp", "spa", "spanish", "spanishlatam", "latinspanish", "es-es", "es-419", "es-mx", "es_es", "es_419", "es_mx",
        "it", "ita", "italian", "it-it", "it_it",
        "ja", "jpn", "jp", "jap", "japanese", "ja-jp", "ja_jp",
        "ko", "kor", "kr", "korean", "ko-kr", "ko_kr",
        "pt", "por", "ptb", "pt-br", "pt-pt", "pt_br", "pt_pt", "portuguese", "brazilianportuguese", "brazportuguese",
        "ru", "rus", "russian", "ru-ru", "ru_ru",
        "zh", "zho", "zhs", "zht", "chs", "cht", "zh-cn", "zh-tw", "zh_cn", "zh_tw", "zh-hans", "zh-hant", "zh_hans", "zh_hant",
        "chinese", "chinesesimplified", "chinesetraditional", "chinesehongkong", "simplifiedchinese", "traditionalchinese",
        "pl", "pol", "polish", "tr", "tur", "turkish", "th", "tha", "thai",
        "nl", "nld", "dutch", "sv", "swe", "swedish", "da", "dan", "danish",
        "fi", "fin", "finn", "finnish", "no", "nor", "nb", "norwegian",
        "cs", "ces", "cze", "czech", "hu", "hun", "hungarian", "ro", "ron", "romanian",
        "uk", "ukr", "ukrainian", "vi", "vie", "vietnamese", "id", "ind", "indonesian",
        "ar", "ara", "arabic", "he", "heb", "hebrew", "el", "ell", "greek", "hi", "hin", "bg", "bul"
    };

    public static string StripContainerPrefix(string resourcePath)
    {
        int bar = resourcePath.LastIndexOf(" | ", StringComparison.Ordinal);
        return (bar >= 0 ? resourcePath[(bar + 3)..] : resourcePath).Replace('\\', '/');
    }

    public static bool IsKnownLocaleToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (KnownLocaleCodes.Contains(token)) return true;
        int sep = token.IndexOfAny(['_', '-']);
        if (sep is 2 or 3 && token.LastIndexOfAny(['_', '-']) == sep)
        {
            string lang = token[..sep];
            string region = token[(sep + 1)..];
            if (KnownLocaleCodes.Contains(lang) && region.Length is >= 2 and <= 4 && region.All(char.IsLetterOrDigit))
                return true;
        }
        return false;
    }

    public static bool IsExplicitLocalizationDirectory(string resourcePath)
    {
        string[] parts = StripContainerPrefix(resourcePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (ExplicitLocalizationDirectories.Contains(parts[i]))
                return true;
        }
        return false;
    }

    public static bool IsLocalizationContextPath(string normalizedInnerPath)
    {
        string[] parts = normalizedInnerPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (LocalizationContextFolders.Contains(parts[i]))
                return true;
        }
        string fileName = parts.Length > 0 ? parts[^1] : "";
        foreach (string stem in LocalizationFileStems)
        {
            if (fileName.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static bool IsEnglishLocaleCode(string? code) =>
        !string.IsNullOrWhiteSpace(code)
        && (code.Equals("en", StringComparison.OrdinalIgnoreCase)
            || code.Equals("eng", StringComparison.OrdinalIgnoreCase)
            || code.Equals("english", StringComparison.OrdinalIgnoreCase)
            || code.Equals("englishus", StringComparison.OrdinalIgnoreCase)
            || code.Equals("englishuk", StringComparison.OrdinalIgnoreCase)
            || code.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
            || code.StartsWith("en_", StringComparison.OrdinalIgnoreCase));

    public static bool IsGettextTemplate(string resourcePath)
    {
        string p = StripContainerPrefix(resourcePath);
        if (!Path.GetExtension(p).Equals(".pot", StringComparison.OrdinalIgnoreCase))
            return false;
        string? locale = ExtractPathLocale(resourcePath);
        return locale == null || IsEnglishLocaleCode(locale);
    }

    public static bool IsPrimaryLocaleSource(string resourcePath) =>
        IsEnglishLocaleCode(ExtractPathLocale(resourcePath)) || IsGettextTemplate(resourcePath);

    public static bool IsDedicatedLocalizationResource(string resourcePath, string formatName)
    {
        if (LocalizationProviderRegistry.TryGetByName(formatName, out var provider) && provider.Metadata.IsDedicatedLocalization)
            return true;
        if (formatName == "xml/xliff")
        {
            string ext = Path.GetExtension(StripContainerPrefix(resourcePath));
            if (ext.Equals(".xlf", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".xliff", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return IsExplicitLocalizationDirectory(resourcePath) && IsPrimaryLocaleSource(resourcePath);
    }

    public static string? ExtractPathLocale(string resourcePath)
    {
        string p = StripContainerPrefix(resourcePath);
        string[] parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string seg = parts[i];
            if (IsKnownLocaleToken(seg))
                return seg;
        }
        string fileNoExt = Path.GetFileNameWithoutExtension(p);
        if (IsKnownLocaleToken(fileNoExt))
            return fileNoExt;

        int dot = fileNoExt.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < fileNoExt.Length)
        {
            string suffix = fileNoExt[(dot + 1)..];
            if (IsKnownLocaleToken(suffix))
                return suffix;
        }

        string[] tokens = fileNoExt.Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 2)
        {
            for (int i = tokens.Length - 2; i >= 0; i--)
            {
                string pair = tokens[i] + "_" + tokens[i + 1];
                if (IsKnownLocaleToken(pair))
                    return pair;
            }

            string ext = Path.GetExtension(p);
            bool dedicatedExt = DedicatedLocalizationExtensions.Contains(ext);
            bool inLocContext = dedicatedExt || IsLocalizationContextPath(p);
            for (int i = tokens.Length - 1; i >= 0; i--)
            {
                string tok = tokens[i];
                if (!KnownLocaleCodes.Contains(tok)) continue;
                if (tok.Length >= 5)
                    return tok;
                if (!inLocContext) continue;
                if (tok.Length >= 3)
                    return tok;
                if (!AmbiguousShortLocaleTokens.Contains(tok))
                    return tok;
                if (dedicatedExt && i == tokens.Length - 1)
                    return tok;
            }
        }
        return null;
    }
}
