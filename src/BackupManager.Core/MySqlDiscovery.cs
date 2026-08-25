using System.Diagnostics;
using System.Xml.Linq;

namespace BackupManager.Core;
public sealed record MySqlWorkbenchProfile(string Name, string Host, int Port, string UserName);
public sealed class MySqlDiscovery
{
    public IReadOnlyList<MySqlWorkbenchProfile> FindWorkbenchProfiles()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MySQL", "Workbench", "connections.xml");
        if (!File.Exists(file)) return [];
        var document = XDocument.Load(file);
        return document.Descendants("value").Where(x => (string?)x.Attribute("struct-name") == "db.mgmt.Connection")
            .Select(x => new MySqlWorkbenchProfile(Value(x, "name") ?? "MySQL connection", Value(x, "hostName") ?? "localhost", int.TryParse(Value(x, "port"), out var port) ? port : 3306, Value(x, "userName") ?? "root"))
            .DistinctBy(x => new { x.Host, x.Port, x.UserName }).ToArray();
    }
    public async Task<IReadOnlyList<string>> ListDatabasesAsync(MySqlConnectionOptions connection, string? password, CancellationToken token)
    {
        var executable = connection.MySqlExecutable ?? Locate("mysql.exe");
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--host=" + connection.Host); start.ArgumentList.Add("--port=" + connection.Port); start.ArgumentList.Add("--user=" + connection.UserName); start.ArgumentList.Add("--batch"); start.ArgumentList.Add("--skip-column-names"); start.ArgumentList.Add("--execute=SHOW DATABASES");
        if (!string.IsNullOrWhiteSpace(password)) start.Environment["MYSQL_PWD"] = password; else start.ArgumentList.Add("--skip-password");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start mysql.exe."); var output = await process.StandardOutput.ReadToEndAsync(token); var error = await process.StandardError.ReadToEndAsync(token); await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new InvalidOperationException("Unable to query MySQL. Enter the password for the selected Workbench connection. " + error.Trim());
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => x is not "information_schema" and not "mysql" and not "performance_schema" and not "sys").ToArray();
    }
    public static string Locate(string file) => new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MySQL", "MySQL Server 8.0", "bin", file), file }.FirstOrDefault(File.Exists) ?? file;
    private static string? Value(XElement parent, string key) => parent.Descendants("value").FirstOrDefault(x => (string?)x.Attribute("key") == key)?.Value;
}
