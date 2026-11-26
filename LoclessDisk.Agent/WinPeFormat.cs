using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace LoclessDisk.Agent;

[SupportedOSPlatform("windows")]
internal static partial class WinPeFormat
{
    private const uint StorageQueryProperty = 0x002d1400;
    private const uint DiskCreate = 0x0007c058;
    private const uint UpdateProperties = 0x00070140;
    private const uint DiskGetLayout = 0x00070050;
    private const uint ReadWrite = 0xc0000000;

    public static string SelectDisk()
    {
        using var miniNt = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
        if (miniNt is null || !string.Equals(Environment.GetEnvironmentVariable("SystemDrive"), "X:",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Форматирование допускается только в WinPE.");

        var paths = new List<string>();
        byte[] query = new byte[12];
        byte[] descriptor = new byte[1024];
        for (int index = 0; index < 128; index++)
        {
            string path = $@"\\.\PhysicalDrive{index}";
            using var handle = Open(path, 0, 3, 0, 3, 0, 0);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is 2 or 3 or 21)
                    continue;
                throw new Win32Exception(error, $"Не удалось проверить {path}.");
            }
            if (Control(handle, StorageQueryProperty, query, query.Length, descriptor, descriptor.Length, out int bytes, 0) == 0 ||
                bytes < 32)
                throw new InvalidOperationException($"Не удалось определить тип {path}.");
            uint bus = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(28));
            if (bus is 1 or 3 or 8 or 10 or 11 or 17)
                paths.Add(path);
        }
        return paths.Count == 1 ? paths[0]
            : throw new InvalidOperationException($"Для форматирования нужен один внутренний диск. Найдено: {paths.Count}.");
    }

    public static async Task FormatAsync(string path)
    {
        if (SelectDisk() != path)
            throw new InvalidOperationException("Список дисков изменился после подтверждения.");
        string selection = "select disk " + path[@"\\.\PhysicalDrive".Length..];
        await DiskPartAsync(selection, "clean");
        using (var handle = Open(path, ReadWrite, 3, 0, 3, 0, 0))
        {
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            var gpt = new byte[24];
            BinaryPrimitives.WriteUInt32LittleEndian(gpt, 1);
            Guid.NewGuid().TryWriteBytes(gpt.AsSpan(4, 16));
            BinaryPrimitives.WriteUInt32LittleEndian(gpt.AsSpan(20), 128);
            Invoke(handle, DiskCreate, gpt, []);
            Invoke(handle, UpdateProperties, [], []);
        }
        await DiskPartAsync("rescan", selection, "create partition primary", "format fs=ntfs quick label=WB");
        using var verify = Open(path, 0, 3, 0, 3, 0, 0);
        var layout = new byte[4096];
        Invoke(verify, DiskGetLayout, [], layout);
        if (BinaryPrimitives.ReadUInt32LittleEndian(layout) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(layout.AsSpan(4)) != 1)
            throw new InvalidOperationException("После форматирования ожидалась GPT с одним разделом.");
    }

    private static void Invoke(SafeFileHandle handle, uint code, byte[] input, byte[] output)
    {
        if (Control(handle, code, input, input.Length, output, output.Length, out _, 0) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Операция с диском 0x{code:x} не выполнена.");
    }

    private static async Task DiskPartAsync(params string[] lines)
    {
        string script = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(script, lines);
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "diskpart.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add(script);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("DiskPart не запустился.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"DiskPart завершился с кодом {process.ExitCode}. " +
                    string.Join(" ", (await output + await errors).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3)));
        }
        finally { File.Delete(script); }
    }

    [LibraryImport("kernel32", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle Open(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static partial int Control(SafeFileHandle handle, uint code,
        [MarshalUsing(CountElementName = nameof(inputLength))] byte[] input, int inputLength,
        [Out, MarshalUsing(CountElementName = nameof(outputLength))] byte[] output, int outputLength, out int bytesReturned, nint overlapped);
}
