using LoclessDisk.Core;
using Microsoft.EntityFrameworkCore;

namespace LoclessDisk.Storage;

internal sealed class PxeComputerRow
{
    public required string Mac { get; set; }
    public string? Name { get; set; }
    public long LastSeen { get; set; }
}

public sealed class SqlitePxeComputers(CatalogDb db) : IPxeComputers
{
    public async Task<bool> ObserveAsync(string mac, CancellationToken cancellationToken)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PxeComputers" ("Mac", "LastSeen") VALUES ({mac}, {now})
            ON CONFLICT ("Mac") DO UPDATE SET "LastSeen" = excluded."LastSeen"
                WHERE "PxeComputers"."LastSeen" < excluded."LastSeen"
            """, cancellationToken);
        return await db.PxeComputers.AnyAsync(row => row.Mac == mac && row.Name != null, cancellationToken);
    }

    public async Task<IReadOnlyList<PxeComputer>> ListAsync(CancellationToken cancellationToken) =>
        await db.PxeComputers.OrderByDescending(row => row.LastSeen).ThenBy(row => row.Mac)
            .Select(row => new PxeComputer(row.Mac, row.Name, row.LastSeen)).ToListAsync(cancellationToken);

    public async Task<bool> RegisterAsync(string mac, string name, CancellationToken cancellationToken) =>
        await db.PxeComputers.Where(row => row.Mac == mac && row.Name == null)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Name, name), cancellationToken) == 1;
}
