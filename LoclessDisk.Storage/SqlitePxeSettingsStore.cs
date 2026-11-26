using LoclessDisk.Core;
using Microsoft.EntityFrameworkCore;

namespace LoclessDisk.Storage;

internal sealed class PxeSettingsRow
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string? BindAddress { get; set; }
    public string? BootScriptUrl { get; set; }
    public string? ExitMode { get; set; }
}

public sealed class SqlitePxeSettingsStore(CatalogDb db) : IPxeSettingsStore
{
    public Task<PxeSettings?> ReadAsync(CancellationToken cancellationToken) =>
        db.PxeSettings.Where(row => row.Id == 1)
            .Select(row => new PxeSettings(row.Enabled, row.BindAddress, row.BootScriptUrl, row.ExitMode))
            .SingleOrDefaultAsync(cancellationToken);

    public Task SaveAsync(PxeSettings settings, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PxeSettings" ("Id", "Enabled", "BindAddress", "BootScriptUrl", "ExitMode")
            VALUES (1, {settings.Enabled}, {settings.BindAddress}, {settings.BootScriptUrl}, {settings.ExitMode})
            ON CONFLICT ("Id") DO UPDATE SET
                "Enabled" = excluded."Enabled", "BindAddress" = excluded."BindAddress", "BootScriptUrl" = excluded."BootScriptUrl", "ExitMode" = excluded."ExitMode"
            """, cancellationToken);
}
