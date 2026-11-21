using System.Buffers.Binary;
using System.Text;

namespace Locara.Core;

/// <summary>
/// Pure-C# reader and writer for Unreal Engine 4 &amp; 5 binary localization resources (.locres v0–v3).
/// Preserves Namespace, Key, FTextKey hashes, and SourceStringHash bit-for-bit, and encodes Arabic/non-ASCII
/// strings as UTF-16LE FStrings required by Unreal Engine.
/// </summary>
public sealed class UnrealLocresFormat : IFormatHandler
{
    public static UnrealLocresFormat Instance { get; } = new();
    public string Name => "unreal-locres";

    private static ReadOnlySpan<byte> Magic =>
    [
        0x0E, 0x14, 0x74, 0x75, 0x67, 0x4A, 0x03, 0xFC,
        0x4A, 0x15, 0x90, 0x9D, 0xC3, 0x37, 0x7F, 0x1B
    ];

    public bool Supports(string path) =>
        Path.GetExtension(path).Equals(".locres", StringComparison.OrdinalIgnoreCase);

    internal static bool IsLocresBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 25 && bytes[..16].SequenceEqual(Magic))
            return bytes[16] is >= 1 and <= 3;
        return TryParse(bytes, out _);
    }

    public IReadOnlyList<TextSlot> Extract(byte[] bytes)
    {
        if (!TryParse(bytes, out var parsed) || parsed == null)
            throw new InvalidDataException("ملف Unreal .locres غير صالح أو إصداره غير مدعوم.");

        var slots = new List<TextSlot>();
        foreach (var ns in parsed.Namespaces)
        {
            foreach (var entry in ns.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Value)) continue;
                string locator = MakeLocator(ns.Name, entry.Key);
                string context = string.IsNullOrEmpty(ns.Name) ? entry.Key : $"{ns.Name} / {entry.Key}";
                slots.Add(new TextSlot(locator, entry.Value, context));
            }
        }
        return slots;
    }

    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations)
    {
        if (translations.Count == 0) return bytes;
        if (!TryParse(bytes, out var parsed) || parsed == null)
            throw new InvalidDataException("ملف Unreal .locres غير صالح أو إصداره غير مدعوم.");

        bool anyChanged = false;
        var updatedNamespaces = new List<LocresNamespace>(parsed.Namespaces.Count);
        foreach (var ns in parsed.Namespaces)
        {
            var updatedEntries = new List<LocresEntry>(ns.Entries.Count);
            foreach (var entry in ns.Entries)
            {
                string locator = MakeLocator(ns.Name, entry.Key);
                if (translations.TryGetValue(locator, out string? translated) && translated != entry.Value)
                {
                    updatedEntries.Add(entry with { Value = translated });
                    anyChanged = true;
                }
                else
                {
                    updatedEntries.Add(entry);
                }
            }
            updatedNamespaces.Add(ns with { Entries = updatedEntries });
        }

        if (!anyChanged) return bytes;
        return Write(new LocresDocument(parsed.Version, updatedNamespaces));
    }

    internal static string MakeLocator(string ns, string key) =>
        $"locres:{ns.Length}:{ns}:{key}";

    internal sealed record LocresEntry(uint KeyHash, string Key, uint SourceStringHash, string Value);
    internal sealed record LocresNamespace(uint NameHash, string Name, IReadOnlyList<LocresEntry> Entries);
    internal sealed record LocresDocument(byte Version, IReadOnlyList<LocresNamespace> Namespaces);

    internal static bool TryParse(ReadOnlySpan<byte> data, out LocresDocument? document)
    {
        document = null;
        if (data.Length < 4) return false;

        try
        {
            byte version = 0;
            int pos = 0;
            string[]? stringTable = null;

            if (data.Length >= 25 && data[..16].SequenceEqual(Magic))
            {
                version = data[16];
                if (version is < 1 or > 3) return false;
                long stringArrayOffset = BinaryPrimitives.ReadInt64LittleEndian(data[17..]);
                if (stringArrayOffset < 25 || stringArrayOffset >= data.Length) return false;

                int sp = checked((int)stringArrayOffset);
                if (sp + 4 > data.Length) return false;
                int strCount = BinaryPrimitives.ReadInt32LittleEndian(data[sp..]);
                sp += 4;
                if (strCount < 0 || strCount > 5_000_000) return false;

                stringTable = new string[strCount];
                for (int i = 0; i < strCount; i++)
                {
                    if (!TryReadFString(data, ref sp, out string text)) return false;
                    if (version >= 2)
                    {
                        if (sp + 4 > data.Length) return false;
                        sp += 4; // RefCount
                    }
                    stringTable[i] = text;
                }
                pos = 25;
            }

            if (version >= 2)
            {
                if (pos + 4 > data.Length) return false;
                pos += 4; // EntriesCount
            }

            if (pos + 4 > data.Length) return false;
            uint nsCount = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
            pos += 4;
            if (nsCount > 1_000_000) return false;

            var namespaces = new List<LocresNamespace>((int)Math.Min(nsCount, 4096));
            for (uint n = 0; n < nsCount; n++)
            {
                uint nsHash = 0;
                if (version >= 2)
                {
                    if (pos + 4 > data.Length) return false;
                    nsHash = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
                    pos += 4;
                }
                if (!TryReadFString(data, ref pos, out string nsName)) return false;
                if (pos + 4 > data.Length) return false;
                uint keyCount = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
                pos += 4;
                if (keyCount > 5_000_000) return false;

                var entries = new List<LocresEntry>((int)Math.Min(keyCount, 4096));
                for (uint k = 0; k < keyCount; k++)
                {
                    uint keyHash = 0;
                    if (version >= 2)
                    {
                        if (pos + 4 > data.Length) return false;
                        keyHash = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
                        pos += 4;
                    }
                    if (!TryReadFString(data, ref pos, out string key)) return false;
                    if (pos + 4 > data.Length) return false;
                    uint sourceHash = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
                    pos += 4;

                    string value;
                    if (version == 0)
                    {
                        if (!TryReadFString(data, ref pos, out value)) return false;
                    }
                    else
                    {
                        if (pos + 4 > data.Length) return false;
                        int strIndex = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
                        pos += 4;
                        if (stringTable == null || strIndex < 0 || strIndex >= stringTable.Length) return false;
                        value = stringTable[strIndex];
                    }
                    entries.Add(new LocresEntry(keyHash, key, sourceHash, value));
                }
                namespaces.Add(new LocresNamespace(nsHash, nsName, entries));
            }

            if (version == 0 && pos != data.Length) return false;
            document = new LocresDocument(version, namespaces);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static byte[] Write(LocresDocument doc)
    {
        using var ms = new MemoryStream();
        if (doc.Version == 0)
        {
            WriteU32(ms, checked((uint)doc.Namespaces.Count));
            foreach (var ns in doc.Namespaces)
            {
                WriteFString(ms, ns.Name);
                WriteU32(ms, checked((uint)ns.Entries.Count));
                foreach (var entry in ns.Entries)
                {
                    WriteFString(ms, entry.Key);
                    WriteU32(ms, entry.SourceStringHash);
                    WriteFString(ms, entry.Value);
                }
            }
            return ms.ToArray();
        }

        // Build deduplicated localized string pool + ref counts
        var pool = new List<string>();
        var refCounts = new List<int>();
        var poolIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        uint totalEntries = 0;

        foreach (var ns in doc.Namespaces)
        {
            foreach (var entry in ns.Entries)
            {
                totalEntries++;
                if (poolIndex.TryGetValue(entry.Value, out int existing))
                {
                    refCounts[existing]++;
                }
                else
                {
                    poolIndex[entry.Value] = pool.Count;
                    pool.Add(entry.Value);
                    refCounts.Add(1);
                }
            }
        }

        ms.Write(Magic);
        ms.WriteByte(doc.Version);
        long offsetFieldPos = ms.Position;
        WriteI64(ms, 0); // patched below

        if (doc.Version >= 2)
            WriteU32(ms, totalEntries);

        WriteU32(ms, checked((uint)doc.Namespaces.Count));
        foreach (var ns in doc.Namespaces)
        {
            if (doc.Version >= 2) WriteU32(ms, ns.NameHash);
            WriteFString(ms, ns.Name);
            WriteU32(ms, checked((uint)ns.Entries.Count));
            foreach (var entry in ns.Entries)
            {
                if (doc.Version >= 2) WriteU32(ms, entry.KeyHash);
                WriteFString(ms, entry.Key);
                WriteU32(ms, entry.SourceStringHash);
                WriteI32(ms, poolIndex[entry.Value]);
            }
        }

        long stringArrayOffset = ms.Position;
        WriteI32(ms, pool.Count);
        for (int i = 0; i < pool.Count; i++)
        {
            WriteFString(ms, pool[i]);
            if (doc.Version >= 2) WriteI32(ms, refCounts[i]);
        }

        ms.Position = offsetFieldPos;
        WriteI64(ms, stringArrayOffset);
        return ms.ToArray();
    }

    internal static bool TryReadFString(ReadOnlySpan<byte> data, ref int pos, out string value)
    {
        value = "";
        if (pos + 4 > data.Length) return false;
        int len = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
        pos += 4;
        if (len == 0) return true;

        if (len > 0)
        {
            if (len > 16 * 1024 * 1024 || pos + len > data.Length) return false;
            var slice = data.Slice(pos, len);
            pos += len;
            int contentLen = slice[^1] == 0 ? len - 1 : len;
            value = DecodeAnsiOrUtf8(slice[..contentLen]);
            return true;
        }
        else
        {
            if (len == int.MinValue) return false;
            int charCount = -len;
            long byteLenLong = (long)charCount * 2;
            if (byteLenLong > 32L * 1024 * 1024 || pos + byteLenLong > data.Length) return false;
            int byteLen = (int)byteLenLong;
            var slice = data.Slice(pos, byteLen);
            pos += byteLen;
            int contentBytes = byteLen >= 2 && slice[^1] == 0 && slice[^2] == 0 ? byteLen - 2 : byteLen;
            value = Encoding.Unicode.GetString(slice[..contentBytes]);
            return true;
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static string DecodeAnsiOrUtf8(ReadOnlySpan<byte> bytes)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    internal static void WriteFString(Stream s, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            WriteI32(s, 0);
            return;
        }

        bool isAscii = true;
        foreach (char c in value)
        {
            if (c >= 0x80) { isAscii = false; break; }
        }

        if (isAscii)
        {
            WriteI32(s, value.Length + 1);
            s.Write(Encoding.ASCII.GetBytes(value));
            s.WriteByte(0);
        }
        else
        {
            WriteI32(s, -(value.Length + 1));
            s.Write(Encoding.Unicode.GetBytes(value));
            s.WriteByte(0);
            s.WriteByte(0);
        }
    }

    private static void WriteI32(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); s.Write(b); }
    private static void WriteU32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); s.Write(b); }
    private static void WriteI64(Stream s, long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); s.Write(b); }
}
