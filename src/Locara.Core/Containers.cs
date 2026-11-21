using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;

namespace Locara.Core;

public sealed record ResourceEntry(string Path, long Size, Action<Stream, CancellationToken> CopyTo)
{
    public byte[] Read(CancellationToken cancellation)
    {
        if (Size > 128L * 1024 * 1024)
            throw new InvalidDataException("يتجاوز المورد النصي حد القراءة (128 MiB) — " + Path);
        using var bytes = new MemoryStream((int)Size);
        CopyTo(bytes, cancellation);
        return bytes.ToArray();
    }

    public string Hash(CancellationToken cancellation) => CopyAndHash(Stream.Null, cancellation);

    internal string CopyAndHash(Stream destination, CancellationToken cancellation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var output = new HashingStream(destination, hash);
        CopyTo(output, cancellation);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class HashingStream(Stream destination, IncrementalHash hash) : Stream
    {
        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            destination.Write(buffer);
            hash.AppendData(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Flush() => destination.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    internal static ResourceEntry FromBytes(string path, byte[] bytes) =>
        new(path, bytes.LongLength, (output, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            output.Write(bytes);
        });
}

public sealed class ResourceIndex(IDisposable? owner = null) : IDisposable
{
    private readonly HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ResourceEntry> entries = [];
    private long size;
    public IReadOnlyList<ResourceEntry> Entries => entries;
    internal uint[]? GodotVersion { get; init; }
    internal uint PckFormatVersion { get; init; } = 2;
    internal uint PckFlags { get; init; }
    internal bool PckUsesResPrefix { get; set; } = true;
    internal bool Encrypted { get; init; }

    internal void Add(string path, long length, Action<Stream, CancellationToken> copy, bool archive = false, bool compressedArchive = false)
    {
        string normalized = Identity.SafePath(path);
        if (!paths.Add(normalized)) throw new InvalidDataException("Duplicate resource path: " + normalized);
        long limit = compressedArchive ? 1024L * 1024 * 1024 : 16L * 1024 * 1024 * 1024;
        if (length < 0 || paths.Count > 100000 || (archive && length > limit - size))
            throw new InvalidDataException("Container exceeds the supported resource count or size limit.");
        size += length;
        entries.Add(new(normalized, length, copy));
    }

    public void Dispose() => owner?.Dispose();
}

public static class Containers
{
    public static IReadOnlyList<IContainerAdapter> All { get; } = [new FolderContainer(), new ZipContainer(), new PckContainer()];
    public static IContainerAdapter For(string path) => All.FirstOrDefault(c => c.Supports(path))
        ?? throw new NotSupportedException("الحاويات المدعومة هي المجلد وZIP وGodot PCK المستقل بإصدار الصيغة 2 أو 3.");
    internal static bool IsArchive(string path) => All.Skip(1).Any(adapter => adapter.Supports(path));

    internal static void Copy(Stream input, Stream output, long length, CancellationToken cancellation, bool checkEnd = true, IncrementalHash? checksum = null)
    {
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (length > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, (int)Math.Min(length, buffer.Length));
                if (read == 0) throw new InvalidDataException("Resource ended before its declared size.");
                output.Write(buffer, 0, read);
                checksum?.AppendData(buffer, 0, read);
                length -= read;
            }
            if (checkEnd && input.ReadByte() != -1) throw new InvalidDataException("Resource exceeds its declared size.");
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }
}

public abstract class ContainerAdapter : IContainerAdapter
{
    public abstract string Name { get; }
    public abstract bool Supports(string path);
    public abstract ResourceIndex Index(string source, string? password, CancellationToken cancellation);
    public abstract void WriteIndexed(string destination, IReadOnlyList<ResourceEntry> resources, string? password,
        CancellationToken cancellation, ResourceIndex? original = null);

    public IReadOnlyList<Resource> Read(string source, string? password, CancellationToken cancellation)
    {
        using var index = Index(source, password, cancellation);
        return index.Entries.Select(entry => new Resource(entry.Path, entry.Read(cancellation))).ToList();
    }

    public void Write(string destination, IReadOnlyList<Resource> resources, string? password, CancellationToken cancellation) =>
        WriteIndexed(destination, resources.Select(r => ResourceEntry.FromBytes(r.Path, r.Bytes)).ToList(), password, cancellation);
}

public sealed class FolderContainer : ContainerAdapter
{
    public override string Name => "folder";
    public override bool Supports(string path) => Directory.Exists(path);
    public override ResourceIndex Index(string source, string? password, CancellationToken cancellation)
    {
        Identity.RequireNoLinks(source);
        var index = new ResourceIndex();
        if (File.Exists(source)) AddFile(index, Path.GetFileName(source), source);
        else Visit(source);
        return index;

        void Visit(string directory)
        {
            foreach (var file in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Choose a source without filesystem links.");
                if ((attributes & FileAttributes.Directory) == 0)
                    AddFile(index, Path.GetRelativePath(source, file), file);
                else if (Path.GetFileName(file) is not (".git" or ".godot")) Visit(file);
            }
        }
    }

    private static void AddFile(ResourceIndex index, string path, string file)
    {
        long size = new FileInfo(file).Length;
        index.Add(path, size, (output, cancellation) =>
        {
            Identity.RequireNoLinks(file);
            using var input = File.OpenRead(file);
            Containers.Copy(input, output, size, cancellation);
        });
    }

    public override void WriteIndexed(string destination, IReadOnlyList<ResourceEntry> resources, string? password,
        CancellationToken cancellation, ResourceIndex? original = null)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in resources)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = Path.Combine(destination, Identity.SafePath(entry.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = File.Create(path);
            entry.CopyTo(output, cancellation);
        }
    }
}

public sealed class ZipContainer : ContainerAdapter
{
    public override string Name => "zip";
    public override bool Supports(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)) return true;
        if (!extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
        Identity.RequireNoLinks(path);
        using var input = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[4];
        return input.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false) == signature.Length
            && (signature.SequenceEqual("PK\x03\x04"u8) || signature.SequenceEqual("PK\x05\x06"u8));
    }

    public override ResourceIndex Index(string source, string? password, CancellationToken cancellation)
    {
        Identity.RequireNoLinks(source);
        var zip = new ZipFile(source) { Password = password };
        try
        {
            bool encrypted = zip.Cast<ZipEntry>().Any(entry => entry.IsFile && entry.IsCrypted);
            if (encrypted && string.IsNullOrEmpty(password))
                throw new InvalidDataException("تحتاج حزمة ZIP إلى كلمة مرور للجلسة.");
            var index = new ResourceIndex(zip) { Encrypted = encrypted };
            foreach (ZipEntry entry in zip)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!entry.IsFile) continue;
                index.Add(entry.Name, entry.Size, (output, token) =>
                {
                    using var input = zip.GetInputStream(entry);
                    Containers.Copy(input, output, entry.Size, token);
                }, archive: true, compressedArchive: true);
            }
            return index;
        }
        catch { zip.Close(); throw; }
    }

    public override void WriteIndexed(string destination, IReadOnlyList<ResourceEntry> resources, string? password,
        CancellationToken cancellation, ResourceIndex? original = null)
    {
        using var zip = new ZipOutputStream(File.Create(destination));
        string? encryptionPassword = original?.Encrypted == false ? null : password;
        zip.Password = encryptionPassword;
        zip.SetLevel(6);
        foreach (var resource in resources)
        {
            cancellation.ThrowIfCancellationRequested();
            var entry = new ZipEntry(Identity.SafePath(resource.Path)) { Size = resource.Size };
            if (!string.IsNullOrEmpty(encryptionPassword)) entry.AESKeySize = 256;
            zip.PutNextEntry(entry);
            resource.CopyTo(zip, cancellation);
            zip.CloseEntry();
        }
        zip.Finish();
    }
}

public sealed class PckContainer : ContainerAdapter
{
    private const uint MagicGdpc = 0x43504447u;
    private const uint FlagDirEncrypted = 1u;
    private const uint FlagRelFileBase = 2u;

    public override string Name => "godot-pck";
    public override bool Supports(string path) => Path.GetExtension(path).Equals(".pck", StringComparison.OrdinalIgnoreCase);

    public override ResourceIndex Index(string source, string? password, CancellationToken cancellation)
    {
        Identity.RequireNoLinks(source);
        var stream = File.OpenRead(source);
        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            uint magic = reader.ReadUInt32();
            uint formatVersion = reader.ReadUInt32();
            if (magic != MagicGdpc || formatVersion is not (2u or 3u))
                throw new NotSupportedException("يدعم Locara ملفات Godot PCK المستقلة بإصدار الصيغة 2 أو 3.");
            uint[] godotVersion = [reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()];
            uint flags = reader.ReadUInt32();
            ulong fileBase = reader.ReadUInt64();
            if ((flags & FlagDirEncrypted) != 0 || (flags & ~FlagRelFileBase) != 0)
                throw new NotSupportedException("جدول موارد PCK المشفر غير مدعوم.");

            if (formatVersion == 3u)
            {
                ulong dirBase = reader.ReadUInt64();
                if (dirBase > (ulong)stream.Length)
                    throw new InvalidDataException("Invalid PCK v3 directory offset.");
                stream.Position = (long)dirBase;
            }
            else
            {
                stream.Position += 64;
            }

            var index = new ResourceIndex(stream)
            {
                GodotVersion = godotVersion,
                PckFormatVersion = formatVersion,
                PckFlags = flags,
                PckUsesResPrefix = formatVersion == 2u
            };

            uint count = reader.ReadUInt32();
            if (count > 100000) throw new InvalidDataException("PCK directory limit.");
            bool sawEntry = false;
            bool sawResPrefix = false;
            for (int i = 0; i < count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                uint nameLength = reader.ReadUInt32();
                if (nameLength > 65536) throw new InvalidDataException("Invalid PCK name.");
                string name = Encoding.UTF8.GetString(reader.ReadBytes((int)nameLength)).TrimEnd((char)0);
                if (!sawEntry)
                {
                    sawEntry = true;
                    sawResPrefix = name.StartsWith("res://", StringComparison.Ordinal);
                }
                ulong offset = reader.ReadUInt64(), size = reader.ReadUInt64();
                byte[] checksum = reader.ReadBytes(16);
                if (reader.ReadUInt32() != 0) throw new NotSupportedException("موارد PCK المشفرة أو المحذوفة غير مدعومة.");
                if (offset > ulong.MaxValue - fileBase || offset + fileBase > (ulong)stream.Length
                    || size > (ulong)stream.Length - (offset + fileBase))
                    throw new InvalidDataException("Invalid PCK entry bounds.");
                index.Add(name, (long)size, (output, token) =>
                {
                    stream.Position = (long)(offset + fileBase);
                    using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                    Containers.Copy(stream, output, (long)size, token, checkEnd: false, checksum: md5);
                    if (!checksum.AsSpan().SequenceEqual(md5.GetHashAndReset()))
                        throw new InvalidDataException("PCK checksum mismatch: " + name);
                }, archive: true);
            }
            if (sawEntry) index.PckUsesResPrefix = sawResPrefix;
            return index;
        }
        catch { stream.Dispose(); throw; }
    }

    public override void WriteIndexed(string destination, IReadOnlyList<ResourceEntry> resources, string? password,
        CancellationToken cancellation, ResourceIndex? original = null)
    {
        uint formatVersion = original?.PckFormatVersion == 3u ? 3u : 2u;
        bool useResPrefix = original?.PckUsesResPrefix ?? (formatVersion == 2u);
        if (formatVersion == 3u)
        {
            WriteV3(destination, resources, original, useResPrefix, cancellation);
            return;
        }
        WriteV2(destination, resources, original, useResPrefix, cancellation);
    }

    private static void WriteV2(string destination, IReadOnlyList<ResourceEntry> resources, ResourceIndex? original,
        bool useResPrefix, CancellationToken cancellation)
    {
        using var stream = File.Create(destination);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(MagicGdpc);
        writer.Write(2u);
        foreach (uint part in original?.GodotVersion ?? [4u, 4u, 0u]) writer.Write(part);
        writer.Write(0u);
        writer.Write(0ul);
        writer.Write(new byte[64]);
        writer.Write((uint)resources.Count);
        var offsets = new List<long>(resources.Count);
        string prefix = useResPrefix ? "res://" : "";
        foreach (var entry in resources)
        {
            cancellation.ThrowIfCancellationRequested();
            byte[] name = Encoding.UTF8.GetBytes(prefix + Identity.SafePath(entry.Path));
            int padding = (4 - name.Length % 4) % 4;
            writer.Write((uint)(name.Length + padding));
            writer.Write(name);
            writer.Write(new byte[padding]);
            offsets.Add(stream.Position);
            writer.Write(0ul);
            writer.Write((ulong)entry.Size);
            writer.Write(new byte[16]);
            writer.Write(0u);
        }
        for (int i = 0; i < resources.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            while (stream.Position % 32 != 0) writer.Write((byte)0);
            long start = stream.Position;
            using var md5 = MD5.Create();
            using var hashing = new CryptoStream(stream, md5, CryptoStreamMode.Write, leaveOpen: true);
            resources[i].CopyTo(hashing, cancellation);
            hashing.FlushFinalBlock();
            long end = stream.Position;
            if (end - start != resources[i].Size) throw new IOException("PCK resource size changed.");
            stream.Position = offsets[i];
            writer.Write((ulong)start);
            writer.Write((ulong)(end - start));
            writer.Write(md5.Hash!);
            stream.Position = end;
        }
    }

    private static void WriteV3(string destination, IReadOnlyList<ResourceEntry> resources, ResourceIndex? original,
        bool useResPrefix, CancellationToken cancellation)
    {
        const ulong fileBase = 112ul; // 104-byte v3 header padded to 16-byte alignment (matches Godot 4.5+)
        using var stream = File.Create(destination);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(MagicGdpc);
        writer.Write(3u);
        foreach (uint part in original?.GodotVersion ?? [4u, 5u, 0u]) writer.Write(part);
        writer.Write(FlagRelFileBase);
        writer.Write(fileBase);
        long dirBaseHeaderPos = stream.Position; // offset 32
        writer.Write(0ul); // dirBase placeholder
        writer.Write(new byte[64]); // 16 reserved uint32s
        while (stream.Position < (long)fileBase) writer.Write((byte)0);

        string prefix = useResPrefix ? "res://" : "";
        var dirEntries = new List<(byte[] NameBytes, int Padding, ulong RelOffset, ulong Size, byte[] Md5)>(resources.Count);
        for (int i = 0; i < resources.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            while (stream.Position % 32 != 0) writer.Write((byte)0);
            long start = stream.Position;
            using var md5 = MD5.Create();
            using var hashing = new CryptoStream(stream, md5, CryptoStreamMode.Write, leaveOpen: true);
            resources[i].CopyTo(hashing, cancellation);
            hashing.FlushFinalBlock();
            long end = stream.Position;
            if (end - start != resources[i].Size) throw new IOException("PCK resource size changed.");
            byte[] name = Encoding.UTF8.GetBytes(prefix + Identity.SafePath(resources[i].Path));
            int padding = (4 - name.Length % 4) % 4;
            dirEntries.Add((name, padding, (ulong)(start - (long)fileBase), (ulong)(end - start), md5.Hash!));
        }

        while (stream.Position % 32 != 0) writer.Write((byte)0);
        ulong dirBase = (ulong)stream.Position;
        writer.Write((uint)dirEntries.Count);
        foreach (var item in dirEntries)
        {
            writer.Write((uint)(item.NameBytes.Length + item.Padding));
            writer.Write(item.NameBytes);
            if (item.Padding > 0) writer.Write(new byte[item.Padding]);
            writer.Write(item.RelOffset);
            writer.Write(item.Size);
            writer.Write(item.Md5);
            writer.Write(0u);
        }

        writer.Flush();
        stream.Position = dirBaseHeaderPos;
        writer.Write(dirBase);
        writer.Flush();
    }
}
