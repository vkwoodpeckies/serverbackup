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
    public async Task ExportUsersAndPrivilegesAsync(MySqlConnectionOptions connection, IReadOnlyList<MySqlSource> databases, string outputFile, string? password, CancellationToken token)
    {
        var users = new HashSet<(string User, string Host)>();
        foreach (var database in databases)
        {
            var escaped = database.DatabaseName.Replace("'", "''");
            var query = $"SELECT DISTINCT User,Host FROM mysql.db WHERE Db='{escaped}' UNION SELECT DISTINCT User,Host FROM mysql.tables_priv WHERE Db='{escaped}' UNION SELECT DISTINCT User,Host FROM mysql.procs_priv WHERE Db='{escaped}'";
            var rows = await RunMysqlAsync(connection, query, password, token);
            foreach (var row in rows.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = row.Split('\t'); if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[0])) users.Add((parts[0], parts[1]));
            }
        }
        await using var writer = new StreamWriter(outputFile, false);
        await writer.WriteLineAsync("-- MySQL users and privileges required by this backup.");
        await writer.WriteLineAsync("-- Restore with a MySQL administrative account after importing database SQL files.");
        foreach (var (user, host) in users.OrderBy(x => x.User).ThenBy(x => x.Host))
        {
            var account = $"'{user.Replace("'", "''")}'@'{host.Replace("'", "''")}'";
            var create = await RunMysqlAsync(connection, $"SHOW CREATE USER {account}", password, token);
            var grants = await RunMysqlAsync(connection, $"SHOW GRANTS FOR {account}", password, token);
            var createStatement = create.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => { var index = x.IndexOf("CREATE USER", StringComparison.OrdinalIgnoreCase); return index >= 0 ? x[index..].Trim() : ""; }).FirstOrDefault(x => x.Length > 0);
            if (!string.IsNullOrWhiteSpace(createStatement)) await writer.WriteLineAsync(createStatement.TrimEnd(';') + ";");
            foreach (var grant in grants.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) if (grant.StartsWith("GRANT ", StringComparison.OrdinalIgnoreCase) || grant.StartsWith("REVOKE ", StringComparison.OrdinalIgnoreCase)) await writer.WriteLineAsync(grant.TrimEnd(';') + ";");
            await writer.WriteLineAsync();
        }
        if (users.Count == 0) await writer.WriteLineAsync("-- No database-specific MySQL user grants were found for the selected databases.");
    }
    private static async Task<string> RunMysqlAsync(MySqlConnectionOptions connection, string query, string? password, CancellationToken token)
    {
        var tool = connection.MySqlExecutable ?? MySqlDiscovery.Locate("mysql.exe");
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "--host=" + connection.Host, "--port=" + connection.Port, "--user=" + connection.UserName, "--batch", "--skip-column-names", "--raw", "--execute=" + query }) start.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(password)) start.Environment["MYSQL_PWD"] = password; else start.ArgumentList.Add("--skip-password");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start mysql.exe for user and privilege export."); var output = await process.StandardOutput.ReadToEndAsync(token); var error = await process.StandardError.ReadToEndAsync(token); await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new InvalidOperationException($"MySQL user and privilege export failed. {error.Trim()}"); return output;
    }
}
