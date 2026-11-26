using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text.Json;
using static System.Buffers.Binary.BinaryPrimitives;

namespace LoclessDisk.Images;

public sealed class ImageWriter : IAsyncDisposable
{
    private const int HeaderSize = 80;
    private const int FooterSize = 56;
    private const int BlockHeaderSize = 48;
    private static readonly string ZeroHash = new('0', 64);

    private readonly FileStream file;
    private readonly byte[][] chain;
    private readonly Dictionary<string, (ushort Level, long Offset)> known = new(StringComparer.Ordinal);
    private readonly Dictionary<(uint Partition, uint Position), string> placed = [];
    private readonly SemaphoreSlim gate = new(1);
    private bool closed;

    public int BlockSize { get; }
    public string Id => Convert.ToHexString(chain[^1]);

    public ImageWriter(string path, ImageChain? parent, int? fullBlockSize = null)
    {
        BlockSize = parent?.BlockSize ?? fullBlockSize ?? 1 << 20;
        byte[] id = RandomNumberGenerator.GetBytes(16);
        chain = [.. (parent?.LayerIds() ?? []), id];
        if (chain.Length > ushort.MaxValue)
            throw new InvalidDataException("Too many image versions.");
        if (parent is not null)
        {
            foreach (var location in parent.StoredLocations())
                known.TryAdd(Convert.ToHexString(location.Hash), (location.Level, location.Offset));
        }
        byte[] imageHeader = new byte[HeaderSize];
        "LDSK"u8.CopyTo(imageHeader);
        WriteUInt16LittleEndian(imageHeader.AsSpan(4), 2);
        WriteUInt16LittleEndian(imageHeader.AsSpan(6), parent is null ? (ushort)1 : (ushort)2);
        WriteInt32LittleEndian(imageHeader.AsSpan(8), BlockSize);
        id.CopyTo(imageHeader, 12);
        if (parent is not null)
        {
            chain[^2].CopyTo(imageHeader, 28);
            parent.TipDigest().CopyTo(imageHeader, 44);
        }
        WriteUInt32LittleEndian(imageHeader.AsSpan(76), Crc32.HashToUInt32(imageHeader.AsSpan(0, 76)));

        file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1 << 20, FileOptions.Asynchronous);
        try { file.Write(imageHeader); }
        catch { file.Dispose(); throw; }
    }

    public async Task<byte[]> MissingAsync(byte[] hashes, CancellationToken ct)
    {
        if (hashes.Length % 32 != 0)
            throw new InvalidDataException("Truncated hash list.");
        await gate.WaitAsync(ct);
        try
        {
            CheckOpen();
            var result = new byte[hashes.Length / 32];
            for (int i = 0; i < result.Length; i++)
            {
                if (!known.ContainsKey(Convert.ToHexString(hashes.AsSpan(i * 32, 32))))
                    result[i] = 1;
            }
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task ReceiveAsync(Stream source, CancellationToken ct)
    {
        var record = new byte[41];
        while (await source.ReadAsync(record.AsMemory(0, 1), ct) != 0)
        {
            await source.ReadExactlyAsync(record.AsMemory(1), ct);
            var key = (ReadUInt32LittleEndian(record), ReadUInt32LittleEndian(record.AsSpan(4)));
            string hash = Convert.ToHexString(record.AsSpan(8, 32));
            byte[]? header = null;
            byte[]? payload = null;
            if (record[40] == 1)
            {
                header = new byte[BlockHeaderSize];
                await source.ReadExactlyAsync(header, ct);
                uint stored = ReadUInt32LittleEndian(header);
                uint raw = ReadUInt32LittleEndian(header.AsSpan(4));
                ushort codec = ReadUInt16LittleEndian(header.AsSpan(8));
                if (stored == 0 || stored > BlockSize || raw == 0 || raw > BlockSize ||
                    codec > 1 || (codec == 0 && stored != raw) ||
                    !header.AsSpan(12, 32).SequenceEqual(record.AsSpan(8, 32)) || hash == ZeroHash)
                    throw new InvalidDataException("Invalid block header.");
                payload = new byte[(int)stored];
                await source.ReadExactlyAsync(payload, ct);
                var crc = new Crc32();
                crc.Append(header.AsSpan(0, 44));
                crc.Append(payload);
                if (crc.GetCurrentHashAsUInt32() != ReadUInt32LittleEndian(header.AsSpan(44)))
                    throw new InvalidDataException("Block CRC mismatch.");
            }
            else if (record[40] != 0)
                throw new InvalidDataException("Invalid clone record marker.");

            await gate.WaitAsync(ct);
            try
            {
                CheckOpen();
                if (header is not null && !known.ContainsKey(hash))
                {
                    long offset = file.Position;
                    try
                    {
                        await file.WriteAsync(header, ct);
                        await file.WriteAsync(payload!, ct);
                    }
                    catch
                    {
                        file.SetLength(offset);
                        file.Position = offset;
                        throw;
                    }
                    known.Add(hash, ((ushort)(chain.Length - 1), offset));
                }
                if (hash != ZeroHash && !known.ContainsKey(hash))
                    throw new InvalidDataException("Block is missing from the chain.");
                if (!placed.TryAdd(key, hash) && placed[key] != hash)
                    throw new InvalidDataException("Two blocks claim the same position.");
            }
            finally { gate.Release(); }
        }
    }

    public async Task<long> FinishAsync(JsonElement manifest, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            CheckOpen();
            var partitions = ReadPartitionSizes(manifest);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            if (manifestBytes.Length > 1 << 20)
                throw new InvalidDataException("Manifest is too large.");
            using var index = new MemoryStream();
            using var writer = new BinaryWriter(index, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(manifestBytes.Length);
            writer.Write(manifestBytes);
            writer.Write((ushort)chain.Length);
            foreach (byte[] id in chain)
                writer.Write(id);
            writer.Write((ulong)placed.Count);
            foreach (var entry in placed.OrderBy(item => item.Key.Partition).ThenBy(item => item.Key.Position))
            {
                var (partition, position) = entry.Key;
                if (!partitions.TryGetValue(partition, out ulong size) || (ulong)position * (uint)BlockSize >= size)
                    throw new InvalidDataException("Block is outside a captured partition.");
                known.TryGetValue(entry.Value, out var place);
                writer.Write(partition);
                writer.Write(position);
                writer.Write(place.Level);
                writer.Write(place.Offset);
                writer.Write(Convert.FromHexString(entry.Value));
            }
            if (index.Length > 128 * 1024 * 1024)
                throw new InvalidDataException("Image index is too large.");
            ReadOnlyMemory<byte> data = index.GetBuffer().AsMemory(0, (int)index.Length);
            byte[] footer = new byte[FooterSize];
            WriteInt64LittleEndian(footer, file.Position);
            WriteInt64LittleEndian(footer.AsSpan(8), index.Length);
            SHA256.HashData(data.Span, footer.AsSpan(16, 32));
            "LDND"u8.CopyTo(footer.AsSpan(48));
            WriteUInt32LittleEndian(footer.AsSpan(52), Crc32.HashToUInt32(footer.AsSpan(0, 52)));
            await file.WriteAsync(data, ct);
            await file.WriteAsync(footer, ct);
            await file.FlushAsync(ct);
            file.Flush(flushToDisk: true);
            long sizeOnDisk = file.Position;
            closed = true;
            await file.DisposeAsync();
            return sizeOnDisk;
        }
        finally { gate.Release(); }
    }

    private static Dictionary<uint, ulong> ReadPartitionSizes(JsonElement manifest)
    {
        uint sector = manifest.GetProperty("sectorSize").GetUInt32();
        ulong disk = manifest.GetProperty("diskSize").GetUInt64();
        if (manifest.GetProperty("version").GetInt32() != 1 || disk == 0 ||
            sector < 512 || (sector & (sector - 1)) != 0)
            throw new InvalidDataException("Invalid disk geometry.");
        _ = manifest.GetProperty("diskGuid").GetGuid();

        var sizes = new Dictionary<uint, ulong>();
        var seen = new HashSet<uint>();
        ulong end = 0;
        int fill = 0;
        foreach (var partition in manifest.GetProperty("partitions").EnumerateArray())
        {
            uint number = partition.GetProperty("number").GetUInt32();
            ulong offset = partition.GetProperty("offset").GetUInt64();
            ulong size = partition.GetProperty("size").GetUInt64();
            string? policy = partition.GetProperty("policy").GetString();
            string? content = partition.GetProperty("content").GetString();
            _ = partition.GetProperty("guid").GetGuid();
            _ = partition.GetProperty("typeGuid").GetGuid();
            if (!seen.Add(number) || offset < end || offset > disk || size == 0 ||
                size > disk - offset || offset % sector != 0 || size % sector != 0 ||
                policy is not ("fixed" or "fill") || content is not ("blocks" or "none") ||
                (policy == "fill" && ++fill > 1))
                throw new InvalidDataException("Invalid partition geometry.");
            end = offset + size;
            if (content == "blocks")
                sizes.Add(number, size);
        }
        if (seen.Count == 0)
            throw new InvalidDataException("The manifest has no partitions.");
        return sizes;
    }

    private void CheckOpen()
    {
        if (closed)
            throw new InvalidOperationException("Image writer is closed.");
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (!closed)
            {
                closed = true;
                await file.DisposeAsync();
            }
        }
        finally { gate.Release(); }
    }
}
