using System.Buffers.Binary;

namespace Locara.Core;

public sealed record ArabicFontChoice(
    string Id,
    string DisplayName,
    string Category,
    string FontFamilyName,
    string? ResourceName = null,
    string? FilePath = null,
    bool IsCustom = false);

public sealed record FontCompatibility(bool IsValidFont, int PresentationFormsCount, bool FullyCompatible, string Summary);

public static class ArabicFontCatalog
{
    public const string DefaultFontId = "builtin:noto-sans-regular";

    public static IReadOnlyList<ArabicFontChoice> BuiltInFonts { get; } =
    [
        new("builtin:noto-sans-regular", "Noto Sans Arabic — نوتو عادي (الافتراضي)", "مدمج", "Noto Sans Arabic", ResourceName: "Locara.NotoSansArabic"),
        new("builtin:noto-sans-bold", "Noto Sans Arabic Bold — نوتو عريض للواجهات", "مدمج", "Noto Sans Arabic", ResourceName: "Locara.NotoSansArabicBold"),
        new("builtin:ibm-plex-medium", "IBM Plex Sans Arabic — عصري / خيال علمي", "مدمج", "IBM Plex Sans Arabic", ResourceName: "Locara.IBMPlexSansArabicMedium"),
        new("builtin:ibm-plex-bold", "IBM Plex Sans Arabic Bold — عصري عريض (رائع للألعاب)", "مدمج", "IBM Plex Sans Arabic", ResourceName: "Locara.IBMPlexSansArabicBold"),
        new("builtin:noto-naskh-bold", "Noto Naskh Arabic Bold — نسخ حديث واضح", "مدمج", "Noto Naskh Arabic", ResourceName: "Locara.NotoNaskhArabicBold"),
        new("builtin:amiri-regular", "Amiri — أميري روائي وكلاسيكي", "مدمج", "Amiri", ResourceName: "Locara.AmiriRegular"),
        new("builtin:amiri-bold", "Amiri Bold — أميري عريض للعناوين والحوارات", "مدمج", "Amiri", ResourceName: "Locara.AmiriBold")
    ];

    private static readonly (string FileName, string DisplayName, string Family)[] KnownSystemFonts =
    [
        ("segoeuib.ttf", "Segoe UI Bold — خط ويندوز عريض", "Segoe UI"),
        ("segoeui.ttf", "Segoe UI — خط ويندوز عادي", "Segoe UI"),
        ("tahomabd.ttf", "Tahoma Bold — تاهوما عريض (كلاسيكي للألعاب)", "Tahoma"),
        ("tahoma.ttf", "Tahoma — تاهوما عادي", "Tahoma"),
        ("majallab.ttf", "Sakkal Majalla Bold — مجلة عريض أنيق", "Sakkal Majalla"),
        ("majalla.ttf", "Sakkal Majalla — مجلة عادي", "Sakkal Majalla"),
        ("arialbd.ttf", "Arial Bold — أريال عريض", "Arial"),
        ("tradbdo.ttf", "Traditional Arabic Bold — عربي تقليدي عريض", "Traditional Arabic")
    ];

    public static IReadOnlyList<ArabicFontChoice> SystemFonts()
    {
        string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (string.IsNullOrEmpty(fontsDir) || !Directory.Exists(fontsDir)) return [];
        var list = new List<ArabicFontChoice>();
        foreach (var (fileName, displayName, family) in KnownSystemFonts)
        {
            string full = Path.Combine(fontsDir, fileName);
            if (File.Exists(full))
                list.Add(new("system:" + fileName, displayName, "ويندوز", family, FilePath: full));
        }
        return list;
    }

    public static IReadOnlyList<ArabicFontChoice> CustomFonts(string? customFontsDir)
    {
        if (string.IsNullOrWhiteSpace(customFontsDir) || !Directory.Exists(customFontsDir)) return [];
        var list = new List<ArabicFontChoice>();
        foreach (string file in Directory.EnumerateFiles(customFontsDir)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ttf" or ".otf")
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            string fileName = Path.GetFileName(file);
            string baseName = Path.GetFileNameWithoutExtension(file);
            list.Add(new("custom:" + fileName, $"{baseName} (خط خاص)", "مخصّص", baseName, FilePath: file, IsCustom: true));
        }
        return list;
    }

    public static List<ArabicFontChoice> All(string? customFontsDir = null)
    {
        var all = new List<ArabicFontChoice>(BuiltInFonts.Count + 16);
        all.AddRange(BuiltInFonts);
        all.AddRange(CustomFonts(customFontsDir));
        all.AddRange(SystemFonts());
        return all;
    }

    public static ArabicFontChoice Resolve(string? selection, string? customFontsDir = null)
    {
        string key = (selection ?? "").Trim();
        if (key.Length > 0)
        {
            var match = All(customFontsDir).FirstOrDefault(f => f.Id.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
            if (File.Exists(key) && Path.GetExtension(key).ToLowerInvariant() is ".ttf" or ".otf")
            {
                string baseName = Path.GetFileNameWithoutExtension(key);
                return new(key, baseName, "مخصّص", baseName, FilePath: key, IsCustom: true);
            }
        }
        return BuiltInFonts[0];
    }

    public static ArabicFontChoice ResolveContrastVariant(ArabicFontChoice? preferred)
    {
        var baseChoice = preferred ?? BuiltInFonts[0];
        string? upgradedId = baseChoice.Id.ToLowerInvariant() switch
        {
            "builtin:noto-sans-regular" => "builtin:noto-sans-bold",
            "builtin:ibm-plex-medium" => "builtin:ibm-plex-bold",
            "builtin:amiri-regular" => "builtin:amiri-bold",
            "system:segoeui.ttf" => "system:segoeuib.ttf",
            "system:tahoma.ttf" => "system:tahomabd.ttf",
            "system:majalla.ttf" => "system:majallab.ttf",
            _ => null
        };
        if (upgradedId == null) return baseChoice;
        var match = All().FirstOrDefault(f => f.Id.Equals(upgradedId, StringComparison.OrdinalIgnoreCase));
        return match ?? baseChoice;
    }

    public static byte[] LoadBytes(ArabicFontChoice choice)
    {
        byte[] bytes;
        if (!string.IsNullOrEmpty(choice.ResourceName))
        {
            using var stream = typeof(ArabicFontCatalog).Assembly.GetManifestResourceStream(choice.ResourceName)
                ?? throw new InvalidOperationException($"ملف الخط المدمج «{choice.DisplayName}» غير موجود.");
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }
        else if (!string.IsNullOrEmpty(choice.FilePath))
        {
            if (!File.Exists(choice.FilePath))
                throw new FileNotFoundException($"لم يُعثر على ملف الخط: {choice.FilePath}", choice.FilePath);
            bytes = File.ReadAllBytes(choice.FilePath);
        }
        else
        {
            throw new InvalidOperationException("اختيار الخط غير مكتمل.");
        }

        if (!HasValidFontHeader(bytes))
            throw new InvalidDataException($"الملف «{choice.DisplayName}» ليس ملف خط TrueType/OpenType (.ttf / .otf) صالحاً.");
        return bytes;
    }

    public static bool HasValidFontHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12) return false;
        uint tag = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return tag is 0x00010000u or 0x4F54544Fu /* OTTO */ or 0x74727565u /* true */ or 0x74797031u /* typ1 */;
    }

    public static FontCompatibility Inspect(ReadOnlySpan<byte> bytes)
    {
        if (!HasValidFontHeader(bytes))
            return new(false, 0, false, "الملف ليس خط TrueType/OpenType (.ttf أو .otf) صالحاً.");

        int numTables = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        if (12 + numTables * 16 > bytes.Length)
            return new(false, 0, false, "ترويسة ملف الخط تالفة.");

        int cmapOffset = -1;
        int cmapLength = 0;
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            if (bytes.Slice(rec, 4).SequenceEqual("cmap"u8))
            {
                cmapOffset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[(rec + 8)..]));
                cmapLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[(rec + 12)..]));
                break;
            }
        }
        if (cmapOffset < 0 || cmapOffset + 4 > bytes.Length || cmapLength < 4)
            return new(true, 0, false, "الخط لا يحتوي جدول محارف (cmap) صالح.");

        int numSubtables = BinaryPrimitives.ReadUInt16BigEndian(bytes[(cmapOffset + 2)..]);
        var presForms = new HashSet<int>();
        for (int i = 0; i < numSubtables; i++)
        {
            int rec = cmapOffset + 4 + i * 8;
            if (rec + 8 > bytes.Length) break;
            int subOffset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[(rec + 4)..]));
            int st = cmapOffset + subOffset;
            if (st < 0 || st + 4 > bytes.Length) continue;
            ushort format = BinaryPrimitives.ReadUInt16BigEndian(bytes[st..]);
            if (format == 4 && st + 14 <= bytes.Length)
            {
                int segCount = BinaryPrimitives.ReadUInt16BigEndian(bytes[(st + 6)..]) / 2;
                int endOff = st + 14;
                int startOff = endOff + segCount * 2 + 2;
                if (startOff + segCount * 2 > bytes.Length) continue;
                for (int s = 0; s < segCount; s++)
                {
                    int endCode = BinaryPrimitives.ReadUInt16BigEndian(bytes[(endOff + s * 2)..]);
                    int startCode = BinaryPrimitives.ReadUInt16BigEndian(bytes[(startOff + s * 2)..]);
                    if (startCode == 0xFFFF) break;
                    int lo = Math.Max(startCode, 0xFE70);
                    int hi = Math.Min(endCode, 0xFEFC);
                    for (int c = lo; c <= hi; c++) presForms.Add(c);
                }
            }
            else if (format == 12 && st + 16 <= bytes.Length)
            {
                int nGroups = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[(st + 12)..]));
                for (int g = 0; g < nGroups; g++)
                {
                    int gOff = st + 16 + g * 12;
                    if (gOff + 12 > bytes.Length) break;
                    uint startCode = BinaryPrimitives.ReadUInt32BigEndian(bytes[gOff..]);
                    uint endCode = BinaryPrimitives.ReadUInt32BigEndian(bytes[(gOff + 4)..]);
                    if (endCode < 0xFE70 || startCode > 0xFEFC) continue;
                    int lo = (int)Math.Max(startCode, 0xFE70u);
                    int hi = (int)Math.Min(endCode, 0xFEFCu);
                    for (int c = lo; c <= hi; c++) presForms.Add(c);
                }
            }
        }

        int count = presForms.Count;
        bool ok = count >= 115;
        string summary = ok
            ? $"متوافق 100% مع عرض العربية داخل اللعبة ({count}/141 شكلاً عربياً)."
            : count > 0
                ? $"تنبيه: هذا الخط يدعم {count}/141 شكلاً عربياً فقط (قد تظهر بعض الحروف مفقودة داخل اللعبة)."
                : "تحذير: هذا الخط لا يحتوي أشكال العربية المتصلة (Presentation Forms-B) — ستظهر الحروف فارغة داخل اللعبة!";
        return new(true, count, ok, summary);
    }

    public static bool SupportsLatinAndArabic(ReadOnlySpan<byte> bytes)
    {
        if (!HasValidFontHeader(bytes)) return false;
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        if (12 + numTables * 16 > bytes.Length) return false;

        int cmapOffset = -1;
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            if (bytes.Slice(rec, 4).SequenceEqual("cmap"u8))
            {
                cmapOffset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[(rec + 8)..]));
                break;
            }
        }
        if (cmapOffset < 0 || cmapOffset + 4 > bytes.Length) return false;

        bool hasUpperA = false, hasLowerZ = false, hasDigit0 = false;
        var arabicSeen = new HashSet<int>();
        int numSubtables = BinaryPrimitives.ReadUInt16BigEndian(bytes[(cmapOffset + 2)..]);

        void CheckRange(int startCode, int endCode)
        {
            if (startCode <= 'A' && endCode >= 'A') hasUpperA = true;
            if (startCode <= 'z' && endCode >= 'z') hasLowerZ = true;
            if (startCode <= '0' && endCode >= '0') hasDigit0 = true;
            if (endCode >= 0x0621 && startCode <= 0x064A)
            {
                int lo = Math.Max(startCode, 0x0621);
                int hi = Math.Min(endCode, 0x064A);
                for (int c = lo; c <= hi; c++) arabicSeen.Add(c);
            }
        }

        for (int i = 0; i < numSubtables; i++)
        {
            int rec = cmapOffset + 4 + i * 8;
            if (rec + 8 > bytes.Length) break;
            int subOffset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[(rec + 4)..]));
            int st = cmapOffset + subOffset;
            if (st < 0 || st + 4 > bytes.Length) continue;
            ushort format = BinaryPrimitives.ReadUInt16BigEndian(bytes[st..]);
            if (format == 4 && st + 14 <= bytes.Length)
            {
                int segCount = BinaryPrimitives.ReadUInt16BigEndian(bytes[(st + 6)..]) / 2;
                int endOff = st + 14;
                int startOff = endOff + segCount * 2 + 2;
                if (startOff + segCount * 2 > bytes.Length) continue;
                for (int s = 0; s < segCount; s++)
                {
                    int endCode = BinaryPrimitives.ReadUInt16BigEndian(bytes[(endOff + s * 2)..]);
                    int startCode = BinaryPrimitives.ReadUInt16BigEndian(bytes[(startOff + s * 2)..]);
                    if (startCode == 0xFFFF) break;
                    CheckRange(startCode, endCode);
                }
            }
            else if (format == 12 && st + 16 <= bytes.Length)
            {
                int nGroups = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[(st + 12)..]));
                for (int g = 0; g < nGroups; g++)
                {
                    int gOff = st + 16 + g * 12;
                    if (gOff + 12 > bytes.Length) break;
                    uint startCode = BinaryPrimitives.ReadUInt32BigEndian(bytes[gOff..]);
                    uint endCode = BinaryPrimitives.ReadUInt32BigEndian(bytes[(gOff + 4)..]);
                    if (startCode > 0x064Au) continue;
                    CheckRange((int)startCode, (int)Math.Min(endCode, 0x064Au));
                }
            }
        }
        return hasUpperA && hasLowerZ && hasDigit0 && arabicSeen.Count >= 28;
    }

    public static (ArabicFontChoice Choice, byte[] Bytes) ResolveBilingual(ArabicFontChoice? preferred)
    {
        if (preferred != null)
        {
            try
            {
                byte[] preferredBytes = LoadBytes(preferred);
                if (SupportsLatinAndArabic(preferredBytes))
                    return (preferred, preferredBytes);
            }
            catch { }
        }
        // Fallback to bilingual IBM Plex Sans Arabic Bold (full Latin A-Z + digits + full Arabic)
        // so untranslated English UI labels/versions stay crisp alongside Arabic!
        var bilingual = BuiltInFonts.First(f => f.Id == "builtin:ibm-plex-bold");
        return (bilingual, LoadBytes(bilingual));
    }

    public static (ArabicFontChoice Choice, byte[] Bytes) ResolveForUnreal(ArabicFontChoice? preferred) =>
        ResolveBilingual(preferred);

    public static void WriteLicense(string directory, ArabicFontChoice? choice)
    {
        if (choice == null || string.IsNullOrEmpty(choice.ResourceName))
        {
            WriteEmbeddedLicense(directory, "Locara.NotoArabicLicense", "Noto-Arabic-OFL.txt");
            return;
        }
        (string resourceName, string fileName) = choice.ResourceName switch
        {
            "Locara.IBMPlexSansArabicMedium" or "Locara.IBMPlexSansArabicBold" => ("Locara.IBMPlexLicense", "IBM-Plex-OFL.txt"),
            "Locara.AmiriRegular" or "Locara.AmiriBold" => ("Locara.AmiriLicense", "Amiri-OFL.txt"),
            _ => ("Locara.NotoArabicLicense", "Noto-Arabic-OFL.txt")
        };
        WriteEmbeddedLicense(directory, resourceName, fileName);
    }

    private static void WriteEmbeddedLicense(string directory, string resourceName, string fileName)
    {
        using var stream = typeof(ArabicFontCatalog).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return;
        Directory.CreateDirectory(directory);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        File.WriteAllBytes(Path.Combine(directory, fileName), ms.ToArray());
    }
}
