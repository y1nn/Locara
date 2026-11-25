using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Locara.Core;

internal sealed record UnrealPakEntryInfo(
    string Path,
    long Offset,
    long Size,
    long UncompressedSize,
    uint CompressionMethodIndex,
    string CompressionMethod,
    bool IsEncrypted);

internal sealed record UnrealPakArchiveInfo(
    int Version,
    string MountPoint,
    ulong PathHashSeed,
    bool IsIndexEncrypted,
    IReadOnlyList<string> CompressionMethods,
    IReadOnlyList<UnrealPakEntryInfo> Entries);

internal sealed record UnrealPakPatchFile(string RelativePath, byte[] Bytes);

/// <summary>
/// Pure-C# reader for Unreal Engine 4 &amp; 5 .pak archives (v4–v12) and writer for drop-in
/// patch archives (*_Locara_P.pak in v8 for UE4, v11 for UE4.26–5.4, and v12 for UE5.5+).
/// </summary>
internal static class UnrealPakCodec
{
    public const uint PakMagic = 0x5A6F12E1u;
    public const string PatchPakFileName = "Locara_Arabic_P.pak";
    public const string PatchPakSuffix = "_Locara_P.pak";

    public static bool IsLocaraPatchPakName(string fileName) =>
        fileName.Equals(PatchPakFileName, StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(PatchPakSuffix, StringComparison.OrdinalIgnoreCase);

    public static bool IsUnrealPakFile(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            return TryReadFooter(fs, out _);
        }
        catch
        {
            return false;
        }
    }

    public static UnrealPakArchiveInfo ReadIndex(string pakPath, CancellationToken cancellation = default)
    {
        using var fs = File.OpenRead(pakPath);
        if (!TryReadFooter(fs, out var footer))
            throw new InvalidDataException("الملف ليس حزمة Unreal Engine (.pak) صالحة.");

        if (footer.IsIndexEncrypted)
            throw new NotSupportedException("حزمة Unreal مشفّرة بمفتاح AES — يتطلب فكها مفتاح التشفير الخاص باللعبة.");

        if (footer.IndexOffset < 0 || footer.IndexSize <= 0 || footer.IndexOffset + footer.IndexSize > fs.Length || footer.IndexSize > 256L * 1024 * 1024)
            throw new InvalidDataException("فهرس حزمة Unreal (.pak) تالف أو خارج حدود الملف.");

        fs.Position = footer.IndexOffset;
        byte[] indexBytes = new byte[(int)footer.IndexSize];
        fs.ReadExactly(indexBytes);

        int pos = 0;
        if (!UnrealLocresFormat.TryReadFString(indexBytes, ref pos, out string mountPoint))
            throw new InvalidDataException("تعذّرت قراءة نقطة التثبيت (MountPoint) في حزمة Unreal.");

        if (pos + 4 > indexBytes.Length)
            throw new InvalidDataException("فهرس حزمة Unreal غير مكتمل.");

        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(indexBytes.AsSpan(pos));
        pos += 4;
        if (entryCount < 0 || entryCount > 2_000_000)
            throw new InvalidDataException("عدد الملفات في حزمة Unreal غير صالح.");

        ulong pathHashSeed = 0;
        var entries = footer.Version >= 10
            ? ParseSplitIndex(fs, indexBytes, pos, footer, out pathHashSeed, cancellation)
            : ParseLegacyIndex(indexBytes, pos, entryCount, footer, cancellation);

        return new UnrealPakArchiveInfo(footer.Version, mountPoint, pathHashSeed, footer.IsIndexEncrypted, footer.CompressionMethods, entries);
    }

    public static byte[] ReadEntryBytes(string pakPath, UnrealPakEntryInfo entry, int pakVersion,
        IReadOnlyList<string> compressionMethods, CancellationToken cancellation = default)
    {
        if (entry.IsEncrypted)
            throw new NotSupportedException($"الملف «{entry.Path}» داخل حزمة Unreal مشفّر بمفتاح AES.");
        if (entry.UncompressedSize < 0 || entry.UncompressedSize > 128L * 1024 * 1024)
            throw new InvalidDataException($"حجم الملف «{entry.Path}» يتجاوز الحد المسموح.");

        using var fs = File.OpenRead(pakPath);
        if (entry.Offset < 0 || entry.Offset >= fs.Length)
            throw new InvalidDataException($"موضع الملف «{entry.Path}» خارج حدود حزمة Unreal.");

        // Every UE pak entry begins at entry.Offset with a self-describing inline FPakEntry record.
        fs.Position = entry.Offset;
        var inline = ReadInlineEntryHeader(fs, pakVersion);
        if (inline.IsEncrypted)
            throw new NotSupportedException($"الملف «{entry.Path}» داخل حزمة Unreal مشفّر بمفتاح AES.");

        long payloadStart = fs.Position;
        long uncompressedSize = inline.UncompressedSize >= 0 ? inline.UncompressedSize : entry.UncompressedSize;
        long compressedSize = inline.Size >= 0 ? inline.Size : entry.Size;
        uint methodIdx = inline.CompressionMethodIndex;

        if (methodIdx == 0)
        {
            byte[] raw = new byte[checked((int)uncompressedSize)];
            fs.ReadExactly(raw);
            return raw;
        }

        string methodName = ResolveCompressionName(pakVersion, methodIdx, compressionMethods);
        if (inline.Blocks.Count == 0)
        {
            byte[] comp = new byte[checked((int)compressedSize)];
            fs.ReadExactly(comp);
            return DecompressBlock(comp, checked((int)uncompressedSize), methodName, entry.Path);
        }

        using var output = new MemoryStream(checked((int)uncompressedSize));
        long remaining = uncompressedSize;
        int blockSizeHint = inline.CompressionBlockSize > 0 ? checked((int)inline.CompressionBlockSize) : 65536;
        foreach (var (blockStart, blockEnd) in inline.Blocks)
        {
            cancellation.ThrowIfCancellationRequested();
            // UE v5+ stores block offsets relative to entry.Offset; v4 stores absolute file offsets.
            long absStart = pakVersion >= 5 ? entry.Offset + blockStart : blockStart;
            long absEnd = pakVersion >= 5 ? entry.Offset + blockEnd : blockEnd;
            if (absStart < payloadStart || absEnd < absStart || absEnd > fs.Length)
                throw new InvalidDataException($"حدود كتلة الضغط غير صالحة في «{entry.Path}».");

            int compLen = checked((int)(absEnd - absStart));
            int expectedOut = checked((int)Math.Min(remaining, blockSizeHint));
            fs.Position = absStart;
            byte[] comp = new byte[compLen];
            fs.ReadExactly(comp);

            byte[] decompressed = DecompressBlock(comp, expectedOut, methodName, entry.Path);
            output.Write(decompressed);
            remaining -= decompressed.Length;
        }
        return output.ToArray();
    }

    public static void WritePatchPak(string destinationPath, string mountPoint, IReadOnlyList<UnrealPakPatchFile> files,
        int targetVersion = 11, CancellationToken cancellation = default, ulong pathHashSeed = 0x9B56FB58ul)
    {
        if (files.Count == 0)
            throw new InvalidOperationException("لا توجد ملفات لكتابتها في حزمة تعديل Unreal.");

        int writeVersion = targetVersion >= 12 ? 12 : targetVersion >= 10 ? 11 : 8;
        string normalizedMount = string.IsNullOrWhiteSpace(mountPoint) ? "../../../" : mountPoint;

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using (var fs = File.Open(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var written = new List<(string Path, long Offset, long Size, byte[] Sha1)>(files.Count);
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                string rel = file.RelativePath.Replace('\\', '/').TrimStart('/');
                byte[] sha1 = SHA1.HashData(file.Bytes);
                long entryOffset = fs.Position;

                WriteInlineEntryHeader(fs, offsetField: 0, file.Bytes.LongLength, file.Bytes.LongLength, sha1);
                fs.Write(file.Bytes);
                written.Add((rel, entryOffset, file.Bytes.LongLength, sha1));
            }

            long indexOffset;
            byte[] indexBytes;
            if (writeVersion >= 10)
            {
                WriteSplitIndex(fs, normalizedMount, written, writeVersion, pathHashSeed, out indexOffset, out indexBytes);
            }
            else
            {
                indexOffset = fs.Position;
                indexBytes = BuildLegacyIndexV8(normalizedMount, written);
                fs.Write(indexBytes);
            }

            byte[] indexSha1 = SHA1.HashData(indexBytes);
            WriteFooter(fs, writeVersion, indexOffset, indexBytes.LongLength, indexSha1);
            fs.Flush(flushToDisk: true);
        }

        // Verify read-back of every written entry so corrupt patch paks can never reach the game folder.
        var verify = ReadIndex(destinationPath, cancellation);
        if (verify.Entries.Count != files.Count)
            throw new IOException("فشل التحقق من عدد الملفات داخل حزمة تعديل Unreal.");
        foreach (var file in files)
        {
            string rel = file.RelativePath.Replace('\\', '/').TrimStart('/');
            var entry = verify.Entries.FirstOrDefault(e => e.Path.Equals(rel, StringComparison.OrdinalIgnoreCase))
                ?? throw new IOException($"لم يُعثر على «{rel}» عند التحقق من حزمة Unreal.");
            byte[] readBack = ReadEntryBytes(destinationPath, entry, verify.Version, verify.CompressionMethods, cancellation);
            if (!readBack.AsSpan().SequenceEqual(file.Bytes))
                throw new IOException($"فشل التحقق من محتوى «{rel}» داخل حزمة Unreal.");
        }
    }

    /// <summary>
    /// Stream-patches an existing unencrypted UE4.26+/UE5 (v10–v12) .pak archive in place without
    /// decompressing or re-encoding any untouched entries. Used for UE5 IoStore games where a
    /// companion .utoc/.ucas sits beside the base .pak so the engine mounts the patched entries
    /// directly without hitting IoStore's "utoc not found" restriction on standalone _P.pak files.
    /// </summary>
    public static void PatchExistingPak(string sourcePakPath, string destinationPakPath,
        IReadOnlyList<UnrealPakPatchFile> files, CancellationToken cancellation = default)
    {
        if (files.Count == 0)
            throw new InvalidOperationException("لا توجد ملفات لتحديثها في حزمة Unreal.");

        using var src = File.OpenRead(sourcePakPath);
        if (!TryReadFooter(src, out var footer))
            throw new InvalidDataException("الملف ليس حزمة Unreal Engine (.pak) صالحة.");
        if (footer.IsIndexEncrypted)
            throw new NotSupportedException("حزمة Unreal مشفّرة بمفتاح AES.");
        if (footer.Version < 10)
            throw new NotSupportedException("التحديث المباشر لحزم Unreal يدعم الإصدارات v10–v12 فقط.");

        // Read original raw footer (221 bytes for v10-v12)
        int footerSize = 221;
        if (src.Length < footerSize) throw new InvalidDataException("تذييل حزمة Unreal مقطوع.");
        src.Position = src.Length - footerSize;
        byte[] origFooter = new byte[footerSize];
        src.ReadExactly(origFooter);
        if (BinaryPrimitives.ReadUInt32LittleEndian(origFooter.AsSpan(17)) != PakMagic)
            throw new InvalidDataException("تخطيط تذييل حزمة Unreal غير متوقع.");

        // Read original PrimaryIndex
        src.Position = footer.IndexOffset;
        byte[] origPrimary = new byte[checked((int)footer.IndexSize)];
        src.ReadExactly(origPrimary);

        int pos = 0;
        if (!UnrealLocresFormat.TryReadFString(origPrimary, ref pos, out _))
            throw new InvalidDataException("تعذّرت قراءة MountPoint في حزمة Unreal.");
        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(origPrimary.AsSpan(pos)); pos += 4;
        ulong pathHashSeed = BinaryPrimitives.ReadUInt64LittleEndian(origPrimary.AsSpan(pos)); pos += 8;

        int hasPathHash = BinaryPrimitives.ReadInt32LittleEndian(origPrimary.AsSpan(pos)); pos += 4;
        int phHeaderOff = pos;
        long origPhOff = 0, origPhSize = 0;
        if (hasPathHash != 0)
        {
            origPhOff = BinaryPrimitives.ReadInt64LittleEndian(origPrimary.AsSpan(pos)); pos += 8;
            origPhSize = BinaryPrimitives.ReadInt64LittleEndian(origPrimary.AsSpan(pos)); pos += 8;
            pos += 20; // SHA-1
        }

        int hasFullDir = BinaryPrimitives.ReadInt32LittleEndian(origPrimary.AsSpan(pos)); pos += 4;
        int fdHeaderOff = pos;
        long origFdOff = 0, origFdSize = 0;
        if (hasFullDir != 0)
        {
            origFdOff = BinaryPrimitives.ReadInt64LittleEndian(origPrimary.AsSpan(pos)); pos += 8;
            origFdSize = BinaryPrimitives.ReadInt64LittleEndian(origPrimary.AsSpan(pos)); pos += 8;
            pos += 20; // SHA-1
        }

        int encLenPos = pos;
        int origEncLen = BinaryPrimitives.ReadInt32LittleEndian(origPrimary.AsSpan(pos)); pos += 4;
        byte[] origEncBytes = origPrimary.AsSpan(pos, origEncLen).ToArray();
        pos += origEncLen;
        byte[] primaryTailAfterEnc = origPrimary.AsSpan(pos).ToArray(); // Fallback Files array

        byte[]? phBytes = null;
        if (hasPathHash != 0)
        {
            src.Position = origPhOff;
            phBytes = new byte[checked((int)origPhSize)];
            src.ReadExactly(phBytes);
        }

        byte[]? fdBytes = null;
        if (hasFullDir != 0)
        {
            src.Position = origFdOff;
            fdBytes = new byte[checked((int)origFdSize)];
            src.ReadExactly(fdBytes);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPakPath)!);
        using (var dst = File.Open(destinationPakPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            // 1. Stream-copy original payload bytes [0 .. IndexOffset) without decompressing anything!
            src.Position = 0;
            Containers.Copy(src, dst, footer.IndexOffset, cancellation, checkEnd: false);

            // 2. Append patched file payloads + build their new EncodedPakEntries
            using var newEncMs = new MemoryStream(origEncBytes.Length + files.Count * 16);
            newEncMs.Write(origEncBytes);

            var patchNewEncOffByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var patchNewEncOffByHash = new Dictionary<ulong, int>();
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                string rel = file.RelativePath.Replace('\\', '/').TrimStart('/');
                byte[] sha1 = SHA1.HashData(file.Bytes);
                long newPayloadEntryOff = dst.Position;

                WriteInlineEntryHeader(dst, offsetField: 0, file.Bytes.LongLength, file.Bytes.LongLength, sha1);
                dst.Write(file.Bytes);

                int encOff = checked((int)newEncMs.Position);
                bool off32 = newPayloadEntryOff is >= 0 and <= uint.MaxValue;
                bool uncomp32 = file.Bytes.LongLength is >= 0 and <= uint.MaxValue;
                uint flags = (off32 ? 1u << 31 : 0u) | (uncomp32 ? 1u << 30 : 0u) | (1u << 29);
                WriteU32(newEncMs, flags);
                if (off32) WriteU32(newEncMs, (uint)newPayloadEntryOff); else WriteI64(newEncMs, newPayloadEntryOff);
                if (uncomp32) WriteU32(newEncMs, (uint)file.Bytes.LongLength); else WriteI64(newEncMs, file.Bytes.LongLength);

                patchNewEncOffByPath[rel] = encOff;
                patchNewEncOffByHash[ComputePakPathHashV11(rel, pathHashSeed)] = encOff;
            }
            byte[] updatedEncBytes = newEncMs.ToArray();

            // 3. Patch EncOff in-place inside PathHashIndex (+ its trailing PrunedDirectoryIndex) and FullDirectoryIndex
            //    preserving EVERY untouched entry, string format (v10/v11/v12), and hash bit-for-bit!
            if (phBytes != null)
            {
                int php = 0;
                int phCount = BinaryPrimitives.ReadInt32LittleEndian(phBytes.AsSpan(php)); php += 4;
                for (int i = 0; i < phCount; i++)
                {
                    ulong h = BinaryPrimitives.ReadUInt64LittleEndian(phBytes.AsSpan(php)); php += 8;
                    if (patchNewEncOffByHash.TryGetValue(h, out int newOff))
                        BinaryPrimitives.WriteInt32LittleEndian(phBytes.AsSpan(php), newOff);
                    php += 4;
                }
                if (php < phBytes.Length)
                    PatchDirectoryIndexOffsetsInPlace(phBytes, php, patchNewEncOffByPath);
            }

            if (fdBytes != null)
                PatchDirectoryIndexOffsetsInPlace(fdBytes, 0, patchNewEncOffByPath);

            // 4. Assemble updated PrimaryIndex in canonical order: PrimaryIndex -> PathHashIndex -> FullDirectoryIndex -> Footer
            long newIndexOffset = dst.Position;
            byte[] BuildUpdatedPrimary(long newPhOff, long newFdOff)
            {
                using var ms = new MemoryStream(origPrimary.Length + files.Count * 16);
                ms.Write(origPrimary, 0, encLenPos);
                WriteI32(ms, updatedEncBytes.Length);
                ms.Write(updatedEncBytes);
                ms.Write(primaryTailAfterEnc);
                byte[] buf = ms.ToArray();

                if (hasPathHash != 0 && phBytes != null)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(phHeaderOff), newPhOff);
                    BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(phHeaderOff + 8), phBytes.LongLength);
                    SHA1.HashData(phBytes).CopyTo(buf.AsSpan(phHeaderOff + 16));
                }
                if (hasFullDir != 0 && fdBytes != null)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(fdHeaderOff), newFdOff);
                    BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(fdHeaderOff + 8), fdBytes.LongLength);
                    SHA1.HashData(fdBytes).CopyTo(buf.AsSpan(fdHeaderOff + 16));
                }
                return buf;
            }

            int primaryLen = BuildUpdatedPrimary(0, 0).Length;
            long cursor = newIndexOffset + primaryLen;
            long newPhOffset = cursor;
            if (phBytes != null) cursor += phBytes.LongLength;
            long newFdOffset = cursor;

            byte[] updatedPrimary = BuildUpdatedPrimary(newPhOffset, newFdOffset);
            dst.Write(updatedPrimary);
            if (phBytes != null) dst.Write(phBytes);
            if (fdBytes != null) dst.Write(fdBytes);

            byte[] updatedFooter = origFooter.ToArray();
            BinaryPrimitives.WriteInt64LittleEndian(updatedFooter.AsSpan(25), newIndexOffset);
            BinaryPrimitives.WriteInt64LittleEndian(updatedFooter.AsSpan(33), updatedPrimary.LongLength);
            SHA1.HashData(updatedPrimary).CopyTo(updatedFooter.AsSpan(41));
            dst.Write(updatedFooter);
            dst.Flush(flushToDisk: true);
        }

        // 5. Verify read-back of every patched file inside the rewritten pak!
        var verify = ReadIndex(destinationPakPath, cancellation);
        if (verify.Entries.Count != entryCount)
            throw new IOException("تغيّر عدد ملفات فهرس Unreal بعد التحديث المباشر.");
        foreach (var file in files)
        {
            string rel = file.RelativePath.Replace('\\', '/').TrimStart('/');
            var entry = verify.Entries.FirstOrDefault(e => e.Path.Equals(rel, StringComparison.OrdinalIgnoreCase))
                ?? throw new IOException($"لم يُعثر على «{rel}» بعد التحديث المباشر لحزمة Unreal.");
            byte[] readBack = ReadEntryBytes(destinationPakPath, entry, verify.Version, verify.CompressionMethods, cancellation);
            if (!readBack.AsSpan().SequenceEqual(file.Bytes))
                throw new IOException($"فشل التحقق من محتوى «{rel}» بعد التحديث المباشر لحزمة Unreal.");
        }
    }

    private static void PatchDirectoryIndexOffsetsInPlace(byte[] buf, int startPos, Dictionary<string, int> newOffsetsByPath)
    {
        int pos = startPos;
        if (pos + 4 > buf.Length) return;
        int dirCount = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos)); pos += 4;
        for (int d = 0; d < dirCount; d++)
        {
            if (!UnrealLocresFormat.TryReadFString(buf, ref pos, out string dirName)) break;
            if (pos + 4 > buf.Length) break;
            int fileCount = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos)); pos += 4;
            string cleanDir = dirName.Replace('\\', '/').Trim('/');
            for (int f = 0; f < fileCount; f++)
            {
                if (!UnrealLocresFormat.TryReadFString(buf, ref pos, out string fileName)) break;
                if (pos + 4 > buf.Length) break;
                string fullRel = cleanDir.Length == 0 ? fileName.TrimStart('/') : cleanDir + "/" + fileName.TrimStart('/');
                if (newOffsetsByPath.TryGetValue(fullRel, out int newOff))
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos), newOff);
                pos += 4;
            }
        }
    }

    private sealed record PakFooter(
        int Version,
        long IndexOffset,
        long IndexSize,
        bool IsIndexEncrypted,
        IReadOnlyList<string> CompressionMethods);

    private static bool TryReadFooter(FileStream fs, out PakFooter footer)
    {
        footer = null!;
        if (fs.Length < 44) return false;
        int tailLen = (int)Math.Min(256, fs.Length);
        fs.Position = fs.Length - tailLen;
        byte[] buf = new byte[tailLen];
        fs.ReadExactly(buf);

        int magicPos = -1;
        for (int i = tailLen - 44; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i)) == PakMagic)
            {
                magicPos = i;
                break;
            }
        }
        if (magicPos < 0 || magicPos + 44 > tailLen) return false;

        int version = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(magicPos + 4));
        if (version is < 1 or > 13) return false;
        long indexOffset = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(magicPos + 8));
        long indexSize = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(magicPos + 16));
        bool isEncrypted = version >= 4 && magicPos >= 1 && buf[magicPos - 1] != 0;

        var methods = new List<string>();
        if (version >= 8)
        {
            int compStart = magicPos + 44 + (version == 9 ? 1 : 0);
            int remaining = tailLen - compStart;
            int slots = Math.Min(5, remaining / 32);
            for (int k = 0; k < slots; k++)
            {
                string name = Encoding.ASCII.GetString(buf, compStart + k * 32, 32).TrimEnd('\0');
                methods.Add(name);
            }
        }

        footer = new PakFooter(version, indexOffset, indexSize, isEncrypted, methods);
        return true;
    }

    private static List<UnrealPakEntryInfo> ParseLegacyIndex(byte[] indexBytes, int pos, int entryCount,
        PakFooter footer, CancellationToken cancellation)
    {
        var list = new List<UnrealPakEntryInfo>(Math.Min(entryCount, 4096));
        for (int i = 0; i < entryCount; i++)
        {
            if ((i & 0xFF) == 0) cancellation.ThrowIfCancellationRequested();
            if (!UnrealLocresFormat.TryReadFString(indexBytes, ref pos, out string fileName))
                throw new InvalidDataException("فهرس حزمة Unreal (v4–v9) مقطوع.");

            var entry = ReadEntryStructFromBuffer(indexBytes, ref pos, footer.Version);
            if (entry.IsDeleted) continue;
            string compName = ResolveCompressionName(footer.Version, entry.CompressionMethodIndex, footer.CompressionMethods);
            list.Add(new UnrealPakEntryInfo(
                fileName.Replace('\\', '/').TrimStart('/'),
                entry.Offset,
                entry.Size,
                entry.UncompressedSize,
                entry.CompressionMethodIndex,
                compName,
                entry.IsEncrypted));
        }
        return list;
    }

    private static List<UnrealPakEntryInfo> ParseSplitIndex(FileStream fs, byte[] indexBytes, int pos,
        PakFooter footer, out ulong pathHashSeed, CancellationToken cancellation)
    {
        if (pos + 12 > indexBytes.Length)
            throw new InvalidDataException("ترويسة فهرس Unreal (v10–v12) مقطوعة.");

        pathHashSeed = BinaryPrimitives.ReadUInt64LittleEndian(indexBytes.AsSpan(pos)); pos += 8;
        int hasPathHash = BinaryPrimitives.ReadInt32LittleEndian(indexBytes.AsSpan(pos)); pos += 4;
        if (hasPathHash != 0)
        {
            if (pos + 36 > indexBytes.Length) throw new InvalidDataException("قسم PathHashIndex مقطوع.");
            pos += 8 + 8 + 20;
        }

        if (pos + 4 > indexBytes.Length) throw new InvalidDataException("حقل FullDirectoryIndex مفقود.");
        int hasFullDir = BinaryPrimitives.ReadInt32LittleEndian(indexBytes.AsSpan(pos)); pos += 4;
        long fullDirOffset = 0, fullDirSize = 0;
        if (hasFullDir != 0)
        {
            if (pos + 36 > indexBytes.Length) throw new InvalidDataException("قسم FullDirectoryIndex مقطوع.");
            fullDirOffset = BinaryPrimitives.ReadInt64LittleEndian(indexBytes.AsSpan(pos)); pos += 8;
            fullDirSize = BinaryPrimitives.ReadInt64LittleEndian(indexBytes.AsSpan(pos)); pos += 8;
            pos += 20; // FullDirectoryIndexHash
        }

        if (pos + 4 > indexBytes.Length) throw new InvalidDataException("قسم EncodedPakEntries مفقود.");
        int encodedLen = BinaryPrimitives.ReadInt32LittleEndian(indexBytes.AsSpan(pos)); pos += 4;
        if (encodedLen < 0 || pos + encodedLen > indexBytes.Length)
            throw new InvalidDataException("طول EncodedPakEntries غير صالح.");
        ReadOnlySpan<byte> encodedEntries = indexBytes.AsSpan(pos, encodedLen);
        pos += encodedLen;

        var fallbackFiles = new List<RawEntryStruct>();
        if (pos + 4 <= indexBytes.Length)
        {
            int fallbackCount = BinaryPrimitives.ReadInt32LittleEndian(indexBytes.AsSpan(pos)); pos += 4;
            if (fallbackCount is > 0 and < 1_000_000)
            {
                for (int i = 0; i < fallbackCount && pos < indexBytes.Length; i++)
                    fallbackFiles.Add(ReadEntryStructFromBuffer(indexBytes, ref pos, footer.Version));
            }
        }

        if (hasFullDir == 0 || fullDirOffset < 0 || fullDirSize <= 0 || fullDirOffset + fullDirSize > fs.Length || fullDirSize > 256L * 1024 * 1024)
            throw new InvalidDataException("فهرس المجلدات الكامل (FullDirectoryIndex) غير متوفر في حزمة Unreal.");

        fs.Position = fullDirOffset;
        byte[] dirBytes = new byte[(int)fullDirSize];
        fs.ReadExactly(dirBytes);

        int dp = 0;
        if (dp + 4 > dirBytes.Length) throw new InvalidDataException("FullDirectoryIndex فارغ.");
        int dirCount = BinaryPrimitives.ReadInt32LittleEndian(dirBytes.AsSpan(dp)); dp += 4;
        if (dirCount < 0 || dirCount > 1_000_000) throw new InvalidDataException("عدد المجلدات في FullDirectoryIndex غير صالح.");

        var result = new List<UnrealPakEntryInfo>();
        for (int d = 0; d < dirCount; d++)
        {
            if ((d & 0x3F) == 0) cancellation.ThrowIfCancellationRequested();
            if (!UnrealLocresFormat.TryReadFString(dirBytes, ref dp, out string dirName))
                throw new InvalidDataException("اسم مجلد مقطوع في FullDirectoryIndex.");
            if (dp + 4 > dirBytes.Length) throw new InvalidDataException("عدد ملفات مجلد مقطوع.");
            int fileCount = BinaryPrimitives.ReadInt32LittleEndian(dirBytes.AsSpan(dp)); dp += 4;
            if (fileCount < 0 || fileCount > 1_000_000) throw new InvalidDataException("عدد ملفات غير صالح.");

            string cleanDir = dirName.Replace('\\', '/').Trim('/');
            for (int f = 0; f < fileCount; f++)
            {
                if (!UnrealLocresFormat.TryReadFString(dirBytes, ref dp, out string fileName))
                    throw new InvalidDataException("اسم ملف مقطوع في FullDirectoryIndex.");
                if (dp + 4 > dirBytes.Length) throw new InvalidDataException("مؤشر ملف مقطوع في FullDirectoryIndex.");
                int encodedOff = BinaryPrimitives.ReadInt32LittleEndian(dirBytes.AsSpan(dp)); dp += 4;

                string fullRel = cleanDir.Length == 0 ? fileName.TrimStart('/') : cleanDir + "/" + fileName.TrimStart('/');
                if (encodedOff >= 0 && encodedOff + 4 <= encodedEntries.Length)
                {
                    var decoded = DecodeBitfieldEntry(encodedEntries, encodedOff);
                    string compName = ResolveCompressionName(footer.Version, decoded.CompressionMethodIndex, footer.CompressionMethods);
                    result.Add(new UnrealPakEntryInfo(
                        fullRel,
                        decoded.Offset,
                        decoded.Size,
                        decoded.UncompressedSize,
                        decoded.CompressionMethodIndex,
                        compName,
                        decoded.IsEncrypted));
                }
                else if (encodedOff < 0 && encodedOff != int.MinValue)
                {
                    int idx = -(encodedOff + 1);
                    if (idx >= 0 && idx < fallbackFiles.Count && !fallbackFiles[idx].IsDeleted)
                    {
                        var fb = fallbackFiles[idx];
                        string compName = ResolveCompressionName(footer.Version, fb.CompressionMethodIndex, footer.CompressionMethods);
                        result.Add(new UnrealPakEntryInfo(
                            fullRel, fb.Offset, fb.Size, fb.UncompressedSize, fb.CompressionMethodIndex, compName, fb.IsEncrypted));
                    }
                }
            }
        }
        return result;
    }

    private sealed record RawEntryStruct(
        long Offset,
        long Size,
        long UncompressedSize,
        uint CompressionMethodIndex,
        bool IsEncrypted,
        bool IsDeleted,
        uint CompressionBlockSize,
        IReadOnlyList<(long Start, long End)> Blocks);

    private static RawEntryStruct ReadEntryStructFromBuffer(ReadOnlySpan<byte> buf, ref int pos, int version)
    {
        if (pos + 28 > buf.Length) throw new InvalidDataException("سجل FPakEntry مقطوع.");
        long offset = BinaryPrimitives.ReadInt64LittleEndian(buf[pos..]); pos += 8;
        long size = BinaryPrimitives.ReadInt64LittleEndian(buf[pos..]); pos += 8;
        long uncompressed = BinaryPrimitives.ReadInt64LittleEndian(buf[pos..]); pos += 8;
        uint compMethod = BinaryPrimitives.ReadUInt32LittleEndian(buf[pos..]); pos += 4;
        if (version <= 1) pos += 8; // Timestamp
        if (pos + 20 > buf.Length) throw new InvalidDataException("تجزئة SHA-1 مقطوعة في FPakEntry.");
        pos += 20; // SHA-1

        var blocks = new List<(long Start, long End)>();
        if (version >= 3 && compMethod != 0)
        {
            if (pos + 4 > buf.Length) throw new InvalidDataException("عدد كتل الضغط مقطوع.");
            int blockCount = BinaryPrimitives.ReadInt32LittleEndian(buf[pos..]); pos += 4;
            if (blockCount < 0 || pos + blockCount * 16 > buf.Length) throw new InvalidDataException("جدول كتل الضغط مقطوع.");
            for (int b = 0; b < blockCount; b++)
            {
                long start = BinaryPrimitives.ReadInt64LittleEndian(buf[pos..]); pos += 8;
                long end = BinaryPrimitives.ReadInt64LittleEndian(buf[pos..]); pos += 8;
                blocks.Add((start, end));
            }
        }

        bool isEncrypted = false, isDeleted = false;
        uint compBlockSize = 0;
        if (version >= 3)
        {
            if (pos + 5 > buf.Length) throw new InvalidDataException("أعلام FPakEntry مقطوعة.");
            byte flags = buf[pos++];
            isEncrypted = (flags & 1) != 0;
            isDeleted = (flags & 2) != 0;
            compBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(buf[pos..]); pos += 4;
        }
        return new RawEntryStruct(offset, size, uncompressed, compMethod, isEncrypted, isDeleted, compBlockSize, blocks);
    }

    private static RawEntryStruct ReadInlineEntryHeader(FileStream fs, int version)
    {
        Span<byte> fixedHead = stackalloc byte[48];
        fs.ReadExactly(fixedHead);
        long offset = BinaryPrimitives.ReadInt64LittleEndian(fixedHead[0..]);
        long size = BinaryPrimitives.ReadInt64LittleEndian(fixedHead[8..]);
        long uncompressed = BinaryPrimitives.ReadInt64LittleEndian(fixedHead[16..]);
        uint compMethod = BinaryPrimitives.ReadUInt32LittleEndian(fixedHead[24..]);
        // bytes 28..48 are SHA-1 (for version >= 2)
        if (version <= 1) fs.Position += 8;

        var blocks = new List<(long Start, long End)>();
        if (version >= 3 && compMethod != 0)
        {
            Span<byte> cntBuf = stackalloc byte[4];
            fs.ReadExactly(cntBuf);
            int blockCount = BinaryPrimitives.ReadInt32LittleEndian(cntBuf);
            if (blockCount < 0 || blockCount > 1_000_000) throw new InvalidDataException("عدد كتل الضغط في ترويسة الملف غير صالح.");
            if (blockCount > 0)
            {
                byte[] blkBuf = new byte[blockCount * 16];
                fs.ReadExactly(blkBuf);
                for (int b = 0; b < blockCount; b++)
                {
                    long s = BinaryPrimitives.ReadInt64LittleEndian(blkBuf.AsSpan(b * 16));
                    long e = BinaryPrimitives.ReadInt64LittleEndian(blkBuf.AsSpan(b * 16 + 8));
                    blocks.Add((s, e));
                }
            }
        }

        bool isEncrypted = false, isDeleted = false;
        uint compBlockSize = 0;
        if (version >= 3)
        {
            Span<byte> tail = stackalloc byte[5];
            fs.ReadExactly(tail);
            isEncrypted = (tail[0] & 1) != 0;
            isDeleted = (tail[0] & 2) != 0;
            compBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(tail[1..]);
        }
        return new RawEntryStruct(offset, size, uncompressed, compMethod, isEncrypted, isDeleted, compBlockSize, blocks);
    }

    private static (long Offset, long Size, long UncompressedSize, uint CompressionMethodIndex, bool IsEncrypted)
        DecodeBitfieldEntry(ReadOnlySpan<byte> data, int pos)
    {
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]); pos += 4;
        uint compBlockSizeRaw = flags & 0x3Fu;
        bool isEncrypted = ((flags >> 22) & 1u) != 0;
        uint compMethod = (flags >> 23) & 0x3Fu;
        bool size32 = ((flags >> 29) & 1u) != 0;
        bool uncomp32 = ((flags >> 30) & 1u) != 0;
        bool offset32 = ((flags >> 31) & 1u) != 0;

        if (compBlockSizeRaw == 0x3Fu) pos += 4; // explicit CompressionBlockSize
        long offset = offset32 ? BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]) : BinaryPrimitives.ReadInt64LittleEndian(data[pos..]);
        pos += offset32 ? 4 : 8;
        long uncompressed = uncomp32 ? BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]) : BinaryPrimitives.ReadInt64LittleEndian(data[pos..]);
        pos += uncomp32 ? 4 : 8;
        long size = compMethod != 0
            ? (size32 ? BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]) : BinaryPrimitives.ReadInt64LittleEndian(data[pos..]))
            : uncompressed;
        return (offset, size, uncompressed, compMethod, isEncrypted);
    }

    private static string ResolveCompressionName(int version, uint methodIndex, IReadOnlyList<string> methods)
    {
        if (methodIndex == 0) return "None";
        if (version >= 8)
        {
            int idx = (int)methodIndex - 1;
            return idx >= 0 && idx < methods.Count && !string.IsNullOrWhiteSpace(methods[idx])
                ? methods[idx]
                : $"Method#{methodIndex}";
        }
        return methodIndex switch
        {
            1 => "Zlib",
            2 => "Gzip",
            _ => $"Method#{methodIndex}"
        };
    }

    private static byte[] DecompressBlock(byte[] compressed, int expectedUncompressed, string methodName, string entryPath)
    {
        if (methodName.Equals("None", StringComparison.OrdinalIgnoreCase))
            return compressed;

        if (methodName.Equals("Zlib", StringComparison.OrdinalIgnoreCase))
        {
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(Math.Max(expectedUncompressed, 0));
            zlib.CopyTo(output);
            return output.ToArray();
        }

        if (methodName.Equals("Gzip", StringComparison.OrdinalIgnoreCase))
        {
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(Math.Max(expectedUncompressed, 0));
            gzip.CopyTo(output);
            return output.ToArray();
        }

        if (methodName.Equals("LZ4", StringComparison.OrdinalIgnoreCase))
            return Lz4Block.Decompress(compressed, expectedUncompressed);

        throw new NotSupportedException($"خوارزمية الضغط «{methodName}» للملف «{entryPath}» غير مدعومة حالياً في Locara.");
    }

    private static void WriteInlineEntryHeader(Stream s, long offsetField, long size, long uncompressedSize, byte[] sha1)
    {
        WriteI64(s, offsetField);
        WriteI64(s, size);
        WriteI64(s, uncompressedSize);
        WriteU32(s, 0); // CompressionMethodIndex = None
        s.Write(sha1);  // 20 bytes
        s.WriteByte(0); // Flags (not encrypted, not deleted)
        WriteU32(s, 0); // CompressionBlockSize
    }

    private static byte[] BuildLegacyIndexV8(string mountPoint, List<(string Path, long Offset, long Size, byte[] Sha1)> entries)
    {
        using var ms = new MemoryStream();
        UnrealLocresFormat.WriteFString(ms, mountPoint);
        WriteI32(ms, entries.Count);
        foreach (var (path, offset, size, sha1) in entries)
        {
            UnrealLocresFormat.WriteFString(ms, path);
            WriteInlineEntryHeader(ms, offset, size, size, sha1);
        }
        return ms.ToArray();
    }

    private static void WriteSplitIndex(FileStream fs, string mountPoint,
        List<(string Path, long Offset, long Size, byte[] Sha1)> entries,
        int writeVersion, ulong pathHashSeed,
        out long indexOffset, out byte[] indexBytes)
    {
        // 1. Encode each entry into EncodedPakEntries and track its offset
        using var encodedMs = new MemoryStream();
        var encodedOffsets = new int[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            encodedOffsets[i] = checked((int)encodedMs.Position);
            var (_, offset, size, _) = entries[i];
            bool off32 = offset is >= 0 and <= uint.MaxValue;
            bool uncomp32 = size is >= 0 and <= uint.MaxValue;
            uint flags = (off32 ? 1u << 31 : 0u) | (uncomp32 ? 1u << 30 : 0u) | (1u << 29);
            WriteU32(encodedMs, flags);
            if (off32) WriteU32(encodedMs, (uint)offset); else WriteI64(encodedMs, offset);
            if (uncomp32) WriteU32(encodedMs, (uint)size); else WriteI64(encodedMs, size);
        }
        byte[] encodedPakEntries = encodedMs.ToArray();

        // 2. Build FullDirectoryIndex (TMap<FString Dir, TMap<FileKey, int32 EncodedEntryOffset>>)
        //    In PakFile_Version >= 12 (UE 5.5+), filenames inside directory maps are UTF-8 length-prefixed WITHOUT null terminator,
        //    while directory names remain null-terminated FStrings!
        var byDir = new Dictionary<string, List<(string FileName, int EncodedOffset)>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < entries.Count; i++)
        {
            string rel = entries[i].Path;
            int slash = rel.LastIndexOf('/');
            string dir = slash >= 0 ? rel[..(slash + 1)] : "/";
            string file = slash >= 0 ? rel[(slash + 1)..] : rel;
            if (!byDir.TryGetValue(dir, out var list))
            {
                list = [];
                byDir[dir] = list;
            }
            list.Add((file, encodedOffsets[i]));
        }

        byte[] fullDirBytes;
        using (var fd = new MemoryStream())
        {
            WriteI32(fd, byDir.Count);
            foreach (var (dir, list) in byDir)
            {
                UnrealLocresFormat.WriteFString(fd, dir);
                WriteI32(fd, list.Count);
                foreach (var (file, off) in list)
                {
                    if (writeVersion >= 12) WriteUtf8NoNull(fd, file);
                    else UnrealLocresFormat.WriteFString(fd, file);
                    WriteI32(fd, off);
                }
            }
            fullDirBytes = fd.ToArray();
        }

        // 3. Build PathHashIndex: UE4.26+/UE5 serializes TMap<uint64 PathHash, int32 EncodedEntryOffset>
        //    IMMEDIATELY followed by PrunedDirectoryIndex (TMap<FString Dir, TMap<FString File, int32>>) in the same blob!
        byte[] pathHashBytes;
        using (var ph = new MemoryStream())
        {
            WriteI32(ph, entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                WriteU64(ph, ComputePakPathHashV11(entries[i].Path, pathHashSeed));
                WriteI32(ph, encodedOffsets[i]);
            }
            ph.Write(fullDirBytes);
            pathHashBytes = ph.ToArray();
        }

        byte[] pathHashSha1 = SHA1.HashData(pathHashBytes);
        byte[] fullDirSha1 = SHA1.HashData(fullDirBytes);

        // 4. Write in Unreal's canonical v11 layout: PrimaryIndex -> PathHashIndex -> FullDirectoryIndex
        indexOffset = fs.Position;
        byte[] BuildPrimaryIndex(long phOffset, long fdOffset)
        {
            using var idx = new MemoryStream();
            UnrealLocresFormat.WriteFString(idx, mountPoint);
            WriteI32(idx, entries.Count);
            WriteU64(idx, pathHashSeed);

            WriteI32(idx, 1); // bHasPathHashIndex
            WriteI64(idx, phOffset);
            WriteI64(idx, pathHashBytes.LongLength);
            idx.Write(pathHashSha1);

            WriteI32(idx, 1); // bHasFullDirectoryIndex
            WriteI64(idx, fdOffset);
            WriteI64(idx, fullDirBytes.LongLength);
            idx.Write(fullDirSha1);

            WriteI32(idx, encodedPakEntries.Length);
            idx.Write(encodedPakEntries);
            WriteI32(idx, 0); // Files fallback count = 0
            return idx.ToArray();
        }

        int primarySize = BuildPrimaryIndex(0, 0).Length;
        long pathHashOffset = indexOffset + primarySize;
        long fullDirOffset = pathHashOffset + pathHashBytes.LongLength;
        indexBytes = BuildPrimaryIndex(pathHashOffset, fullDirOffset);

        fs.Write(indexBytes);
        fs.Write(pathHashBytes);
        fs.Write(fullDirBytes);
    }

    private static ulong ComputePakPathHashV11(string path, ulong seed)
    {
        // Unreal Engine v11 (PakFile_Version_Fnv64BugFix) FNV-1a 64-bit over lowercased UTF-16LE path.
        const ulong offsetBasis = 0xCBF29CE484222325ul;
        const ulong prime = 0x00000100000001B3ul;
        ulong hash = offsetBasis + seed;
        string lower = path.ToLowerInvariant();
        foreach (char c in lower)
        {
            hash ^= (byte)(c & 0xFF);
            hash *= prime;
            hash ^= (byte)(c >> 8);
            hash *= prime;
        }
        return hash;
    }

    private static void WriteFooter(FileStream fs, int version, long indexOffset, long indexSize, byte[] indexSha1)
    {
        fs.Write(new byte[16]); // EncryptionKeyGuid (zero = unencrypted)
        fs.WriteByte(0);        // bEncryptedIndex = false
        WriteU32(fs, PakMagic);
        WriteI32(fs, version);
        WriteI64(fs, indexOffset);
        WriteI64(fs, indexSize);
        fs.Write(indexSha1);    // 20 bytes
        fs.Write(new byte[5 * 32]); // 5 empty compression method slots
    }

    private static void WriteUtf8NoNull(Stream s, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        WriteI32(s, utf8.Length);
        s.Write(utf8);
    }

    private static void WriteI32(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); s.Write(b); }
    private static void WriteU32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); s.Write(b); }
    private static void WriteI64(Stream s, long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); s.Write(b); }
    private static void WriteU64(Stream s, ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); s.Write(b); }
}

/// <summary>
/// Format handler for Unreal Engine .pak archives containing .locres localization tables.
/// Extracts English/primary .locres entries cleanly and produces drop-in Locara_Arabic_P.pak mod files
/// without touching original game .pak archives.
/// </summary>
public sealed class UnrealPakFormat : IFormatHandler
{
    public static UnrealPakFormat Instance { get; } = new();
    public string Name => "unreal-pak";

    public bool Supports(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (!normalized.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)) return false;
        if (UnrealPakCodec.IsLocaraPatchPakName(Path.GetFileName(normalized)))
            return false;
        // Restrict to Unreal Content/Paks/ folders (or standalone .pak file) so Chromium chrome_*.pak files are ignored.
        return !normalized.Contains('/')
            || normalized.Contains("/Content/Paks/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Content/Paks/", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<TextSlot> Extract(byte[] bytes) => [];

    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations) => bytes;

    internal static bool HasTranslatableLocres(string pakPath, out string? diagnostic)
    {
        diagnostic = null;
        try
        {
            if (!UnrealPakCodec.IsUnrealPakFile(pakPath)) return false;
            var info = UnrealPakCodec.ReadIndex(pakPath);
            return SelectPrimaryLocresEntries(info.Entries).Count > 0
                || SelectStringTableEntries(pakPath, info).Count > 0;
        }
        catch (NotSupportedException ex)
        {
            diagnostic = ex.Message;
            return false;
        }
        catch
        {
            return false;
        }
    }

    internal static IReadOnlyList<TextSlot> ExtractFromFile(string pakPath, CancellationToken cancellation = default)
    {
        var info = UnrealPakCodec.ReadIndex(pakPath, cancellation);
        var selected = SelectPrimaryLocresEntries(info.Entries);
        var stringTables = SelectStringTableEntries(pakPath, info, cancellation);
        if (selected.Count == 0 && stringTables.Count == 0) return [];

        var slots = new List<TextSlot>();
        int totalSources = selected.Count + stringTables.Count;
        foreach (var entry in selected)
        {
            cancellation.ThrowIfCancellationRequested();
            byte[] locresBytes = UnrealPakCodec.ReadEntryBytes(pakPath, entry, info.Version, info.CompressionMethods, cancellation);
            var extracted = UnrealLocresFormat.Instance.Extract(locresBytes);
            string fileLabel = Path.GetFileNameWithoutExtension(entry.Path);
            foreach (var slot in extracted)
            {
                string locator = MakePakLocator(entry.Path, slot.Locator);
                string context = totalSources > 1 ? $"{fileLabel} · {slot.Context}" : slot.Context;
                slots.Add(new TextSlot(locator, slot.Source, context));
            }
        }
        foreach (var (uassetEntry, uexpEntry) in stringTables)
        {
            cancellation.ThrowIfCancellationRequested();
            byte[] uassetBytes = UnrealPakCodec.ReadEntryBytes(pakPath, uassetEntry, info.Version, info.CompressionMethods, cancellation);
            byte[]? uexpBytes = uexpEntry != null
                ? UnrealPakCodec.ReadEntryBytes(pakPath, uexpEntry, info.Version, info.CompressionMethods, cancellation)
                : null;
            var extracted = UnrealStringTableFormat.ExtractPair(uassetBytes, uexpBytes);
            string fileLabel = Path.GetFileNameWithoutExtension(uassetEntry.Path);
            foreach (var slot in extracted)
            {
                string locator = MakePakStringTableLocator(uassetEntry.Path, slot.Locator);
                string context = totalSources > 1 ? $"{fileLabel} · {slot.Context}" : slot.Context;
                slots.Add(new TextSlot(locator, slot.Source, context));
            }
        }
        return slots;
    }

    internal static (string MountPoint, int Version, ulong PathHashSeed, List<UnrealPakPatchFile> PatchedFiles, string? InjectedFontName, int PatchedFontCount) BuildPatchedLocresEntries(
        string pakPath, IReadOnlyDictionary<string, string> translations, ArabicFontChoice? fontChoice = null,
        CancellationToken cancellation = default)
    {
        var info = UnrealPakCodec.ReadIndex(pakPath, cancellation);
        var grouped = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var groupedStringTables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        bool anyArabic = false;
        foreach (var (locator, target) in translations)
        {
            if (TrySplitPakLocator(locator, out string entryPath, out string innerLocator))
            {
                if (StarTruckerDisplay.HasArabic(target)) anyArabic = true;
                if (!grouped.TryGetValue(entryPath, out var map))
                {
                    map = new Dictionary<string, string>(StringComparer.Ordinal);
                    grouped[entryPath] = map;
                }
                map[innerLocator] = target;
            }
            else if (TrySplitPakStringTableLocator(locator, out string stPath, out string stInner))
            {
                if (StarTruckerDisplay.HasArabic(target)) anyArabic = true;
                if (!groupedStringTables.TryGetValue(stPath, out var stMap))
                {
                    stMap = new Dictionary<string, string>(StringComparer.Ordinal);
                    groupedStringTables[stPath] = stMap;
                }
                stMap[stInner] = target;
            }
        }

        var patched = new List<UnrealPakPatchFile>();
        foreach (var (entryPath, innerTranslations) in grouped)
        {
            cancellation.ThrowIfCancellationRequested();
            var entry = info.Entries.FirstOrDefault(e => e.Path.Equals(entryPath, StringComparison.OrdinalIgnoreCase));
            if (entry == null) continue;
            byte[] originalLocres = UnrealPakCodec.ReadEntryBytes(pakPath, entry, info.Version, info.CompressionMethods, cancellation);
            byte[] updatedLocres = UnrealLocresFormat.Instance.Apply(originalLocres, innerTranslations);
            if (!ReferenceEquals(updatedLocres, originalLocres))
                patched.Add(new UnrealPakPatchFile(entry.Path, updatedLocres));
        }

        foreach (var (stPath, innerTranslations) in groupedStringTables)
        {
            cancellation.ThrowIfCancellationRequested();
            var uassetEntry = info.Entries.FirstOrDefault(e => e.Path.Equals(stPath, StringComparison.OrdinalIgnoreCase));
            if (uassetEntry == null) continue;
            string expectedUexpPath = Path.ChangeExtension(uassetEntry.Path, ".uexp").Replace('\\', '/');
            var uexpEntry = info.Entries.FirstOrDefault(e => e.Path.Equals(expectedUexpPath, StringComparison.OrdinalIgnoreCase));
            byte[] origUasset = UnrealPakCodec.ReadEntryBytes(pakPath, uassetEntry, info.Version, info.CompressionMethods, cancellation);
            byte[]? origUexp = uexpEntry != null
                ? UnrealPakCodec.ReadEntryBytes(pakPath, uexpEntry, info.Version, info.CompressionMethods, cancellation)
                : null;
            var (newUasset, newUexp, changed) = UnrealStringTableFormat.ApplyPair(origUasset, origUexp, innerTranslations);
            if (changed)
            {
                patched.Add(new UnrealPakPatchFile(uassetEntry.Path, newUasset));
                if (uexpEntry != null && newUexp != null)
                    patched.Add(new UnrealPakPatchFile(uexpEntry.Path, newUexp));
            }
        }

        string? injectedFontName = null;
        int patchedFonts = 0;
        if (anyArabic && patched.Count > 0)
        {
            var (fontPatches, fontDisplayName) = BuildArabicFontPatches(pakPath, info, fontChoice, cancellation);
            if (fontPatches.Count > 0)
            {
                patched.AddRange(fontPatches);
                injectedFontName = fontDisplayName;
                patchedFonts = fontPatches.Count;
            }
        }
        return (info.MountPoint, info.Version, info.PathHashSeed, patched, injectedFontName, patchedFonts);
    }

    private static readonly HashSet<string> EngineFontPathsToPatch = new(StringComparer.OrdinalIgnoreCase)
    {
        "Engine/Content/EngineFonts/Faces/RobotoRegular.ufont",
        "Engine/Content/EngineFonts/Faces/RobotoBold.ufont",
        "Engine/Content/EngineFonts/Faces/RobotoItalic.ufont",
        "Engine/Content/EngineFonts/Faces/RobotoBoldItalic.ufont",
        "Engine/Content/EngineFonts/Faces/RobotoLight.ufont",
        "Engine/Content/EngineFonts/Faces/DroidSansFallback.ufont",
        "Engine/Content/Slate/Fonts/Roboto-Regular.ttf",
        "Engine/Content/Slate/Fonts/Roboto-Bold.ttf",
        "Engine/Content/Slate/Fonts/DroidSansFallback.ttf"
    };

    private static (List<UnrealPakPatchFile> Patches, string FontDisplayName) BuildArabicFontPatches(
        string pakPath, UnrealPakArchiveInfo info, ArabicFontChoice? fontChoice, CancellationToken cancellation)
    {
        var (resolvedChoice, rawFontBytes) = ArabicFontCatalog.ResolveForUnreal(fontChoice);
        var candidates = info.Entries
            .Where(e => !e.IsEncrypted && IsPatchableUnrealFont(e.Path))
            .ToList();
        if (candidates.Count == 0) return ([], resolvedChoice.DisplayName);

        // Detect whether .ufont files in this pak use UE4 raw TTF/OTF format or UE5 length-prefixed cooked format
        // by inspecting a small .ufont entry (never reading 30+ MB CJK fonts just to check envelope format).
        bool? detectedUe5Envelope = null;
        foreach (var probe in candidates.Where(c => c.Path.EndsWith(".ufont", StringComparison.OrdinalIgnoreCase) && c.UncompressedSize is > 16 and <= 2_000_000))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                byte[] sample = UnrealPakCodec.ReadEntryBytes(pakPath, probe, info.Version, info.CompressionMethods, cancellation);
                if (IsUe5WrappedUfont(sample)) { detectedUe5Envelope = true; break; }
                if (ArabicFontCatalog.HasValidFontHeader(sample)) { detectedUe5Envelope = false; break; }
            }
            catch { }
        }
        bool ue5UfontWrapped = detectedUe5Envelope ?? (info.Version >= 10);
        byte[] wrappedUfontBytes = ue5UfontWrapped ? WrapUe5Ufont(rawFontBytes) : rawFontBytes;

        var patches = new List<UnrealPakPatchFile>(candidates.Count);
        foreach (var entry in candidates)
        {
            cancellation.ThrowIfCancellationRequested();
            bool isUfont = entry.Path.EndsWith(".ufont", StringComparison.OrdinalIgnoreCase);

            // For small game fonts, check if the font ALREADY supports Arabic (skip if already Arabic-capable).
            if (!entry.Path.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase) && entry.UncompressedSize <= 4_000_000)
            {
                try
                {
                    byte[] existing = UnrealPakCodec.ReadEntryBytes(pakPath, entry, info.Version, info.CompressionMethods, cancellation);
                    ReadOnlySpan<byte> innerFont = isUfont && IsUe5WrappedUfont(existing)
                        ? existing.AsSpan(4, existing.Length - 8)
                        : existing.AsSpan();
                    if (ArabicFontCatalog.SupportsLatinAndArabic(innerFont))
                        continue;

                    byte[] payload = isUfont
                        ? (IsUe5WrappedUfont(existing) ? WrapUe5Ufont(rawFontBytes, existing.AsSpan(existing.Length - 4)) : rawFontBytes)
                        : rawFontBytes;
                    patches.Add(new UnrealPakPatchFile(entry.Path, payload));
                    continue;
                }
                catch { }
            }

            patches.Add(new UnrealPakPatchFile(entry.Path, isUfont ? wrappedUfontBytes : rawFontBytes));
        }
        return (patches, resolvedChoice.DisplayName);
    }

    private static bool IsPatchableUnrealFont(string path)
    {
        if (EngineFontPathsToPatch.Contains(path)) return true;
        if (path.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase)) return false;

        string ext = Path.GetExtension(path);
        if (!ext.Equals(".ufont", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".otf", StringComparison.OrdinalIgnoreCase))
            return false;

        string name = Path.GetFileNameWithoutExtension(path);
        // Skip dedicated CJK / Thai / Arabic fallback fonts so we don't bloat the mod pak unnecessarily.
        if (name.Contains("NotoSansJP", StringComparison.OrdinalIgnoreCase)
            || name.Contains("NotoSansKR", StringComparison.OrdinalIgnoreCase)
            || name.Contains("NotoSansSC", StringComparison.OrdinalIgnoreCase)
            || name.Contains("NotoSansTC", StringComparison.OrdinalIgnoreCase)
            || name.Contains("NotoSansCJK", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Arabic", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Thai", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    internal static bool IsUe5WrappedUfont(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20) return false;
        int declaredLen = BinaryPrimitives.ReadInt32LittleEndian(bytes[0..]);
        return declaredLen == bytes.Length - 8 && ArabicFontCatalog.HasValidFontHeader(bytes[4..]);
    }

    internal static byte[] WrapUe5Ufont(byte[] rawFontBytes, ReadOnlySpan<byte> tail4 = default)
    {
        byte[] wrapped = new byte[rawFontBytes.Length + 8];
        BinaryPrimitives.WriteInt32LittleEndian(wrapped.AsSpan(0), rawFontBytes.Length);
        Buffer.BlockCopy(rawFontBytes, 0, wrapped, 4, rawFontBytes.Length);
        if (tail4.Length == 4) tail4.CopyTo(wrapped.AsSpan(4 + rawFontBytes.Length));
        return wrapped;
    }

    internal static bool HasCompanionUtoc(string pakFilePath) =>
        File.Exists(Path.ChangeExtension(pakFilePath, ".utoc"));

    internal static string PatchPakRelativePathFor(string sourcePakRelativePath)
    {
        // Place <BasePakStem>_Locara_P.pak directly inside Content/Paks/ next to the base pak so it:
        // 1. Matches Unreal's "%sPaks/%s-" top-tier PakOrder bucket (Order 4, same as base pak)
        // 2. Ends with "_P.pak" for the official Patch priority boost over the base pak
        // 3. Works universally across UE4 & UE5 (even games that do not scan ~mods/)
        string normalized = sourcePakRelativePath.Replace('\\', '/');
        string stem = Path.GetFileNameWithoutExtension(normalized);
        if (stem.EndsWith("_P", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^2];
        string patchFile = (string.IsNullOrWhiteSpace(stem) ? "Locara_Arabic" : stem) + UnrealPakCodec.PatchPakSuffix;

        int paksIdx = normalized.LastIndexOf("/Content/Paks/", StringComparison.OrdinalIgnoreCase);
        if (paksIdx >= 0)
            return normalized[..(paksIdx + "/Content/Paks/".Length)] + patchFile;
        if (normalized.StartsWith("Content/Paks/", StringComparison.OrdinalIgnoreCase))
            return "Content/Paks/" + patchFile;
        string? dir = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
        return string.IsNullOrEmpty(dir) ? patchFile : dir.TrimEnd('/') + "/" + patchFile;
    }

    internal static List<UnrealPakEntryInfo> SelectPrimaryLocresEntries(IReadOnlyList<UnrealPakEntryInfo> allEntries)
    {
        var allLocres = allEntries
            .Where(e => e.Path.EndsWith(".locres", StringComparison.OrdinalIgnoreCase) && e.UncompressedSize > 0)
            .ToList();
        if (allLocres.Count == 0) return [];

        // Prefer game localization over Unreal Engine internal Engine/* boilerplate (Engine/Content and Engine/Plugins).
        var gameLocres = allLocres
            .Where(e => !e.Path.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var candidates = gameLocres.Count > 0 ? gameLocres : allLocres;

        // Prefer English locale folders (/en/, /en-US/, /en-GB/) so non-English locales don't bloat translation units.
        var english = candidates.Where(e => IsEnglishLocresPath(e.Path)).ToList();
        return english.Count > 0 ? english : candidates;
    }

    internal static bool IsEnglishLocresPath(string path)
    {
        string[] segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string seg in segments)
        {
            if (seg.Equals("en", StringComparison.OrdinalIgnoreCase)
                || seg.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
                || seg.StartsWith("en_", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static string MakePakLocator(string entryPath, string innerLocator) =>
        $"paklocres:{entryPath.Length}:{entryPath}|{innerLocator}";

    internal static string MakePakStringTableLocator(string entryPath, string innerLocator) =>
        $"pakst:{entryPath.Length}:{entryPath}|{innerLocator}";

    internal static bool TrySplitPakLocator(string locator, out string entryPath, out string innerLocator) =>
        TrySplitPrefixedLocator(locator, "paklocres:", out entryPath, out innerLocator);

    internal static bool TrySplitPakStringTableLocator(string locator, out string entryPath, out string innerLocator) =>
        TrySplitPrefixedLocator(locator, "pakst:", out entryPath, out innerLocator);

    private static bool TrySplitPrefixedLocator(string locator, string prefix, out string entryPath, out string innerLocator)
    {
        entryPath = "";
        innerLocator = "";
        if (!locator.StartsWith(prefix, StringComparison.Ordinal)) return false;
        int colon = locator.IndexOf(':', prefix.Length);
        if (colon < 0) return false;
        if (!int.TryParse(locator.AsSpan(prefix.Length, colon - prefix.Length), out int len) || len <= 0) return false;
        int pathStart = colon + 1;
        if (pathStart + len + 1 > locator.Length || locator[pathStart + len] != '|') return false;
        entryPath = locator.Substring(pathStart, len);
        innerLocator = locator[(pathStart + len + 1)..];
        return true;
    }

    internal static List<(UnrealPakEntryInfo Uasset, UnrealPakEntryInfo? Uexp)> SelectStringTableEntries(
        string pakPath,
        UnrealPakArchiveInfo info,
        CancellationToken cancellation = default)
    {
        var candidateUassets = info.Entries
            .Where(e => !e.IsEncrypted
                && e.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                && !e.Path.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase)
                && e.UncompressedSize is >= 64 and <= 4 * 1024 * 1024)
            .ToList();
        if (candidateUassets.Count == 0) return [];

        var byPath = info.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var matched = new List<(UnrealPakEntryInfo Uasset, UnrealPakEntryInfo? Uexp)>();
        foreach (var ua in candidateUassets)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                byte[] uaBytes = UnrealPakCodec.ReadEntryBytes(pakPath, ua, info.Version, info.CompressionMethods, cancellation);
                if (!UnrealStringTableFormat.IsStringTableHeader(uaBytes)) continue;
                string uexpPath = Path.ChangeExtension(ua.Path, ".uexp").Replace('\\', '/');
                byPath.TryGetValue(uexpPath, out var uexpEntry);
                byte[]? uexpBytes = uexpEntry != null
                    ? UnrealPakCodec.ReadEntryBytes(pakPath, uexpEntry, info.Version, info.CompressionMethods, cancellation)
                    : null;
                if (UnrealStringTableFormat.ExtractPair(uaBytes, uexpBytes).Count > 0)
                    matched.Add((ua, uexpEntry));
            }
            catch { }
        }
        return matched;
    }
}
