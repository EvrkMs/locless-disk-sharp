using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ZstdSharp;
using static System.Buffers.Binary.BinaryPrimitives;

namespace LoclessDisk.Images;

public enum BlockKind { Unallocated, Zero, Data }

public readonly record struct BlockRead(BlockKind Kind, int Length);

/// <summary>Read-only chain ordered from the full image to the requested version.</summary>
public sealed class ImageChain : IDisposable
{
    private const int HeaderSize = 80;
    private const int FooterSize = 56;
    private const int EntrySize = 50;
    private const int BlockHeaderSize = 48;

    private sealed record Layer(SafeFileHandle Handle, byte[] Header, byte[] Digest,
        long IndexOffset, byte[] Index);
    private sealed record Entry(ushort Level, long Offset, byte[] Hash);

    private readonly List<Layer> layers = [];
    private readonly Dictionary<(uint, uint), Entry> entries = [];
    private readonly Dictionary<uint, ulong> partitionSizes = [];
    private bool disposed;

    private ImageChain() { }

    public int BlockSize { get; private set; }
    public JsonElement Manifest { get; private set; }

    public static async Task<ImageChain> OpenAsync(IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
            throw new ArgumentException("Supply a full image followed by any deltas.", nameof(paths));

        var chain = new ImageChain();
        try
        {
            foreach (string path in paths)
            {
                var layer = await OpenLayerAsync(path, cancellationToken).ConfigureAwait(false);
                chain.layers.Add(layer);
                chain.ValidateLayer(chain.layers.Count - 1);
            }

            chain.ReadIndex(chain.layers[^1].Index);
            return chain;
        }
        catch
        {
            chain.Dispose();
            throw;
        }
    }

    /// <summary>Reads into caller-owned memory. Unallocated blocks leave it untouched.</summary>
    public async ValueTask<BlockRead> ReadBlockAsync(uint partition, uint position,
        Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ulong start = (ulong)position * (uint)BlockSize;
        if (!partitionSizes.TryGetValue(partition, out ulong size) || start >= size)
            throw new ArgumentOutOfRangeException(nameof(position), "Block is outside the partition.");

        int length = (int)Math.Min((ulong)BlockSize, size - start);
        if (!entries.TryGetValue((partition, position), out var entry))
            return new(BlockKind.Unallocated, length);
        if (destination.Length < length)
            throw new ArgumentException("Destination is smaller than the block.", nameof(destination));

        if (entry.Hash.AsSpan().IndexOfAnyExcept((byte)0) < 0)
        {
            destination.Span[..length].Clear();
            return new(BlockKind.Zero, length);
        }

        Layer layer = layers[entry.Level];
        var header = new byte[BlockHeaderSize];
        await ReadExactlyAsync(layer.Handle, header, entry.Offset, cancellationToken).ConfigureAwait(false);
        uint stored = ReadUInt32LittleEndian(header);
        uint raw = ReadUInt32LittleEndian(header.AsSpan(4));
        ushort codec = ReadUInt16LittleEndian(header.AsSpan(8));
        if (raw != length || stored == 0 || stored > BlockSize + BlockSize / 8 + 1024 ||
            stored > layer.IndexOffset - entry.Offset - BlockHeaderSize ||
            codec > 1 || (codec == 0 && stored != raw) ||
            !header.AsSpan(12, 32).SequenceEqual(entry.Hash))
            throw new InvalidDataException("Invalid stored block header.");

        byte[]? compressed = codec == 1 ? new byte[(int)stored] : null;
        Memory<byte> output = destination[..length];
        Memory<byte> payload = compressed is null ? output : compressed;
        await ReadExactlyAsync(layer.Handle, payload, entry.Offset + BlockHeaderSize, cancellationToken)
            .ConfigureAwait(false);

        var crc = new Crc32();
        crc.Append(header.AsSpan(0, 44));
        crc.Append(payload.Span);
        if (crc.GetCurrentHashAsUInt32() != ReadUInt32LittleEndian(header.AsSpan(44)))
            throw new InvalidDataException("Block CRC mismatch.");

        if (compressed is not null)
        {
            using var decoder = new Decompressor();
            if (!decoder.TryUnwrap(compressed, output.Span, out int written) || written != length)
                throw new InvalidDataException("Invalid Zstandard block.");
        }

        if (!SHA256.HashData(output.Span).AsSpan().SequenceEqual(entry.Hash))
            throw new InvalidDataException("Block SHA-256 mismatch.");

        return new(BlockKind.Data, length);
    }

    private static async Task<Layer> OpenLayerAsync(string path, CancellationToken cancellationToken)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            FileOptions.Asynchronous);
        try
        {
            long size = RandomAccess.GetLength(handle);
            if (size < HeaderSize + FooterSize)
                throw new InvalidDataException("Image is truncated.");

            var header = new byte[HeaderSize];
            var footer = new byte[FooterSize];
            await ReadExactlyAsync(handle, header, 0, cancellationToken).ConfigureAwait(false);
            await ReadExactlyAsync(handle, footer, size - FooterSize, cancellationToken).ConfigureAwait(false);
            if (!header.AsSpan(0, 4).SequenceEqual("LDSK"u8) ||
                ReadUInt16LittleEndian(header.AsSpan(4)) != 2 ||
                Crc32.HashToUInt32(header.AsSpan(0, 76)) != ReadUInt32LittleEndian(header.AsSpan(76)))
                throw new InvalidDataException("Invalid or unsupported image header.");

            if (!footer.AsSpan(48, 4).SequenceEqual("LDND"u8) ||
                Crc32.HashToUInt32(footer.AsSpan(0, 52)) != ReadUInt32LittleEndian(footer.AsSpan(52)))
                throw new InvalidDataException("Invalid image footer.");

            long offset = ReadInt64LittleEndian(footer);
            long length = ReadInt64LittleEndian(footer.AsSpan(8));
            if (offset < HeaderSize || offset > size - FooterSize ||
                length != size - FooterSize - offset || length is < 14 or > 128 * 1024 * 1024)
                throw new InvalidDataException("Invalid or unsupported image index size.");

            var index = new byte[(int)length];
            await ReadExactlyAsync(handle, index, offset, cancellationToken).ConfigureAwait(false);
            byte[] digest = SHA256.HashData(index);
            if (!digest.AsSpan().SequenceEqual(footer.AsSpan(16, 32)))
                throw new InvalidDataException("Index SHA-256 mismatch.");

            return new(handle, header, digest, offset, index);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private void ValidateLayer(int level)
    {
        Layer current = layers[level];
        ReadOnlySpan<byte> header = current.Header;
        uint blockSize = ReadUInt32LittleEndian(header[8..]);
        ushort kind = ReadUInt16LittleEndian(header[6..]);
        if (blockSize is < 65536 or > 64 * 1024 * 1024 ||
            (blockSize & (blockSize - 1)) != 0 || header.Slice(12, 16).IndexOfAnyExcept((byte)0) < 0)
            throw new InvalidDataException("Invalid block size or image ID.");

        if (level == 0)
        {
            if (kind != 1 || header.Slice(28, 48).IndexOfAnyExcept((byte)0) >= 0)
                throw new InvalidDataException("Chain must start with a full image.");
            BlockSize = (int)blockSize;
        }
        else
        {
            Layer parent = layers[level - 1];
            if (kind != 2 || blockSize != BlockSize ||
                !header.Slice(28, 16).SequenceEqual(parent.Header.AsSpan(12, 16)) ||
                !header.Slice(44, 32).SequenceEqual(parent.Digest))
                throw new InvalidDataException("Delta does not match its parent.");
        }

        var chain = SplitIndex(current.Index, out _, out _);
        if (chain.Length != (level + 1) * 16)
            throw new InvalidDataException("Image index lists a different number of layers.");
        for (int i = 0; i <= level; i++)
        {
            if (!chain.Slice(i * 16, 16).SequenceEqual(layers[i].Header.AsSpan(12, 16)))
                throw new InvalidDataException("Image index references a different layer.");
        }
    }

    private void ReadIndex(byte[] index)
    {
        SplitIndex(index, out int manifestLength, out var blocks);
        using var document = JsonDocument.Parse(index.AsMemory(4, manifestLength));
        Manifest = document.RootElement.Clone();
        foreach (var partition in Manifest.GetProperty("partitions").EnumerateArray())
            partitionSizes.Add(partition.GetProperty("number").GetUInt32(),
                partition.GetProperty("size").GetUInt64());

        for (; !blocks.IsEmpty; blocks = blocks[EntrySize..])
        {
            uint partition = ReadUInt32LittleEndian(blocks);
            uint position = ReadUInt32LittleEndian(blocks[4..]);
            ushort level = ReadUInt16LittleEndian(blocks[8..]);
            long offset = ReadInt64LittleEndian(blocks[10..]);
            var hash = blocks.Slice(18, 32);
            if (!partitionSizes.TryGetValue(partition, out ulong size) ||
                (ulong)position * (uint)BlockSize >= size ||
                (hash.IndexOfAnyExcept((byte)0) >= 0 &&
                    (level >= layers.Count || offset < HeaderSize ||
                        offset > layers[level].IndexOffset - BlockHeaderSize)) ||
                !entries.TryAdd((partition, position), new(level, offset, hash.ToArray())))
                throw new InvalidDataException("Invalid or duplicate block index entry.");
        }
    }

    private static ReadOnlySpan<byte> SplitIndex(ReadOnlySpan<byte> index,
        out int manifestLength, out ReadOnlySpan<byte> blocks)
    {
        manifestLength = ReadInt32LittleEndian(index);
        if (manifestLength < 0 || manifestLength > Math.Min(1 << 20, index.Length - 14))
            throw new InvalidDataException("Invalid manifest size.");
        var tail = index[(4 + manifestLength)..];
        int chainBytes = ReadUInt16LittleEndian(tail) * 16;
        if (chainBytes > tail.Length - 10)
            throw new InvalidDataException("Truncated layer list.");
        var count = ReadUInt64LittleEndian(tail[(2 + chainBytes)..]);
        blocks = tail[(10 + chainBytes)..];
        if (blocks.Length % EntrySize != 0 || count != (ulong)(blocks.Length / EntrySize))
            throw new InvalidDataException("Invalid block index size.");
        return tail.Slice(2, chainBytes);
    }

    private static async ValueTask ReadExactlyAsync(SafeFileHandle handle, Memory<byte> buffer,
        long offset, CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            int read = await RandomAccess.ReadAsync(handle, buffer, offset, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Image ended before the requested data.");
            offset += read;
            buffer = buffer[read..];
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        foreach (Layer layer in layers)
            layer.Handle.Dispose();
    }
}
