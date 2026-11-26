using System.Text.Json;
using LoclessDisk.Core;
using Microsoft.EntityFrameworkCore;

namespace LoclessDisk.Storage;

public sealed class CatalogDb(DbContextOptions<CatalogDb> options) : DbContext(options)
{
    internal DbSet<PxeComputerRow> PxeComputers => Set<PxeComputerRow>();
    internal DbSet<PxeSettingsRow> PxeSettings => Set<PxeSettingsRow>();
    internal DbSet<LinkCheckRow> LinkChecks => Set<LinkCheckRow>();
    internal DbSet<SettingsRow> Settings => Set<SettingsRow>();
    internal DbSet<EmptyImageRow> EmptyImages => Set<EmptyImageRow>();
    internal DbSet<RetiredVersionRow> RetiredVersions => Set<RetiredVersionRow>();
    internal DbSet<VersionRow> Versions => Set<VersionRow>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<PxeComputerRow>().HasKey(row => row.Mac);
        builder.Entity<LinkCheckRow>().HasIndex(row => row.Mac).IsUnique().HasFilter("\"FinishedAt\" IS NULL");
    }
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
    public bool Disabled { get; set; }
}

internal sealed class EmptyImageRow
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Directory { get; set; }
}

internal sealed class RetiredVersionRow
{
    public required string Id { get; set; }
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
        else
            settings.ImageRoot = root;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ImageVersion>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Versions.AsNoTracking().OrderBy(row => row.Name)
            .ThenByDescending(row => row.Depth).ThenBy(row => row.Id).ToListAsync(cancellationToken);
        return rows.Select(row => new ImageVersion(row.ImageId, row.Id, row.Name,
            row.Depth, JsonSerializer.Deserialize<string[]>(row.Layers)!, row.Disabled))
            .OrderBy(row => row.Name).ThenByDescending(row => row.Number).ThenBy(row => row.VersionId).ToArray();
    }

    public async Task<IReadOnlyList<EmptyImage>> ListEmptyAsync(CancellationToken ct) =>
        await db.EmptyImages.OrderBy(row => row.Name)
            .Select(row => new EmptyImage(row.Id, row.Name, row.Directory)).ToListAsync(ct);

    public async Task<bool> CreateEmptyAsync(EmptyImage image, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "EmptyImages" ("Id", "Name", "Directory") VALUES ({image.Id}, {image.Name}, {image.Directory})
            ON CONFLICT ("Id") DO NOTHING
            """, ct) == 1;

    public async Task<bool> RemoveEmptyAsync(string id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.EmptyImages.Where(row => row.Id == id).ExecuteDeleteAsync(ct) == 0)
            return false;
        await db.PxeComputers.Where(row => row.ImageId == id)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.ImageId, (string?)null), ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task SelectVersionsAsync(string imageId, IReadOnlyList<string> active, CancellationToken ct)
    {
        string[] ids = active.ToArray();
        await db.Versions.Where(row => row.ImageId == imageId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Disabled, row => !ids.Contains(row.Id)), ct);
    }

    public async Task ReplaceImageAsync(string root, ImageVersion merged, IReadOnlyList<ImageVersion> previous,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!StringComparer.Ordinal.Equals(root, await GetRootAsync(ct)))
            throw new InvalidOperationException("Image root changed during merge.");

        await db.Versions.Where(row => row.ImageId == merged.ImageId).ExecuteDeleteAsync(ct);
        db.RetiredVersions.AddRange(previous.Select(item => new RetiredVersionRow { Id = item.VersionId }));
        db.Versions.Add(new VersionRow
        {
            Id = merged.VersionId, ImageId = merged.ImageId, Name = merged.Name,
            Depth = merged.Depth, Layers = JsonSerializer.Serialize(merged.Paths)
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task RemoveVersionAsync(string versionId, EmptyImage? remainingImage, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Versions.Where(row => row.Id == versionId).ExecuteDeleteAsync(ct);
        if (remainingImage is not null && !await CreateEmptyAsync(remainingImage, ct))
            throw new InvalidOperationException("Image already exists.");
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> AddVersionAsync(string root, ImageVersion version, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!StringComparer.Ordinal.Equals(root, await GetRootAsync(ct)))
            return false;

        string layers = JsonSerializer.Serialize(version.Paths);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Versions" ("Id", "ImageId", "Name", "Depth", "Layers", "Disabled")
            VALUES ({version.VersionId}, {version.ImageId}, {version.Name}, {version.Depth}, {layers}, 0)
            ON CONFLICT ("Id") DO NOTHING
            """, ct);
        await db.EmptyImages.Where(row => row.Id == version.ImageId).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> ReplaceAsync(string root, IReadOnlyList<ImageVersion> versions,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!StringComparer.Ordinal.Equals(root, await GetRootAsync(cancellationToken)))
            return false;

        var previous = await db.Versions.ToDictionaryAsync(row => row.Id, cancellationToken);
        var retired = (await db.RetiredVersions.Select(row => row.Id).ToListAsync(cancellationToken)).ToHashSet();
        var empty = await db.EmptyImages.ToListAsync(cancellationToken);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var group in versions.Where(version => !retired.Contains(version.VersionId)).GroupBy(version => version.ImageId))
        {
            var known = group.Select(version => previous.GetValueOrDefault(version.VersionId)).FirstOrDefault(row => row is not null);
            var folder = Path.GetDirectoryName(group.First().Paths[0]);
            var placeholder = known is null ? empty.FirstOrDefault(row => comparer.Equals(row.Directory, folder)) : null;
            string imageId = known?.ImageId ?? placeholder?.Id ?? group.Key;
            foreach (var version in group)
            {
                string layers = JsonSerializer.Serialize(version.Paths);
                if (previous.TryGetValue(version.VersionId, out var row))
                {
                    row.Layers = layers;
                    continue;
                }
                db.Versions.Add(new VersionRow
                {
                    Id = version.VersionId, ImageId = imageId, Name = placeholder?.Name ?? version.Name,
                    Depth = version.Depth, Layers = layers
                });
            }
            if (placeholder is not null)
            {
                db.EmptyImages.Remove(placeholder);
                empty.Remove(placeholder);
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
