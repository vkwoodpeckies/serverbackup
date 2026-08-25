using System.Diagnostics;

namespace BackupManager.Core;
public sealed class MySqlDumpService
{
    public async Task DumpAsync(MySqlConnectionOptions connection, MySqlSource database, string outputFile, string? password, CancellationToken token)
    {
        var tool = connection.MySqlDumpExecutable ?? MySqlDiscovery.Locate("mysqldump.exe");
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "--host=" + connection.Host, "--port=" + connection.Port, "--user=" + connection.UserName, "--single-transaction", "--routines", "--events", "--triggers", database.DatabaseName }) start.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(password)) start.Environment["MYSQL_PWD"] = password; else start.ArgumentList.Add("--skip-password");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start mysqldump.exe."); await using var output = File.Create(outputFile); await process.StandardOutput.BaseStream.CopyToAsync(output, token); var error = await process.StandardError.ReadToEndAsync(token); await process.WaitForExitAsync(token);
        if (process.ExitCode != 0 || new FileInfo(outputFile).Length == 0) throw new InvalidOperationException($"MySQL backup failed for '{database.DatabaseName}'. {error.Trim()}");
    }
}
