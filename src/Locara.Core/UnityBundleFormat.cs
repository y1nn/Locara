using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Locara.Core;

internal static class Lz4Block
{
    public static byte[] Decompress(ReadOnlySpan<byte> src, int uncompressedSize)
    {
        if (uncompressedSize < 0) throw new InvalidDataException("حجم بيانات LZ4 غير صالح.");
        if (uncompressedSize == 0) return [];
        byte[] dst = new byte[uncompressedSize];
        int s = 0, d = 0;
        while (s < src.Length)
        {
            byte token = src[s++];
            int litLen = token >> 4;
            if (litLen == 15)
            {
                byte b;
                do
                {
                    if (s >= src.Length) throw new InvalidDataException("بيانات LZ4 مقطوعة أثناء قراءة طول النص الحرفي.");
                    b = src[s++];
                    litLen += b;
                } while (b == 255);
            }
            if (s + litLen > src.Length || d + litLen > uncompressedSize)
                throw new InvalidDataException("بيانات LZ4 خارج الحدود.");
            src.Slice(s, litLen).CopyTo(dst.AsSpan(d, litLen));
            s += litLen;
            d += litLen;
            if (s >= src.Length) break;

            if (s + 2 > src.Length) throw new InvalidDataException("بيانات LZ4 مقطوعة عند إزاحة المطابقة.");
            int offset = src[s] | (src[s + 1] << 8);
            s += 2;
            if (offset <= 0 || offset > d) throw new InvalidDataException("إزاحة LZ4 غير صالحة.");

            int matchLen = (token & 0x0F) + 4;
            if ((token & 0x0F) == 15)
            {
                byte b;
                do
                {
                    if (s >= src.Length) throw new InvalidDataException("بيانات LZ4 مقطوعة أثناء قراءة طول المطابقة.");
                    b = src[s++];
                    matchLen += b;
                } while (b == 255);
            }
            if (d + matchLen > uncompressedSize)
                throw new InvalidDataException("تجاوز حجم فك ضغط LZ4 المتوقع.");
            int matchPos = d - offset;
            for (int i = 0; i < matchLen; i++)
                dst[d++] = dst[matchPos + i];
        }
        if (d != uncompressedSize)
            throw new InvalidDataException($"حجم فك ضغط LZ4 غير مطابق ({d} != {uncompressedSize}).");
        return dst;
    }

    public static byte[] Compress(ReadOnlySpan<byte> src)
    {
        if (src.Length == 0) return [];
        const int hashLog = 14;
        const int hashSize = 1 << hashLog;
        const int minMatch = 4;
        const int mfLimit = 12;
        const int lastLiterals = 5;

        int[] hashTable = new int[hashSize];
        Array.Fill(hashTable, -1);

        using var ms = new MemoryStream(src.Length);
        int anchor = 0;
        int ip = 0;
        int matchLimit = src.Length - lastLiterals;
        int mflimitPos = src.Length - mfLimit;

        if (src.Length >= mfLimit)
        {
            while (ip <= mflimitPos)
            {
                uint seq = BinaryPrimitives.ReadUInt32LittleEndian(src[ip..]);
                int h = (int)((seq * 2654435761u) >> (32 - hashLog));
                int candidate = hashTable[h];
                hashTable[h] = ip;

                if (candidate >= 0 && ip - candidate <= 65535
                    && BinaryPrimitives.ReadUInt32LittleEndian(src[candidate..]) == seq)
                {
                    int matchLen = minMatch;
                    while (ip + matchLen < matchLimit && src[candidate + matchLen] == src[ip + matchLen])
                        matchLen++;

                    int litLen = ip - anchor;
                    int tokenLit = Math.Min(litLen, 15);
                    int tokenMatch = Math.Min(matchLen - minMatch, 15);
                    ms.WriteByte((byte)((tokenLit << 4) | tokenMatch));

                    if (litLen >= 15) WriteExtraLength(ms, litLen - 15);
                    ms.Write(src.Slice(anchor, litLen));

                    int offset = ip - candidate;
                    ms.WriteByte((byte)(offset & 0xFF));
                    ms.WriteByte((byte)((offset >> 8) & 0xFF));

                    if (matchLen - minMatch >= 15) WriteExtraLength(ms, matchLen - minMatch - 15);

                    ip += matchLen;
                    anchor = ip;
                    if (ip <= mflimitPos)
                    {
                        int prev = ip - 2;
                        uint prevSeq = BinaryPrimitives.ReadUInt32LittleEndian(src[prev..]);
                        hashTable[(int)((prevSeq * 2654435761u) >> (32 - hashLog))] = prev;
                    }
                    continue;
                }
                ip++;
            }
        }

        int remLit = src.Length - anchor;
        ms.WriteByte((byte)(Math.Min(remLit, 15) << 4));
        if (remLit >= 15) WriteExtraLength(ms, remLit - 15);
        ms.Write(src[anchor..]);
        return ms.ToArray();
    }

    private static void WriteExtraLength(Stream output, int len)
    {
        while (len >= 255)
        {
            output.WriteByte(255);
            len -= 255;
        }
        output.WriteByte((byte)len);
    }
}

internal static class UnityCrc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++)
                c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : (c >> 1);
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
            crc = (crc >> 8) ^ Table[(crc ^ data[i]) & 0xFF];
        return ~crc;
    }
}

internal sealed record UnityFsNode(string Name, uint Flags, byte[] Bytes);

internal sealed class UnityFsBundle
{
    private const long MaxBundleUncompressedBytes = 128L * 1024 * 1024;
    public const int CompressionNone = 0;
    public const int CompressionLzma = 1;
    public const int CompressionLz4 = 2;
    public const int CompressionLz4Hc = 3;

    public uint Version { get; init; }
    public string UnityVersion { get; init; } = "5.x.x";
    public string UnityRevision { get; init; } = "";
    public uint Flags { get; init; }
    public byte[] DataHash { get; init; } = new byte[16];
    public IReadOnlyList<UnityFsNode> Nodes { get; init; } = [];
    public uint UncompressedStreamCrc32 { get; init; }

    public static bool IsUnityFs(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 32 && bytes[..8].SequenceEqual("UnityFS\0"u8);

    public static UnityFsBundle Read(ReadOnlySpan<byte> data)
    {
        if (!IsUnityFs(data))
            throw new InvalidDataException("الملف ليس حزمة UnityFS صالحة.");

        int pos = 0;
        _ = ReadCString(data, ref pos); // "UnityFS"
        uint version = ReadU32BE(data, ref pos);
        if (version is < 6 or > 8)
            throw new NotSupportedException($"إصدار حزمة UnityFS ({version}) غير مدعوم حالياً.");

        string unityVersion = ReadCString(data, ref pos);
        string unityRevision = ReadCString(data, ref pos);
        long declaredSize = ReadI64BE(data, ref pos);
        if (declaredSize != data.Length)
            throw new InvalidDataException("حجم حزمة UnityFS لا يطابق ترويستها.");

        int compInfoSize = ReadI32BE(data, ref pos);
        int uncompInfoSize = ReadI32BE(data, ref pos);
        uint flags = ReadU32BE(data, ref pos);

        if (version >= 7) pos = (pos + 15) & ~15;

        bool blocksInfoAtEnd = (flags & 0x80) != 0;
        int infoOffset = blocksInfoAtEnd ? data.Length - compInfoSize : pos;
        if (infoOffset < 0 || infoOffset + compInfoSize > data.Length || uncompInfoSize <= 0)
            throw new InvalidDataException("فهرس كتل حزمة UnityFS تالف.");

        int infoComp = (int)(flags & 0x3F);
        byte[] blocksInfo = DecompressChunk(data.Slice(infoOffset, compInfoSize), uncompInfoSize, infoComp);

        if (!blocksInfoAtEnd) pos += compInfoSize;
        if ((flags & 0x200) != 0) pos = (pos + 15) & ~15;

        int ip = 0;
        if (blocksInfo.Length < 24) throw new InvalidDataException("بيانات فهرس UnityFS ناقصة.");
        byte[] dataHash = blocksInfo[..16];
        ip = 16;

        int blockCount = ReadI32BE(blocksInfo, ref ip);
        if (blockCount < 0 || ip + blockCount * 10 > blocksInfo.Length)
            throw new InvalidDataException("عدد كتل حزمة UnityFS غير صالح.");

        var blocks = new (int Uncomp, int Comp, ushort Flags)[blockCount];
        long totalUncompressed = 0;
        for (int i = 0; i < blockCount; i++)
        {
            int bu = ReadI32BE(blocksInfo, ref ip);
            int bc = ReadI32BE(blocksInfo, ref ip);
            ushort bf = ReadU16BE(blocksInfo, ref ip);
            if (bu < 0 || bc < 0) throw new InvalidDataException("كتلة UnityFS غير صالحة.");
            blocks[i] = (bu, bc, bf);
            totalUncompressed += bu;
        }
        if (totalUncompressed > MaxBundleUncompressedBytes)
            throw new InvalidDataException("حجم حزمة UnityFS بعد فك الضغط يتجاوز الحد المسموح.");

        int nodeCount = ReadI32BE(blocksInfo, ref ip);
        if (nodeCount < 0 || nodeCount > 4096) throw new InvalidDataException("عدد ملفات UnityFS غير صالح.");
        var nodeMeta = new (long Offset, long Size, uint Flags, string Name)[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            long no = ReadI64BE(blocksInfo, ref ip);
            long ns = ReadI64BE(blocksInfo, ref ip);
            uint nf = ReadU32BE(blocksInfo, ref ip);
            string name = ReadCString(blocksInfo, ref ip);
            if (no < 0 || ns < 0 || no + ns > totalUncompressed)
                throw new InvalidDataException("حدود ملف داخلي في UnityFS غير صالحة.");
            nodeMeta[i] = (no, ns, nf, name);
        }

        byte[] stream = new byte[(int)totalUncompressed];
        int bp = pos;
        int wp = 0;
        foreach (var (bu, bc, bf) in blocks)
        {
            if (bp + bc > (blocksInfoAtEnd ? infoOffset : data.Length))
                throw new InvalidDataException("تجاوزت كتل UnityFS حجم الملف.");
            byte[] chunk = DecompressChunk(data.Slice(bp, bc), bu, bf & 0x3F);
            Buffer.BlockCopy(chunk, 0, stream, wp, bu);
            bp += bc;
            wp += bu;
        }

        uint streamCrc = UnityCrc32.Compute(stream);
        var nodes = new List<UnityFsNode>(nodeCount);
        foreach (var (no, ns, nf, name) in nodeMeta)
        {
            byte[] slice = new byte[(int)ns];
            Buffer.BlockCopy(stream, (int)no, slice, 0, (int)ns);
            nodes.Add(new UnityFsNode(name, nf, slice));
        }

        return new UnityFsBundle
        {
            Version = version,
            UnityVersion = unityVersion,
            UnityRevision = unityRevision,
            Flags = flags,
            DataHash = dataHash,
            Nodes = nodes,
            UncompressedStreamCrc32 = streamCrc
        };
    }

    public byte[] Write(IReadOnlyList<UnityFsNode> updatedNodes, out uint newUncompressedCrc32)
    {
        long totalLen = updatedNodes.Sum(n => (long)n.Bytes.Length);
        if (totalLen > MaxBundleUncompressedBytes)
            throw new InvalidDataException("حجم حزمة UnityFS الناتجة يتجاوز الحد المسموح.");

        byte[] stream = new byte[(int)totalLen];
        var nodeOffsets = new (long Offset, long Size, uint Flags, string Name)[updatedNodes.Count];
        int cursor = 0;
        for (int i = 0; i < updatedNodes.Count; i++)
        {
            var node = updatedNodes[i];
            Buffer.BlockCopy(node.Bytes, 0, stream, cursor, node.Bytes.Length);
            nodeOffsets[i] = (cursor, node.Bytes.Length, node.Flags, node.Name);
            cursor += node.Bytes.Length;
        }
        newUncompressedCrc32 = UnityCrc32.Compute(stream);

        bool useLz4 = (Flags & 0x3F) is CompressionLz4 or CompressionLz4Hc;
        const int chunkSize = 0x20000; // 128 KiB standard Unity chunk size
        var compressedChunks = new List<(int Uncomp, byte[] Data, ushort BlockFlags)>();
        for (int off = 0; off < stream.Length; off += chunkSize)
        {
            int len = Math.Min(chunkSize, stream.Length - off);
            ReadOnlySpan<byte> slice = stream.AsSpan(off, len);
            if (useLz4)
            {
                byte[] comp = Lz4Block.Compress(slice);
                if (comp.Length < len)
                {
                    compressedChunks.Add((len, comp, CompressionLz4));
                    continue;
                }
            }
            compressedChunks.Add((len, slice.ToArray(), CompressionNone));
        }
        if (compressedChunks.Count == 0)
            compressedChunks.Add((0, [], CompressionNone));

        byte[] blocksInfo;
        using (var bi = new MemoryStream())
        {
            bi.Write(DataHash.Length == 16 ? DataHash : new byte[16]);
            WriteI32BE(bi, compressedChunks.Count);
            foreach (var (u, d, bf) in compressedChunks)
            {
                WriteI32BE(bi, u);
                WriteI32BE(bi, d.Length);
                WriteU16BE(bi, bf);
            }
            WriteI32BE(bi, nodeOffsets.Length);
            foreach (var (no, ns, nf, name) in nodeOffsets)
            {
                WriteI64BE(bi, no);
                WriteI64BE(bi, ns);
                WriteU32BE(bi, nf);
                WriteCString(bi, name);
            }
            blocksInfo = bi.ToArray();
        }

        byte[] encodedBlocksInfo = blocksInfo;
        uint outFlags = Flags & ~0x3Fu;
        if (useLz4)
        {
            byte[] compInfo = Lz4Block.Compress(blocksInfo);
            if (compInfo.Length < blocksInfo.Length)
            {
                encodedBlocksInfo = compInfo;
                outFlags |= CompressionLz4;
            }
        }
        outFlags |= 0x40u; // BlocksAndDirectoryInfoCombined
        bool atEnd = (outFlags & 0x80u) != 0;

        using var output = new MemoryStream();
        WriteCString(output, "UnityFS");
        WriteU32BE(output, Version);
        WriteCString(output, UnityVersion);
        WriteCString(output, UnityRevision);
        long sizeFieldPos = output.Position;
        WriteI64BE(output, 0); // patched below
        WriteI32BE(output, encodedBlocksInfo.Length);
        WriteI32BE(output, blocksInfo.Length);
        WriteU32BE(output, outFlags);

        if (Version >= 7) WriteAlign16(output);
        if (!atEnd)
        {
            output.Write(encodedBlocksInfo);
            if ((outFlags & 0x200u) != 0) WriteAlign16(output);
        }
        foreach (var (_, d, _) in compressedChunks)
            output.Write(d);
        if (atEnd)
            output.Write(encodedBlocksInfo);

        byte[] result = output.ToArray();
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan((int)sizeFieldPos), result.Length);
        return result;
    }

    private static byte[] DecompressChunk(ReadOnlySpan<byte> chunk, int uncompressedSize, int compType) => compType switch
    {
        CompressionNone => chunk.Length == uncompressedSize
            ? chunk.ToArray()
            : throw new InvalidDataException("حجم الكتلة غير المضغوطة غير مطابق."),
        CompressionLz4 or CompressionLz4Hc => Lz4Block.Decompress(chunk, uncompressedSize),
        CompressionLzma => throw new NotSupportedException("ضغط LZMA داخل حزم Unity غير مدعوم حالياً (يدعم Locara حزم LZ4/LZ4HC أو غير المضغوطة)."),
        _ => throw new NotSupportedException($"نوع ضغط UnityFS ({compType}) غير مدعوم.")
    };

    private static string ReadCString(ReadOnlySpan<byte> data, ref int pos)
    {
        int start = pos;
        while (pos < data.Length && data[pos] != 0) pos++;
        if (pos >= data.Length) throw new InvalidDataException("نص مقطوع داخل ترويسة UnityFS.");
        string s = Encoding.UTF8.GetString(data.Slice(start, pos - start));
        pos++;
        return s;
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

internal sealed class UnitySerializedFile
{
    public sealed record TypeNode(byte Level, string TypeName, string Name, int ByteSize, int MetaFlag);
    public sealed record SerializedType(int ClassId, IReadOnlyList<TypeNode> Nodes);
    public sealed record ObjectEntry(long PathId, long ByteStart, int ByteSize, int TypeIndex, int RecordOffset);

    public int Format { get; init; }
    public long DataOffset { get; init; }
    public IReadOnlyList<SerializedType> Types { get; init; } = [];
    public IReadOnlyList<ObjectEntry> Objects { get; init; } = [];
    public byte[] RawBytes { get; init; } = [];

    private static readonly Dictionary<int, string> CommonStrings = new()
    {
        [0] = "AABB", [5] = "AnimationClip", [19] = "AnimationCurve", [34] = "AnimationState", [49] = "Array",
        [55] = "Base", [60] = "BitField", [69] = "bitset", [76] = "bool", [81] = "char", [86] = "ColorRGBA",
        [96] = "Component", [106] = "data", [111] = "deque", [117] = "double", [124] = "dynamic_array",
        [138] = "FastPropertyName", [155] = "first", [161] = "float", [167] = "Font", [172] = "GameObject",
        [183] = "Generic Mono", [196] = "GradientNEW", [208] = "GUID", [213] = "GUIStyle", [222] = "int",
        [226] = "list", [231] = "long long", [241] = "map", [245] = "Matrix4x4f", [256] = "MdFour",
        [263] = "MonoBehaviour", [277] = "MonoScript", [288] = "m_ByteSize", [299] = "m_Curve",
        [307] = "m_EditorClassIdentifier", [331] = "m_EditorHideFlags", [349] = "m_Enabled",
        [359] = "m_ExtensionPtr", [374] = "m_GameObject", [387] = "m_Index", [395] = "m_IsArray",
        [405] = "m_IsStatic", [416] = "m_MetaFlag", [427] = "m_Name", [434] = "m_ObjectHideFlags",
        [452] = "m_PrefabInternal", [469] = "m_PrefabParentObject", [490] = "m_Script",
        [499] = "m_StaticEditorFlags", [519] = "m_Type", [526] = "m_Version", [536] = "Object", [543] = "pair",
        [548] = "PPtr<Component>", [564] = "PPtr<GameObject>", [581] = "PPtr<Material>",
        [596] = "PPtr<MonoBehaviour>", [616] = "PPtr<MonoScript>", [633] = "PPtr<Object>",
        [646] = "PPtr<Prefab>", [659] = "PPtr<Sprite>", [672] = "PPtr<TextAsset>", [688] = "PPtr<Texture>",
        [702] = "PPtr<Texture2D>", [718] = "PPtr<Transform>", [734] = "Prefab", [741] = "Quaternionf",
        [753] = "Rectf", [759] = "RectInt", [767] = "RectOffset", [778] = "second", [785] = "set",
        [789] = "short", [795] = "size", [800] = "SInt16", [807] = "SInt32", [814] = "SInt64",
        [821] = "SInt8", [827] = "staticvector", [840] = "string", [847] = "TextAsset", [857] = "TextMesh",
        [866] = "Texture", [874] = "Texture2D", [884] = "Transform", [894] = "TypelessData",
        [907] = "UInt16", [914] = "UInt32", [921] = "UInt64", [928] = "UInt8", [934] = "unsigned int",
        [947] = "unsigned long long", [966] = "unsigned short", [981] = "vector", [988] = "Vector2f",
        [997] = "Vector3f", [1006] = "Vector4f", [1015] = "m_ScriptingClassIdentifier", [1042] = "Gradient",
        [1051] = "Type*", [1057] = "int2_storage", [1070] = "int3_storage", [1083] = "BoundsInt",
        [1093] = "m_CorrespondingSourceObject", [1121] = "m_PrefabInstance", [1138] = "m_PrefabAsset",
        [1152] = "FileSize", [1161] = "Hash128"
    };

    public static UnitySerializedFile? TryRead(byte[] cab)
    {
        if (cab.Length < 48) return null;
        int meta32 = BinaryPrimitives.ReadInt32BigEndian(cab.AsSpan(0));
        int file32 = BinaryPrimitives.ReadInt32BigEndian(cab.AsSpan(4));
        int format = BinaryPrimitives.ReadInt32BigEndian(cab.AsSpan(8));
        int data32 = BinaryPrimitives.ReadInt32BigEndian(cab.AsSpan(12));
        if (format is < 17 or > 26) return null;

        int pos = 16;
        byte endian = cab[pos++];
        if (endian != 0) return null; // Little-endian object payloads only (PC/Windows)
        pos += 3;

        long fileSize = file32;
        long dataOff = data32;
        if (format >= 22)
        {
            if (cab.Length < 48) return null;
            _ = BinaryPrimitives.ReadInt32BigEndian(cab.AsSpan(pos)); pos += 4; // big metadata size
            fileSize = BinaryPrimitives.ReadInt64BigEndian(cab.AsSpan(pos)); pos += 8;
            dataOff = BinaryPrimitives.ReadInt64BigEndian(cab.AsSpan(pos)); pos += 8;
            pos += 8; // unknown
        }
        else if (meta32 <= 0)
        {
            return null;
        }

        if (fileSize != cab.Length || dataOff <= pos || dataOff > cab.Length)
            return null;

        // Unity version string
        int verEnd = Array.IndexOf<byte>(cab, 0, pos);
        if (verEnd < 0 || verEnd - pos > 64) return null;
        pos = verEnd + 1;

        if (pos + 9 > dataOff) return null;
        pos += 4; // targetPlatform
        bool hasTypeTree = cab[pos++] != 0;
        int typeCount = BinaryPrimitives.ReadInt32LittleEndian(cab.AsSpan(pos)); pos += 4;
        if (typeCount < 0 || typeCount > 4096) return null;

        var types = new List<SerializedType>(typeCount);
        for (int t = 0; t < typeCount; t++)
        {
            if (pos + 23 > dataOff) return null;
            int classId = BinaryPrimitives.ReadInt32LittleEndian(cab.AsSpan(pos)); pos += 4;
            pos++; // stripped
            pos += 2; // scriptTypeIndex
            if (classId == 114) pos += 16; // scriptId
            pos += 16; // oldTypeHash

            var nodes = new List<TypeNode>();
            if (hasTypeTree)
            {
                if (pos + 8 > dataOff) return null;
                int nodeCount = BinaryPrimitives.ReadInt32LittleEndian(cab.AsSpan(pos)); pos += 4;
                int strBufLen = BinaryPrimitives.ReadInt32LittleEndian(cab.AsSpan(pos)); pos += 4;
                int nodeSize = format >= 19 ? 32 : 24;
                if (nodeCount < 0 || strBufLen < 0 || pos + (long)nodeCount * nodeSize + strBufLen > dataOff)
                    return null;
                int nodesStart = pos;
                int strStart = nodesStart + nodeCount * nodeSize;
                for (int n = 0; n < nodeCount; n++)
                {
                    int np = nodesStart + n * nodeSize;
                    byte level = cab[np + 2];
                    uint typeStrOff = BinaryPrimitives.ReadUInt32LittleEndian(cab.AsSpan(np + 4));
                    uint nameStrOff = BinaryPrimitives.ReadUInt32LittleEndian(cab.AsSpan(np + 8));
                    int byteSize = BinaryPrimitives.ReadInt32LittleEndian(cab.AsSpan(np + 12));
                    int metaFlag = BinaryPrimitives.ReadInt32LittleEndian(cab.AsSpan(np + 20));
                    string tn = ResolveTreeString(cab, strStart, strBufLen, typeStrOff);
                    string nn = ResolveTreeString(cab, strStart, strBufLen, nameStrOff);
                    nodes.Add(new TypeNode(level, tn, nn, byteSize, metaFlag));
                }
                pos = strStart + strBufLen;
                if (format >= 21)
                {
                    if (pos + 4 > dataOff) return null;
                    int depCount = BinaryPrimitives.ReadInt32LittleEndian(cab.AsSpan(pos)); pos += 4;
                    if (depCount < 0 || pos + (long)depCount * 4 > dataOff) return null;
                    pos += depCount * 4;
                }
            }
            types.Add(new SerializedType(classId, nodes));
        }

        if (pos + 4 > dataOff) return null;
        int objCount = BinaryPrimitives.ReadInt32LittleEndian(cab.AsSpan(pos)); pos += 4;
        if (objCount < 0 || objCount > 100_000) return null;

        var objects = new List<ObjectEntry>(objCount);
        for (int i = 0; i < objCount; i++)
        {
            pos = (pos + 3) & ~3;
            int recOff = pos;
            int recSize = format >= 22 ? 24 : 20;
            if (pos + recSize > dataOff) return null;

            long pathId = BinaryPrimitives.ReadInt64LittleEndian(cab.AsSpan(pos)); pos += 8;
            long byteStart = format >= 22
                ? BinaryPrimitives.ReadInt64LittleEndian(cab.AsSpan(pos))
                : BinaryPrimitives.ReadUInt32LittleEndian(cab.AsSpan(pos));
            pos += format >= 22 ? 8 : 4;
            int byteSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(cab.AsSpan(pos))); pos += 4;
            int typeIndex = BinaryPrimitives.ReadInt32LittleEndian(cab.AsSpan(pos)); pos += 4;

            if (byteStart < 0 || byteSize < 0 || dataOff + byteStart + byteSize > cab.Length
                || typeIndex < 0 || typeIndex >= types.Count)
                return null;
            objects.Add(new ObjectEntry(pathId, byteStart, byteSize, typeIndex, recOff));
        }

        return new UnitySerializedFile
        {
            Format = format,
            DataOffset = dataOff,
            Types = types,
            Objects = objects,
            RawBytes = cab
        };
    }

    public ReadOnlySpan<byte> Slice(ObjectEntry obj) =>
        RawBytes.AsSpan(checked((int)(DataOffset + obj.ByteStart)), obj.ByteSize);

    public bool IsStringTableType(int typeIndex)
    {
        var type = Types[typeIndex];
        if (type.ClassId != 114 || type.Nodes.Count == 0) return false;
        bool hasLocale = type.Nodes.Any(n => n.Name == "m_LocaleId" && n.TypeName == "LocaleIdentifier");
        bool hasTableData = type.Nodes.Any(n => n.Name == "m_TableData" && n.TypeName == "TableEntryData");
        bool hasLocalized = type.Nodes.Any(n => n.Name == "m_Localized" && n.TypeName == "string");
        bool hasId = type.Nodes.Any(n => n.Name == "m_Id" && n.TypeName == "SInt64");
        bool hasRidMeta = type.Nodes.Any(n => n.Name == "rid" && n.TypeName == "SInt64");
        return hasLocale && hasTableData && hasLocalized && hasId && hasRidMeta;
    }

    public bool IsTextAssetType(int typeIndex) => Types[typeIndex].ClassId == 49;

    public byte[] Rewrite(IReadOnlyDictionary<long, byte[]> replacements)
    {
        if (replacements.Count == 0) return RawBytes;
        int start = checked((int)DataOffset);
        byte[] header = RawBytes[..start];

        using var output = new MemoryStream(RawBytes.Length + 4096);
        output.Write(header);

        // Preserve original object ordering by ByteStart so layout stays clean & 8-byte aligned.
        var ordered = Objects.OrderBy(o => o.ByteStart).ToList();
        var updatedRecords = new Dictionary<long, (long NewStart, int NewSize, byte[] Expected)>(ordered.Count);

        foreach (var obj in ordered)
        {
            int pad = (int)((8 - ((output.Position - start) % 8)) % 8);
            if (pad > 0) output.Write(new byte[pad]);

            long newStart = output.Position - start;
            byte[] payload = replacements.TryGetValue(obj.PathId, out var rep) ? rep : Slice(obj).ToArray();
            output.Write(payload);

            Span<byte> rec = header.AsSpan(obj.RecordOffset);
            if (Format >= 22)
            {
                BinaryPrimitives.WriteInt64LittleEndian(rec[8..], newStart);
                BinaryPrimitives.WriteUInt32LittleEndian(rec[16..], (uint)payload.Length);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(rec[8..], checked((uint)newStart));
                BinaryPrimitives.WriteUInt32LittleEndian(rec[12..], (uint)payload.Length);
            }
            updatedRecords[obj.PathId] = (newStart, payload.Length, payload);
        }

        if (Format >= 22)
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(24), output.Length);
        else
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), checked((int)output.Length));

        output.Position = 0;
        output.Write(header);
        byte[] result = output.ToArray();

        // Read-back verification of every single object in the rewritten SerializedFile
        var reread = TryRead(result) ?? throw new InvalidDataException("تعذّر التحقق من ملف Unity المعاد بناؤه.");
        if (reread.Objects.Count != Objects.Count)
            throw new InvalidDataException("تغيّر عدد الكائنات في ملف Unity المعاد بناؤه.");
        foreach (var after in reread.Objects)
        {
            if (!updatedRecords.TryGetValue(after.PathId, out var expected)
                || !reread.Slice(after).SequenceEqual(expected.Expected))
                throw new InvalidDataException("تعذّرت مطابقة كائنات Unity بعد إعادة البناء.");
        }
        return result;
    }

    private static string ResolveTreeString(byte[] buf, int strStart, int strBufLen, uint offset)
    {
        if ((offset & 0x80000000u) != 0)
            return CommonStrings.TryGetValue((int)(offset & 0x7FFFFFFFu), out string? s) ? s : "";
        int pos = strStart + (int)offset;
        int end = strStart + strBufLen;
        if (pos < strStart || pos >= end) return "";
        int nul = Array.IndexOf<byte>(buf, 0, pos, end - pos);
        return nul < 0 ? "" : Encoding.UTF8.GetString(buf, pos, nul - pos);
    }
}

public sealed partial class UnityBundleFormat : IFormatHandler
{
    public string Name => "unity-bundle";

    public bool Supports(string path) =>
        path.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<TextSlot> Extract(byte[] bytes)
    {
        if (!UnityFsBundle.IsUnityFs(bytes)) return [];
        var bundle = UnityFsBundle.Read(bytes);
        var slots = new List<TextSlot>();

        for (int nodeIndex = 0; nodeIndex < bundle.Nodes.Count; nodeIndex++)
        {
            var node = bundle.Nodes[nodeIndex];
            if ((node.Flags & 4) == 0) continue; // SerializedFile nodes have flag 0x4
            var sf = UnitySerializedFile.TryRead(node.Bytes);
            if (sf == null) continue;

            foreach (var obj in sf.Objects)
            {
                if (sf.IsStringTableType(obj.TypeIndex))
                {
                    var table = ParseStringTable(sf.Slice(obj));
                    if (table == null) continue;
                    string context = string.IsNullOrWhiteSpace(table.LocaleCode)
                        ? table.Name
                        : $"{table.Name} ({table.LocaleCode})";
                    foreach (var entry in table.Entries)
                    {
                        if (string.IsNullOrWhiteSpace(entry.Localized)) continue;
                        slots.Add(new TextSlot($"st:{nodeIndex}:{obj.PathId}:{entry.Id}", entry.Localized, context));
                    }
                }
                else if (sf.IsTextAssetType(obj.TypeIndex))
                {
                    var ta = ParseTextAsset(sf.Slice(obj));
                    if (ta == null || !IsTranslatableTextAsset(ta.Name, ta.Text)) continue;
                    slots.Add(new TextSlot($"ta:{nodeIndex}:{obj.PathId}", ta.Text, $"TextAsset: {ta.Name}"));
                }
            }
        }
        return slots;
    }

    public byte[] Apply(byte[] bytes, IReadOnlyDictionary<string, string> translations) =>
        ApplyWithDisplay(bytes, translations, displayOptions: null);

    internal byte[] ApplyWithDisplay(byte[] bytes, IReadOnlyDictionary<string, string> translations,
        UnityArabicDisplayOptions? displayOptions)
    {
        if (translations.Count == 0 || !UnityFsBundle.IsUnityFs(bytes)) return bytes;
        var bundle = UnityFsBundle.Read(bytes);
        var updatedNodes = new List<UnityFsNode>(bundle.Nodes.Count);
        bool anyChanged = false;

        for (int nodeIndex = 0; nodeIndex < bundle.Nodes.Count; nodeIndex++)
        {
            var node = bundle.Nodes[nodeIndex];
            if ((node.Flags & 4) == 0)
            {
                updatedNodes.Add(node);
                continue;
            }
            var sf = UnitySerializedFile.TryRead(node.Bytes);
            if (sf == null)
            {
                updatedNodes.Add(node);
                continue;
            }

            var replacements = new Dictionary<long, byte[]>();
            foreach (var obj in sf.Objects)
            {
                if (sf.IsStringTableType(obj.TypeIndex))
                {
                    var table = ParseStringTable(sf.Slice(obj));
                    if (table == null) continue;
                    string prefix = $"st:{nodeIndex}:{obj.PathId}:";
                    bool changed = false;
                    var newEntries = new List<StringTableEntry>(table.Entries.Count);
                    foreach (var entry in table.Entries)
                    {
                        string locator = prefix + entry.Id;
                        if (translations.TryGetValue(locator, out string? translated) && translated != entry.Localized)
                        {
                            string finalText = displayOptions != null ? displayOptions.TransformArabic(translated) : translated;
                            newEntries.Add(entry with { Localized = finalText });
                            changed = true;
                        }
                        else
                        {
                            newEntries.Add(entry);
                        }
                    }
                    if (changed)
                        replacements[obj.PathId] = RewriteStringTable(table, newEntries);
                }
                else if (sf.IsTextAssetType(obj.TypeIndex))
                {
                    string locator = $"ta:{nodeIndex}:{obj.PathId}";
                    if (!translations.TryGetValue(locator, out string? translated)) continue;
                    var ta = ParseTextAsset(sf.Slice(obj));
                    if (ta == null || translated == ta.Text) continue;
                    string finalText = displayOptions != null ? displayOptions.TransformArabic(translated) : translated;
                    replacements[obj.PathId] = RewriteTextAsset(ta, finalText);
                }
            }

            if (replacements.Count > 0)
            {
                byte[] rebuiltCab = sf.Rewrite(replacements);
                updatedNodes.Add(node with { Bytes = rebuiltCab });
                anyChanged = true;
            }
            else
            {
                updatedNodes.Add(node);
            }
        }

        if (!anyChanged) return bytes;
        byte[] rebuilt = bundle.Write(updatedNodes, out _);

        // Verify read-back of the entire rebuilt UnityFS bundle
        _ = UnityFsBundle.Read(rebuilt);
        return rebuilt;
    }

    internal static string? DetectStringTableLocale(byte[] bytes)
    {
        if (!UnityFsBundle.IsUnityFs(bytes)) return null;
        try
        {
            var bundle = UnityFsBundle.Read(bytes);
            foreach (var node in bundle.Nodes)
            {
                if ((node.Flags & 4) == 0) continue;
                var sf = UnitySerializedFile.TryRead(node.Bytes);
                if (sf == null) continue;
                foreach (var obj in sf.Objects)
                {
                    if (!sf.IsStringTableType(obj.TypeIndex)) continue;
                    var table = ParseStringTable(sf.Slice(obj));
                    if (table != null && !string.IsNullOrWhiteSpace(table.LocaleCode))
                        return table.LocaleCode.Trim();
                }
            }
        }
        catch { }
        return null;
    }

    internal static uint ComputeUncompressedCrc32(byte[] bytes) =>
        UnityFsBundle.Read(bytes).UncompressedStreamCrc32;

    internal static bool HasTranslatableContent(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length < 32 || info.Length > 32L * 1024 * 1024) return false;
            Span<byte> magic = stackalloc byte[8];
            using (var fs = File.OpenRead(filePath))
            {
                if (fs.Read(magic) < 8 || !magic.SequenceEqual("UnityFS\0"u8)) return false;
            }
            byte[] bytes = File.ReadAllBytes(filePath);
            return new UnityBundleFormat().Extract(bytes).Count > 0;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsCandidateBundlePath(string relativePath)
    {
        string p = relativePath.Replace('\\', '/');
        if (p.Contains("/StreamingAssets/aa/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("StreamingAssets/aa/", StringComparison.OrdinalIgnoreCase))
            return true;
        string name = Path.GetFileName(p);
        return CandidateBundleNamePattern().IsMatch(name);
    }

    [GeneratedRegex(@"(?:loc|lang|string|table|text|dialog|i18n|l10n)", RegexOptions.IgnoreCase)]
    private static partial Regex CandidateBundleNamePattern();

    private sealed record StringTableEntry(long Id, string Localized, byte[] MetadataRaw);
    private sealed record ParsedStringTable(string Name, string LocaleCode, byte[] PrefixBeforeTableData,
        IReadOnlyList<StringTableEntry> Entries, byte[] TailAfterTableData);

    private static ParsedStringTable? ParseStringTable(ReadOnlySpan<byte> data)
    {
        // Standard Unity.Localization.Tables.StringTable layout (verified on Unity 2020–Unity 6):
        // m_GameObject (12) + m_Enabled (4 aligned) + m_Script (12) + m_Name (aligned str)
        // + m_LocaleId.m_Code (aligned str) + m_SharedData (12)
        // + m_Metadata.m_Items (int count + count*8 rid)
        // + m_TableData (int count + entries: { Int64 m_Id, aligned str m_Localized, m_Metadata.m_Items (int + count*8) })
        // + references tail (verbatim)
        int pos = 0;
        if (data.Length < 48) return null;
        pos += 12; // m_GameObject
        pos += 4;  // m_Enabled + 3B align
        pos += 12; // m_Script
        if (!TryReadAlignedString(data, ref pos, out string name)) return null;
        if (!TryReadAlignedString(data, ref pos, out string localeCode)) return null;
        if (pos + 16 > data.Length) return null;
        pos += 12; // m_SharedData

        int tableMetaCount = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]); pos += 4;
        if (tableMetaCount < 0 || tableMetaCount > 10_000 || pos + tableMetaCount * 8 > data.Length) return null;
        pos += tableMetaCount * 8;

        int tableDataStart = pos;
        if (pos + 4 > data.Length) return null;
        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]); pos += 4;
        if (entryCount < 0 || entryCount > 200_000) return null;

        var entries = new List<StringTableEntry>(entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            if (pos + 12 > data.Length) return null;
            long id = BinaryPrimitives.ReadInt64LittleEndian(data[pos..]); pos += 8;
            if (!TryReadAlignedString(data, ref pos, out string localized)) return null;
            if (pos + 4 > data.Length) return null;
            int metaStart = pos;
            int metaCount = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]); pos += 4;
            if (metaCount < 0 || metaCount > 10_000 || pos + metaCount * 8 > data.Length) return null;
            pos += metaCount * 8;
            entries.Add(new StringTableEntry(id, localized, data.Slice(metaStart, pos - metaStart).ToArray()));
        }

        return new ParsedStringTable(
            name,
            localeCode,
            data[..tableDataStart].ToArray(),
            entries,
            data[pos..].ToArray());
    }

    private static byte[] RewriteStringTable(ParsedStringTable table, IReadOnlyList<StringTableEntry> entries)
    {
        using var ms = new MemoryStream(table.PrefixBeforeTableData.Length + table.TailAfterTableData.Length + entries.Count * 32);
        ms.Write(table.PrefixBeforeTableData);
        WriteI32LE(ms, entries.Count);
        foreach (var entry in entries)
        {
            WriteI64LE(ms, entry.Id);
            WriteAlignedString(ms, entry.Localized);
            ms.Write(entry.MetadataRaw);
        }
        ms.Write(table.TailAfterTableData);
        return ms.ToArray();
    }

    internal sealed record ParsedTextAsset(string Name, string Text, byte[] Tail);

    internal static ParsedTextAsset? ParseTextAsset(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        if (!TryReadAlignedString(data, ref pos, out string name)) return null;
        if (!TryReadAlignedString(data, ref pos, out string text)) return null;
        return new ParsedTextAsset(name, text, data[pos..].ToArray());
    }

    internal static byte[] RewriteTextAsset(ParsedTextAsset ta, string newText)
    {
        using var ms = new MemoryStream();
        WriteAlignedString(ms, ta.Name);
        WriteAlignedString(ms, newText);
        ms.Write(ta.Tail);
        return ms.ToArray();
    }

    private static bool IsTranslatableTextAsset(string name, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256_000 || text.Contains('\0')) return false;
        if (IgnoredTextAssetNamePattern().IsMatch(name)) return false;
        // Skip binary/control blobs
        int controlCount = 0;
        foreach (char c in text)
        {
            if (char.IsControl(c) && c is not ('\r' or '\n' or '\t')) controlCount++;
        }
        if (controlCount > 0) return false;
        return TextQualityClassifier.ClassifyString(text) == StringQuality.Human;
    }

    private static bool TryReadAlignedString(ReadOnlySpan<byte> data, ref int pos, out string value)
    {
        value = "";
        if (pos + 4 > data.Length) return false;
        int len = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
        pos += 4;
        if (len < 0 || pos + len > data.Length) return false;
        value = Encoding.UTF8.GetString(data.Slice(pos, len));
        int aligned = (len + 3) & ~3;
        if (pos + aligned > data.Length) return false;
        pos += aligned;
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
    private static void WriteI64LE(Stream s, long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); s.Write(b); }

    [GeneratedRegex(@"(?:^link$|shader|license|notice|thirdparty|readme|changelog|manifest|version)", RegexOptions.IgnoreCase)]
    private static partial Regex IgnoredTextAssetNamePattern();
}

internal static partial class UnityAddressablesCatalog
{
    internal sealed record BundleUpdate(string RelativePath, uint OldCabCrc, uint OldBundleSize, uint NewCabCrc, uint NewBundleSize);
    internal sealed record CatalogPatch(string RelativePath, string OriginalHash, byte[] RewrittenBytes, int UpdatedBundles);

    public static IReadOnlyList<CatalogPatch> PreparePatches(string gameRoot, IReadOnlyList<BundleUpdate> updates, CancellationToken cancellation)
    {
        if (updates.Count == 0 || !Directory.Exists(gameRoot)) return [];

        var byCatalogDir = new Dictionary<string, List<BundleUpdate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var update in updates)
        {
            string? catalogDir = FindAddressablesDir(gameRoot, update.RelativePath);
            if (catalogDir == null) continue;
            if (!byCatalogDir.TryGetValue(catalogDir, out var list))
            {
                list = [];
                byCatalogDir[catalogDir] = list;
            }
            list.Add(update);
        }

        var patches = new List<CatalogPatch>();
        foreach (var (catalogDir, list) in byCatalogDir)
        {
            cancellation.ThrowIfCancellationRequested();
            string binPath = Path.Combine(catalogDir, "catalog.bin");
            if (File.Exists(binPath))
            {
                Identity.RequireNoLinks(binPath);
                byte[] original = File.ReadAllBytes(binPath);
                byte[] patched = PatchBinaryCatalog(original, list, out int count);
                if (count > 0)
                {
                    string rel = Identity.SafePath(Path.GetRelativePath(gameRoot, binPath));
                    patches.Add(new CatalogPatch(rel, Identity.Hash(original), patched, count));
                }
            }

            string jsonPath = Path.Combine(catalogDir, "catalog.json");
            if (File.Exists(jsonPath))
            {
                Identity.RequireNoLinks(jsonPath);
                byte[] original = File.ReadAllBytes(jsonPath);
                byte[] patched = PatchJsonCatalog(original, list, out int count);
                if (count > 0)
                {
                    string rel = Identity.SafePath(Path.GetRelativePath(gameRoot, jsonPath));
                    patches.Add(new CatalogPatch(rel, Identity.Hash(original), patched, count));
                }
            }
        }
        return patches;
    }

    internal static byte[] PatchBinaryCatalog(byte[] original, IReadOnlyList<BundleUpdate> updates, out int updatedCount)
    {
        byte[] copy = (byte[])original.Clone();
        updatedCount = 0;
        foreach (var u in updates)
        {
            if (u.OldCabCrc == u.NewCabCrc && u.OldBundleSize == u.NewBundleSize) continue;
            var offsets = new List<int>();
            for (int i = 0; i <= copy.Length - 8; i++)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(i)) == u.OldCabCrc
                    && BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(i + 4)) == u.OldBundleSize)
                {
                    offsets.Add(i);
                }
            }
            if (offsets.Count == 1)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(offsets[0]), u.NewCabCrc);
                BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(offsets[0] + 4), u.NewBundleSize);
                updatedCount++;
            }
        }
        return copy;
    }

    internal static byte[] PatchJsonCatalog(byte[] original, IReadOnlyList<BundleUpdate> updates, out int updatedCount)
    {
        string text = Encoding.UTF8.GetString(original);
        updatedCount = 0;
        foreach (var u in updates)
        {
            if (u.OldCabCrc == u.NewCabCrc && u.OldBundleSize == u.NewBundleSize) continue;
            string oldCrcToken = $"\"m_Crc\":{u.OldCabCrc}";
            string oldSizeToken = $"\"m_BundleSize\":{u.OldBundleSize}";
            int crcIdx = text.IndexOf(oldCrcToken, StringComparison.Ordinal);
            if (crcIdx < 0) continue;
            int searchEnd = Math.Min(text.Length, crcIdx + 512);
            int sizeIdx = text.IndexOf(oldSizeToken, crcIdx, searchEnd - crcIdx, StringComparison.Ordinal);
            if (sizeIdx < 0) continue;

            text = text[..crcIdx]
                + $"\"m_Crc\":{u.NewCabCrc}"
                + text[(crcIdx + oldCrcToken.Length)..sizeIdx]
                + $"\"m_BundleSize\":{u.NewBundleSize}"
                + text[(sizeIdx + oldSizeToken.Length)..];
            updatedCount++;
        }
        return Encoding.UTF8.GetBytes(text);
    }

    private static string? FindAddressablesDir(string gameRoot, string bundleRelativePath)
    {
        string full = Path.GetFullPath(Path.Combine(gameRoot, bundleRelativePath));
        string root = Path.GetFullPath(gameRoot);
        for (string? dir = Path.GetDirectoryName(full);
             dir != null && Identity.Within(dir, root);
             dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "catalog.bin")) || File.Exists(Path.Combine(dir, "catalog.json")))
                return dir;
            if (StringComparer.OrdinalIgnoreCase.Equals(dir, root)) break;
        }
        return null;
    }
}
