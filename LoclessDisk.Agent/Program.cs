using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

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
            return ProbeWindow.Run(target?.Url.AbsoluteUri ?? "Деморежим", target is null ? null : () => CheckAsync(target));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            return ProbeWindow.Run("Ошибка конфигурации", null, error.Message);
        }
    }

    private sealed record ProbeTarget(Uri Url, string? Mac = null);

    private static ProbeTarget? ReadTarget(string[] args)
    {
        if (args is ["--probe", var url])
            return new ProbeTarget(HttpUri(url));
        if (args is ["--demo"])
            return null;
        bool pxe = args is ["--pxe"];
        if (args.Length != 0 && !pxe)
            throw new ArgumentException("Укажите --demo или --probe http://адрес/health.");

        string path = Path.Combine(AppContext.BaseDirectory, "fogs-agent.json");
        if (!pxe && !File.Exists(path))
            return null;
        using var config = JsonDocument.Parse(File.ReadAllBytes(path));
        if (config.RootElement.ValueKind != JsonValueKind.Object ||
            !config.RootElement.TryGetProperty("server", out var server) || server.ValueKind != JsonValueKind.String ||
            !config.RootElement.TryGetProperty("mac", out var mac) || mac.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(mac.GetString()))
            throw new ArgumentException("В fogs-agent.json нужны адрес сервера и MAC.");

        string macAddress = mac.GetString()!;
        return new ProbeTarget(new Uri(HttpUri(server.GetString()), "/agent/task?mac=" + Uri.EscapeDataString(macAddress)),
            pxe ? macAddress : null);
    }

    private static Uri HttpUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is ("http" or "https")
            ? uri : throw new ArgumentException("Нужен абсолютный HTTP(S)-адрес.");

    private static async Task<string> CheckAsync(ProbeTarget target)
    {
        try
        {
            using var response = await Client.GetAsync(target.Url);
            if (target.Mac is not null && response.StatusCode == HttpStatusCode.OK)
                return await FinishProbeAsync(target, response);
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
        catch (JsonException)
        {
            return "Sharp вернул некорректные данные о задаче.";
        }
        catch (HttpRequestException error)
        {
            return "Ошибка HTTP · " + error.GetBaseException().Message;
        }
    }

    private static async Task<string> FinishProbeAsync(ProbeTarget target, HttpResponseMessage response)
    {
        using var task = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        if (task.RootElement.ValueKind != JsonValueKind.Object ||
            !task.RootElement.TryGetProperty("kind", out var kind) ||
            kind.ValueKind != JsonValueKind.String || kind.GetString() != "agent-csharp" ||
            !task.RootElement.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out long taskId) || taskId <= 0)
            return "HTTP 200 · Sharp ответил, задачи проверки C#-агента нет.";

        var url = new Uri(target.Url, $"/agent/tasks/{taskId}/finish");
        var report = new ProbeReport(target.Mac!, true, "C#-агент запустился в WinPE и подключился к Sharp.");
        using var body = JsonContent.Create(report, ProbeJsonContext.Default.ProbeReport);
        using var result = await Client.PostAsync(url, body);
        return result.StatusCode == HttpStatusCode.NoContent
            ? "HTTP 200 · Связь проверена. Sharp принял результат."
            : $"HTTP {(int)result.StatusCode} · Sharp ответил, но результат не принят.";
    }
}

internal sealed record ProbeReport(
    [property: JsonPropertyName("mac")] string Mac,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message);

[JsonSerializable(typeof(ProbeReport))]
internal sealed partial class ProbeJsonContext : JsonSerializerContext;
