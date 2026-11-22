using System.IO.Compression;
using System.Text;

namespace Locara.Core;

/// <summary>
/// Pure-C# Godot 3 &amp; 4 binary .translation resource (RSRC) reader and writer.
/// Supports both OptimizedTranslation / PHashTranslation (hash_table + bucket_table + SMAZ-compressed strings)
/// and uncompressed Translation (messages PackedStringArray or Dictionary).
/// </summary>
public sealed class GodotTranslationFormat : IFormatHandler
{
    public static GodotTranslationFormat Instance { get; } = new();
    public string Name => "godot-translation";

    private const uint VariantNil = 1;
    private const uint VariantBool = 2;
    private const uint VariantInt = 3;
    private const uint VariantFloat = 4;
    private const uint VariantString = 5;
    private const uint VariantDictionary = 26;
    private const uint VariantArray = 30;
    private const uint VariantPackedByteArray = 31;
    private const uint VariantPackedInt32Array = 32;
    private const uint VariantPackedFloat32Array = 33;
    private const uint VariantPackedStringArray = 34;
    private const uint VariantInt64 = 40;
    private const uint VariantDouble = 41;
    private const uint VariantStringName = 43;

    private const uint FlagUids = 2u;
    private const uint FlagHasScriptClass = 8u;

    public bool Supports(string path) =>
        Path.GetExtension(path).Equals(".translation", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<TextSlot> Extract(byte[] bytes)
    {
        var doc = ParseRsrc(bytes);
        if (doc == null) return [];

        string locale = doc.GetStringProperty("locale") ?? "";
        string ctxBase = string.IsNullOrWhiteSpace(locale) ? $"Godot {doc.ResourceType}" : $"Godot {doc.ResourceType} ({locale})";

        if (doc.ResourceType is "OptimizedTranslation" or "PHashTranslation")
            return ExtractOptimized(doc, ctxBase);

        if (doc.ResourceType == "Translation")
            return ExtractPlainTranslation(doc, ctxBase);

        return [];
    }

    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations)
    {
        if (translations.Count == 0) return bytes;
        var doc = ParseRsrc(bytes);
        if (doc == null) return bytes;

        bool changed = doc.ResourceType switch
        {
            "OptimizedTranslation" or "PHashTranslation" => ApplyOptimized(doc, translations),
            "Translation" => ApplyPlainTranslation(doc, translations),
            _ => false
        };

        return changed ? WriteRsrc(doc) : bytes;
    }

    private static IReadOnlyList<TextSlot> ExtractOptimized(RsrcDocument doc, string ctxBase)
    {
        int[]? hashTable = doc.GetInt32ArrayProperty("hash_table");
        int[]? bucketTable = doc.GetInt32ArrayProperty("bucket_table");
        byte[]? strings = doc.GetByteArrayProperty("strings");
        if (hashTable == null || bucketTable == null || strings == null) return [];

        var slots = new List<TextSlot>();
        var visitedBuckets = new HashSet<int>();
        foreach (int bucketOff in hashTable)
        {
            if (bucketOff < 0 || !visitedBuckets.Add(bucketOff)) continue;
            if (bucketOff + 2 > bucketTable.Length) continue;
            int elemCount = bucketTable[bucketOff];
            if (elemCount <= 0 || bucketOff + 2L + (long)elemCount * 4 > bucketTable.Length) continue;

            for (int e = 0; e < elemCount; e++)
            {
                int baseIdx = bucketOff + 2 + e * 4;
                uint key = unchecked((uint)bucketTable[baseIdx]);
                uint strOffset = unchecked((uint)bucketTable[baseIdx + 1]);
                uint compSize = unchecked((uint)bucketTable[baseIdx + 2]);
                uint uncompSize = unchecked((uint)bucketTable[baseIdx + 3]);
                if (strOffset > (uint)strings.Length || compSize > (uint)strings.Length - strOffset) continue;

                string text = DecodeOptimizedString(strings.AsSpan((int)strOffset, (int)compSize), compSize, uncompSize);
                if (string.IsNullOrWhiteSpace(text)) continue;

                string locator = $"opt:{bucketOff}:{e}:{key:x8}";
                slots.Add(new TextSlot(locator, text, $"{ctxBase} · key 0x{key:x8}"));
            }
        }
        return slots;
    }

    private static bool ApplyOptimized(RsrcDocument doc, IReadOnlyDictionary<string, string> translations)
    {
        int[]? hashTable = doc.GetInt32ArrayProperty("hash_table");
        int[]? bucketTable = doc.GetInt32ArrayProperty("bucket_table");
        byte[]? origStrings = doc.GetByteArrayProperty("strings");
        if (hashTable == null || bucketTable == null || origStrings == null) return false;

        int[] newBuckets = (int[])bucketTable.Clone();
        using var newStrings = new MemoryStream(origStrings.Length + 4096);
        bool changed = false;
        var visitedBuckets = new HashSet<int>();

        foreach (int bucketOff in hashTable)
        {
            if (bucketOff < 0 || !visitedBuckets.Add(bucketOff)) continue;
            if (bucketOff + 2 > newBuckets.Length) continue;
            int elemCount = newBuckets[bucketOff];
            if (elemCount <= 0 || bucketOff + 2L + (long)elemCount * 4 > newBuckets.Length) continue;

            for (int e = 0; e < elemCount; e++)
            {
                int baseIdx = bucketOff + 2 + e * 4;
                uint key = unchecked((uint)newBuckets[baseIdx]);
                uint origOffset = unchecked((uint)newBuckets[baseIdx + 1]);
                uint origCompSize = unchecked((uint)newBuckets[baseIdx + 2]);
                uint origUncompSize = unchecked((uint)newBuckets[baseIdx + 3]);
                if (origOffset > (uint)origStrings.Length || origCompSize > (uint)origStrings.Length - origOffset)
                    continue;

                string locator = $"opt:{bucketOff}:{e}:{key:x8}";
                uint newOffset = checked((uint)newStrings.Position);

                if (translations.TryGetValue(locator, out string? replacement))
                {
                    string existing = DecodeOptimizedString(origStrings.AsSpan((int)origOffset, (int)origCompSize), origCompSize, origUncompSize);
                    if (replacement != existing)
                    {
                        byte[] utf8 = Encoding.UTF8.GetBytes(replacement);
                        newStrings.Write(utf8);
                        newBuckets[baseIdx + 1] = unchecked((int)newOffset);
                        newBuckets[baseIdx + 2] = utf8.Length;
                        newBuckets[baseIdx + 3] = utf8.Length; // comp_size == uncomp_size -> Godot reads raw UTF-8
                        changed = true;
                        continue;
                    }
                }

                // Preserve original entry verbatim (keeping SMAZ compression if originally compressed)
                newStrings.Write(origStrings, (int)origOffset, (int)origCompSize);
                newBuckets[baseIdx + 1] = unchecked((int)newOffset);
            }
        }

        if (!changed) return false;
        doc.SetInt32ArrayProperty("bucket_table", newBuckets);
        doc.SetByteArrayProperty("strings", newStrings.ToArray());
        return true;
    }

    private static IReadOnlyList<TextSlot> ExtractPlainTranslation(RsrcDocument doc, string ctxBase)
    {
        var prop = doc.FindProperty("messages");
        if (prop == null) return [];

        var slots = new List<TextSlot>();
        if (prop.VariantType == VariantPackedStringArray && prop.StringArrayValue != null)
        {
            string[] arr = prop.StringArrayValue;
            for (int i = 0; i + 1 < arr.Length; i += 2)
            {
                string key = arr[i];
                string val = arr[i + 1];
                if (string.IsNullOrWhiteSpace(val)) continue;
                slots.Add(new TextSlot($"msg:{i / 2}", val, $"{ctxBase} · {key}"));
            }
        }
        else if (prop.VariantType == VariantDictionary && prop.DictValue != null)
        {
            for (int i = 0; i < prop.DictValue.Count; i++)
            {
                var (k, v) = prop.DictValue[i];
                if (v.VariantType is not (VariantString or VariantStringName) || string.IsNullOrWhiteSpace(v.StringValue))
                    continue;
                string keyLabel = k.StringValue ?? $"#{i}";
                slots.Add(new TextSlot($"dict:{i}", v.StringValue, $"{ctxBase} · {keyLabel}"));
            }
        }
        return slots;
    }

    private static bool ApplyPlainTranslation(RsrcDocument doc, IReadOnlyDictionary<string, string> translations)
    {
        var prop = doc.FindProperty("messages");
        if (prop == null) return false;

        bool changed = false;
        if (prop.VariantType == VariantPackedStringArray && prop.StringArrayValue != null)
        {
            string[] updated = (string[])prop.StringArrayValue.Clone();
            for (int i = 0; i + 1 < updated.Length; i += 2)
            {
                string locator = $"msg:{i / 2}";
                if (translations.TryGetValue(locator, out string? replacement) && replacement != updated[i + 1])
                {
                    updated[i + 1] = replacement;
                    changed = true;
                }
            }
            if (changed) prop.StringArrayValue = updated;
        }
        else if (prop.VariantType == VariantDictionary && prop.DictValue != null)
        {
            for (int i = 0; i < prop.DictValue.Count; i++)
            {
                string locator = $"dict:{i}";
                var (k, v) = prop.DictValue[i];
                if (v.VariantType is VariantString or VariantStringName
                    && translations.TryGetValue(locator, out string? replacement) && replacement != v.StringValue)
                {
                    prop.DictValue[i] = (k, new RsrcVariant(v.VariantType, replacement, null, null, null, null, null));
                    changed = true;
                }
            }
        }
        return changed;
    }

    internal static string DecodeOptimizedString(ReadOnlySpan<byte> payload, uint compSize, uint uncompSize)
    {
        if (compSize == uncompSize)
            return Encoding.UTF8.GetString(payload);
        byte[] decompressed = SmazDecompress(payload, checked((int)uncompSize));
        return Encoding.UTF8.GetString(decompressed);
    }

    // Official Godot thirdparty/misc/smaz.c reverse codebook (254 entries).
    private static readonly string[] SmazRcb =
    [
        " ", "the", "e", "t", "a", "of", "o", "and", "i", "n", "s", "e ", "r", " th",
        " t", "in", "he", "th", "h", "he ", "to", "\r\n", "l", "s ", "d", " a", "an",
        "er", "c", " o", "d ", "on", " of", "re", "of ", "t ", ", ", "is", "u", "at",
        "   ", "n ", "or", "which", "f", "m", "as", "it", "that", "\n", "was", "en",
        "  ", " w", "es", " an", " i", "\r", "f ", "g", "p", "nd", " s", "nd ", "ed ",
        "w", "ed", "http://", "for", "te", "ing", "y ", "The", " c", "ti", "r ", "his",
        "st", " in", "ar", "nt", ",", " to", "y", "ng", " h", "with", "le", "al", "to ",
        "b", "ou", "be", "were", " b", "se", "o ", "ent", "ha", "ng ", "their", "\"",
        "hi", "from", " f", "in ", "de", "ion", "me", "v", ".", "ve", "all", "re ",
        "ri", "ro", "is ", "co", "f t", "are", "ea", ". ", "her", " m", "er ", " p",
        "es ", "by", "they", "di", "ra", "ic", "not", "s, ", "d t", "at ", "ce", "la",
        "h ", "ne", "as ", "tio", "on ", "n t", "io", "we", " a ", "om", ", a", "s o",
        "ur", "li", "ll", "ch", "had", "this", "e t", "g ", "e\r\n", " wh", "ere",
        " co", "e o", "a ", "us", " d", "ss", "\n\r\n", "\r\n\r", "=\"", " be", " e",
        "s a", "ma", "one", "t t", "or ", "but", "el", "so", "l ", "e s", "s,", "no",
        "ter", " wa", "iv", "ho", "e a", " r", "hat", "s t", "ns", "ch ", "wh", "tr",
        "ut", "/", "have", "ly ", "ta", " ha", " on", "tha", "-", " l", "ati", "en ",
        "pe", " re", "there", "ass", "si", " fo", "wa", "ec", "our", "who", "its", "z",
        "fo", "rs", ">", "ot", "un", "<", "im", "th ", "nc", "ate", "><", "ver", "ad",
        " we", "ly", "ee", " n", "id", " cl", "ac", "il", "</", "rt", " wi", "div",
        "e, ", " it", "whi", " ma", "ge", "x", "e c", "men", ".com"
    ];

    internal static byte[] SmazDecompress(ReadOnlySpan<byte> input, int expectedLength)
    {
        if (expectedLength < 0 || expectedLength > 16 * 1024 * 1024)
            throw new InvalidDataException("حجم نص SMAZ غير صالح داخل ملف .translation.");
        byte[] output = new byte[expectedLength];
        int i = 0, o = 0;
        while (i < input.Length)
        {
            byte code = input[i++];
            if (code == 254)
            {
                if (i >= input.Length || o >= output.Length)
                    throw new InvalidDataException("بيانات SMAZ مقطوعة داخل ملف .translation.");
                output[o++] = input[i++];
            }
            else if (code == 255)
            {
                if (i >= input.Length)
                    throw new InvalidDataException("بيانات SMAZ مقطوعة داخل ملف .translation.");
                int len = input[i++] + 1;
                if (i + len > input.Length || o + len > output.Length)
                    throw new InvalidDataException("بيانات SMAZ خارج الحدود داخل ملف .translation.");
                input.Slice(i, len).CopyTo(output.AsSpan(o, len));
                i += len;
                o += len;
            }
            else
            {
                string token = SmazRcb[code];
                if (o + token.Length > output.Length)
                    throw new InvalidDataException("تجاوز طول نص SMAZ المتوقع داخل ملف .translation.");
                for (int k = 0; k < token.Length; k++)
                    output[o++] = (byte)token[k];
            }
        }
        return o == output.Length ? output : output[..o];
    }

    private sealed class RsrcProperty
    {
        public uint NameIndex { get; init; }
        public string Name { get; init; } = "";
        public bool InlineName { get; init; }
        public uint VariantType { get; init; }
        public string? StringValue { get; set; }
        public byte[]? ByteArrayValue { get; set; }
        public int[]? Int32ArrayValue { get; set; }
        public string[]? StringArrayValue { get; set; }
        public List<(RsrcVariant Key, RsrcVariant Value)>? DictValue { get; set; }
        public byte[]? RawPayload { get; set; }
    }

    private sealed record RsrcVariant(
        uint VariantType,
        string? StringValue,
        byte[]? ByteArrayValue,
        int[]? Int32ArrayValue,
        string[]? StringArrayValue,
        List<(RsrcVariant Key, RsrcVariant Value)>? DictValue,
        byte[]? RawPayload);

    private sealed class RsrcDocument
    {
        public uint UseReal64 { get; init; }
        public uint VerMajor { get; init; }
        public uint VerMinor { get; init; }
        public uint FormatVersion { get; init; }
        public string ResourceType { get; init; } = "";
        public uint Flags { get; init; }
        public long Uid { get; init; }
        public string ScriptClass { get; init; } = "";
        public List<string> StringTable { get; } = [];
        public List<(string Type, string Path, long Uid, bool HasUid)> ExtResources { get; } = [];
        public string InternalPath { get; set; } = "local://0";
        public string ObjectType { get; set; } = "";
        public List<RsrcProperty> Properties { get; } = [];
        public byte[]? ImportMetadataTail { get; set; }

        public RsrcProperty? FindProperty(string name) =>
            Properties.FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal));

        public string? GetStringProperty(string name) => FindProperty(name)?.StringValue;
        public int[]? GetInt32ArrayProperty(string name) => FindProperty(name)?.Int32ArrayValue;
        public byte[]? GetByteArrayProperty(string name) => FindProperty(name)?.ByteArrayValue;

        public void SetInt32ArrayProperty(string name, int[] value)
        {
            var p = FindProperty(name);
            if (p != null) p.Int32ArrayValue = value;
        }

        public void SetByteArrayProperty(string name, byte[] value)
        {
            var p = FindProperty(name);
            if (p != null) p.ByteArrayValue = value;
        }
    }

    private static byte[] DecompressRscc(byte[] bytes)
    {
        if (bytes.Length < 20)
            throw new InvalidDataException("ترويسة ملف Godot RSCC مقطوعة.");
        using var ms = new MemoryStream(bytes, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        ms.Position = 4; // skip "RSCC"
        uint mode = r.ReadUInt32(); // 0=FastLZ, 1=Deflate(Zlib), 2=Zstd, 3=Gzip
        uint blockSize = r.ReadUInt32();
        uint readTotal = r.ReadUInt32();
        if (mode is not (1u or 3u))
        {
            string modeName = mode switch { 0u => "FastLZ", 2u => "Zstd", _ => $"#{mode}" };
            throw new NotSupportedException($"ملف Godot .translation مضغوط بصيغة RSCC ({modeName}) غير مدعومة حالياً (المدعوم: Zlib/Deflate وGzip وRSRC غير المضغوط).");
        }
        if (blockSize == 0 || readTotal == 0 || readTotal > 128u * 1024u * 1024u)
            throw new InvalidDataException("أحجام كتل RSCC غير صالحة داخل ملف .translation.");

        long blockCountLong = ((long)readTotal + blockSize - 1) / blockSize;
        if (blockCountLong <= 0 || blockCountLong > 65536 || ms.Position + blockCountLong * 4 > bytes.Length)
            throw new InvalidDataException("جدول كتل RSCC غير صالح داخل ملف .translation.");

        int blockCount = (int)blockCountLong;
        uint[] compSizes = new uint[blockCount];
        for (int i = 0; i < blockCount; i++)
            compSizes[i] = r.ReadUInt32();

        byte[] output = new byte[4 + readTotal];
        "RSRC"u8.CopyTo(output.AsSpan(0, 4));
        int written = 0;
        for (int i = 0; i < blockCount; i++)
        {
            uint csize = compSizes[i];
            if (csize == 0 || ms.Position + csize > bytes.Length)
                throw new InvalidDataException("كتلة RSCC مضغوطة خارج حدود الملف.");
            byte[] comp = r.ReadBytes((int)csize);
            int expected = (int)Math.Min(blockSize, readTotal - (uint)written);
            using var compStream = new MemoryStream(comp, writable: false);
            using Stream decompressor = mode == 3u
                ? new GZipStream(compStream, CompressionMode.Decompress)
                : new ZLibStream(compStream, CompressionMode.Decompress);
            int got = 0;
            while (got < expected)
            {
                int n = decompressor.Read(output, 4 + written + got, expected - got);
                if (n <= 0) break;
                got += n;
            }
            if (got != expected)
                throw new InvalidDataException("فشل فك ضغط كتلة RSCC داخل ملف .translation.");
            written += got;
        }
        return output;
    }

    private static RsrcDocument? ParseRsrc(byte[] bytes)
    {
        if (bytes.Length < 48) return null;
        if (bytes.AsSpan(0, 4).SequenceEqual("RSCC"u8))
            bytes = DecompressRscc(bytes);
        if (!bytes.AsSpan(0, 4).SequenceEqual("RSRC"u8)) return null;

        using var ms = new MemoryStream(bytes, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        ms.Position = 4;
        uint bigEndian = r.ReadUInt32();
        if (bigEndian != 0) throw new NotSupportedException("ملفات Godot .translation بترتيب Big-Endian غير مدعومة.");
        uint useReal64 = r.ReadUInt32();
        uint verMajor = r.ReadUInt32();
        uint verMinor = r.ReadUInt32();
        uint formatVersion = r.ReadUInt32();
        string resType = ReadUnicodeString(r);
        if (resType is not ("OptimizedTranslation" or "PHashTranslation" or "Translation"))
            return null;

        ulong importMdOfs = r.ReadUInt64();
        uint flags = 0;
        long uid = 0;
        string scriptClass = "";
        if (formatVersion >= 4)
        {
            flags = r.ReadUInt32();
            uid = r.ReadInt64();
            if ((flags & FlagHasScriptClass) != 0)
                scriptClass = ReadUnicodeString(r);
            ms.Position += 11 * 4; // RESERVED_FIELDS
        }
        else
        {
            ms.Position += 14 * 4;
        }

        var doc = new RsrcDocument
        {
            UseReal64 = useReal64,
            VerMajor = verMajor,
            VerMinor = verMinor,
            FormatVersion = formatVersion,
            ResourceType = resType,
            Flags = flags,
            Uid = uid,
            ScriptClass = scriptClass
        };

        uint strTableCount = r.ReadUInt32();
        if (strTableCount > 100_000) throw new InvalidDataException("جدول نصوص RSRC يتجاوز الحد.");
        for (uint i = 0; i < strTableCount; i++)
            doc.StringTable.Add(ReadUnicodeString(r));

        uint extCount = r.ReadUInt32();
        if (extCount > 10_000) throw new InvalidDataException("عدد الموارد الخارجية غير صالح.");
        bool extHasUid = formatVersion >= 4 && (flags & FlagUids) != 0;
        for (uint i = 0; i < extCount; i++)
        {
            string eType = ReadUnicodeString(r);
            string ePath = ReadUnicodeString(r);
            long eUid = extHasUid ? r.ReadInt64() : 0;
            doc.ExtResources.Add((eType, ePath, eUid, extHasUid));
        }

        uint intCount = r.ReadUInt32();
        if (intCount != 1)
            throw new NotSupportedException("يدعم Locara ملفات .translation التي تحتوي مورداً داخلياً واحداً.");
        doc.InternalPath = ReadUnicodeString(r);
        ulong objOffset = r.ReadUInt64();
        if (objOffset > (ulong)bytes.Length)
            throw new InvalidDataException("إزاحة الكائن داخل ملف .translation غير صالحة.");

        ms.Position = (long)objOffset;
        doc.ObjectType = ReadUnicodeString(r);
        uint propCount = r.ReadUInt32();
        if (propCount > 10_000) throw new InvalidDataException("عدد خصائص .translation غير صالح.");

        for (uint i = 0; i < propCount; i++)
        {
            uint nameIdx = r.ReadUInt32();
            bool inlineName = (nameIdx & 0x80000000u) != 0;
            string propName;
            if (inlineName)
            {
                int byteLen = (int)(nameIdx & 0x7FFFFFFFu);
                propName = Encoding.UTF8.GetString(r.ReadBytes(byteLen)).TrimEnd('\0');
            }
            else
            {
                if (nameIdx >= doc.StringTable.Count)
                    throw new InvalidDataException("فهرس خاصية خارج جدول النصوص في .translation.");
                propName = doc.StringTable[(int)nameIdx];
            }

            var variant = ReadVariant(r, useReal64 != 0);
            doc.Properties.Add(new RsrcProperty
            {
                NameIndex = nameIdx,
                Name = propName,
                InlineName = inlineName,
                VariantType = variant.VariantType,
                StringValue = variant.StringValue,
                ByteArrayValue = variant.ByteArrayValue,
                Int32ArrayValue = variant.Int32ArrayValue,
                StringArrayValue = variant.StringArrayValue,
                DictValue = variant.DictValue,
                RawPayload = variant.RawPayload
            });
        }

        if (importMdOfs > 0 && importMdOfs < (ulong)bytes.Length - 4)
        {
            int tailLen = (int)((ulong)bytes.Length - 4 - importMdOfs);
            if (tailLen > 0)
                doc.ImportMetadataTail = bytes.AsSpan((int)importMdOfs, tailLen).ToArray();
        }

        return doc;
    }

    private static byte[] WriteRsrc(RsrcDocument doc)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write("RSRC"u8);
        w.Write(0u); // little-endian
        w.Write(doc.UseReal64);
        w.Write(doc.VerMajor);
        w.Write(doc.VerMinor);
        w.Write(doc.FormatVersion);
        WriteUnicodeString(w, doc.ResourceType);

        long importMdOfsPos = ms.Position;
        w.Write(0ul);

        if (doc.FormatVersion >= 4)
        {
            w.Write(doc.Flags);
            w.Write(doc.Uid);
            if ((doc.Flags & FlagHasScriptClass) != 0)
                WriteUnicodeString(w, doc.ScriptClass);
            w.Write(new byte[11 * 4]);
        }
        else
        {
            w.Write(new byte[14 * 4]);
        }

        w.Write((uint)doc.StringTable.Count);
        foreach (string s in doc.StringTable)
            WriteUnicodeString(w, s);

        w.Write((uint)doc.ExtResources.Count);
        foreach (var (eType, ePath, eUid, hasUid) in doc.ExtResources)
        {
            WriteUnicodeString(w, eType);
            WriteUnicodeString(w, ePath);
            if (hasUid) w.Write(eUid);
        }

        w.Write(1u);
        WriteUnicodeString(w, doc.InternalPath);
        long objOffsetPos = ms.Position;
        w.Write(0ul);

        long actualObjOffset = ms.Position;
        WriteUnicodeString(w, doc.ObjectType);
        w.Write((uint)doc.Properties.Count);
        foreach (var p in doc.Properties)
        {
            if (p.InlineName)
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(p.Name);
                w.Write(0x80000000u | (uint)utf8.Length);
                w.Write(utf8);
            }
            else
            {
                w.Write(p.NameIndex);
            }
            WriteVariant(w, new RsrcVariant(
                p.VariantType, p.StringValue, p.ByteArrayValue, p.Int32ArrayValue,
                p.StringArrayValue, p.DictValue, p.RawPayload));
        }

        ulong actualImportMdOfs = 0;
        if (doc.ImportMetadataTail is { Length: > 0 })
        {
            actualImportMdOfs = (ulong)ms.Position;
            w.Write(doc.ImportMetadataTail);
        }

        w.Write("RSRC"u8);
        w.Flush();

        ms.Position = objOffsetPos;
        w.Write((ulong)actualObjOffset);
        ms.Position = importMdOfsPos;
        w.Write(actualImportMdOfs);
        w.Flush();

        return ms.ToArray();
    }

    private static RsrcVariant ReadVariant(BinaryReader r, bool useReal64)
    {
        uint type = r.ReadUInt32();
        switch (type)
        {
            case VariantNil:
                return new(type, null, null, null, null, null, []);
            case VariantBool:
            case VariantInt:
                return new(type, null, null, null, null, null, r.ReadBytes(4));
            case VariantFloat:
                return new(type, null, null, null, null, null, r.ReadBytes(useReal64 ? 8 : 4));
            case VariantInt64:
            case VariantDouble:
                return new(type, null, null, null, null, null, r.ReadBytes(8));
            case VariantString:
            case VariantStringName:
                return new(type, ReadAlignedString(r), null, null, null, null, null);
            case VariantPackedByteArray:
            {
                int len = checked((int)r.ReadUInt32());
                byte[] data = r.ReadBytes(len);
                int pad = (4 - (len % 4)) % 4;
                if (pad > 0) r.ReadBytes(pad);
                return new(type, null, data, null, null, null, null);
            }
            case VariantPackedInt32Array:
            {
                int count = checked((int)r.ReadUInt32());
                int[] arr = new int[count];
                for (int i = 0; i < count; i++) arr[i] = r.ReadInt32();
                return new(type, null, null, arr, null, null, null);
            }
            case VariantPackedFloat32Array:
            {
                long start = r.BaseStream.Position;
                int count = checked((int)r.ReadUInt32());
                r.BaseStream.Position = start;
                return new(type, null, null, null, null, null, r.ReadBytes(checked(4 + count * 4)));
            }
            case VariantPackedStringArray:
            {
                int count = checked((int)r.ReadUInt32());
                string[] arr = new string[count];
                for (int i = 0; i < count; i++) arr[i] = ReadAlignedString(r);
                return new(type, null, null, null, arr, null, null);
            }
            case VariantDictionary:
            {
                uint rawCount = r.ReadUInt32();
                int count = checked((int)(rawCount & 0x7FFFFFFFu));
                var pairs = new List<(RsrcVariant, RsrcVariant)>(count);
                for (int i = 0; i < count; i++)
                    pairs.Add((ReadVariant(r, useReal64), ReadVariant(r, useReal64)));
                return new(type, null, null, null, null, pairs, null);
            }
            case VariantArray:
            {
                long start = r.BaseStream.Position;
                uint rawCount = r.ReadUInt32();
                int count = checked((int)(rawCount & 0x7FFFFFFFu));
                for (int i = 0; i < count; i++) _ = ReadVariant(r, useReal64);
                long end = r.BaseStream.Position;
                r.BaseStream.Position = start;
                byte[] raw = r.ReadBytes(checked((int)(end - start)));
                return new(type, null, null, null, null, null, raw);
            }
            default:
                throw new NotSupportedException($"نوع خاصية Godot غير مدعوم داخل ملف .translation (الرمز {type}).");
        }
    }

    private static void WriteVariant(BinaryWriter w, RsrcVariant v)
    {
        w.Write(v.VariantType);
        switch (v.VariantType)
        {
            case VariantNil:
                break;
            case VariantString:
            case VariantStringName:
                WriteAlignedString(w, v.StringValue ?? "");
                break;
            case VariantPackedByteArray:
            {
                byte[] data = v.ByteArrayValue ?? [];
                w.Write((uint)data.Length);
                w.Write(data);
                int pad = (4 - (data.Length % 4)) % 4;
                if (pad > 0) w.Write(new byte[pad]);
                break;
            }
            case VariantPackedInt32Array:
            {
                int[] arr = v.Int32ArrayValue ?? [];
                w.Write((uint)arr.Length);
                foreach (int item in arr) w.Write(item);
                break;
            }
            case VariantPackedStringArray:
            {
                string[] arr = v.StringArrayValue ?? [];
                w.Write((uint)arr.Length);
                foreach (string s in arr) WriteAlignedString(w, s);
                break;
            }
            case VariantDictionary:
            {
                var dict = v.DictValue ?? [];
                w.Write((uint)dict.Count);
                foreach (var (k, val) in dict)
                {
                    WriteVariant(w, k);
                    WriteVariant(w, val);
                }
                break;
            }
            default:
                if (v.RawPayload != null) w.Write(v.RawPayload);
                break;
        }
    }

    private static string ReadUnicodeString(BinaryReader r)
    {
        int len = checked((int)r.ReadUInt32());
        if (len <= 0) return "";
        if (len > 4 * 1024 * 1024) throw new InvalidDataException("طول نص RSRC غير صالح.");
        byte[] data = r.ReadBytes(len);
        int trimmedLen = len > 0 && data[^1] == 0 ? len - 1 : len;
        return Encoding.UTF8.GetString(data, 0, trimmedLen);
    }

    private static void WriteUnicodeString(BinaryWriter w, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        w.Write((uint)(utf8.Length + 1));
        w.Write(utf8);
        w.Write((byte)0);
    }

    private static string ReadAlignedString(BinaryReader r)
    {
        int len = checked((int)r.ReadUInt32());
        if (len < 0 || len > 16 * 1024 * 1024) throw new InvalidDataException("طول نص Variant غير صالح.");
        byte[] data = r.ReadBytes(len);
        int pad = (4 - (len % 4)) % 4;
        if (pad > 0) r.ReadBytes(pad);
        return Encoding.UTF8.GetString(data);
    }

    private static void WriteAlignedString(BinaryWriter w, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        w.Write((uint)utf8.Length);
        w.Write(utf8);
        int pad = (4 - (utf8.Length % 4)) % 4;
        if (pad > 0) w.Write(new byte[pad]);
    }
}
