using LoclessDisk.Core;
using LoclessDisk.Server.Controllers;
using LoclessDisk.Server.Events;
using LoclessDisk.Server.Imaging;

namespace LoclessDisk.Server.Scenarios;

public sealed class BenchmarkScenario(IConfiguration configuration, IServiceScopeFactory scopes,
    LinkCheckUpdates tasksChanged, DeploySessions deployments, CloneSessions clones, ILogger<BenchmarkScenario> log)
    : BackgroundService
{
    public sealed record Step(string Name, string Kind, string Image, long? Task, string Status);
    public sealed record Run(string Mac, string Host, string Status, Step[] Steps, string? Error = null);

    private readonly SemaphoreSlim gate = new(1);
    private readonly ChangeSignal signal = new();
    private Run[] runs = [];

    public bool Enabled => configuration.GetValue("Dev", false);
    public IAsyncEnumerable<Run[]> Stream(CancellationToken ct) =>
        signal.ReadAsync(_ => Task.FromResult(Volatile.Read(ref runs)), ct);

    public async Task<string?> StartRunAsync(string mac, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (runs.FirstOrDefault()?.Status == "running")
                return "Сценарий уже выполняется.";

            await using var scope = scopes.CreateAsyncScope();
            var computers = scope.ServiceProvider.GetRequiredService<IPxeComputers>();
            var catalog = scope.ServiceProvider.GetRequiredService<IImageCatalog>();
            var tasks = scope.ServiceProvider.GetRequiredService<ILinkChecks>();
            var pc = (await computers.ListAsync(ct)).FirstOrDefault(item => item.Mac == mac && item.Name is not null);
            if (pc?.ImageId is null)
                return "Выберите ПК с назначенным образом.";
            var version = (await catalog.ListAsync(ct)).FirstOrDefault(item => item.ImageId == pc.ImageId && !item.Disabled);
            if (version is null)
                return "У образа нет готовой версии.";
            string? root = await catalog.GetRootAsync(ct);
            if (root is null || !Directory.Exists(root))
                return "Папка образов недоступна.";
            try
            {
                long bytes = version.Paths.Sum(path => new FileInfo(path).Length);
                var drive = new DriveInfo(Path.GetPathRoot(root)!);
                if (drive.AvailableFreeSpace - (5L << 30) < bytes)
                    return "Недостаточно свободного места для полного клона.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                log.LogWarning(error, "Cannot verify benchmark disk space");
                return "Не удалось проверить свободное место для сценария.";
            }

            string name = "benchmark-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var first = await QueueAsync(tasks, mac, AgentTaskKind.Deploy, pc.ImageId, ct);
            if (first is null)
                return "У ПК уже есть активная задача.";
            string displayName = ImagesController.DisplayName(version);
            var run = new Run(mac, pc.Name!, "running", [
                new("Развёртывание", AgentTaskKind.Deploy, displayName, first.Id, "running"),
                new("Дельта-клон", AgentTaskKind.Clone, displayName, null, "waiting"),
                new("Полный клон", AgentTaskKind.Clone, name, null, "waiting")]);
            ImageId = pc.ImageId;
            Publish([run, .. runs.Take(9)]);
            return null;
        }
        finally { gate.Release(); }
    }

    private string? ImageId { get; set; }

    public async Task<bool> StopRunAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (runs.FirstOrDefault() is not { Status: "running" } run)
                return false;
            var steps = run.Steps.ToArray();
            int index = Array.FindIndex(steps, item => item.Status == "running");
            if (steps[index].Task is { } id)
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ILinkChecks>().CancelAsync(id, ct);
                await deployments.AbortAsync(id);
                await clones.AbortAsync(id);
                tasksChanged.Publish();
            }
            steps[index] = steps[index] with { Status = "stopped" };
            Update(run with { Status = "stopped", Steps = steps });
            return true;
        }
        finally { gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await AdvanceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.Data.Common.DbException)
            {
                log.LogWarning(error, "Benchmark scenario could not update");
            }
        }
    }

    private async Task AdvanceAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (runs.FirstOrDefault() is not { Status: "running" } run)
                return;
            var steps = run.Steps.ToArray();
            int index = Array.FindIndex(steps, item => item.Status == "running");
            await using var scope = scopes.CreateAsyncScope();
            var tasks = scope.ServiceProvider.GetRequiredService<ILinkChecks>();
            var current = await tasks.FindAsync(steps[index].Task!.Value, ct);
            if (current is { FinishedAt: null })
                return;
            if (current?.Status != LinkCheckStatus.Succeeded)
            {
                steps[index] = steps[index] with { Status = "failed" };
                Update(run with { Status = "failed", Steps = steps, Error = current?.Message });
                return;
            }
            steps[index] = steps[index] with { Status = "done" };
            if (++index == steps.Length)
            {
                Update(run with { Status = "done", Steps = steps });
                return;
            }

            string? image = index == 1 ? ImageId : "new:" + steps[index].Image;
            var next = await QueueAsync(tasks, run.Mac, steps[index].Kind, image, ct);
            if (next is null)
            {
                steps[index] = steps[index] with { Status = "failed" };
                Update(run with { Status = "failed", Steps = steps, Error = "Не удалось поставить следующую задачу." });
                return;
            }
            steps[index] = steps[index] with { Status = "running", Task = next.Id };
            Update(run with { Steps = steps });
        }
        finally { gate.Release(); }
    }

    private async Task<LinkCheck?> QueueAsync(ILinkChecks tasks, string mac, string kind, string? image,
        CancellationToken ct)
    {
        await ImagesController.ScanGate.WaitAsync(ct);
        try
        {
            var task = await tasks.QueueAsync(mac, kind, image, ct);
            if (task is not null)
                tasksChanged.Publish();
            return task;
        }
        finally { ImagesController.ScanGate.Release(); }
    }

    private void Update(Run run) => Publish([run, .. runs.Skip(1)]);

    private void Publish(Run[] next)
    {
        Volatile.Write(ref runs, next);
        signal.Publish();
    }
}
