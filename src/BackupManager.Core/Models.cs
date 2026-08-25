namespace BackupManager.Core;

public enum JobState { Enabled, Paused, Disabled }
public enum BackupRunState { Queued, Preparing, BackingUpFiles, BackingUpDatabase, Compressing, Verifying, Uploading, Completed, CompletedWithWarnings, Failed, Cancelled }
public sealed record BackupSource(Guid Id, string Path, bool IncludeSubdirectories = true, IReadOnlyList<string>? Exclusions = null);
public sealed record MySqlSource(Guid Id, string DatabaseName);
public sealed class MySqlConnectionOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 3306;
    public required string UserName { get; init; }
    public string? MySqlExecutable { get; init; }
    public string? MySqlDumpExecutable { get; init; }
}
public sealed record Schedule(string Kind, int? EveryHours = null, DayOfWeek? Day = null, TimeOnly? Time = null);
public sealed class BackupJob { public Guid Id { get; init; } = Guid.NewGuid(); public required string Name { get; init; } public string Description { get; init; } = ""; public JobState State { get; set; } = JobState.Enabled; public required string DestinationPath { get; init; } public Schedule Schedule { get; set; } = new("Manual"); public List<BackupSource> Sources { get; init; } = []; public List<MySqlSource> Databases { get; init; } = []; public MySqlConnectionOptions? MySqlConnection { get; set; } public DateTimeOffset? LastRun { get; set; } public DateTimeOffset? NextRun { get; set; } }
public sealed record BackupProgress(Guid JobId, BackupRunState State, string Message, long ProcessedBytes = 0, long TotalBytes = 0);
public sealed record BackupArchive(string Category, string ArchivePath, string Sha256);
public sealed record BackupResult(Guid BackupId, BackupRunState State, string ArchivePath, string Sha256, DateTimeOffset StartedAt, DateTimeOffset CompletedAt, string? Warning = null, IReadOnlyList<BackupArchive>? Archives = null);
