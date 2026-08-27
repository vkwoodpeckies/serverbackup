using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace BackupManager.Core;
public sealed class ArchiveService
{
    public async Task<BackupResult> CreateAsync(BackupJob job, IProgress<BackupProgress>? progress, CancellationToken token, string? mysqlPassword = null)
    {
        var started = DateTimeOffset.UtcNow; var id = Guid.NewGuid(); var stamp = started.ToString("yyyy-MM-dd_HH-mm-ss");
        var root = Path.Combine(job.DestinationPath, job.Name, started.ToString("yyyy"), started.ToString("MM"), started.ToString("dd"));
        var stage = Path.Combine(root, ".staging", id.ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            progress?.Report(new(job.Id, BackupRunState.BackingUpFiles, "Copying files"));
            foreach (var source in job.Sources) await CopySourceAsync(source, Path.Combine(stage, "files", source.Id.ToString("N")), token);
            if (job.Databases.Count > 0)
            {
                if (job.MySqlConnection is null) throw new InvalidOperationException("MySQL backup sources require a discovered MySQL connection.");
                var databaseDirectory = Path.Combine(stage, "databases"); Directory.CreateDirectory(databaseDirectory);
                var dumper = new MySqlDumpService(); progress?.Report(new(job.Id, BackupRunState.BackingUpDatabase, "Exporting MySQL databases"));
                foreach (var database in job.Databases) await dumper.DumpAsync(job.MySqlConnection, database, Path.Combine(databaseDirectory, database.DatabaseName + ".sql"), mysqlPassword, token);
                if (job.IncludeMySqlUsersAndPrivileges) { progress?.Report(new(job.Id, BackupRunState.BackingUpDatabase, "Exporting MySQL users and privileges")); await dumper.ExportUsersAndPrivilegesAsync(job.MySqlConnection, job.Databases, Path.Combine(databaseDirectory, "users-and-privileges.sql"), mysqlPassword, token); }
            }
            var manifest = new { backupId = id, jobId = job.Id, jobName = job.Name, startedAt = started, computerName = Environment.MachineName, sources = job.Sources.Select(x => x.Path), databases = job.Databases.Select(x => x.DatabaseName), status = "Completed" };
            var manifestJson = JsonSerializer.Serialize(manifest);
            var archives = new List<BackupArchive>();
            if (job.Sources.Count > 0)
            {
                var filesStage = Path.Combine(stage, "files"); await File.WriteAllTextAsync(Path.Combine(filesStage, "manifest.json"), manifestJson, token);
                var filesZip = Path.Combine(root, $"{job.Name}_{stamp}_files.zip"); progress?.Report(new(job.Id, BackupRunState.Compressing, "Creating folders ZIP archive")); ZipFile.CreateFromDirectory(filesStage, filesZip, CompressionLevel.Optimal, false);
                progress?.Report(new(job.Id, BackupRunState.Verifying, "Validating folders ZIP archive")); using (ZipFile.OpenRead(filesZip)) { }
                archives.Add(new BackupArchive("Files", filesZip, await Sha256Async(filesZip, token)));
            }
            if (job.Databases.Count > 0)
            {
                var databaseStage = Path.Combine(stage, "databases"); await File.WriteAllTextAsync(Path.Combine(databaseStage, "manifest.json"), manifestJson, token);
                var databaseZip = Path.Combine(root, $"{job.Name}_{stamp}_mysql.zip"); progress?.Report(new(job.Id, BackupRunState.Compressing, "Creating MySQL ZIP archive")); ZipFile.CreateFromDirectory(databaseStage, databaseZip, CompressionLevel.Optimal, false);
                progress?.Report(new(job.Id, BackupRunState.Verifying, "Validating MySQL ZIP archive")); using (ZipFile.OpenRead(databaseZip)) { }
                archives.Add(new BackupArchive("MySql", databaseZip, await Sha256Async(databaseZip, token)));
            }
            if (archives.Count == 0) throw new InvalidOperationException("Select at least one folder or MySQL database for this backup job.");
            Directory.Delete(stage, true); var primary = archives[0];
            return new(id, BackupRunState.Completed, primary.ArchivePath, primary.Sha256, started, DateTimeOffset.UtcNow, Archives: archives);
        }
        catch { if (Directory.Exists(stage)) Directory.Delete(stage, true); throw; }
    }
    public static async Task<string> Sha256Async(string file, CancellationToken token) { await using var stream = File.OpenRead(file); return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)); }
    private static async Task CopySourceAsync(BackupSource source, string target, CancellationToken token)
    {
        if (!Directory.Exists(source.Path)) throw new DirectoryNotFoundException($"Source folder is unavailable: {source.Path}"); Directory.CreateDirectory(target);
        var exclusions = source.Exclusions ?? []; var option = source.IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var file in Directory.EnumerateFiles(source.Path, "*", option)) { token.ThrowIfCancellationRequested(); if (exclusions.Any(e => file.Contains(e, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file).EndsWith(e.TrimStart('*'), StringComparison.OrdinalIgnoreCase))) continue; var dest = Path.Combine(target, Path.GetRelativePath(source.Path, file)); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); await using var input = File.OpenRead(file); await using var output = File.Create(dest); await input.CopyToAsync(output, token); }
    }
}
