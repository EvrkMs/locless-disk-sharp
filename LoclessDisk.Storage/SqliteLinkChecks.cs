using LoclessDisk.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LoclessDisk.Storage;

internal sealed class LinkCheckRow
{
    public long Id { get; set; }
    public required string Mac { get; set; }
    public string Status { get; set; } = LinkCheckStatus.Queued;
    public int Percent { get; set; }
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public long? FinishedAt { get; set; }

    public LinkCheck ToModel() => new(Id, Mac, Status, Percent, Stage, Message, CreatedAt, FinishedAt);
}

public sealed class SqliteLinkChecks(CatalogDb db) : ILinkChecks
{
    public async Task<LinkCheck?> QueueAsync(string mac, CancellationToken ct)
    {
        if (!await db.PxeComputers.AnyAsync(row => row.Mac == mac && row.Name != null, ct))
            return null;

        var task = new LinkCheckRow { Mac = mac };
        db.LinkChecks.Add(task);
        try
        {
            await db.SaveChangesAsync(ct);
            return task.ToModel();
        }
        catch (DbUpdateException error) when (error.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            db.Entry(task).State = EntityState.Detached;
            return null;
        }
    }

    public async Task<LinkCheck?> GetActiveAsync(string mac, CancellationToken ct) =>
        (await db.LinkChecks.AsNoTracking().SingleOrDefaultAsync(row => row.Mac == mac && row.FinishedAt == null, ct))
            ?.ToModel();

    public async Task<IReadOnlyList<LinkCheck>> ListAsync(CancellationToken ct) =>
        (await db.LinkChecks.AsNoTracking().OrderBy(row => row.FinishedAt != null)
            .ThenByDescending(row => row.Id).Take(100).ToListAsync(ct)).Select(row => row.ToModel()).ToArray();

    public async Task<bool> ReportAsync(long id, string mac, int percent, string stage, string message,
        CancellationToken ct) =>
        await Active(id, mac).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.Status, LinkCheckStatus.Running)
            .SetProperty(row => row.Percent, percent)
            .SetProperty(row => row.Stage, stage)
            .SetProperty(row => row.Message, message), ct) != 0;

    public async Task<bool> FinishAsync(long id, string mac, bool ok, string message, CancellationToken ct)
    {
        string status = ok ? LinkCheckStatus.Succeeded : LinkCheckStatus.Failed;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int updated = await Active(id, mac).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.FinishedAt, now)
            .SetProperty(row => row.Status, status)
            .SetProperty(row => row.Percent, row => ok ? 100 : row.Percent)
            .SetProperty(row => row.Message, message), ct);
        return updated != 0 || await db.LinkChecks.AnyAsync(row =>
            row.Id == id && row.Mac == mac && row.Status == status, ct);
    }

    public async Task<bool> CancelAsync(long id, CancellationToken ct)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return await db.LinkChecks.Where(row => row.Id == id && row.FinishedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, LinkCheckStatus.Cancelled)
                .SetProperty(row => row.FinishedAt, now), ct) != 0;
    }

    private IQueryable<LinkCheckRow> Active(long id, string mac) =>
        db.LinkChecks.Where(row => row.Id == id && row.Mac == mac && row.FinishedAt == null);
}
