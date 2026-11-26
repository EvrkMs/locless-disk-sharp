using System.Buffers.Binary;
using System.Text.Json;
using LoclessDisk.Core;
using Microsoft.AspNetCore.Http.Features;

namespace LoclessDisk.Server.Boot.GoAgent;

internal sealed partial class GoAgentEndpoints
{
    private void MapImageRoutes(WebApplication app)
    {
        app.MapPost("/agent/table", async (string? mac, HttpContext context, CancellationToken ct) =>
        {
            if (!TryMac(mac, out string key, out _))
                return Results.BadRequest("Invalid MAC address.");
            byte[] table;
            try { table = await ReadBodyAsync(context, 64 * 1024, ct); }
            catch (InvalidDataException error) { return Results.BadRequest(error.Message); }
            await using var scope = scopes.CreateAsyncScope();
            var hosts = scope.ServiceProvider.GetRequiredService<IPxeComputers>();
            return await hosts.SaveRecoveryTableAsync(key, table.Length == 0 ? null : table, ct)
                ? Results.NoContent() : Results.NotFound();
        });

        var deploy = app.MapGroup("/agent/tasks/{id:long}/deploy");
        deploy.MapPost("/start", (long id, string? mac, HttpContext ctx, CancellationToken ct) =>
            WithImageTask(id, mac, AgentTaskKind.Deploy, ctx, async (task, catalog) =>
            {
                var version = (await catalog.ListAsync(ct)).FirstOrDefault(item => item.ImageId == task.ImageId && !item.Disabled)
                    ?? throw new InvalidOperationException("Assigned image is unavailable.");
                var result = await deployments.StartAsync(id, version, ct);
                if (!await IsOpenAsync(task, ct))
                {
                    await deployments.AbortAsync(id);
                    return Results.StatusCode(StatusCodes.Status410Gone);
                }
                return Results.Json(result);
            }, ct));
        deploy.MapPost("/list", (long id, string? mac, HttpContext ctx, CancellationToken ct) =>
            WithImageTask(id, mac, AgentTaskKind.Deploy, ctx, async (_, _) =>
            {
                ctx.Response.ContentType = "application/octet-stream";
                if (!await deployments.ListAsync(id, ctx.Response.Body, ct))
                    return Results.Conflict("No active deploy.");
                return Results.Empty;
            }, ct));
        deploy.MapPost("/blocks", (long id, string? mac, HttpContext ctx, CancellationToken ct) =>
            WithImageTask(id, mac, AgentTaskKind.Deploy, ctx, async (_, _) =>
            {
                byte[] body = await ReadBodyAsync(ctx, 1 << 20, ct);
                if (body.Length % 4 != 0)
                    return Results.BadRequest("Truncated block number list.");
                var numbers = new uint[body.Length / 4];
                for (int i = 0; i < numbers.Length; i++)
                    numbers[i] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i * 4));
                ctx.Response.ContentType = "application/octet-stream";
                if (!await deployments.BlocksAsync(id, numbers, ctx.Response.Body, ct))
                    return Results.Conflict("No active deploy.");
                return Results.Empty;
            }, ct));
        deploy.MapPost("/abort", (long id, string? mac, HttpContext ctx, CancellationToken ct) =>
            WithImageTask(id, mac, AgentTaskKind.Deploy, ctx, async (_, _) =>
            {
                await deployments.AbortAsync(id);
                return Results.NoContent();
            }, ct));

        var clone = app.MapGroup("/agent/tasks/{id:long}/clone");
        clone.MapPost("/claim", (long id, string? mac, HttpContext ctx, CancellationToken ct) =>
            WithImageTask(id, mac, AgentTaskKind.Clone, ctx, async (task, catalog) =>
            {
                if (!await clones.ClaimAsync(task, catalog, ct))
                    return Results.StatusCode(StatusCodes.Status423Locked);
                if (!await IsOpenAsync(task, ct))
                {
                    await clones.AbortAsync(id);
                    return Results.StatusCode(StatusCodes.Status410Gone);
                }
                return Results.NoContent();
            }, ct));
        clone.MapPost("/start", (long id, string? mac, HttpContext ctx, CancellationToken ct) =>
            WithImageTask(id, mac, AgentTaskKind.Clone, ctx, async (_, _) =>
                Results.Json(await clones.StartAsync(id, ct)), ct));
        clone.MapPost("/check", (long id, string? mac, HttpContext ctx, CancellationToken ct) =>
            WithImageTask(id, mac, AgentTaskKind.Clone, ctx, async (_, _) =>
                Results.Bytes(await clones.CheckAsync(id, await ReadBodyAsync(ctx, 1 << 20, ct), ct),
                    "application/octet-stream"), ct));
        clone.MapPost("/put", (long id, string? mac, HttpContext ctx, CancellationToken ct) =>
            WithImageTask(id, mac, AgentTaskKind.Clone, ctx, async (_, _) =>
            {
                LimitBody(ctx, 512L << 20);
                await clones.PutAsync(id, ctx.Request.Body, ct);
                return Results.NoContent();
            }, ct));
        clone.MapPost("/finish", (long id, string? mac, HttpContext ctx, CancellationToken ct) =>
            WithImageTask(id, mac, AgentTaskKind.Clone, ctx, async (_, catalog) =>
            {
                byte[] body = await ReadBodyAsync(ctx, 1 << 20, ct);
                using var manifest = JsonDocument.Parse(body);
                return Results.Json(await clones.FinishAsync(id, manifest.RootElement, catalog, ct));
            }, ct));
        clone.MapPost("/abort", (long id, string? mac, HttpContext ctx, CancellationToken ct) =>
            WithImageTask(id, mac, AgentTaskKind.Clone, ctx, async (_, _) =>
            {
                await clones.AbortAsync(id);
                return Results.NoContent();
            }, ct));
    }

    private async Task<bool> IsOpenAsync(LinkCheck task, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ILinkChecks>()
            .GetActiveAsync(task.Mac, ct))?.Id == task.Id;
    }

    private async Task<IResult> WithImageTask(long id, string? mac, string kind, HttpContext context,
        Func<LinkCheck, IImageCatalog, Task<IResult>> operation, CancellationToken ct)
    {
        if (!TryMac(mac, out string key, out _))
            return Results.BadRequest("Invalid MAC address.");
        await using var scope = scopes.CreateAsyncScope();
        var task = await scope.ServiceProvider.GetRequiredService<ILinkChecks>().GetActiveAsync(key, ct);
        if (task is null || task.Id != id || task.Kind != kind)
            return Results.StatusCode(StatusCodes.Status410Gone);
        try
        {
            return await operation(task, scope.ServiceProvider.GetRequiredService<IImageCatalog>());
        }
        catch (Exception error) when (ImageScanner.IsImageError(error))
        {
            log.LogWarning(error, "Image operation {Path} failed for task {Task}", context.Request.Path, id);
            if (context.Response.HasStarted)
            {
                context.Abort();
                return Results.Empty;
            }
            return error is InvalidOperationException && error is not ObjectDisposedException
                ? Results.Conflict(error.Message) : Results.BadRequest(error.Message);
        }
    }

    private static void LimitBody(HttpContext context, long size)
    {
        if (context.Request.ContentLength > size)
            throw new InvalidDataException("Request body is too large.");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limits)
            limits.MaxRequestBodySize = size;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext context, int size, CancellationToken ct)
    {
        LimitBody(context, size);
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }
}
