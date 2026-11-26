using LoclessDisk.Core;
using Microsoft.EntityFrameworkCore;

namespace LoclessDisk.Storage;

internal sealed class PxeSettingsRow
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string? BindAddress { get; set; }
    public string? BootScriptUrl { get; set; }
}

public sealed class SqlitePxeSettingsStore(CatalogDb db) : IPxeSettingsStore
{
    public Task<PxeSettings?> ReadAsync(CancellationToken cancellationToken) =>
        db.PxeSettings.Where(row => row.Id == 1)
            .Select(row => new PxeSettings(row.Enabled, row.BindAddress, row.BootScriptUrl))
            .SingleOrDefaultAsync(cancellationToken);

    public Task SaveAsync(PxeSettings settings, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PxeSettings" ("Id", "Enabled", "BindAddress", "BootScriptUrl")
            VALUES (1, {settings.Enabled}, {settings.BindAddress}, {settings.BootScriptUrl})
            ON CONFLICT ("Id") DO UPDATE SET
                "Enabled" = excluded."Enabled", "BindAddress" = excluded."BindAddress", "BootScriptUrl" = excluded."BootScriptUrl"
            """, cancellationToken);
}
