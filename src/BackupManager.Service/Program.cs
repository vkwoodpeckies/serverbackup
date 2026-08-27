using BackupManager.Core;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

Host.CreateDefaultBuilder(args).UseWindowsService(options => options.ServiceName = "Backup Manager Service").ConfigureServices(services => services.AddHostedService<SchedulerWorker>()).Build().Run();
sealed class SchedulerWorker(ILogger<SchedulerWorker> log) : BackgroundService
{
    private static readonly string DataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupManager");
    private static readonly string ConfigPath = Path.Combine(DataPath, "desktop.json");
    private readonly ArchiveService _archive = new();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Backup Manager Service started.");
        while (!stoppingToken.IsCancellationRequested) { try { await RunDueJobsAsync(stoppingToken); } catch (Exception ex) { log.LogError(ex, "Scheduled backup cycle failed."); } await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
    }
    private async Task RunDueJobsAsync(CancellationToken token)
    {
        if (!File.Exists(ConfigPath)) return;
        var config = JsonSerializer.Deserialize<ServiceConfig>(await File.ReadAllTextAsync(ConfigPath, token)); if (config is null || config.Paused) return;
        var now = DateTimeOffset.Now;
        foreach (var job in config.Jobs.Where(j => j.State == JobState.Enabled && j.Schedule.Kind != "Manual"))
        {
            if (job.NextRun is null) { job.NextRun = ScheduleCalculator.Next(job.Schedule, now); continue; }
            if (job.NextRun > now) continue;
            try { var result = await _archive.CreateAsync(job, null, token, Unprotect(config.EncryptedMySqlPassword)); var remote = await UploadAsync(config.RemoteFtp, result, job.Name, token); job.LastRun = result.CompletedAt; job.NextRun = ScheduleCalculator.Next(job.Schedule, result.CompletedAt); config.LastBackup = result.CompletedAt; config.History.Add(new ServiceRunRecord(job.Name, result.CompletedAt, "Completed", result.ArchivePath, result.Sha256, remote)); DeleteLocalArchives(result); log.LogInformation("Scheduled backup {Job} completed and uploaded.", job.Name); }
            catch (Exception ex) { job.NextRun = ScheduleCalculator.Next(job.Schedule, now); config.History.Add(new ServiceRunRecord(job.Name, now, "CompletedWithWarnings", "", "", null, ex.Message)); log.LogError(ex, "Scheduled backup {Job} failed.", job.Name); }
        }
        await File.WriteAllTextAsync(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }), token);
    }
    private static async Task<string?> UploadAsync(ServiceFtpConfig? ftp, BackupResult result, string jobName, CancellationToken token)
    {
        if (ftp is null || string.IsNullOrWhiteSpace(ftp.Host) || string.IsNullOrWhiteSpace(ftp.EncryptedPassword)) throw new InvalidOperationException("Remote FTP settings are not saved.");
        var password = Unprotect(ftp.EncryptedPassword) ?? throw new InvalidOperationException("Remote FTP password cannot be decrypted by the service. Save it again in Settings."); var uploaded = new List<string>();
        var records = new List<ServiceRemoteRecord>(); foreach (var archive in result.Archives ?? [new BackupArchive("Files", result.ArchivePath, result.Sha256)]) { var stamp = result.StartedAt.ToLocalTime().ToString("dd_MMM_yyyy_hh_mm_tt").ToLowerInvariant(); var category = archive.Category.Equals("mysql", StringComparison.OrdinalIgnoreCase) ? "mysql" : "files"; var remote = $"{ftp.RemoteFolder.TrimEnd('/')}/{category}/backup_{category}_{stamp}.zip"; await CurlAsync(ftp, password, ["--upload-file", archive.ArchivePath, Uri(ftp, remote).ToString()], token); uploaded.Add(remote); records.Add(new ServiceRemoteRecord(Path.GetFileName(remote), category, result.StartedAt, archive.Sha256, jobName)); }
        await UpdateRemoteIndexAsync(ftp, password, records, token);
        return string.Join(";", uploaded);
    }
    private static async Task UpdateRemoteIndexAsync(ServiceFtpConfig ftp, string password, IReadOnlyList<ServiceRemoteRecord> additions, CancellationToken token)
    {
        var options = new RemoteFtpOptions(ftp.Host, ftp.Port, ftp.UserName, ftp.UseFtps, ftp.TrustInvalidCertificate); var indexPath = ftp.RemoteFolder.TrimEnd('/') + "/backup-index.json"; var temp = Path.GetTempFileName(); var entries = new List<ServiceRemoteRecord>();
        try { var downloaded = await new FtpTransferService().ExecuteAsync(options, password, ["--output", temp, Uri(ftp, indexPath).ToString()], token); if (downloaded.ExitCode == 0) entries = JsonSerializer.Deserialize<List<ServiceRemoteRecord>>(await File.ReadAllBytesAsync(temp, token)) ?? []; } catch { }
        foreach (var item in additions) { entries.RemoveAll(x => x.FileName.Equals(item.FileName, StringComparison.OrdinalIgnoreCase) && x.Category.Equals(item.Category, StringComparison.OrdinalIgnoreCase)); entries.Add(item); }
        var payload = JsonSerializer.SerializeToUtf8Bytes(entries.OrderByDescending(x => x.CompletedAt)); await File.WriteAllBytesAsync(temp, payload, token); await new FtpTransferService().UploadAsync(options, password, temp, indexPath, token); try { File.Delete(temp); } catch { }
    }
    private static async Task CurlAsync(ServiceFtpConfig ftp, string password, IEnumerable<string> args, CancellationToken token)
    {
        var result = await new FtpTransferService().ExecuteAsync(new RemoteFtpOptions(ftp.Host, ftp.Port, ftp.UserName, ftp.UseFtps, ftp.TrustInvalidCertificate), password, args, token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error.Trim());
    }
    // Use the FTP URI scheme for explicit TLS; curl negotiates TLS via --ssl-reqd.
    private static Uri Uri(ServiceFtpConfig ftp, string path) => new($"ftp://{ftp.Host}:{ftp.Port}/{path.TrimStart('/')}");
    private static string? Unprotect(string? value) { if (string.IsNullOrWhiteSpace(value)) return null; try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.LocalMachine)); } catch { return null; } }
    private static void DeleteLocalArchives(BackupResult result) { foreach (var archive in result.Archives ?? []) try { File.Delete(archive.ArchivePath); } catch { } }
}
sealed class ServiceConfig { public bool Paused { get; set; } public int RetentionDays { get; set; } = 7; public List<BackupJob> Jobs { get; set; } = []; public List<ServiceRunRecord> History { get; set; } = []; public DateTimeOffset? LastBackup { get; set; } public string? EncryptedMySqlPassword { get; set; } public ServiceFtpConfig? RemoteFtp { get; set; } }
sealed class ServiceFtpConfig { public string Host { get; set; } = ""; public int Port { get; set; } = 21; public string UserName { get; set; } = ""; public string RemoteFolder { get; set; } = "/backups"; public bool UseFtps { get; set; } = true; public bool TrustInvalidCertificate { get; set; } = true; public string? EncryptedPassword { get; set; } }
sealed record ServiceRunRecord(string JobName, DateTimeOffset CompletedAt, string Status, string ArchivePath, string Sha256, string? RemotePath, string? Warning = null);
sealed record ServiceRemoteRecord(string FileName, string Category, DateTimeOffset CompletedAt, string Sha256, string JobName);
