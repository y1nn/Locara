using System.Buffers.Binary;

namespace Locara.Core;

/// <summary>
/// Pure-C# reader and writer for Unreal Engine 4 &amp; 5 UStringTable assets (.uasset, both standalone
/// single-file packages and cooked split .uasset + .uexp pairs, on disk or inside .pak archives).
/// Preserves FPackageFileSummary, NameMap, ImportMap, ExportMap, UObject tagged properties, and StringTable metadata
/// while updating SerialSize and encoding Arabic text as UTF-16LE FStrings.
/// </summary>
public sealed class UnrealStringTableFormat : IFormatHandler
{
    public static UnrealStringTableFormat Instance { get; } = new();
    public string Name => "unreal-stringtable";

    public const uint PackageFileTag = 0x9E2A83C1u;

    public bool Supports(string path) =>
        Path.GetExtension(path).Equals(".uasset", StringComparison.OrdinalIgnoreCase);

    internal sealed record StringTableEntry(string Key, string Value);
    internal sealed record StringTableMetaItem(int MetaIdNameIndex, int MetaIdNumber, string MetaValue);
    internal sealed record StringTableMetaGroup(string Key, IReadOnlyList<StringTableMetaItem> Items);

    private sealed record ParsedStringTable(
        int TotalHeaderSize,
        int SerialSizeFieldOffset,
        bool IsSplitUexp,
        byte[] PropertyPrefixBytes,
        string TableNamespace,
        IReadOnlyList<StringTableEntry> Entries,
        IReadOnlyList<StringTableMetaGroup> MetaGroups,
        IReadOnlyDictionary<string, string> CommentsByKey);

    public IReadOnlyList<TextSlot> Extract(byte[] bytes)
    {
        if (!TryParsePair(bytes, null, out var parsed) || parsed == null)
            return [];
        return BuildSlots(parsed);
    }

    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations)
    {
        var (newUasset, _, changed) = ApplyPair(bytes, null, translations);
        return changed ? newUasset : bytes;
    }

    public static bool HasStringTable(string uassetPath)
    {
        try
        {
            var info = new FileInfo(uassetPath);
            if (!info.Exists || info.Length < 64 || info.Length > 16L * 1024 * 1024) return false;
            byte[] uasset = File.ReadAllBytes(uassetPath);
            if (!TryInspectUassetHeader(uasset, out _, out _, out _)) return false;
            string uexpPath = Path.ChangeExtension(uassetPath, ".uexp");
            byte[]? uexp = File.Exists(uexpPath) ? File.ReadAllBytes(uexpPath) : null;
            return TryParsePair(uasset, uexp, out var parsed) && parsed != null && parsed.Entries.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<TextSlot> ExtractFromFiles(string uassetPath, string? uexpPath = null)
    {
        byte[] uasset = File.ReadAllBytes(uassetPath);
        string resolvedUexp = uexpPath ?? Path.ChangeExtension(uassetPath, ".uexp");
        byte[]? uexp = File.Exists(resolvedUexp) ? File.ReadAllBytes(resolvedUexp) : null;
        return ExtractPair(uasset, uexp);
    }

    internal static IReadOnlyList<TextSlot> ExtractPair(byte[] uasset, byte[]? uexp)
    {
        if (!TryParsePair(uasset, uexp, out var parsed) || parsed == null)
            return [];
        return BuildSlots(parsed);
    }

    internal static bool IsStringTableHeader(ReadOnlySpan<byte> uasset) =>
        TryInspectUassetHeader(uasset, out _, out _, out _);

    /// <summary>
    /// Fast zero-false-negative prefix filter for multi-block .uasset entries inside .pak archives.
    /// Returns false only when the package header is invalid or the entire NameMap fits inside <paramref name="prefix"/>
    /// without referencing "StringTable".
    /// </summary>
    internal static bool CouldBeStringTablePrefix(ReadOnlySpan<byte> prefix, long fullLength)
    {
        if (prefix.Length >= fullLength)
            return IsStringTableHeader(prefix);
        if (prefix.Length < 64)
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(prefix) != PackageFileTag)
            return false;

        int legacyVer = BinaryPrimitives.ReadInt32LittleEndian(prefix[4..]);
        if (legacyVer is > -6 or < -9) return false;

        int pos = 8;
        if (legacyVer != -4) pos += 4;
        if (pos + 16 > prefix.Length) return false;
        int ue4Ver = BinaryPrimitives.ReadInt32LittleEndian(prefix[pos..]); pos += 4;
        int ue5Ver = 0;
        if (legacyVer <= -8)
        {
            ue5Ver = BinaryPrimitives.ReadInt32LittleEndian(prefix[pos..]);
            pos += 4;
        }
        pos += 4; // FileVersionLicenseeUE

        int totalHeaderSize;
        if (ue5Ver >= 1012)
        {
            if (pos + 28 > prefix.Length) return true;
            pos += 20;
            totalHeaderSize = BinaryPrimitives.ReadInt32LittleEndian(prefix[pos..]); pos += 4;
            int customCount = BinaryPrimitives.ReadInt32LittleEndian(prefix[pos..]); pos += 4;
            if (customCount < 0 || customCount > 4096) return false;
            if (pos + customCount * 20 > prefix.Length) return true;
            pos += customCount * 20;
        }
        else
        {
            if (pos + 8 > prefix.Length) return true;
            int customCount = BinaryPrimitives.ReadInt32LittleEndian(prefix[pos..]); pos += 4;
            if (customCount < 0 || customCount > 4096) return false;
            if (pos + customCount * 20 + 4 > prefix.Length) return true;
            pos += customCount * 20;
            totalHeaderSize = BinaryPrimitives.ReadInt32LittleEndian(prefix[pos..]); pos += 4;
        }

        if (totalHeaderSize <= 0 || totalHeaderSize > fullLength) return false;
        if (!UnrealLocresFormat.TryReadFString(prefix, ref pos, out _)) return true;
        if (pos + 12 > prefix.Length) return true;
        pos += 4; // PackageFlags
        int nameCount = BinaryPrimitives.ReadInt32LittleEndian(prefix[pos..]); pos += 4;
        int nameOffset = BinaryPrimitives.ReadInt32LittleEndian(prefix[pos..]); pos += 4;

        if (nameCount <= 0 || nameCount > 100_000 || nameOffset <= 0 || nameOffset >= totalHeaderSize)
            return false;
        if (nameOffset >= prefix.Length)
            return true;

        bool hasHashes = ue4Ver >= 504 || legacyVer <= -8;
        int npos = nameOffset;
        for (int i = 0; i < nameCount; i++)
        {
            if (!UnrealLocresFormat.TryReadFString(prefix, ref npos, out string name))
                return true;
            if (hasHashes)
            {
                if (npos + 4 > prefix.Length) return true;
                npos += 4;
            }
            if (name.Equals("StringTable", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    internal static (byte[] Uasset, byte[]? Uexp, bool Changed) ApplyPair(
        byte[] uasset,
        byte[]? uexp,
        IReadOnlyDictionary<string, string> translations)
    {
        if (translations.Count == 0) return (uasset, uexp, false);
        if (!TryParsePair(uasset, uexp, out var parsed) || parsed == null)
            throw new InvalidDataException("ملف Unreal StringTable (.uasset) غير صالح أو غير مدعوم.");

        bool anyChanged = false;
        var updatedEntries = new List<StringTableEntry>(parsed.Entries.Count);
        foreach (var entry in parsed.Entries)
        {
            string locator = MakeLocator(parsed.TableNamespace, entry.Key);
            if (translations.TryGetValue(locator, out string? target) && target != entry.Value)
            {
                updatedEntries.Add(new StringTableEntry(entry.Key, target));
                anyChanged = true;
            }
            else
            {
                updatedEntries.Add(entry);
            }
        }
        if (!anyChanged) return (uasset, uexp, false);

        byte[] newPayload;
        using (var pms = new MemoryStream())
        {
            pms.Write(parsed.PropertyPrefixBytes);
            UnrealLocresFormat.WriteFString(pms, parsed.TableNamespace);
            WriteI32(pms, updatedEntries.Count);
            foreach (var e in updatedEntries)
            {
                UnrealLocresFormat.WriteFString(pms, e.Key);
                UnrealLocresFormat.WriteFString(pms, e.Value);
            }
            WriteI32(pms, parsed.MetaGroups.Count);
            foreach (var g in parsed.MetaGroups)
            {
                UnrealLocresFormat.WriteFString(pms, g.Key);
                WriteI32(pms, g.Items.Count);
                foreach (var item in g.Items)
                {
                    WriteI32(pms, item.MetaIdNameIndex);
                    WriteI32(pms, item.MetaIdNumber);
                    UnrealLocresFormat.WriteFString(pms, item.MetaValue);
                }
            }
            newPayload = pms.ToArray();
        }

        if (parsed.IsSplitUexp)
        {
            byte[] newUasset = (byte[])uasset.Clone();
            BinaryPrimitives.WriteInt64LittleEndian(newUasset.AsSpan(parsed.SerialSizeFieldOffset), newPayload.LongLength);

            byte[] newUexp = new byte[newPayload.Length + 4];
            Buffer.BlockCopy(newPayload, 0, newUexp, 0, newPayload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(newUexp.AsSpan(newPayload.Length), PackageFileTag);
            return (newUasset, newUexp, true);
        }
        else
        {
            byte[] combined = new byte[parsed.TotalHeaderSize + newPayload.Length + 4];
            Buffer.BlockCopy(uasset, 0, combined, 0, parsed.TotalHeaderSize);
            BinaryPrimitives.WriteInt64LittleEndian(combined.AsSpan(parsed.SerialSizeFieldOffset), newPayload.LongLength);
            Buffer.BlockCopy(newPayload, 0, combined, parsed.TotalHeaderSize, newPayload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(combined.AsSpan(parsed.TotalHeaderSize + newPayload.Length), PackageFileTag);
            return (combined, null, true);
        }
    }

    internal static string MakeLocator(string ns, string key) =>
        $"ust:{ns.Length}:{ns}:{key}";

    private static IReadOnlyList<TextSlot> BuildSlots(ParsedStringTable parsed)
    {
        var slots = new List<TextSlot>(parsed.Entries.Count);
        foreach (var e in parsed.Entries)
        {
            if (string.IsNullOrWhiteSpace(e.Value)) continue;
            string locator = MakeLocator(parsed.TableNamespace, e.Key);
            string ctx = string.IsNullOrEmpty(parsed.TableNamespace) ? e.Key : $"{parsed.TableNamespace} / {e.Key}";
            if (parsed.CommentsByKey.TryGetValue(e.Key, out string? comment) && !string.IsNullOrWhiteSpace(comment))
                ctx += $" — {comment}";
            slots.Add(new TextSlot(locator, e.Value, ctx));
        }
        return slots;
    }

    private static bool TryInspectUassetHeader(
        ReadOnlySpan<byte> uasset,
        out int totalHeaderSize,
        out List<string> names,
        out int afterNameMapOffset)
    {
        totalHeaderSize = 0;
        names = [];
        afterNameMapOffset = 0;
        if (uasset.Length < 64) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(uasset) != PackageFileTag) return false;

        int legacyVer = BinaryPrimitives.ReadInt32LittleEndian(uasset[4..]);
        if (legacyVer is > -6 or < -9) return false;

        int pos = 8;
        if (legacyVer != -4) pos += 4; // LegacyUE3Version
        if (pos + 16 > uasset.Length) return false;
        int ue4Ver = BinaryPrimitives.ReadInt32LittleEndian(uasset[pos..]); pos += 4;
        int ue5Ver = 0;
        if (legacyVer <= -8)
        {
            ue5Ver = BinaryPrimitives.ReadInt32LittleEndian(uasset[pos..]);
            pos += 4;
        }
        pos += 4; // FileVersionLicenseeUE

        if (ue5Ver >= 1012)
        {
            if (pos + 28 > uasset.Length) return false;
            pos += 20; // SavedHash (FIoHash)
            totalHeaderSize = BinaryPrimitives.ReadInt32LittleEndian(uasset[pos..]); pos += 4;
            int customCount = BinaryPrimitives.ReadInt32LittleEndian(uasset[pos..]); pos += 4;
            if (customCount < 0 || customCount > 4096 || pos + customCount * 20 > uasset.Length) return false;
            pos += customCount * 20;
        }
        else
        {
            if (pos + 8 > uasset.Length) return false;
            int customCount = BinaryPrimitives.ReadInt32LittleEndian(uasset[pos..]); pos += 4;
            if (customCount < 0 || customCount > 4096 || pos + customCount * 20 + 4 > uasset.Length) return false;
            pos += customCount * 20;
            totalHeaderSize = BinaryPrimitives.ReadInt32LittleEndian(uasset[pos..]); pos += 4;
        }

        if (totalHeaderSize <= 0 || totalHeaderSize > uasset.Length) return false;
        if (!UnrealLocresFormat.TryReadFString(uasset, ref pos, out _)) return false; // FolderName
        if (pos + 12 > uasset.Length) return false;
        pos += 4; // PackageFlags
        int nameCount = BinaryPrimitives.ReadInt32LittleEndian(uasset[pos..]); pos += 4;
        int nameOffset = BinaryPrimitives.ReadInt32LittleEndian(uasset[pos..]); pos += 4;

        if (nameCount <= 0 || nameCount > 100_000 || nameOffset <= 0 || nameOffset >= totalHeaderSize)
            return false;

        bool hasHashes = ue4Ver >= 504 || legacyVer <= -8;
        int npos = nameOffset;
        names = new List<string>(nameCount);
        bool sawStringTable = false;
        for (int i = 0; i < nameCount; i++)
        {
            if (!UnrealLocresFormat.TryReadFString(uasset, ref npos, out string name)) return false;
            if (hasHashes)
            {
                if (npos + 4 > totalHeaderSize) return false;
                npos += 4;
            }
            if (name.Equals("StringTable", StringComparison.Ordinal)) sawStringTable = true;
            names.Add(name);
        }
        afterNameMapOffset = npos;
        return sawStringTable;
    }

    private static bool TryParsePair(ReadOnlySpan<byte> uasset, ReadOnlySpan<byte> uexp, out ParsedStringTable? result)
    {
        result = null;
        if (!TryInspectUassetHeader(uasset, out int totalHeaderSize, out var names, out int afterNameMapOffset))
            return false;

        int noneIdx = names.FindIndex(n => n.Equals("None", StringComparison.Ordinal));
        if (noneIdx < 0) return false;

        bool isSplit = !uexp.IsEmpty;
        ReadOnlySpan<byte> payload;
        if (isSplit)
        {
            if (uexp.Length < 16) return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(uexp[(uexp.Length - 4)..]) != PackageFileTag)
                return false;
            payload = uexp[..^4];
        }
        else
        {
            if (uasset.Length < totalHeaderSize + 16) return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(uasset[(uasset.Length - 4)..]) != PackageFileTag)
                return false;
            payload = uasset.Slice(totalHeaderSize, uasset.Length - 4 - totalHeaderSize);
        }

        // Locate exact FObjectExport (int64 SerialSize == payload.Length, int64 SerialOffset == totalHeaderSize)
        int serialSizeFieldOffset = -1;
        for (int scan = afterNameMapOffset; scan <= totalHeaderSize - 16; scan += 4)
        {
            long candSize = BinaryPrimitives.ReadInt64LittleEndian(uasset[scan..]);
            long candOff = BinaryPrimitives.ReadInt64LittleEndian(uasset[(scan + 8)..]);
            if (candSize == payload.Length && candOff == totalHeaderSize)
            {
                serialSizeFieldOffset = scan;
                break;
            }
        }
        if (serialSizeFieldOffset < 0) return false;

        // Locate end of UObject tagged properties (FName("None") = (noneIdx, 0) + int32 bHasGuid = 0)
        // Usually right at offset 0 of payload (12 bytes), or after tagged properties.
        for (int propEnd = 0; propEnd <= Math.Min(payload.Length - 12, 4096); propEnd += 4)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(payload[propEnd..]) != noneIdx) continue;
            if (BinaryPrimitives.ReadInt32LittleEndian(payload[(propEnd + 4)..]) != 0) continue;

            // Try both with 4-byte ObjectGuid flag (0) and without (0 bytes)
            foreach (int guidBytes in new[] { 4, 0 })
            {
                int stStart = propEnd + 8 + guidBytes;
                if (stStart > payload.Length) continue;
                if (guidBytes == 4 && BinaryPrimitives.ReadInt32LittleEndian(payload[(propEnd + 8)..]) != 0)
                    continue;

                if (TryParseFStringTableBody(payload, stStart, names, out string ns, out var entries, out var metaGroups, out var comments))
                {
                    result = new ParsedStringTable(
                        totalHeaderSize,
                        serialSizeFieldOffset,
                        isSplit,
                        payload[..stStart].ToArray(),
                        ns,
                        entries,
                        metaGroups,
                        comments);
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryParseFStringTableBody(
        ReadOnlySpan<byte> payload,
        int startPos,
        List<string> names,
        out string tableNamespace,
        out List<StringTableEntry> entries,
        out List<StringTableMetaGroup> metaGroups,
        out Dictionary<string, string> comments)
    {
        tableNamespace = "";
        entries = [];
        metaGroups = [];
        comments = new Dictionary<string, string>(StringComparer.Ordinal);

        int pos = startPos;
        if (!UnrealLocresFormat.TryReadFString(payload, ref pos, out tableNamespace)) return false;
        if (pos + 4 > payload.Length) return false;
        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(payload[pos..]); pos += 4;
        if (entryCount < 0 || entryCount > 2_000_000) return false;

        entries = new List<StringTableEntry>(Math.Min(entryCount, 4096));
        for (int i = 0; i < entryCount; i++)
        {
            if (!UnrealLocresFormat.TryReadFString(payload, ref pos, out string key)) return false;
            if (!UnrealLocresFormat.TryReadFString(payload, ref pos, out string val)) return false;
            entries.Add(new StringTableEntry(key, val));
        }

        if (pos + 4 > payload.Length) return false;
        int metaCount = BinaryPrimitives.ReadInt32LittleEndian(payload[pos..]); pos += 4;
        if (metaCount < 0 || metaCount > 2_000_000) return false;

        metaGroups = new List<StringTableMetaGroup>(Math.Min(metaCount, 1024));
        for (int m = 0; m < metaCount; m++)
        {
            if (!UnrealLocresFormat.TryReadFString(payload, ref pos, out string metaKey)) return false;
            if (pos + 4 > payload.Length) return false;
            int mapCount = BinaryPrimitives.ReadInt32LittleEndian(payload[pos..]); pos += 4;
            if (mapCount < 0 || mapCount > 10_000) return false;

            var items = new List<StringTableMetaItem>(mapCount);
            for (int k = 0; k < mapCount; k++)
            {
                if (pos + 8 > payload.Length) return false;
                int nameIdx = BinaryPrimitives.ReadInt32LittleEndian(payload[pos..]); pos += 4;
                int nameNum = BinaryPrimitives.ReadInt32LittleEndian(payload[pos..]); pos += 4;
                if (nameIdx < 0 || nameIdx >= names.Count) return false;
                if (!UnrealLocresFormat.TryReadFString(payload, ref pos, out string metaVal)) return false;
                items.Add(new StringTableMetaItem(nameIdx, nameNum, metaVal));

                string metaIdName = names[nameIdx];
                if (metaIdName.Equals("Comment", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(metaVal))
                    comments[metaKey] = metaVal;
            }
            metaGroups.Add(new StringTableMetaGroup(metaKey, items));
        }

        // Exact end-of-payload verification ensures zero false positives or partial parses
        return pos == payload.Length;
    }

    private static void WriteI32(Stream s, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        s.Write(b);
    }
}
