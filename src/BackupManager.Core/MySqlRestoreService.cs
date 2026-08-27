using System.Diagnostics;

namespace BackupManager.Core;

public sealed class MySqlRestoreService
{
    public async Task RestoreAsync(MySqlConnectionOptions connection, string password, IReadOnlyList<(string SqlFile, string DatabaseName)> databases, bool replaceExisting, string? usersAndPrivilegesFile, bool restoreUsersAndPrivileges, CancellationToken token)
    {
        foreach (var (sqlFile, databaseName) in databases)
        {
            var quoted = "`" + databaseName.Replace("`", "``") + "`";
            var setup = replaceExisting ? $"DROP DATABASE IF EXISTS {quoted}; CREATE DATABASE {quoted};" : $"CREATE DATABASE IF NOT EXISTS {quoted};";
            await RunQueryAsync(connection, password, setup, token);
            await ImportAsync(connection, password, databaseName, sqlFile, token);
        }
        if (restoreUsersAndPrivileges && !string.IsNullOrWhiteSpace(usersAndPrivilegesFile) && File.Exists(usersAndPrivilegesFile)) await ImportAsync(connection, password, null, usersAndPrivilegesFile, token);
    }
    private static async Task RunQueryAsync(MySqlConnectionOptions connection, string password, string query, CancellationToken token)
    {
        var process = Start(connection, password, "--execute=" + query); var error = await process.StandardError.ReadToEndAsync(token); await process.WaitForExitAsync(token); if (process.ExitCode != 0) throw new InvalidOperationException("MySQL restore preparation failed. " + error.Trim());
    }
    private static async Task ImportAsync(MySqlConnectionOptions connection, string password, string? databaseName, string sqlFile, CancellationToken token)
    {
        var process = Start(connection, password, databaseName is null ? null : "--database=" + databaseName); await using var input = File.OpenRead(sqlFile); await input.CopyToAsync(process.StandardInput.BaseStream, token); process.StandardInput.Close(); var error = await process.StandardError.ReadToEndAsync(token); await process.WaitForExitAsync(token); if (process.ExitCode != 0) throw new InvalidOperationException($"MySQL import failed for '{Path.GetFileName(sqlFile)}'. {error.Trim()}");
    }
    private static Process Start(MySqlConnectionOptions connection, string password, string? extraArgument)
    {
        var tool = connection.MySqlExecutable ?? MySqlDiscovery.Locate("mysql.exe"); var start = new ProcessStartInfo(tool) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }; foreach (var argument in new[] { "--host=" + connection.Host, "--port=" + connection.Port, "--user=" + connection.UserName }.Append(extraArgument).Where(x => !string.IsNullOrWhiteSpace(x))) start.ArgumentList.Add(argument!); start.Environment["MYSQL_PWD"] = password; return Process.Start(start) ?? throw new InvalidOperationException("Unable to start mysql.exe for restore.");
    }
}
