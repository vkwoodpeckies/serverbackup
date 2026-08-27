using System.Diagnostics;

namespace BackupManager.Core;

public sealed record RemoteFtpOptions(string Host, int Port, string UserName, bool UseFtps = true, bool TrustInvalidCertificate = true);
public sealed record FtpCommandResult(int ExitCode, string Output, string Error);

/// <summary>Single FTP/explicit-FTPS transport used by both the desktop and service.</summary>
public sealed class FtpTransferService
{
    public async Task UploadAsync(RemoteFtpOptions options, string password, string localPath, string remotePath, CancellationToken token = default)
    {
        var result = await ExecuteAsync(options, password, ["--upload-file", localPath, Uri(options, remotePath).ToString()], token);
        if (result.ExitCode != 0) throw new InvalidOperationException($"FTP upload failed (curl exit {result.ExitCode}): {result.Error.Trim()}");
    }

    public async Task<FtpCommandResult> ExecuteAsync(RemoteFtpOptions options, string password, IEnumerable<string> requestArguments, CancellationToken token = default)
    {
        var curl = Path.Combine(Environment.SystemDirectory, "curl.exe"); if (!File.Exists(curl)) curl = "curl.exe";
        var netrc = Path.Combine(Path.GetTempPath(), "BackupManager-" + Guid.NewGuid().ToString("N") + ".netrc");
        var host = new Uri(options.Host.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ? options.Host : "ftp://" + options.Host).Host;
        try
        {
            await File.WriteAllTextAsync(netrc, $"machine {host} login {options.UserName} password {password}{Environment.NewLine}", token);
            var start = new ProcessStartInfo(curl) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (var argument in new[] { "--fail", "--silent", "--show-error", "--ftp-create-dirs", "--disable-epsv", "--ftp-pasv", "--connect-timeout", "30", "--retry", "3", "--retry-all-errors", "--retry-delay", "3", "--max-time", "0", "--netrc-file", netrc }) start.ArgumentList.Add(argument);
            if (options.UseFtps) { start.ArgumentList.Add("--ssl-reqd"); if (options.TrustInvalidCertificate) start.ArgumentList.Add("--insecure"); }
            foreach (var argument in requestArguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows curl.exe could not be started.");
            var outputTask = process.StandardOutput.ReadToEndAsync(token); var errorTask = process.StandardError.ReadToEndAsync(token); await process.WaitForExitAsync(token);
            var result = new FtpCommandResult(process.ExitCode, await outputTask, await errorTask);
            if (requestArguments.Any(x => string.Equals(x, "--list-only", StringComparison.OrdinalIgnoreCase))) WriteListingDiagnostic(options, requestArguments, result);
            return result;
        }
        finally { try { File.Delete(netrc); } catch { } }
    }

    public static Uri Uri(RemoteFtpOptions options, string remotePath) => new($"ftp://{options.Host}:{options.Port}/{remotePath.TrimStart('/')}");
    private static void WriteListingDiagnostic(RemoteFtpOptions options, IEnumerable<string> arguments, FtpCommandResult result)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupManager", "logs"); Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "ftp-listing.log"), $"{DateTimeOffset.Now:O} | {options.Host}:{options.Port} | {string.Join(" ", arguments)} | exit={result.ExitCode} | output={result.Output.Replace(Environment.NewLine, "\\n")} | error={result.Error.Replace(Environment.NewLine, "\\n")}{Environment.NewLine}");
        }
        catch { }
    }
}
