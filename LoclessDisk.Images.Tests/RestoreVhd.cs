#:project ../LoclessDisk.Images/LoclessDisk.Images.csproj

using System.Globalization;
using LoclessDisk.Images;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
using var image = await ImageChain.OpenAsync(args[2..]);
var manifest = image.Manifest;
uint sectorSize = manifest.GetProperty("sectorSize").GetUInt32();
if (sectorSize != 512)
    throw new NotSupportedException("This VHD integration test requires 512-byte sectors.");

long diskSize = manifest.GetProperty("diskSize").GetInt64();
using var disk = File.OpenHandle(args[0], FileMode.CreateNew, FileAccess.Write,
    FileShare.None, FileOptions.Asynchronous);
RandomAccess.SetLength(disk, diskSize);

using var layout = File.CreateText(args[1]);
layout.WriteLine("label: gpt");
layout.WriteLine($"label-id: {manifest.GetProperty("diskGuid").GetString()}");
layout.WriteLine("unit: sectors");
layout.WriteLine();

var buffer = new byte[image.BlockSize];
long windowsSector = -1;
long storedBlocks = 0;
foreach (var partition in manifest.GetProperty("partitions").EnumerateArray())
{
    uint number = partition.GetProperty("number").GetUInt32();
    long offset = partition.GetProperty("offset").GetInt64();
    long length = partition.GetProperty("size").GetInt64();
    var type = partition.GetProperty("typeGuid").GetGuid();
    var id = partition.GetProperty("guid").GetGuid();
    var attributes = partition.TryGetProperty("attributes", out var flags) ? flags.GetUInt64() : 0;
    string[] attributeNames = ["RequiredPartition", "NoBlockIOProtocol", "LegacyBIOSBootable"];
    string attributeList = string.Join(' ', Enumerable.Range(0, 64)
        .Where(bit => (attributes & (1UL << bit)) != 0)
        .Select(bit => bit < attributeNames.Length ? attributeNames[bit] : $"GUID:{bit}"));
    layout.Write($"{args[0]}{number} : start={offset / sectorSize}, size={length / sectorSize}, type={type}, uuid={id}");
    if (attributeList.Length > 0)
        layout.Write($", attrs=\"{attributeList}\"");
    layout.WriteLine();

    if (partition.GetProperty("policy").GetString() == "fill")
        windowsSector = offset / sectorSize;
    if (partition.GetProperty("content").GetString() != "blocks")
        continue;

    for (uint position = 0; (long)position * buffer.Length < length; position++)
    {
        var block = await image.ReadBlockAsync(number, position, buffer);
        // A newly created sparse disk already reads as zero in unwritten sectors.
        if (block.Kind == BlockKind.Data)
        {
            await RandomAccess.WriteAsync(disk, buffer.AsMemory(0, block.Length),
                offset + (long)position * buffer.Length);
            storedBlocks++;
        }
    }
}

if (windowsSector < 0 || storedBlocks == 0)
    throw new InvalidDataException("Expected a Windows fill partition and stored blocks.");
Console.Error.WriteLine($"Restored {storedBlocks} data blocks.");
Console.WriteLine(windowsSector);
