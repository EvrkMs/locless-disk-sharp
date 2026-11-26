using LoclessDisk.Core;
using Microsoft.EntityFrameworkCore;

namespace LoclessDisk.Storage;

internal sealed class PxeSettingsRow
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string? BindAddress { get; set; }
}

public sealed class SqlitePxeSettingsStore(CatalogDb db) : IPxeSettingsStore
{
    public Task<PxeSettings?> ReadAsync(CancellationToken cancellationToken) =>
        db.PxeSettings.Where(row => row.Id == 1)
            .Select(row => new PxeSettings(row.Enabled, row.BindAddress))
            .SingleOrDefaultAsync(cancellationToken);

    public Task SaveAsync(PxeSettings settings, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PxeSettings" ("Id", "Enabled", "BindAddress")
            VALUES (1, {settings.Enabled}, {settings.BindAddress})
            ON CONFLICT ("Id") DO UPDATE SET
                "Enabled" = excluded."Enabled", "BindAddress" = excluded."BindAddress"
            """, cancellationToken);
}
