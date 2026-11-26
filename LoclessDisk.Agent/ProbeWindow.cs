using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LoclessDisk.Agent;

internal static partial class ProbeWindow
{
    private const uint Destroy = 0x0002, Command = 0x0111, SetFont = 0x0030, ProbeFinished = 0x8001;
    private const int CheckButton = 1, CloseButton = 2;
    private static nint status, check;
    private static Func<Task<string>>? probe;
    private static string? result;

    public static unsafe int Run(string target, Func<Task<string>>? runProbe, string? error = null)
    {
        probe = runProbe;
        nint instance = GetModuleHandle(null);
        const string windowClass = "LoclessDiskAgentProbe";
        fixed (char* name = windowClass)
        {
            var windowClassData = new WindowClass
            {
                Instance = instance,
                Procedure = &HandleMessage,
                Name = name,
                Background = 16,
                Cursor = LoadCursor(0, (nint)32512)
            };
            if (RegisterClass(ref windowClassData) == 0)
                return StartupFailed();
        }

        const int width = 610, height = 265;
        nint hwnd = CreateWindow(0, windowClass, "Locless Disk · Агент WinPE", 0x00CA0000,
            Math.Max(0, (GetSystemMetrics(0) - width) / 2), Math.Max(0, (GetSystemMetrics(1) - height) / 2),
            width, height, 0, 0, instance, 0);
        if (hwnd == 0)
            return StartupFailed();

        if (Control("STATIC", "LOCLESS DISK   /   ПРОВЕРКА WINPE", 0, 22, 20, 550, 20, 0) == 0 ||
            Control("STATIC", "Окно Win32 запустилось.", 0, 22, 54, 550, 22, 0) == 0 ||
            Control("STATIC", target, 0, 22, 82, 550, 34, 0) == 0)
            return StartupFailed();
        status = Control("STATIC", error ?? "Деморежим. Добавьте fogs-agent.json для проверки сети.",
            0, 22, 120, 550, 48, 0);
        check = Control("BUTTON", "Проверить снова", 0x00010000, 22, 178, 155, 30, CheckButton);
        if (status == 0 || check == 0 ||
            Control("BUTTON", "Закрыть", 0x00010000, 190, 178, 96, 30, CloseButton) == 0)
            return StartupFailed();
        EnableWindow(check, probe is null ? 0 : 1);
        ShowWindow(hwnd, 5);
        if (probe is not null)
            StartProbe(hwnd);

        int read;
        while ((read = GetMessage(out var message, 0, 0, 0)) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        return read < 0 ? StartupFailed() : 0;

        nint Control(string type, string text, uint style, int x, int y, int w, int h, int id)
        {
            nint child = CreateWindow(0, type, text, style | 0x50000000, x, y, w, h, hwnd, id, instance, 0);
            if (child != 0)
                SendMessage(child, SetFont, GetStockObject(17), 1);
            return child;
        }
    }

    private static int StartupFailed()
    {
        MessageBox(0, "Не удалось создать окно Win32.", "Locless Disk", 0x10);
        return 1;
    }

    private static void StartProbe(nint hwnd)
    {
        if (probe is not { } execute)
            return;
        EnableWindow(check, 0);
        SetWindowText(status, "Проверяем HTTP-соединение с Sharp…");
        _ = Task.Run(async () =>
        {
            string message;
            try { message = await execute(); }
            catch (Exception error) { message = error.GetBaseException().Message; }
            Volatile.Write(ref result, message);
            PostMessage(hwnd, ProbeFinished, 0, 0);
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint HandleMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == Destroy)
        {
            PostQuitMessage(0);
            return 0;
        }
        if (message == ProbeFinished)
        {
            SetWindowText(status, Volatile.Read(ref result) ?? "");
            EnableWindow(check, 1);
            return 0;
        }
        if (message == Command)
        {
            switch ((int)(wParam & 0xffff))
            {
                case CheckButton:
                    StartProbe(hwnd);
                    return 0;
                case CloseButton:
                    DestroyWindow(hwnd);
                    return 0;
            }
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WindowClass
    {
        public uint Style;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public char* Menu, Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nint WParam, LParam;
        public uint Timestamp;
        public int X, Y;
        public uint Private;
    }

    [LibraryImport("kernel32", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? name);
    [LibraryImport("user32", EntryPoint = "RegisterClassW")]
    private static partial ushort RegisterClass(ref WindowClass windowClass);
    [LibraryImport("user32", EntryPoint = "LoadCursorW")]
    private static partial nint LoadCursor(nint instance, nint name);
    [LibraryImport("user32")]
    private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindow(uint exStyle, string className, string text, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32")]
    private static partial int ShowWindow(nint window, int mode);
    [LibraryImport("user32")]
    private static partial int EnableWindow(nint window, int enable);
    [LibraryImport("user32", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowText(nint window, string text);
    [LibraryImport("gdi32")]
    private static partial nint GetStockObject(int index);
    [LibraryImport("user32", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [LibraryImport("user32", EntryPoint = "PostMessageW")]
    private static partial int PostMessage(nint window, uint message, nint wParam, nint lParam);
    [LibraryImport("user32", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Message message, nint window, uint min, uint max);
    [LibraryImport("user32")]
    private static partial int TranslateMessage(ref Message message);
    [LibraryImport("user32", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(ref Message message);
    [LibraryImport("user32", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [LibraryImport("user32")]
    private static partial int DestroyWindow(nint window);
    [LibraryImport("user32")]
    private static partial void PostQuitMessage(int code);
    [LibraryImport("user32", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint window, string text, string caption, uint type);
}
