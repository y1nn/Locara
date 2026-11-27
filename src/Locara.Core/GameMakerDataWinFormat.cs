using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Locara.Core;

/// <summary>
/// Pure-C# GameMaker IFF FORM (<c>data.win</c>, <c>game.unx</c>, <c>game.ios</c>) reader and zero-corruption
/// <c>STRG</c> string table rebuilder.
/// </summary>
/// <remarks>
/// Streams chunk headers via <see cref="Stream.Seek"/> so large <c>TXTR</c>/<c>AUDO</c> blobs are never loaded into
/// memory during probing, extraction, or rebuilding. Unmodified <c>STRG</c> entries stay at their exact original file
/// offsets; shorter/equal edits patch in-place; expanded edits append with 128-byte chunk alignment padding and shift
/// downstream <c>TXTR</c>/<c>AUDO</c> pointers while failing fast on unknown post-<c>STRG</c> chunks.
/// </remarks>
public sealed class GameMakerDataWinFormat : IFormatHandler
{
    public static GameMakerDataWinFormat Instance { get; } = new();
    public string Name => "gamemaker-datawin";

    private const int StreamCopyBufferSize = 65536;
    private const int PointerScanBufferSize = 65536; // Must be a multiple of 4.
    private const long AlignmentDelta = 128L;
    private const int MaxTxtrHeaderScanBytes = 64;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    // Chunks whose entire payload can safely be scanned for 4-byte-aligned uint32 pointers matching STRG string-data offsets (entryOffset + 4).
    // Includes GEN8 (FileName, Config, Name, DisplayName), SHDR (names + GLSL/HLSL vertex/fragment source), VARI, FUNC, EXTN, and metadata tables.
    private static readonly HashSet<string> WholeChunkPointerScanNames = new(StringComparer.Ordinal)
    {
        "GEN8", "OPTN", "LANG", "EXTN", "SHDR", "VARI", "FUNC", "TGIN", "TAGS", "FEAT"
    };

    // Standard GameMaker pointer-list chunks (uint32 count, uint32[count] itemOffsets) and the per-item relative offsets
    // that hold STRG string-data pointers (entryOffset + 4).
    private static readonly Dictionary<string, int[]> PointerListItemStringOffsets = new(StringComparer.Ordinal)
    {
        ["SPRT"] = [0],
        ["BGND"] = [0],
        ["PATH"] = [0],
        ["SCPT"] = [0],
        ["FONT"] = [0, 4],      // name, displayName
        ["TMLN"] = [0],
        ["OBJT"] = [0],
        ["ROOM"] = [0, 4],      // name, caption
        ["SOND"] = [0, 8, 12],  // name, type, file
        ["AGRP"] = [0],
        ["CODE"] = [0]          // header name pointer only (never scans bytecode)
    };

    private static readonly string[] ShaderMarkers =
    [
        "gl_Position", "gl_FragColor", "gl_FragData", "gl_Vertex", "gl_TexCoord",
        "uniform ", "varying ", "attribute ", "#version ", "void main",
        "precision highp", "precision mediump", "precision lowp",
        "sampler2D", "gm_BaseTexture", "gm_Matrices",
        "in_Position", "in_Colour", "in_TextureCoord", "v_vTexcoord", "v_vColour",
        "SV_POSITION", "SV_TARGET", "cbuffer "
    ];

    private static readonly string[] InternalPrefixes =
    [
        "gml_", "@@", "$$", "spr_", "obj_", "rm_", "scr_", "snd_", "fnt_", "shd_", "bg_", "tl_"
    ];

    private static readonly HashSet<string> TechnicalFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".win", ".unx", ".ios", ".yy", ".yyp", ".yyz", ".gml", ".qoi", ".2zoq", ".audiogroup",
        ".dll", ".exe", ".so", ".dylib", ".pdb", ".bin", ".dat", ".sav",
        ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".toml", ".csv", ".tsv", ".txt",
        ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds", ".webp", ".svg", ".ico",
        ".wav", ".ogg", ".mp3", ".flac", ".mp4", ".webm",
        ".ttf", ".otf", ".woff", ".woff2", ".fnt", ".shader", ".glsl", ".hlsl", ".fx"
    };

    private readonly record struct ChunkInfo(string Name, long HeaderOffset, long PayloadOffset, uint Size)
    {
        public long EndOffset => PayloadOffset + Size;
    }

    private readonly record struct IffIndex(uint FormSize, long FormEnd, IReadOnlyList<ChunkInfo> Chunks, int StrgIndex);

    private readonly record struct InPlacePatch(long EntryOffset, byte[] PatchBytes);

    private readonly record struct UInt32Patch(long FileOffset, uint Value);

    public bool Supports(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string fileName = Path.GetFileName(path.Replace('\\', '/'));
        return fileName.Equals("data.win", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("game.unx", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("game.ios", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fast streamed probe verifying a valid IFF <c>FORM</c> header, <c>STRG</c> chunk, and at least one translatable human string.
    /// Never loads <c>TXTR</c> or <c>AUDO</c> payloads.
    /// </summary>
    public static bool HasTranslatableStrings(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return HasTranslatableStrings(stream);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static IReadOnlyList<TextSlot> ExtractFromFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ExtractFromStream(stream);
    }

    public IReadOnlyList<TextSlot> Extract(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        return ExtractFromStream(stream);
    }

    /// <summary>
    /// Rebuilds a GameMaker <c>data.win</c>/<c>game.unx</c>/<c>game.ios</c> file by streaming unchanged regions and
    /// applying <c>STRG</c> translations with zero corruption to pre-<c>STRG</c> chunks and 128-byte-aligned
    /// <c>TXTR</c>/<c>AUDO</c> pointer shifting.
    /// </summary>
    public static void ApplyToFile(
        string sourcePath,
        string destinationPath,
        IReadOnlyDictionary<string, string> replacements,
        bool shapeArabic = false,
        CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(replacements);

        string fullDest = Path.GetFullPath(destinationPath);
        string? destDir = Path.GetDirectoryName(fullDest);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        string tempPath = fullDest + ".locara-tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                RebuildStream(source, dest, replacements, shapeArabic, cancellation);
            }

            File.Move(tempPath, fullDest, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup of temp output file.
            }
            throw;
        }
    }

    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(translations);

        if (translations.Count == 0)
            return bytes;

        using var source = new MemoryStream(bytes, writable: false);
        using var destination = new MemoryStream(bytes.Length + 4096);
        RebuildStream(source, destination, translations, shapeArabic: false, CancellationToken.None);
        return destination.ToArray();
    }

    private static bool HasTranslatableStrings(Stream stream)
    {
        if (!TryIndexIff(stream, out var index) || index.StrgIndex < 0)
            return false;

        ChunkInfo strg = index.Chunks[index.StrgIndex];
        if (!TryReadStrgOffsets(stream, strg, out uint[] entryOffsets))
            return false;

        if (entryOffsets.Length == 0)
            return false;

        HashSet<uint> excludedDataOffsets = CollectStructuralStringPointers(stream, index, entryOffsets);
        long minEntryOffset = strg.PayloadOffset + 4L + (long)entryOffsets.Length * 4L;

        for (int i = 0; i < entryOffsets.Length; i++)
        {
            uint entryOffset = entryOffsets[i];
            if (entryOffset < minEntryOffset || entryOffset + 4L > strg.EndOffset)
                continue;

            uint dataOffset = entryOffset + 4u;
            if (excludedDataOffsets.Contains(dataOffset))
                continue;

            if (TryReadStrgString(stream, entryOffset, strg.EndOffset, out string text)
                && IsTranslatableHumanString(text))
            {
                return true;
            }
        }

        return false;
    }

    private static List<TextSlot> ExtractFromStream(Stream stream)
    {
        if (!TryIndexIff(stream, out var index) || index.StrgIndex < 0)
            return [];

        ChunkInfo strg = index.Chunks[index.StrgIndex];
        if (!TryReadStrgOffsets(stream, strg, out uint[] entryOffsets) || entryOffsets.Length == 0)
            return [];

        HashSet<uint> excludedDataOffsets = CollectStructuralStringPointers(stream, index, entryOffsets);
        long minEntryOffset = strg.PayloadOffset + 4L + (long)entryOffsets.Length * 4L;
        var slots = new List<TextSlot>();

        for (int i = 0; i < entryOffsets.Length; i++)
        {
            uint entryOffset = entryOffsets[i];
            if (entryOffset < minEntryOffset || entryOffset + 4L > strg.EndOffset)
                continue;

            uint dataOffset = entryOffset + 4u;
            if (excludedDataOffsets.Contains(dataOffset))
                continue;

            if (!TryReadStrgString(stream, entryOffset, strg.EndOffset, out string text))
                continue;

            if (!IsTranslatableHumanString(text))
                continue;

            slots.Add(new TextSlot(
                string.Create(CultureInfo.InvariantCulture, $"strg:{i}"),
                text,
                "GameMaker STRG"));
        }

        return slots;
    }

    private static bool TryIndexIff(Stream stream, out IffIndex index)
    {
        index = default;
        if (stream.Length < 8)
            return false;

        stream.Position = 0;
        Span<byte> header = stackalloc byte[8];
        stream.ReadExactly(header);

        if (header[0] != (byte)'F' || header[1] != (byte)'O' || header[2] != (byte)'R' || header[3] != (byte)'M')
            return false;

        uint formSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        long formEnd = 8L + formSize;
        if (formEnd > stream.Length)
            return false;

        var chunks = new List<ChunkInfo>(32);
        int strgIndex = -1;
        long pos = 8L;

        while (pos + 8L <= formEnd)
        {
            stream.Position = pos;
            stream.ReadExactly(header);
            string name = Encoding.ASCII.GetString(header[..4]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long payloadOffset = pos + 8L;
            long chunkEnd = payloadOffset + size;

            if (chunkEnd > formEnd)
                return false;

            if (name == "STRG" && strgIndex < 0)
                strgIndex = chunks.Count;

            chunks.Add(new ChunkInfo(name, pos, payloadOffset, size));
            pos = chunkEnd;
        }

        index = new IffIndex(formSize, formEnd, chunks, strgIndex);
        return true;
    }

    private static bool TryReadStrgOffsets(Stream stream, ChunkInfo strg, out uint[] entryOffsets)
    {
        entryOffsets = [];
        if (strg.Size < 4)
            return false;

        stream.Position = strg.PayloadOffset;
        uint count = ReadUInt32LE(stream);
        if (4L + (long)count * 4L > strg.Size)
            return false;

        entryOffsets = new uint[count];
        if (count == 0)
            return true;

        byte[] raw = new byte[count * 4];
        stream.ReadExactly(raw);
        for (int i = 0; i < count; i++)
            entryOffsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 4, 4));

        return true;
    }

    private static HashSet<uint> CollectStructuralStringPointers(Stream stream, IffIndex index, uint[] entryOffsets)
    {
        ChunkInfo strg = index.Chunks[index.StrgIndex];
        long minEntryOffset = strg.PayloadOffset + 4L + (long)entryOffsets.Length * 4L;

        var validDataOffsets = new HashSet<uint>(entryOffsets.Length);
        for (int i = 0; i < entryOffsets.Length; i++)
        {
            uint entryOffset = entryOffsets[i];
            if (entryOffset >= minEntryOffset && entryOffset + 4L <= strg.EndOffset)
                validDataOffsets.Add(entryOffset + 4u);
        }

        var excluded = new HashSet<uint>();
        if (validDataOffsets.Count == 0)
            return excluded;

        byte[]? scanBuffer = null;

        foreach (ChunkInfo chunk in index.Chunks)
        {
            if (chunk.Name is "STRG" or "TXTR" or "AUDO")
                continue;

            if (WholeChunkPointerScanNames.Contains(chunk.Name))
            {
                scanBuffer ??= new byte[PointerScanBufferSize];
                ScanChunkForStringPointers(stream, chunk, validDataOffsets, excluded, scanBuffer);
            }
            else if (PointerListItemStringOffsets.TryGetValue(chunk.Name, out int[]? relOffsets))
            {
                ScanPointerListChunkForStringPointers(stream, chunk, relOffsets, validDataOffsets, excluded);
            }
        }

        return excluded;
    }

    private static void ScanChunkForStringPointers(
        Stream stream,
        ChunkInfo chunk,
        HashSet<uint> validDataOffsets,
        HashSet<uint> excluded,
        byte[] buffer)
    {
        long alignedBytes = chunk.Size & ~3L;
        if (alignedBytes <= 0)
            return;

        stream.Position = chunk.PayloadOffset;
        long remaining = alignedBytes;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(remaining, buffer.Length);
            toRead &= ~3;
            if (toRead <= 0)
                break;

            stream.ReadExactly(buffer.AsSpan(0, toRead));
            for (int off = 0; off < toRead; off += 4)
            {
                uint candidate = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(off, 4));
                if (validDataOffsets.Contains(candidate))
                    excluded.Add(candidate);
            }

            remaining -= toRead;
        }
    }

    private static void ScanPointerListChunkForStringPointers(
        Stream stream,
        ChunkInfo chunk,
        int[] relativeOffsets,
        HashSet<uint> validDataOffsets,
        HashSet<uint> excluded)
    {
        if (chunk.Size < 4)
            return;

        stream.Position = chunk.PayloadOffset;
        uint count = ReadUInt32LE(stream);
        long tableBytes = 4L + (long)count * 4L;
        if (count == 0 || tableBytes > chunk.Size)
            return;

        uint[] itemOffsets = new uint[count];
        byte[] rawTable = new byte[count * 4];
        stream.ReadExactly(rawTable);
        for (int i = 0; i < count; i++)
            itemOffsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(rawTable.AsSpan(i * 4, 4));

        long minItemOffset = chunk.PayloadOffset + tableBytes;
        int maxRelEnd = 0;
        for (int r = 0; r < relativeOffsets.Length; r++)
        {
            int end = relativeOffsets[r] + 4;
            if (end > maxRelEnd) maxRelEnd = end;
        }

        Span<byte> itemHead = stackalloc byte[maxRelEnd];

        for (int i = 0; i < count; i++)
        {
            long itemStart = itemOffsets[i];
            if (itemStart < minItemOffset || itemStart + 4L > chunk.EndOffset)
                continue;

            long itemEnd = (i + 1 < count && itemOffsets[i + 1] > itemStart && itemOffsets[i + 1] <= chunk.EndOffset)
                ? itemOffsets[i + 1]
                : chunk.EndOffset;

            int bytesToRead = (int)Math.Min(maxRelEnd, itemEnd - itemStart);
            if (bytesToRead < 4)
                continue;

            stream.Position = itemStart;
            stream.ReadExactly(itemHead[..bytesToRead]);

            for (int r = 0; r < relativeOffsets.Length; r++)
            {
                int rel = relativeOffsets[r];
                if (rel + 4 > bytesToRead)
                    continue;

                uint candidate = BinaryPrimitives.ReadUInt32LittleEndian(itemHead.Slice(rel, 4));
                if (validDataOffsets.Contains(candidate))
                    excluded.Add(candidate);
            }
        }
    }

    private static bool TryReadStrgString(Stream stream, uint entryOffset, long strgEnd, out string text)
    {
        text = "";
        if (entryOffset + 4L > strgEnd)
            return false;

        stream.Position = entryOffset;
        uint byteLen = ReadUInt32LE(stream);
        if (byteLen == 0)
            return false;

        long dataEnd = entryOffset + 4L + byteLen;
        if (dataEnd > strgEnd)
            return false;

        byte[] raw = new byte[byteLen];
        stream.ReadExactly(raw);

        try
        {
            text = StrictUtf8.GetString(raw);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsTranslatableHumanString(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t')
                return false;
        }

        string trimmed = text.Trim();

        for (int i = 0; i < InternalPrefixes.Length; i++)
        {
            if (trimmed.StartsWith(InternalPrefixes[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        for (int i = 0; i < ShaderMarkers.Length; i++)
        {
            if (trimmed.Contains(ShaderMarkers[i], StringComparison.Ordinal))
                return false;
        }

        if (!ContainsWhitespace(trimmed))
        {
            string ext = Path.GetExtension(trimmed);
            if (ext.Length > 1 && TechnicalFileExtensions.Contains(ext))
                return false;
        }

        return TextQualityClassifier.ClassifyString(text) == StringQuality.Human;
    }

    private static bool ContainsWhitespace(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
                return true;
        }

        return false;
    }

    private static void RebuildStream(
        Stream source,
        Stream destination,
        IReadOnlyDictionary<string, string> replacements,
        bool shapeArabic,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();

        if (!TryIndexIff(source, out var index) || index.StrgIndex < 0)
        {
            source.Position = 0;
            CopyExactBytes(source, destination, source.Length, cancellation);
            return;
        }

        ChunkInfo strg = index.Chunks[index.StrgIndex];
        if (!TryReadStrgOffsets(source, strg, out uint[] originalOffsets))
        {
            source.Position = 0;
            CopyExactBytes(source, destination, source.Length, cancellation);
            return;
        }

        // Parse & sort target STRG edits.
        var edits = new SortedDictionary<int, string>();
        foreach (var kvp in replacements)
        {
            if (TryParseStrgLocator(kvp.Key, out int strIndex) && strIndex < originalOffsets.Length)
                edits[strIndex] = kvp.Value;
        }

        if (edits.Count == 0)
        {
            source.Position = 0;
            CopyExactBytes(source, destination, source.Length, cancellation);
            return;
        }

        // Track shared entryOffsets so an in-place edit can never mutate an unmodified aliased entry.
        var offsetRefCounts = new Dictionary<uint, int>(originalOffsets.Length);
        for (int i = 0; i < originalOffsets.Length; i++)
        {
            uint off = originalOffsets[i];
            offsetRefCounts[off] = offsetRefCounts.TryGetValue(off, out int c) ? c + 1 : 1;
        }

        uint[] newEntryOffsets = (uint[])originalOffsets.Clone();
        var inPlacePatches = new List<InPlacePatch>();
        using var appendBuffer = new MemoryStream();
        long minEntryOffset = strg.PayloadOffset + 4L + (long)originalOffsets.Length * 4L;
        int initialAppendPad = (int)((4L - (strg.EndOffset & 3L)) & 3L);
        Span<byte> lenBytes = stackalloc byte[4];
        ReadOnlySpan<byte> zeroPad4 = [0, 0, 0, 0];

        foreach (var (strIndex, rawValue) in edits)
        {
            cancellation.ThrowIfCancellationRequested();

            uint oldEntryOffset = originalOffsets[strIndex];
            if (oldEntryOffset < minEntryOffset || oldEntryOffset + 4L > strg.EndOffset)
                continue;

            source.Position = oldEntryOffset;
            uint oldLen = ReadUInt32LE(source);
            if (oldEntryOffset + 4L + oldLen > strg.EndOffset)
                continue;

            string effective = shapeArabic && ArabicTextEngine.HasArabic(rawValue)
                ? ArabicTextEngine.Shape(rawValue)
                : rawValue;

            byte[] newUtf8 = Utf8NoBom.GetBytes(effective);
            bool isShared = offsetRefCounts.TryGetValue(oldEntryOffset, out int refCount) && refCount > 1;

            if (!isShared && (uint)newUtf8.Length <= oldLen)
            {
                // In-place patch: length prefix + new UTF-8 + null terminator (when room exists) + zero-fill remainder.
                bool hasNullSlot = oldEntryOffset + 4L + oldLen < strg.EndOffset;
                int patchLen = checked((int)(4L + oldLen + (hasNullSlot ? 1L : 0L)));
                byte[] patchBytes = new byte[patchLen];
                BinaryPrimitives.WriteUInt32LittleEndian(patchBytes.AsSpan(0, 4), (uint)newUtf8.Length);
                newUtf8.CopyTo(patchBytes, 4);
                // Remaining bytes (null terminator + excess old bytes) are already zeroed.
                inPlacePatches.Add(new InPlacePatch(oldEntryOffset, patchBytes));
            }
            else
            {
                // Expanded (or shared) string: preserve original entry untouched and append 4-byte-aligned record at end of STRG.
                if (appendBuffer.Length == 0 && initialAppendPad > 0)
                    appendBuffer.Write(zeroPad4[..initialAppendPad]);

                long newAbsOffset = strg.EndOffset + appendBuffer.Length;
                if (newAbsOffset > uint.MaxValue)
                    throw new InvalidDataException("تجاوز ملف data.win حد العنونة 32-بت (4 جيجابايت) أثناء توسيع مقطع STRG.");

                newEntryOffsets[strIndex] = (uint)newAbsOffset;

                BinaryPrimitives.WriteUInt32LittleEndian(lenBytes, (uint)newUtf8.Length);
                appendBuffer.Write(lenBytes);
                appendBuffer.Write(newUtf8);
                appendBuffer.WriteByte(0x00);

                int pad4 = (int)((4L - (appendBuffer.Length & 3L)) & 3L);
                if (pad4 > 0)
                    appendBuffer.Write(zeroPad4[..pad4]);
            }
        }

        long rawAddedBytes = appendBuffer.Length;
        long delta = rawAddedBytes > 0 ? (rawAddedBytes + (AlignmentDelta - 1L)) & ~(AlignmentDelta - 1L) : 0L;

        if (delta > 0)
        {
            if (index.FormSize + delta > uint.MaxValue || strg.Size + delta > uint.MaxValue)
                throw new InvalidDataException("تجاوز حجم حزمة data.win الحد الأقصى لصيغة IFF 32-بت.");

            int tailPad = (int)(delta - rawAddedBytes);
            if (tailPad > 0)
            {
                Span<byte> zeros = stackalloc byte[tailPad];
                zeros.Clear();
                appendBuffer.Write(zeros);
            }
        }

        // Pre-validate post-STRG chunks and prepare pointer shift plans before writing anything (fail-fast).
        var postChunkPlans = new Dictionary<int, List<UInt32Patch>>();
        if (delta > 0)
        {
            for (int c = index.StrgIndex + 1; c < index.Chunks.Count; c++)
            {
                cancellation.ThrowIfCancellationRequested();
                ChunkInfo chunk = index.Chunks[c];
                if (chunk.Name == "TXTR")
                {
                    postChunkPlans[c] = BuildTxtrShiftPatches(source, chunk, delta);
                }
                else if (chunk.Name == "AUDO")
                {
                    postChunkPlans[c] = BuildAudoShiftPatches(source, chunk, delta);
                }
                else
                {
                    throw new NotSupportedException(
                        $"تعذّر توسيع مقطع STRG لأن ملف data.win يحتوي على مقطع غير مدعوم بعد STRG ({chunk.Name}) — أُوقف البناء لحماية ملفات اللعبة من التلف.");
                }
            }
        }

        // 1. Write FORM header with updated total length.
        Span<byte> chunkHeader = stackalloc byte[8];
        chunkHeader[0] = (byte)'F';
        chunkHeader[1] = (byte)'O';
        chunkHeader[2] = (byte)'R';
        chunkHeader[3] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader[4..], (uint)(index.FormSize + delta));
        destination.Write(chunkHeader);

        // 2. Stream-copy all pre-STRG chunks byte-for-byte untouched.
        source.Position = 8L;
        CopyExactBytes(source, destination, strg.HeaderOffset - 8L, cancellation);

        // 3. Write STRG header with updated size.
        chunkHeader[0] = (byte)'S';
        chunkHeader[1] = (byte)'T';
        chunkHeader[2] = (byte)'R';
        chunkHeader[3] = (byte)'G';
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader[4..], (uint)(strg.Size + delta));
        destination.Write(chunkHeader);

        // 4. Write STRG count + offset table (updated only for appended entries).
        WriteUInt32LE(destination, (uint)newEntryOffsets.Length);
        byte[] tableBytes = new byte[newEntryOffsets.Length * 4];
        for (int i = 0; i < newEntryOffsets.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(tableBytes.AsSpan(i * 4, 4), newEntryOffsets[i]);
        destination.Write(tableBytes);

        // 5. Stream-copy original STRG string records applying any sorted in-place patches.
        long strgDataStart = strg.PayloadOffset + 4L + (long)newEntryOffsets.Length * 4L;
        inPlacePatches.Sort(static (a, b) => a.EntryOffset.CompareTo(b.EntryOffset));

        long cursor = strgDataStart;
        foreach (InPlacePatch patch in inPlacePatches)
        {
            cancellation.ThrowIfCancellationRequested();
            if (patch.EntryOffset < cursor)
                throw new InvalidDataException("تداخل غير متوقع في مواضع النصوص داخل مقطع STRG.");

            if (patch.EntryOffset > cursor)
            {
                source.Position = cursor;
                CopyExactBytes(source, destination, patch.EntryOffset - cursor, cancellation);
            }

            destination.Write(patch.PatchBytes);
            cursor = patch.EntryOffset + patch.PatchBytes.Length;
        }

        if (cursor < strg.EndOffset)
        {
            source.Position = cursor;
            CopyExactBytes(source, destination, strg.EndOffset - cursor, cancellation);
        }

        // 6. Write appended STRG records + 128-byte alignment padding.
        if (delta > 0)
        {
            appendBuffer.Position = 0;
            CopyExactBytes(appendBuffer, destination, delta, cancellation);
        }

        // 7. Write post-STRG chunks (TXTR / AUDO), shifting pointers when delta > 0.
        for (int c = index.StrgIndex + 1; c < index.Chunks.Count; c++)
        {
            cancellation.ThrowIfCancellationRequested();
            ChunkInfo chunk = index.Chunks[c];

            Encoding.ASCII.GetBytes(chunk.Name, chunkHeader[..4]);
            BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader[4..], chunk.Size);
            destination.Write(chunkHeader);

            if (delta == 0 || !postChunkPlans.TryGetValue(c, out List<UInt32Patch>? patches) || patches.Count == 0)
            {
                source.Position = chunk.PayloadOffset;
                CopyExactBytes(source, destination, chunk.Size, cancellation);
            }
            else
            {
                CopyRangeWithUInt32Patches(source, destination, chunk.PayloadOffset, chunk.EndOffset, patches, cancellation);
            }
        }

        // 8. Preserve any trailing bytes after FORM if present.
        if (source.Length > index.FormEnd)
        {
            source.Position = index.FormEnd;
            CopyExactBytes(source, destination, source.Length - index.FormEnd, cancellation);
        }
    }

    private static List<UInt32Patch> BuildTxtrShiftPatches(Stream stream, ChunkInfo txtr, long delta)
    {
        var patches = new List<UInt32Patch>();
        if (txtr.Size < 4)
            return patches;

        stream.Position = txtr.PayloadOffset;
        uint count = ReadUInt32LE(stream);
        long tableBytes = 4L + (long)count * 4L;
        if (count == 0 || tableBytes > txtr.Size)
            return patches;

        uint[] texEntryOffsets = new uint[count];
        byte[] rawTable = new byte[count * 4];
        stream.ReadExactly(rawTable);

        for (int k = 0; k < count; k++)
        {
            uint off = BinaryPrimitives.ReadUInt32LittleEndian(rawTable.AsSpan(k * 4, 4));
            texEntryOffsets[k] = off;
            if (off != 0)
            {
                if (off < txtr.PayloadOffset + tableBytes || off >= txtr.EndOffset)
                    throw new InvalidDataException("موضع إدخال TXTR خارج نطاق مقطع TXTR.");

                long shifted = off + delta;
                if (shifted > uint.MaxValue)
                    throw new InvalidDataException("تجاوز موضع إدخال TXTR حد 32-بت.");

                patches.Add(new UInt32Patch(txtr.PayloadOffset + 4L + (long)k * 4L, (uint)shifted));
            }
        }

        // Determine uniform header bounds for each texture entry and locate the textureDataOffset field dynamically.
        long[] headerEnds = new long[count];
        long uniformStride = count >= 2 ? texEntryOffsets[1] - texEntryOffsets[0] : 0L;

        for (int k = 0; k < count; k++)
        {
            long start = texEntryOffsets[k];
            if (k + 1 < count && texEntryOffsets[k + 1] > start && texEntryOffsets[k + 1] <= txtr.EndOffset)
            {
                headerEnds[k] = texEntryOffsets[k + 1];
            }
            else if (uniformStride > 0 && uniformStride <= MaxTxtrHeaderScanBytes && start + uniformStride <= txtr.EndOffset)
            {
                headerEnds[k] = start + uniformStride;
            }
            else
            {
                headerEnds[k] = Math.Min(start + MaxTxtrHeaderScanBytes, txtr.EndOffset);
            }
        }

        int minHeaderLen = int.MaxValue;
        for (int k = 0; k < count; k++)
        {
            int len = (int)Math.Min(MaxTxtrHeaderScanBytes, Math.Max(0L, headerEnds[k] - texEntryOffsets[k]));
            if (len < minHeaderLen) minHeaderLen = len;
        }

        if (minHeaderLen < 4)
            return patches;

        // Read all texture entry headers (small: ~8..64 bytes each).
        byte[][] headers = new byte[count][];
        for (int k = 0; k < count; k++)
        {
            int len = (int)Math.Min(MaxTxtrHeaderScanBytes, Math.Max(0L, headerEnds[k] - texEntryOffsets[k]));
            headers[k] = new byte[len];
            stream.Position = texEntryOffsets[k];
            stream.ReadExactly(headers[k]);
        }

        long minBlobStart = texEntryOffsets[count - 1] + 4L;
        int detectedFieldRelOffset = -1;
        bool sawAnyNonZeroValidPointer = false;

        // Scan candidate 4-byte-aligned relative offsets; prefer the last consistent field matching known texture magic
        // (textureDataOffset is the final field in all GameMaker TXTR entry header versions).
        for (int rel = 0; rel + 4 <= minHeaderLen; rel += 4)
        {
            bool allConsistent = true;
            bool hasMagicPointer = false;

            for (int k = 0; k < count; k++)
            {
                uint candidate = BinaryPrimitives.ReadUInt32LittleEndian(headers[k].AsSpan(rel, 4));
                if (candidate == 0)
                    continue; // External texture entry.

                long fieldEnd = texEntryOffsets[k] + rel + 4L;
                if (candidate < minBlobStart || candidate < fieldEnd || candidate + 4L > txtr.EndOffset)
                {
                    allConsistent = false;
                    break;
                }

                if (!HasKnownTextureMagic(stream, candidate))
                {
                    allConsistent = false;
                    break;
                }

                hasMagicPointer = true;
            }

            if (allConsistent && hasMagicPointer)
            {
                detectedFieldRelOffset = rel;
                sawAnyNonZeroValidPointer = true;
            }
        }

        if (sawAnyNonZeroValidPointer && detectedFieldRelOffset >= 0)
        {
            for (int k = 0; k < count; k++)
            {
                uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(headers[k].AsSpan(detectedFieldRelOffset, 4));
                if (dataOffset == 0)
                    continue;

                long shifted = dataOffset + delta;
                if (shifted > uint.MaxValue)
                    throw new InvalidDataException("تجاوز موضع بيانات الصورة في TXTR حد 32-بت.");

                patches.Add(new UInt32Patch(texEntryOffsets[k] + detectedFieldRelOffset, (uint)shifted));
            }
        }
        else
        {
            // Check whether TXTR actually contains non-empty embedded blob data after the entry headers.
            // If all entries are external (zero) and no blob follows, shifting texEntryOffsets alone is valid;
            // otherwise fail fast instead of leaving an unshifted texture blob pointer.
            bool anyNonZeroInHeaders = false;
            for (int k = 0; k < count && !anyNonZeroInHeaders; k++)
            {
                for (int rel = 0; rel + 4 <= headers[k].Length; rel += 4)
                {
                    uint val = BinaryPrimitives.ReadUInt32LittleEndian(headers[k].AsSpan(rel, 4));
                    if (val >= minBlobStart && val < txtr.EndOffset)
                    {
                        anyNonZeroInHeaders = true;
                        break;
                    }
                }
            }

            if (anyNonZeroInHeaders)
            {
                throw new NotSupportedException(
                    "تعذّر التعرّف بأمان على حقل موضع بيانات الصور داخل مقطع TXTR — أُوقف البناء لتجنّب تلف الرسوميات.");
            }
        }

        patches.Sort(static (a, b) => a.FileOffset.CompareTo(b.FileOffset));
        return patches;
    }

    private static bool HasKnownTextureMagic(Stream stream, uint offset)
    {
        stream.Position = offset;
        Span<byte> magic = stackalloc byte[4];
        stream.ReadExactly(magic);

        // PNG: 89 50 4E 47 ("\x89PNG")
        if (magic[0] == 0x89 && magic[1] == (byte)'P' && magic[2] == (byte)'N' && magic[3] == (byte)'G')
            return true;

        // QOI: "qoif"
        if (magic[0] == (byte)'q' && magic[1] == (byte)'o' && magic[2] == (byte)'i' && magic[3] == (byte)'f')
            return true;

        // BZip2-compressed QOI: "2zoq" or "qoz2"
        if ((magic[0] == (byte)'2' && magic[1] == (byte)'z' && magic[2] == (byte)'o' && magic[3] == (byte)'q')
            || (magic[0] == (byte)'q' && magic[1] == (byte)'o' && magic[2] == (byte)'z' && magic[3] == (byte)'2'))
            return true;

        // DDS: "DDS "
        if (magic[0] == (byte)'D' && magic[1] == (byte)'D' && magic[2] == (byte)'S' && magic[3] == (byte)' ')
            return true;

        return false;
    }

    private static List<UInt32Patch> BuildAudoShiftPatches(Stream stream, ChunkInfo audo, long delta)
    {
        var patches = new List<UInt32Patch>();
        if (audo.Size < 4)
            return patches;

        stream.Position = audo.PayloadOffset;
        uint count = ReadUInt32LE(stream);
        long tableBytes = 4L + (long)count * 4L;
        if (count == 0 || tableBytes > audo.Size)
            return patches;

        byte[] rawTable = new byte[count * 4];
        stream.ReadExactly(rawTable);

        for (int k = 0; k < count; k++)
        {
            uint off = BinaryPrimitives.ReadUInt32LittleEndian(rawTable.AsSpan(k * 4, 4));
            if (off == 0)
                continue;

            if (off < audo.PayloadOffset + tableBytes || off >= audo.EndOffset)
                throw new InvalidDataException("موضع إدخال AUDO خارج نطاق مقطع AUDO.");

            long shifted = off + delta;
            if (shifted > uint.MaxValue)
                throw new InvalidDataException("تجاوز موضع إدخال AUDO حد 32-بت.");

            patches.Add(new UInt32Patch(audo.PayloadOffset + 4L + (long)k * 4L, (uint)shifted));
        }

        return patches;
    }

    private static void CopyRangeWithUInt32Patches(
        Stream source,
        Stream destination,
        long startOffset,
        long endOffset,
        List<UInt32Patch> sortedPatches,
        CancellationToken cancellation)
    {
        long cursor = startOffset;
        Span<byte> word = stackalloc byte[4];

        for (int i = 0; i < sortedPatches.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            UInt32Patch patch = sortedPatches[i];
            if (patch.FileOffset < cursor || patch.FileOffset + 4L > endOffset)
                throw new InvalidDataException("موضع تعديل المؤشر خارج نطاق المقطع.");

            if (patch.FileOffset > cursor)
            {
                source.Position = cursor;
                CopyExactBytes(source, destination, patch.FileOffset - cursor, cancellation);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(word, patch.Value);
            destination.Write(word);
            cursor = patch.FileOffset + 4L;
        }

        if (cursor < endOffset)
        {
            source.Position = cursor;
            CopyExactBytes(source, destination, endOffset - cursor, cancellation);
        }
    }

    private static void CopyExactBytes(Stream source, Stream destination, long byteCount, CancellationToken cancellation)
    {
        if (byteCount <= 0)
            return;

        byte[] buffer = new byte[(int)Math.Min(StreamCopyBufferSize, byteCount)];
        long remaining = byteCount;

        while (remaining > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(buffer.Length, remaining);
            source.ReadExactly(buffer.AsSpan(0, toRead));
            destination.Write(buffer, 0, toRead);
            remaining -= toRead;
        }
    }

    private static bool TryParseStrgLocator(string locator, out int index)
    {
        index = -1;
        if (!locator.StartsWith("strg:", StringComparison.Ordinal))
            return false;

        return int.TryParse(locator.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out index) && index >= 0;
    }

    private static uint ReadUInt32LE(Stream stream)
    {
        Span<byte> buf = stackalloc byte[4];
        stream.ReadExactly(buf);
        return BinaryPrimitives.ReadUInt32LittleEndian(buf);
    }

    private static void WriteUInt32LE(Stream stream, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        stream.Write(buf);
    }
}
