using System.Buffers.Binary;
using System.Text;

namespace Locara.Core;

internal static class UnityFsStream
{
    private sealed record BlockInfo(int Uncompressed, int Compressed, ushort Flags);
    private sealed record NodeMeta(long Offset, long Size, uint Flags, string Name);

    private sealed record BundleHeader(
        uint Version,
        string UnityVersion,
        string UnityRevision,
        uint Flags,
        byte[] DataHash,
        long DataStart,
        IReadOnlyList<BlockInfo> Blocks,
        IReadOnlyList<NodeMeta> Nodes);

    private static BundleHeader? TryReadHeader(FileStream fs)
    {
        if (fs.Length < 64) return null;
        fs.Position = 0;
        byte[] head = new byte[(int)Math.Min(512, fs.Length)];
        fs.ReadExactly(head);
        if (!head.AsSpan(0, 8).SequenceEqual("UnityFS\0"u8)) return null;

        int pos = 8;
        uint version = ReadU32BE(head, ref pos);
        if (version is < 6 or > 8) return null;
        string unityVer = ReadCString(head, ref pos);
        string unityRev = ReadCString(head, ref pos);
        long declaredSize = ReadI64BE(head, ref pos);
        if (declaredSize != fs.Length) return null;
        int compInfoSize = ReadI32BE(head, ref pos);
        int uncompInfoSize = ReadI32BE(head, ref pos);
        uint flags = ReadU32BE(head, ref pos);

        if (version >= 7) pos = (pos + 15) & ~15;
        bool atEnd = (flags & 0x80) != 0;
        long infoOffset = atEnd ? fs.Length - compInfoSize : pos;
        if (infoOffset < 0 || infoOffset + compInfoSize > fs.Length || uncompInfoSize <= 0 || uncompInfoSize > 32 * 1024 * 1024)
            return null;

        fs.Position = infoOffset;
        byte[] compInfo = new byte[compInfoSize];
        fs.ReadExactly(compInfo);

        int infoComp = (int)(flags & 0x3F);
        byte[] info = infoComp switch
        {
            UnityFsBundle.CompressionNone => compInfo,
            UnityFsBundle.CompressionLz4 or UnityFsBundle.CompressionLz4Hc => Lz4Block.Decompress(compInfo, uncompInfoSize),
            _ => throw new NotSupportedException("ضغط LZMA داخل حزم Unity غير مدعوم حالياً.")
        };

        long dataStart = atEnd ? pos : pos + compInfoSize;
        if ((flags & 0x200) != 0) dataStart = (dataStart + 15) & ~15;

        if (info.Length < 24) return null;
        byte[] hash = info[..16];
        int ip = 16;
        int blockCount = ReadI32BE(info, ref ip);
        if (blockCount < 0 || ip + blockCount * 10 > info.Length) return null;
        var blocks = new List<BlockInfo>(blockCount);
        for (int i = 0; i < blockCount; i++)
        {
            int bu = ReadI32BE(info, ref ip);
            int bc = ReadI32BE(info, ref ip);
            ushort bf = ReadU16BE(info, ref ip);
            blocks.Add(new(bu, bc, bf));
        }

        int nodeCount = ReadI32BE(info, ref ip);
        if (nodeCount < 0 || nodeCount > 4096) return null;
        var nodes = new List<NodeMeta>(nodeCount);
        for (int i = 0; i < nodeCount; i++)
        {
            long no = ReadI64BE(info, ref ip);
            long ns = ReadI64BE(info, ref ip);
            uint nf = ReadU32BE(info, ref ip);
            string name = ReadCString(info, ref ip);
            nodes.Add(new(no, ns, nf, name));
        }

        return new(version, unityVer, unityRev, flags, hash, dataStart, blocks, nodes);
    }

    public static IReadOnlyList<string> ListSerializedNodeNames(string bundlePath)
    {
        try
        {
            using var fs = File.OpenRead(bundlePath);
            var header = TryReadHeader(fs);
            if (header == null) return [];
            return header.Nodes
                .Where(n => (n.Flags & 4) != 0 && n.Size > 0 && n.Size <= 128L * 1024 * 1024)
                .Select(n => n.Name)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static byte[]? TryReadNode(string bundlePath, string nodeName)
    {
        try
        {
            using var fs = File.OpenRead(bundlePath);
            var header = TryReadHeader(fs);
            if (header == null) return null;
            var target = header.Nodes.FirstOrDefault(n => n.Name.Equals(nodeName, StringComparison.OrdinalIgnoreCase));
            if (target == null || target.Size <= 0 || target.Size > 128L * 1024 * 1024) return null;

            byte[] dst = new byte[(int)target.Size];
            long streamPos = 0;
            long filePos = header.DataStart;
            long targetEnd = target.Offset + target.Size;

            foreach (var blk in header.Blocks)
            {
                long blkEnd = streamPos + blk.Uncompressed;
                if (blkEnd > target.Offset && streamPos < targetEnd)
                {
                    fs.Position = filePos;
                    byte[] raw = new byte[blk.Compressed];
                    fs.ReadExactly(raw);
                    byte[] uncomp = DecompressBlock(raw, blk);
                    long copyStart = Math.Max(streamPos, target.Offset);
                    long copyEnd = Math.Min(blkEnd, targetEnd);
                    Buffer.BlockCopy(uncomp, (int)(copyStart - streamPos), dst, (int)(copyStart - target.Offset), (int)(copyEnd - copyStart));
                }
                streamPos = blkEnd;
                filePos += blk.Compressed;
                if (streamPos >= targetEnd) break;
            }
            return dst;
        }
        catch
        {
            return null;
        }
    }

    public static void PatchNode(string sourceBundlePath, string destinationBundlePath, string nodeName,
        byte[] newNodeBytes, CancellationToken cancellation)
    {
        using var src = File.OpenRead(sourceBundlePath);
        var header = TryReadHeader(src) ?? throw new InvalidDataException("تعذّرت قراءة ترويسة حزمة UnityFS.");

        int targetIdx = -1;
        for (int i = 0; i < header.Nodes.Count; i++)
        {
            if (header.Nodes[i].Name.Equals(nodeName, StringComparison.OrdinalIgnoreCase))
            {
                targetIdx = i;
                break;
            }
        }
        if (targetIdx < 0) throw new FileNotFoundException("لم يُعثر على العقدة المطلوبة داخل حزمة UnityFS.", nodeName);

        var target = header.Nodes[targetIdx];
        long targetStart = target.Offset;
        long targetEnd = target.Offset + target.Size;

        // Identify the contiguous range of blocks overlapping [targetStart, targetEnd)
        int firstOverlap = -1;
        int lastOverlap = -1;
        long firstOverlapStreamStart = 0;
        long lastOverlapStreamEnd = 0;
        long firstOverlapFileStart = 0;
        long afterOverlapFileStart = 0;

        long sPos = 0;
        long fPos = header.DataStart;
        var blockFileOffsets = new long[header.Blocks.Count];
        for (int i = 0; i < header.Blocks.Count; i++)
        {
            blockFileOffsets[i] = fPos;
            long blkEnd = sPos + header.Blocks[i].Uncompressed;
            if (blkEnd > targetStart && sPos < targetEnd)
            {
                if (firstOverlap < 0)
                {
                    firstOverlap = i;
                    firstOverlapStreamStart = sPos;
                    firstOverlapFileStart = fPos;
                }
                lastOverlap = i;
                lastOverlapStreamEnd = blkEnd;
                afterOverlapFileStart = fPos + header.Blocks[i].Compressed;
            }
            sPos = blkEnd;
            fPos += header.Blocks[i].Compressed;
        }
        if (firstOverlap < 0) throw new InvalidDataException("نطاق العقدة خارج كتل حزمة UnityFS.");

        // Read prefix bytes before targetStart in firstOverlap, and suffix bytes after targetEnd in lastOverlap
        byte[] prefixBytes = [];
        if (targetStart > firstOverlapStreamStart)
        {
            src.Position = firstOverlapFileStart;
            byte[] rawFirst = new byte[header.Blocks[firstOverlap].Compressed];
            src.ReadExactly(rawFirst);
            byte[] uncompFirst = DecompressBlock(rawFirst, header.Blocks[firstOverlap]);
            int prefixLen = checked((int)(targetStart - firstOverlapStreamStart));
            prefixBytes = uncompFirst[..prefixLen];
        }

        byte[] suffixBytes = [];
        if (lastOverlapStreamEnd > targetEnd)
        {
            src.Position = blockFileOffsets[lastOverlap];
            byte[] rawLast = new byte[header.Blocks[lastOverlap].Compressed];
            src.ReadExactly(rawLast);
            byte[] uncompLast = DecompressBlock(rawLast, header.Blocks[lastOverlap]);
            int skipLen = checked((int)(targetEnd - (lastOverlapStreamEnd - header.Blocks[lastOverlap].Uncompressed)));
            suffixBytes = uncompLast[skipLen..];
        }

        // Unity ArchiveStorage requires EVERY interior block (0 .. N-2) to be strictly 128 KiB (0x20000).
        // When patching a node before the final block (e.g. resources.assets before resources.assets.resS),
        // insert zero gap padding between the patched node and suffixBytes so spliced.Length is an exact multiple of 128 KiB!
        const int chunkSize = 0x20000;
        bool useLz4 = (header.Flags & 0x3F) is UnityFsBundle.CompressionLz4 or UnityFsBundle.CompressionLz4Hc;
        int rawSplicedLen = prefixBytes.Length + newNodeBytes.Length + suffixBytes.Length;
        int gapPad = lastOverlap < header.Blocks.Count - 1
            ? (chunkSize - (rawSplicedLen % chunkSize)) % chunkSize
            : 0;

        byte[] spliced = new byte[rawSplicedLen + gapPad];
        Buffer.BlockCopy(prefixBytes, 0, spliced, 0, prefixBytes.Length);
        Buffer.BlockCopy(newNodeBytes, 0, spliced, prefixBytes.Length, newNodeBytes.Length);
        Buffer.BlockCopy(suffixBytes, 0, spliced, prefixBytes.Length + newNodeBytes.Length + gapPad, suffixBytes.Length);

        var replacementBlocks = new List<(BlockInfo Info, byte[] Data)>();
        for (int off = 0; off < spliced.Length; off += chunkSize)
        {
            cancellation.ThrowIfCancellationRequested();
            int len = Math.Min(chunkSize, spliced.Length - off);
            ReadOnlySpan<byte> slice = spliced.AsSpan(off, len);
            if (useLz4)
            {
                byte[] comp = Lz4Block.Compress(slice);
                if (comp.Length < len)
                {
                    replacementBlocks.Add((new BlockInfo(len, comp.Length, UnityFsBundle.CompressionLz4), comp));
                    continue;
                }
            }
            replacementBlocks.Add((new BlockInfo(len, len, UnityFsBundle.CompressionNone), slice.ToArray()));
        }

        // Assemble updated block list and shifted node table
        var newBlocks = new List<BlockInfo>(header.Blocks.Count - (lastOverlap - firstOverlap + 1) + replacementBlocks.Count);
        for (int i = 0; i < firstOverlap; i++) newBlocks.Add(header.Blocks[i]);
        foreach (var rb in replacementBlocks) newBlocks.Add(rb.Info);
        for (int i = lastOverlap + 1; i < header.Blocks.Count; i++) newBlocks.Add(header.Blocks[i]);

        long nodeShift = (newNodeBytes.LongLength - target.Size) + gapPad;
        var newNodes = new List<NodeMeta>(header.Nodes.Count);
        foreach (var node in header.Nodes)
        {
            if (ReferenceEquals(node, target))
                newNodes.Add(node with { Size = newNodeBytes.LongLength });
            else if (node.Offset >= targetEnd)
                newNodes.Add(node with { Offset = node.Offset + nodeShift });
            else
                newNodes.Add(node);
        }

        byte[] blocksInfo;
        using (var bi = new MemoryStream())
        {
            bi.Write(header.DataHash.Length == 16 ? header.DataHash : new byte[16]);
            WriteI32BE(bi, newBlocks.Count);
            foreach (var b in newBlocks)
            {
                WriteI32BE(bi, b.Uncompressed);
                WriteI32BE(bi, b.Compressed);
                WriteU16BE(bi, b.Flags);
            }
            WriteI32BE(bi, newNodes.Count);
            foreach (var n in newNodes)
            {
                WriteI64BE(bi, n.Offset);
                WriteI64BE(bi, n.Size);
                WriteU32BE(bi, n.Flags);
                WriteCString(bi, n.Name);
            }
            blocksInfo = bi.ToArray();
        }

        byte[] encodedBlocksInfo = blocksInfo;
        uint outFlags = header.Flags & ~0x3Fu;
        if (useLz4)
        {
            byte[] compInfo = Lz4Block.Compress(blocksInfo);
            if (compInfo.Length < blocksInfo.Length)
            {
                encodedBlocksInfo = compInfo;
                outFlags |= UnityFsBundle.CompressionLz4;
            }
        }
        outFlags |= 0x40u;
        bool atEnd = (outFlags & 0x80u) != 0;

        Directory.CreateDirectory(Path.GetDirectoryName(destinationBundlePath)!);
        using (var dst = File.Open(destinationBundlePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            WriteCString(dst, "UnityFS");
            WriteU32BE(dst, header.Version);
            WriteCString(dst, header.UnityVersion);
            WriteCString(dst, header.UnityRevision);
            long sizeFieldPos = dst.Position;
            WriteI64BE(dst, 0);
            WriteI32BE(dst, encodedBlocksInfo.Length);
            WriteI32BE(dst, blocksInfo.Length);
            WriteU32BE(dst, outFlags);

            if (header.Version >= 7) WriteAlign16(dst);
            if (!atEnd)
            {
                dst.Write(encodedBlocksInfo);
                if ((outFlags & 0x200u) != 0) WriteAlign16(dst);
            }

            // Stream raw compressed blocks BEFORE target directly (zero decompression!)
            long beforeBytes = firstOverlapFileStart - header.DataStart;
            if (beforeBytes > 0)
            {
                src.Position = header.DataStart;
                Containers.Copy(src, dst, beforeBytes, cancellation, checkEnd: false);
            }

            // Write replacement compressed chunks
            foreach (var rb in replacementBlocks)
            {
                cancellation.ThrowIfCancellationRequested();
                dst.Write(rb.Data);
            }

            // Stream raw compressed blocks AFTER target directly (zero decompression!)
            long afterEndPos = atEnd ? (src.Length - GetOriginalBlocksInfoSize(src)) : src.Length;
            long afterBytes = afterEndPos - afterOverlapFileStart;
            if (afterBytes > 0)
            {
                src.Position = afterOverlapFileStart;
                Containers.Copy(src, dst, afterBytes, cancellation, checkEnd: false);
            }

            if (atEnd) dst.Write(encodedBlocksInfo);

            long finalSize = dst.Position;
            dst.Position = sizeFieldPos;
            WriteI64BE(dst, finalSize);
            dst.Flush(flushToDisk: true);
        }

        // Verify read-back of the patched node AND strict 128 KiB interior block invariant
        using (var verifyFs = File.OpenRead(destinationBundlePath))
        {
            var verifyHeader = TryReadHeader(verifyFs)
                ?? throw new IOException("تعذّرت قراءة ترويسة UnityFS بعد إعادة البناء.");
            for (int i = 0; i < verifyHeader.Blocks.Count - 1; i++)
            {
                if (verifyHeader.Blocks[i].Uncompressed != chunkSize)
                    throw new IOException($"كتلة UnityFS الداخلية {i} غير محاذية لـ 128 KiB.");
            }
        }
        byte[]? verify = TryReadNode(destinationBundlePath, nodeName);
        if (verify == null || !verify.AsSpan().SequenceEqual(newNodeBytes))
            throw new IOException("تعذّر التحقق من عقدة UnityFS بعد إعادة البناء.");
    }

    private static int GetOriginalBlocksInfoSize(FileStream fs)
    {
        fs.Position = 8;
        byte[] head = new byte[(int)Math.Min(512, fs.Length)];
        fs.ReadExactly(head);
        int pos = 4; // skip version
        _ = ReadCString(head, ref pos);
        _ = ReadCString(head, ref pos);
        pos += 8; // declaredSize
        return ReadI32BE(head, ref pos);
    }

    private static byte[] DecompressBlock(byte[] raw, BlockInfo blk) => (blk.Flags & 0x3F) switch
    {
        UnityFsBundle.CompressionNone => raw,
        UnityFsBundle.CompressionLz4 or UnityFsBundle.CompressionLz4Hc => Lz4Block.Decompress(raw, blk.Uncompressed),
        _ => throw new NotSupportedException("ضغط LZMA غير مدعوم.")
    };

    private static string ReadCString(ReadOnlySpan<byte> d, ref int p)
    {
        int s = p;
        while (p < d.Length && d[p] != 0) p++;
        if (p >= d.Length) throw new InvalidDataException("نص مقطوع في ترويسة UnityFS.");
        string r = Encoding.UTF8.GetString(d.Slice(s, p - s));
        p++;
        return r;
    }

    private static int ReadI32BE(ReadOnlySpan<byte> d, ref int p) { int v = BinaryPrimitives.ReadInt32BigEndian(d[p..]); p += 4; return v; }
    private static uint ReadU32BE(ReadOnlySpan<byte> d, ref int p) { uint v = BinaryPrimitives.ReadUInt32BigEndian(d[p..]); p += 4; return v; }
    private static long ReadI64BE(ReadOnlySpan<byte> d, ref int p) { long v = BinaryPrimitives.ReadInt64BigEndian(d[p..]); p += 8; return v; }
    private static ushort ReadU16BE(ReadOnlySpan<byte> d, ref int p) { ushort v = BinaryPrimitives.ReadUInt16BigEndian(d[p..]); p += 2; return v; }
    private static void WriteCString(Stream s, string v) { s.Write(Encoding.UTF8.GetBytes(v)); s.WriteByte(0); }
    private static void WriteI32BE(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }
    private static void WriteU32BE(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
    private static void WriteI64BE(Stream s, long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); s.Write(b); }
    private static void WriteU16BE(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); s.Write(b); }
    private static void WriteAlign16(Stream s) { int pad = (int)((16 - (s.Position % 16)) % 16); if (pad > 0) s.Write(new byte[pad]); }
}

public sealed record UnityArabicDisplayOptions(string WrapMode = "auto", float FontScale = 0.80f, bool ShapeArabic = true, bool ContrastBoost = false)
{
    public static UnityArabicDisplayOptions Default { get; } = new("auto", 0.80f, true, false);

    public int MaxLineChars => ParseWrapMode(WrapMode);

    public static UnityArabicDisplayOptions Parse(string? wrapSetting, string? scaleSetting, string? shapingSetting = null, string? contrastSetting = null) =>
        new(NormalizeWrapMode(wrapSetting), ParseFontScale(scaleSetting), ParseShapeArabic(shapingSetting), ParseContrastBoost(contrastSetting));

    public static bool ParseShapeArabic(string? value)
    {
        string v = (value ?? "").Trim().ToLowerInvariant();
        if (v is "0" or "false" or "off" or "no") return false;
        return true;
    }

    public static bool ParseContrastBoost(string? value)
    {
        string v = (value ?? "").Trim().ToLowerInvariant();
        return v is "1" or "true" or "on" or "yes";
    }

    public string TransformArabic(string text) =>
        ShapeArabic && StarTruckerDisplay.HasArabic(text)
            ? StarTruckerDisplay.Shape(text, MaxLineChars)
            : text;

    public static string NormalizeWrapMode(string? value)
    {
        string v = (value ?? "").Trim().ToLowerInvariant();
        if (v.Length == 0 || v == "auto") return "auto";
        if (v is "0" or "off" or "none") return "0";
        if (int.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out int n))
            return n <= 0 ? "0" : Math.Clamp(n, 10, 120).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return "auto";
    }

    public static int ParseWrapMode(string? value)
    {
        string norm = NormalizeWrapMode(value);
        if (norm == "auto") return StarTruckerDisplay.AutoWrap;
        if (norm == "0") return 0;
        return int.TryParse(norm, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : StarTruckerDisplay.AutoWrap;
    }

    public static float ParseFontScale(string? value)
    {
        string v = (value ?? "").Trim().TrimEnd('%');
        if (v.Length == 0) return 0.80f;
        if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed))
        {
            if (parsed > 5f) parsed /= 100f;
            return MathF.Round(Math.Clamp(parsed, 0.50f, 1.50f), 2);
        }
        return 0.80f;
    }

    public string WrapLabel => !ShapeArabic ? "أصلي (RTL)" : NormalizeWrapMode(WrapMode) switch
    {
        "auto" => "تلقائي",
        "0" => "متوقف",
        var n => $"{n} حرف"
    };

    public string ScaleLabel => $"{Math.Round(FontScale * 100f):0}%";
}

internal static class UnityTmpArabicPatcher
{
    public static bool TryPatch(UnitySerializedFile sf, ArabicFontChoice? fontChoice, Dictionary<long, byte[]> replacements,
        float fallbackFontScale = 0.80f, bool contrastBoost = false)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) return false;

        UnitySerializedFile.ObjectEntry? libFont = null;
        UnitySerializedFile.ObjectEntry? fallbackAsset = null;
        UnitySerializedFile.ObjectEntry? tmpSettings = null;
        UnitySerializedFile.ObjectEntry? fallbackMaterial = null;

        foreach (var obj in sf.Objects)
        {
            int cid = sf.Types[obj.TypeIndex].ClassId;
            if (cid == 128 && libFont == null)
            {
                var data = sf.Slice(obj);
                if (TryReadName(data, 0, out string name) && name == "LiberationSans")
                    libFont = obj;
            }
            else if (cid == 21 && fallbackMaterial == null)
            {
                var data = sf.Slice(obj);
                if (TryReadName(data, 0, out string matName) && matName == "LiberationSans SDF - Fallback Material")
                    fallbackMaterial = obj;
            }
            else if (cid == 114 && obj.ByteSize > 36)
            {
                var data = sf.Slice(obj);
                if (TryReadName(data, 28, out string name))
                {
                    if (name == "LiberationSans SDF - Fallback" && fallbackAsset == null)
                        fallbackAsset = obj;
                    else if (name == "TMP Settings" && tmpSettings == null)
                        tmpSettings = obj;
                }
            }
        }

        if (libFont == null || fallbackAsset == null || tmpSettings == null)
            return false;

        try
        {
            var chosen = fontChoice ?? ArabicFontCatalog.BuiltInFonts[0];
            if (contrastBoost)
                chosen = ArabicFontCatalog.ResolveContrastVariant(chosen);
            byte[] fontBytes = ArabicFontCatalog.LoadBytes(chosen);
            string fontName = string.IsNullOrWhiteSpace(chosen.FontFamilyName) ? "Noto Sans Arabic" : chosen.FontFamilyName;

            byte[] patchedFont = StarTruckerDisplay.ReplaceFont(sf.Slice(libFont), fontBytes, fontName);
            byte[]? patchedSettings = TryPatchTmpSettings(sf.Slice(tmpSettings), fallbackAsset.PathId);
            if (patchedSettings == null) return false;

            replacements[libFont.PathId] = patchedFont;
            replacements[tmpSettings.PathId] = patchedSettings;

            float clampedScale = MathF.Round(Math.Clamp(fallbackFontScale, 0.50f, 1.50f), 2);
            byte[]? scaledFallback = TryScaleFallbackFontAsset(sf.Slice(fallbackAsset), targetScale: clampedScale);
            if (scaledFallback != null)
                replacements[fallbackAsset.PathId] = scaledFallback;

            if (contrastBoost && fallbackMaterial != null)
            {
                byte[]? boostedMat = TryBoostFallbackMaterial(sf.Slice(fallbackMaterial));
                if (boostedMat != null)
                    replacements[fallbackMaterial.PathId] = boostedMat;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool CanPatchFile(string filePath)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) return false;
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length < 256) return false;
            string name = Path.GetFileName(filePath);
            byte[]? resBytes = name.Equals("data.unity3d", StringComparison.OrdinalIgnoreCase)
                ? UnityFsStream.TryReadNode(filePath, "resources.assets")
                : (info.Length <= 128L * 1024 * 1024 ? File.ReadAllBytes(filePath) : null);
            if (resBytes == null) return false;
            var sf = UnitySerializedFile.TryRead(resBytes);
            if (sf == null) return false;
            return TryPatch(sf, fontChoice: null, new Dictionary<long, byte[]>());
        }
        catch
        {
            return false;
        }
    }

    public static bool PatchFile(string sourceFilePath, string destinationFilePath,
        ArabicFontChoice? fontChoice, UnityArabicDisplayOptions? displayOptions, CancellationToken cancellation)
    {
        var opts = displayOptions ?? UnityArabicDisplayOptions.Default;
        string name = Path.GetFileName(sourceFilePath);
        if (name.Equals("data.unity3d", StringComparison.OrdinalIgnoreCase))
        {
            byte[]? resBytes = UnityFsStream.TryReadNode(sourceFilePath, "resources.assets");
            if (resBytes == null) return false;
            var sf = UnitySerializedFile.TryRead(resBytes);
            if (sf == null) return false;
            var replacements = new Dictionary<long, byte[]>();
            if (!TryPatch(sf, fontChoice, replacements, opts.FontScale, opts.ContrastBoost) || replacements.Count == 0)
                return false;
            byte[] rewrittenRes = sf.Rewrite(replacements);
            UnityFsStream.PatchNode(sourceFilePath, destinationFilePath, "resources.assets", rewrittenRes, cancellation);
            return true;
        }
        else
        {
            byte[] orig = File.ReadAllBytes(sourceFilePath);
            var sf = UnitySerializedFile.TryRead(orig);
            if (sf == null) return false;
            var replacements = new Dictionary<long, byte[]>();
            if (!TryPatch(sf, fontChoice, replacements, opts.FontScale, opts.ContrastBoost) || replacements.Count == 0)
                return false;
            byte[] rewritten = sf.Rewrite(replacements);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
            File.WriteAllBytes(destinationFilePath, rewritten);
            return true;
        }
    }

    internal static byte[]? TryScaleFallbackFontAsset(ReadOnlySpan<byte> fallbackData, float targetScale)
    {
        // Locate m_FaceInfo inside standard LiberationSans SDF - Fallback:
        // "Liberation Sans" (aligned) + "Regular" (aligned) + m_PointSize (86f) + m_Scale (1.0f) + m_UnitsPerEM (2048)
        ReadOnlySpan<byte> familyMarker = "Liberation Sans"u8;
        int famPos = fallbackData.IndexOf(familyMarker);
        if (famPos < 4) return null;
        if (BinaryPrimitives.ReadInt32LittleEndian(fallbackData[(famPos - 4)..]) != familyMarker.Length) return null;
        int pos = famPos + ((familyMarker.Length + 3) & ~3);

        if (pos + 4 > fallbackData.Length) return null;
        int styleLen = BinaryPrimitives.ReadInt32LittleEndian(fallbackData[pos..]);
        pos += 4;
        if (styleLen < 0 || styleLen > 64 || pos + ((styleLen + 3) & ~3) + 12 > fallbackData.Length) return null;
        pos += (styleLen + 3) & ~3;

        float pointSize = BinaryPrimitives.ReadSingleLittleEndian(fallbackData[pos..]);
        float currentScale = BinaryPrimitives.ReadSingleLittleEndian(fallbackData[(pos + 4)..]);
        int upem = BinaryPrimitives.ReadInt32LittleEndian(fallbackData[(pos + 8)..]);
        if (pointSize is < 16f or > 256f || currentScale is < 0.25f or > 2.0f || upem is not (1000 or 2048))
            return null;
        if (Math.Abs(currentScale - targetScale) < 0.001f)
            return null;

        byte[] updated = fallbackData.ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(updated.AsSpan(pos + 4), targetScale);
        return updated;
    }

    internal static byte[]? TryBoostFallbackMaterial(ReadOnlySpan<byte> materialData, float targetFaceDilate = 0.16f)
    {
        if (!TryReadName(materialData, 0, out string matName) || matName != "LiberationSans SDF - Fallback Material")
            return null;

        // Locate aligned "_FaceDilate" (len 11 + "_FaceDilate\0") inside m_SavedProperties.m_Floats
        ReadOnlySpan<byte> propName = "_FaceDilate"u8;
        int idx = materialData.IndexOf(propName);
        if (idx < 4) return null;
        if (BinaryPrimitives.ReadInt32LittleEndian(materialData[(idx - 4)..]) != propName.Length)
            return null;

        int floatOffset = idx + ((propName.Length + 3) & ~3);
        if (floatOffset + 4 > materialData.Length) return null;
        float current = BinaryPrimitives.ReadSingleLittleEndian(materialData[floatOffset..]);
        if (float.IsNaN(current) || current is < -1.0f or > 1.0f) return null;
        if (current >= targetFaceDilate - 0.001f) return null;

        byte[] updated = materialData.ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(updated.AsSpan(floatOffset), targetFaceDilate);
        return updated;
    }

    internal static byte[]? TryPatchTmpSettings(ReadOnlySpan<byte> settingsData, long fallbackPathId)
    {
        byte[]? updated = TryAddTmpFallback(settingsData, fallbackPathId);
        if (updated == null) return null;

        // Disable runtime OpenType GPOS 'kern' loading in Unity 6 / TMP 3.2+:
        // OpenType GPOS kerning pairs in Arabic fonts are authored for logical RTL order,
        // so applying them to visually-reordered LTR Arabic shifts descending letters (like ر / ز) over adjacent glyphs!
        ReadOnlySpan<byte> kernTag = "nrek"u8; // 'kern' 0x6B65726E stored little-endian
        int kernPos = updated.AsSpan().IndexOf(kernTag);
        if (kernPos >= 8 && kernPos + 24 <= updated.Length
            && BinaryPrimitives.ReadInt32LittleEndian(updated.AsSpan(kernPos - 4)) == 1  // m_ActiveFontFeatures.size == 1
            && BinaryPrimitives.ReadInt32LittleEndian(updated.AsSpan(kernPos - 8)) <= 1  // m_enableKerning (bool)
            && BinaryPrimitives.ReadInt32LittleEndian(updated.AsSpan(kernPos + 20)) <= 1) // m_GetFontFeaturesAtRuntime (bool)
        {
            BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(kernPos - 8), 0); // m_enableKerning = false
            BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(kernPos), 0);     // neutralize 'kern' tag
            BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(kernPos + 20), 0); // m_GetFontFeaturesAtRuntime = false
        }
        return updated;
    }

    internal static byte[]? TryAddTmpFallback(ReadOnlySpan<byte> settingsData, long fallbackPathId)
    {
        ReadOnlySpan<byte> marker = "Fonts & Materials/"u8;
        int markerPos = settingsData.IndexOf(marker);
        if (markerPos < 4) return null;

        int len = BinaryPrimitives.ReadInt32LittleEndian(settingsData[(markerPos - 4)..]);
        if (len != marker.Length) return null;
        int afterFolder = markerPos + ((len + 3) & ~3);

        // Verify the 7 standard TMP_Settings floats: 36, 0.5, 2, 20, 5, 200, 50
        if (afterFolder + 44 > settingsData.Length) return null;
        if (BinaryPrimitives.ReadSingleLittleEndian(settingsData[afterFolder..]) != 36f
            || BinaryPrimitives.ReadSingleLittleEndian(settingsData[(afterFolder + 4)..]) != 0.5f
            || BinaryPrimitives.ReadSingleLittleEndian(settingsData[(afterFolder + 8)..]) != 2f
            || BinaryPrimitives.ReadSingleLittleEndian(settingsData[(afterFolder + 12)..]) != 20f
            || BinaryPrimitives.ReadSingleLittleEndian(settingsData[(afterFolder + 16)..]) != 5f
            || BinaryPrimitives.ReadSingleLittleEndian(settingsData[(afterFolder + 20)..]) != 200f
            || BinaryPrimitives.ReadSingleLittleEndian(settingsData[(afterFolder + 24)..]) != 50f)
            return null;

        // Dual-anchor verification using "Sprite Assets/" so we never misalign across TMP 3.0 / TMP 3.2 / Unity 6!
        // Between the 7 floats and m_defaultSpriteAsset (12 bytes before "Sprite Assets/"):
        //   m_autoSizeTextContainer (4B aligned bool)
        //   [optional m_IsTextObjectScaleStatic (4B aligned bool)]
        //   m_fallbackFontAssets (int count + count * 12 bytes)
        //   m_matchMaterialPreset (4B aligned bool)
        //   [optional m_HideSubTextObjects (4B aligned bool)]
        ReadOnlySpan<byte> spriteMarker = "Sprite Assets/"u8;
        int spritePos = settingsData.IndexOf(spriteMarker);
        if (spritePos < afterFolder + 40) return null;
        if (BinaryPrimitives.ReadInt32LittleEndian(settingsData[(spritePos - 4)..]) != spriteMarker.Length) return null;
        int defaultSpriteAssetStart = spritePos - 16; // 4B len + 12B PPtr<TMP_SpriteAsset>

        int afterFloats = afterFolder + 28;
        int listOffset = -1;
        int count = -1;
        // Prefer TMP 3.0+/Unity 6 layout (2 bools = +8), fall back to older TMP layout (1 bool = +4)
        foreach (int preBoolBytes in new[] { 8, 4 })
        {
            int candOffset = afterFloats + preBoolBytes;
            if (candOffset + 4 > defaultSpriteAssetStart) continue;
            bool preBoolsValid = true;
            for (int b = afterFloats; b < candOffset; b += 4)
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(settingsData[b..]);
                if (v > 1) { preBoolsValid = false; break; }
            }
            if (!preBoolsValid) continue;

            int candCount = BinaryPrimitives.ReadInt32LittleEndian(settingsData[candOffset..]);
            if (candCount < 0 || candCount > 64) continue;
            int afterList = candOffset + 4 + candCount * 12;
            int postBytes = defaultSpriteAssetStart - afterList;
            if (postBytes is not (4 or 8)) continue;
            bool postBoolsValid = true;
            for (int b = afterList; b < defaultSpriteAssetStart; b += 4)
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(settingsData[b..]);
                if (v > 1) { postBoolsValid = false; break; }
            }
            if (!postBoolsValid) continue;

            listOffset = candOffset;
            count = candCount;
            break;
        }
        if (listOffset < 0) return null;

        // If fallbackPathId is already in the list, return unmodified copy
        for (int i = 0; i < count; i++)
        {
            int entryOff = listOffset + 4 + i * 12;
            int fid = BinaryPrimitives.ReadInt32LittleEndian(settingsData[entryOff..]);
            long pid = BinaryPrimitives.ReadInt64LittleEndian(settingsData[(entryOff + 4)..]);
            if (fid == 0 && pid == fallbackPathId)
                return settingsData.ToArray();
        }

        // Prepend (fileId=0, pathId=fallbackPathId) at index 0
        int firstEntry = listOffset + 4;
        byte[] updated = new byte[settingsData.Length + 12];
        settingsData[..firstEntry].CopyTo(updated);
        BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(listOffset), count + 1);
        BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(firstEntry), 0);
        BinaryPrimitives.WriteInt64LittleEndian(updated.AsSpan(firstEntry + 4), fallbackPathId);
        settingsData[firstEntry..].CopyTo(updated.AsSpan(firstEntry + 12));
        return updated;
    }

    private static bool TryReadName(ReadOnlySpan<byte> data, int offset, out string name)
    {
        name = "";
        if (offset + 4 > data.Length) return false;
        int len = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        if (len <= 0 || len > 120 || offset + 4 + len > data.Length) return false;
        name = Encoding.UTF8.GetString(data.Slice(offset + 4, len));
        return true;
    }
}

public sealed class UnityI2Format : IFormatHandler
{
    public static UnityI2Format Instance { get; } = new();
    public string Name => "unity-i2";

    public bool Supports(string path)
    {
        string file = Path.GetFileName(path);
        return file.Equals("data.unity3d", StringComparison.OrdinalIgnoreCase)
            || file.Equals("resources.assets", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<TextSlot> Extract(byte[] bytes)
    {
        byte[]? resAssets = ResolveResourcesBytes(bytes);
        return resAssets == null ? [] : ExtractFromResources(resAssets);
    }

    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations) =>
        ApplyWithFont(bytes, translations, fontChoice: null, out _);

    internal static byte[] ApplyWithFont(byte[] bytes, IReadOnlyDictionary<string, string> translations,
        ArabicFontChoice? fontChoice, out bool arabicFontPatched, UnityArabicDisplayOptions? displayOptions = null)
    {
        arabicFontPatched = false;
        if (translations.Count == 0) return bytes;
        if (UnityFsBundle.IsUnityFs(bytes))
        {
            var bundle = UnityFsBundle.Read(bytes);
            var updatedNodes = new List<UnityFsNode>(bundle.Nodes.Count);
            bool anyChanged = false;
            foreach (var node in bundle.Nodes)
            {
                if (node.Name.Equals("resources.assets", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] rewritten = ApplyToResources(node.Bytes, translations, fontChoice, out arabicFontPatched, displayOptions);
                    if (!ReferenceEquals(rewritten, node.Bytes)) anyChanged = true;
                    updatedNodes.Add(node with { Bytes = rewritten });
                }
                else updatedNodes.Add(node);
            }
            return anyChanged ? bundle.Write(updatedNodes, out _) : bytes;
        }
        return ApplyToResources(bytes, translations, fontChoice, out arabicFontPatched, displayOptions);
    }

    internal static bool HasI2Languages(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length < 256) return false;
            byte[]? resAssets = ReadResourcesBytesFromFile(filePath);
            if (resAssets == null) return false;
            var sf = UnitySerializedFile.TryRead(resAssets);
            return sf != null && FindI2Objects(sf).Count > 0;
        }
        catch
        {
            return false;
        }
    }

    internal static IReadOnlyList<TextSlot> ExtractFromFile(string filePath)
    {
        byte[]? resAssets = ReadResourcesBytesFromFile(filePath);
        return resAssets == null ? [] : ExtractFromResources(resAssets);
    }

    internal static void ApplyToFile(string sourceFilePath, string destinationFilePath,
        IReadOnlyDictionary<string, string> translations, ArabicFontChoice? fontChoice,
        CancellationToken cancellation, out bool arabicFontPatched, UnityArabicDisplayOptions? displayOptions = null)
    {
        arabicFontPatched = false;
        string file = Path.GetFileName(sourceFilePath);
        if (file.Equals("data.unity3d", StringComparison.OrdinalIgnoreCase))
        {
            byte[] resAssets = UnityFsStream.TryReadNode(sourceFilePath, "resources.assets")
                ?? throw new InvalidDataException("لم يُعثر على resources.assets داخل data.unity3d.");
            byte[] patchedRes = ApplyToResources(resAssets, translations, fontChoice, out arabicFontPatched, displayOptions);
            UnityFsStream.PatchNode(sourceFilePath, destinationFilePath, "resources.assets", patchedRes, cancellation);
            return;
        }

        byte[] orig = File.ReadAllBytes(sourceFilePath);
        byte[] updated = ApplyToResources(orig, translations, fontChoice, out arabicFontPatched, displayOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
        File.WriteAllBytes(destinationFilePath, updated);
    }

    private static byte[]? ReadResourcesBytesFromFile(string filePath)
    {
        string file = Path.GetFileName(filePath);
        if (file.Equals("data.unity3d", StringComparison.OrdinalIgnoreCase))
            return UnityFsStream.TryReadNode(filePath, "resources.assets");
        var info = new FileInfo(filePath);
        return info.Length <= 128L * 1024 * 1024 ? File.ReadAllBytes(filePath) : null;
    }

    private static byte[]? ResolveResourcesBytes(byte[] bytes)
    {
        if (UnityFsBundle.IsUnityFs(bytes))
        {
            var bundle = UnityFsBundle.Read(bytes);
            return bundle.Nodes.FirstOrDefault(n => n.Name.Equals("resources.assets", StringComparison.OrdinalIgnoreCase))?.Bytes;
        }
        return bytes;
    }

    internal static IReadOnlyList<TextSlot> ExtractFromResources(byte[] resAssets)
    {
        var sf = UnitySerializedFile.TryRead(resAssets);
        if (sf == null) return [];
        var slots = new List<TextSlot>();

        foreach (var (obj, parsed) in FindI2Objects(sf))
        {
            int langIdx = parsed.PrimaryLanguageIndex;
            for (int i = 0; i < parsed.Terms.Count; i++)
            {
                var term = parsed.Terms[i];
                if (term.TermType != 0) continue; // Text terms only (0 = Text, skip Sprites/Fonts/Audio)
                if (langIdx >= term.Languages.Count) continue;
                string source = term.Languages[langIdx];
                if (string.IsNullOrWhiteSpace(source)) continue;
                slots.Add(new TextSlot($"i2:{obj.PathId}:{i}:{term.Key}", source, term.Key));
            }
        }
        return slots;
    }

    internal static byte[] ApplyToResources(byte[] resAssets, IReadOnlyDictionary<string, string> translations,
        ArabicFontChoice? fontChoice, out bool arabicFontPatched, UnityArabicDisplayOptions? displayOptions = null)
    {
        arabicFontPatched = false;
        if (translations.Count == 0) return resAssets;
        var sf = UnitySerializedFile.TryRead(resAssets)
            ?? throw new InvalidDataException("تعذّرت قراءة resources.assets.");

        var opts = displayOptions ?? UnityArabicDisplayOptions.Default;
        var replacements = new Dictionary<long, byte[]>();
        bool hasArabic = translations.Values.Any(StarTruckerDisplay.HasArabic);
        if (hasArabic)
            arabicFontPatched = UnityTmpArabicPatcher.TryPatch(sf, fontChoice, replacements, opts.FontScale, opts.ContrastBoost);

        bool anyI2Changed = false;
        foreach (var (obj, parsed) in FindI2Objects(sf))
        {
            int langIdx = parsed.PrimaryLanguageIndex;
            bool objChanged = false;
            var updatedTerms = new List<I2Term>(parsed.Terms.Count);
            for (int i = 0; i < parsed.Terms.Count; i++)
            {
                var term = parsed.Terms[i];
                string locator = $"i2:{obj.PathId}:{i}:{term.Key}";
                if (term.TermType == 0 && langIdx < term.Languages.Count
                    && translations.TryGetValue(locator, out string? translated)
                    && translated != term.Languages[langIdx])
                {
                    string finalValue = arabicFontPatched && opts.ShapeArabic && StarTruckerDisplay.HasArabic(translated)
                        ? StarTruckerDisplay.Shape(translated, opts.MaxLineChars)
                        : translated;
                    var langs = term.Languages.ToArray();
                    langs[langIdx] = finalValue;
                    updatedTerms.Add(term with { Languages = langs });
                    objChanged = true;
                }
                else
                {
                    updatedTerms.Add(term);
                }
            }

            if (objChanged)
            {
                replacements[obj.PathId] = RewriteI2(parsed, updatedTerms);
                anyI2Changed = true;
            }
        }

        if (!anyI2Changed)
        {
            arabicFontPatched = false;
            return resAssets;
        }
        return sf.Rewrite(replacements);
    }

    private sealed record I2Term(string Key, int TermType, IReadOnlyList<string> Languages, byte[] TrailingRaw);
    private sealed record ParsedI2(byte[] PrefixBeforeTerms, IReadOnlyList<I2Term> Terms, byte[] TailAfterTerms, int PrimaryLanguageIndex);

    private static List<(UnitySerializedFile.ObjectEntry Obj, ParsedI2 Data)> FindI2Objects(UnitySerializedFile sf)
    {
        var result = new List<(UnitySerializedFile.ObjectEntry, ParsedI2)>();
        foreach (var obj in sf.Objects)
        {
            if (sf.Types[obj.TypeIndex].ClassId != 114 || obj.ByteSize < 128) continue;
            var data = sf.Slice(obj);
            if (!TryReadAlignedString(data, 28, out string name, out int afterName)) continue;
            if (!name.Contains("I2Languages", StringComparison.OrdinalIgnoreCase)) continue;
            var parsed = TryParseI2(data, afterName);
            if (parsed != null) result.Add((obj, parsed));
        }
        return result;
    }

    private static ParsedI2? TryParseI2(ReadOnlySpan<byte> data, int afterName)
    {
        // Standard I2.Loc.LanguageSourceAsset layout after m_Name:
        // UserAgreesToHaveItOnTheScene(4) + UserAgreesToHaveItInsideTheResourcesFolder(4) + Assets(PPtr[] array: int count + count*12)
        // OR compact 12-byte header (when Assets.Count==0) followed by mTerms(int count + TermData[])
        // Let's parse Assets properly: int flags/agrees (4 or 8 bytes), Assets count, then mTerms!
        // Try both standard I2 v2.8.x header offsets (afterName + 8, +12, +16) so it works across I2 versions.
        int[] candidateTermListOffsets = [afterName + 12, afterName + 8, afterName + 16];
        foreach (int termsStart in candidateTermListOffsets)
        {
            if (TryParseTermsAt(data, termsStart, out var parsed))
                return parsed;
        }
        return null;
    }

    private static bool TryParseTermsAt(ReadOnlySpan<byte> data, int termsStart, out ParsedI2? parsed)
    {
        parsed = null;
        if (termsStart < 0 || termsStart + 4 > data.Length) return false;
        int pos = termsStart;
        int termCount = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]); pos += 4;
        if (termCount <= 0 || termCount > 250_000) return false;

        var terms = new List<I2Term>(Math.Min(termCount, 4096));
        int expectedLangCount = -1;

        for (int t = 0; t < termCount; t++)
        {
            if (!TryReadAlignedString(data, pos, out string key, out pos) || key.Length == 0 || key.Length > 512)
                return false;
            if (pos + 8 > data.Length) return false;
            int termType = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]); pos += 4;
            if (termType < 0 || termType > 32) return false;
            int langCount = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]); pos += 4;
            if (langCount <= 0 || langCount > 128) return false;
            if (expectedLangCount < 0) expectedLangCount = langCount;

            var langs = new string[langCount];
            for (int l = 0; l < langCount; l++)
            {
                if (!TryReadAlignedString(data, pos, out string val, out pos)) return false;
                langs[l] = val;
            }

            int trailingStart = pos;
            if (pos + 4 > data.Length) return false;
            int flagsCount = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]); pos += 4;
            if (flagsCount < 0 || flagsCount > 256) return false;
            int flagsAligned = (flagsCount + 3) & ~3;
            if (pos + flagsAligned + 4 > data.Length) return false;
            pos += flagsAligned;

            int touchCount = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]); pos += 4;
            if (touchCount < 0 || touchCount > 128) return false;
            for (int l = 0; l < touchCount; l++)
            {
                if (!TryReadAlignedString(data, pos, out _, out pos)) return false;
            }

            terms.Add(new I2Term(key, termType, langs, data.Slice(trailingStart, pos - trailingStart).ToArray()));
        }

        // Parse mLanguages in the tail to find English index (and double-check structural validity!)
        int tailStart = pos;
        int primaryIdx = 0;
        if (pos + 12 <= data.Length)
        {
            int p = pos + 8; // CaseInsensitiveTerms(4) + OnMissingTranslation(4)
            if (TryReadAlignedString(data, p, out _, out p) && p + 4 <= data.Length)
            {
                int mLangCount = BinaryPrimitives.ReadInt32LittleEndian(data[p..]); p += 4;
                if (mLangCount == expectedLangCount)
                {
                    for (int i = 0; i < mLangCount; i++)
                    {
                        if (!TryReadAlignedString(data, p, out string lname, out p)) break;
                        if (!TryReadAlignedString(data, p, out string lcode, out p)) break;
                        if (p + 4 > data.Length) break;
                        p += 4; // Flags
                        if (lcode.Equals("en", StringComparison.OrdinalIgnoreCase)
                            || lcode.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
                            || lname.Equals("English", StringComparison.OrdinalIgnoreCase))
                        {
                            primaryIdx = i;
                            break;
                        }
                    }
                }
            }
        }

        parsed = new ParsedI2(
            data[..termsStart].ToArray(),
            terms,
            data[tailStart..].ToArray(),
            primaryIdx);
        return true;
    }

    private static byte[] RewriteI2(ParsedI2 original, IReadOnlyList<I2Term> terms)
    {
        using var ms = new MemoryStream(original.PrefixBeforeTerms.Length + original.TailAfterTerms.Length + terms.Count * 128);
        ms.Write(original.PrefixBeforeTerms);
        WriteI32LE(ms, terms.Count);
        foreach (var term in terms)
        {
            WriteAlignedString(ms, term.Key);
            WriteI32LE(ms, term.TermType);
            WriteI32LE(ms, term.Languages.Count);
            foreach (string lang in term.Languages)
                WriteAlignedString(ms, lang);
            ms.Write(term.TrailingRaw);
        }
        ms.Write(original.TailAfterTerms);
        return ms.ToArray();
    }

    private static bool TryReadAlignedString(ReadOnlySpan<byte> data, int pos, out string value, out int nextPos)
    {
        value = "";
        nextPos = pos;
        if (pos + 4 > data.Length) return false;
        int len = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
        pos += 4;
        if (len < 0 || pos + len > data.Length) return false;
        value = Encoding.UTF8.GetString(data.Slice(pos, len));
        int aligned = (len + 3) & ~3;
        if (pos + aligned > data.Length) return false;
        nextPos = pos + aligned;
        return true;
    }

    private static void WriteAlignedString(Stream ms, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        WriteI32LE(ms, utf8.Length);
        ms.Write(utf8);
        int pad = (4 - (utf8.Length % 4)) % 4;
        if (pad > 0) ms.Write(new byte[pad]);
    }

    private static void WriteI32LE(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); s.Write(b); }
}
