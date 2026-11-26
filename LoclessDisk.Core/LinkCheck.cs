namespace LoclessDisk.Core;

public static class LinkCheckStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public static class AgentTaskKind
{
    public const string Check = "agent";
    public const string Deploy = "deploy";
    public const string Clone = "clone";
    public const string Format = "format";
}

public sealed record LinkCheck(long Id, string Mac, string Status, int Percent, string Stage,
    string Message, long CreatedAt, long? FinishedAt, string Kind, string? ImageId);

public interface ILinkChecks
{
    Task<LinkCheck?> QueueAsync(string mac, string kind, string? imageId, CancellationToken ct);
    Task<string?> ReadMetricsAsync(long id, CancellationToken ct);
    Task SaveMetricsAsync(long id, string mac, string metrics, CancellationToken ct);
    Task<bool> UsesImageAsync(string imageId, CancellationToken ct);
    Task<LinkCheck?> FindAsync(long id, CancellationToken ct);
    Task<LinkCheck?> GetActiveAsync(string mac, CancellationToken ct);
    Task<IReadOnlyList<LinkCheck>> ListAsync(CancellationToken ct);
    Task<bool> ReportAsync(long id, string mac, int percent, string stage, string message, CancellationToken ct);
    Task<bool> FinishAsync(long id, string mac, bool ok, string message, CancellationToken ct);
    Task<bool> CancelAsync(long id, CancellationToken ct);
}
