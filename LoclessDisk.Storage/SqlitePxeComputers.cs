using LoclessDisk.Core;
using Microsoft.EntityFrameworkCore;

namespace LoclessDisk.Storage;

internal sealed class PxeComputerRow
{
    public required string Mac { get; set; }
    public string? Name { get; set; }
    public string? ImageId { get; set; }
    public long LastSeen { get; set; }
}

public sealed class SqlitePxeComputers(CatalogDb db) : IPxeComputers
{
    public async Task<PxeObservation> ObserveAsync(string mac, CancellationToken cancellationToken)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PxeComputers" ("Mac", "LastSeen") VALUES ({mac}, {now})
            ON CONFLICT ("Mac") DO UPDATE SET "LastSeen" = excluded."LastSeen"
                WHERE "PxeComputers"."LastSeen" < excluded."LastSeen"
            """, cancellationToken);
        bool registered = await db.PxeComputers.AnyAsync(row => row.Mac == mac && row.Name != null, cancellationToken);
        return new PxeObservation(registered, updated > 0);
    }

    public Task<string?> GetRegisteredNameAsync(string mac, CancellationToken cancellationToken) =>
        db.PxeComputers.Where(row => row.Mac == mac).Select(row => row.Name)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<PxeComputer>> ListAsync(CancellationToken cancellationToken) =>
        await db.PxeComputers.OrderByDescending(row => row.LastSeen).ThenBy(row => row.Mac)
            .Select(row => new PxeComputer(row.Mac, row.Name, row.LastSeen, row.ImageId)).ToListAsync(cancellationToken);

    public async Task<ComputerSaveResult> SaveAsync(string mac, string name, string? imageId, bool create,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (imageId is not null && !await db.Versions.AnyAsync(row => row.ImageId == imageId, ct) &&
            !await db.PxeComputers.AnyAsync(row => row.Mac == mac && row.ImageId == imageId, ct))
            return ComputerSaveResult.ImageNotFound;

        int updated;
        if (create)
        {
            updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "PxeComputers" ("Mac", "Name", "ImageId", "LastSeen")
                VALUES ({mac}, {name}, {imageId}, 0)
                ON CONFLICT ("Mac") DO UPDATE SET "Name" = excluded."Name", "ImageId" = excluded."ImageId"
                WHERE "PxeComputers"."Name" IS NULL
                """, ct);
        }
        else
        {
            updated = await db.PxeComputers.Where(row => row.Mac == mac && row.Name != null)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Name, name)
                    .SetProperty(row => row.ImageId, imageId), ct);
        }

        await transaction.CommitAsync(ct);
        return updated == 1 ? ComputerSaveResult.Saved : ComputerSaveResult.Conflict;
    }

    public async Task<bool> RegisterAsync(string mac, string name, CancellationToken cancellationToken) =>
        await db.PxeComputers.Where(row => row.Mac == mac && row.Name == null)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Name, name), cancellationToken) == 1;
}
