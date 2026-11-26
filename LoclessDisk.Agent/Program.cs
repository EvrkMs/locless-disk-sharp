using System.Net;
using System.Text.Json;

namespace LoclessDisk.Agent;

internal static class Program
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromSeconds(8) };

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The Win32 probe runs on Windows or WinPE.");
            return 1;
        }

        try
        {
            var target = ReadTarget(args);
            return ProbeWindow.Run(target?.AbsoluteUri ?? "Деморежим", target is null ? null : () => CheckAsync(target));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            return ProbeWindow.Run("Ошибка конфигурации", null, error.Message);
        }
    }

    private static Uri? ReadTarget(string[] args)
    {
        if (args is ["--probe", var url])
            return HttpUri(url);
        if (args is ["--demo"])
            return null;
        if (args.Length != 0)
            throw new ArgumentException("Укажите --demo или --probe http://адрес/health.");

        string path = Path.Combine(AppContext.BaseDirectory, "fogs-agent.json");
        if (!File.Exists(path))
            return null;
        using var config = JsonDocument.Parse(File.ReadAllBytes(path));
        if (config.RootElement.ValueKind != JsonValueKind.Object ||
            !config.RootElement.TryGetProperty("server", out var server) || server.ValueKind != JsonValueKind.String ||
            !config.RootElement.TryGetProperty("mac", out var mac) || mac.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(mac.GetString()))
            throw new ArgumentException("В fogs-agent.json нужны адрес сервера и MAC.");

        return new Uri(HttpUri(server.GetString()), "/agent/task?mac=" + Uri.EscapeDataString(mac.GetString()!));
    }

    private static Uri HttpUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is ("http" or "https")
            ? uri : throw new ArgumentException("Нужен абсолютный HTTP(S)-адрес.");

    private static async Task<string> CheckAsync(Uri target)
    {
        try
        {
            using var response = await Client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead);
            return response.StatusCode switch
            {
                HttpStatusCode.NoContent => "HTTP 204 · Sharp доступен, активной задачи нет.",
                HttpStatusCode.OK => "HTTP 200 · Sharp ответил.",
                _ => $"HTTP {(int)response.StatusCode} · Сервер ответил с ошибкой."
            };
        }
        catch (OperationCanceledException)
        {
            return "Sharp не ответил за 8 секунд.";
        }
        catch (HttpRequestException error)
        {
            return "Ошибка HTTP · " + error.GetBaseException().Message;
        }
    }
}
