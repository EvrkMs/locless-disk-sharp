using System.Text.Json;
using LoclessDisk.Core;
using Microsoft.EntityFrameworkCore;

namespace LoclessDisk.Storage;

public sealed class CatalogDb(DbContextOptions<CatalogDb> options) : DbContext(options)
{
    internal DbSet<PxeComputerRow> PxeComputers => Set<PxeComputerRow>();
    internal DbSet<PxeSettingsRow> PxeSettings => Set<PxeSettingsRow>();
    internal DbSet<SettingsRow> Settings => Set<SettingsRow>();
    internal DbSet<VersionRow> Versions => Set<VersionRow>();

    protected override void OnModelCreating(ModelBuilder builder) =>
        builder.Entity<PxeComputerRow>().HasKey(row => row.Mac);
}

internal sealed class SettingsRow
{
    public int Id { get; set; }
    public required string ImageRoot { get; set; }
}

internal sealed class VersionRow
{
    public required string Id { get; set; }
    public required string ImageId { get; set; }
    public required string Name { get; set; }
    public int Depth { get; set; }
    public required string Layers { get; set; }
}

public sealed class SqliteImageCatalog(CatalogDb db) : IImageCatalog
{
    public Task<string?> GetRootAsync(CancellationToken cancellationToken) =>
        db.Settings.Where(row => row.Id == 1).Select(row => row.ImageRoot)
            .SingleOrDefaultAsync(cancellationToken)!;

    public async Task SetRootAsync(string root, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var settings = await db.Settings.FindAsync([1], cancellationToken);
        if (settings is null)
            db.Settings.Add(new SettingsRow { Id = 1, ImageRoot = root });
        else if (!StringComparer.Ordinal.Equals(settings.ImageRoot, root))
        {
            settings.ImageRoot = root;
            await db.Versions.ExecuteDeleteAsync(cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ImageVersion>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Versions.AsNoTracking().OrderBy(row => row.Name)
            .ThenByDescending(row => row.Depth).ThenBy(row => row.Id).ToListAsync(cancellationToken);
        return rows.Select(row => new ImageVersion(row.ImageId, row.Id, row.Name,
            row.Depth, JsonSerializer.Deserialize<string[]>(row.Layers)!)).ToArray();
    }

    public async Task<bool> ReplaceAsync(string root, IReadOnlyList<ImageVersion> versions,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!StringComparer.Ordinal.Equals(root, await GetRootAsync(cancellationToken)))
            return false;

        await db.Versions.ExecuteDeleteAsync(cancellationToken);
        db.Versions.AddRange(versions.Select(version => new VersionRow
        {
            Id = version.VersionId, ImageId = version.ImageId, Name = version.Name,
            Depth = version.Depth, Layers = JsonSerializer.Serialize(version.Paths)
        }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
