using BackupManager.Core;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;
using UniformGrid = System.Windows.Controls.Primitives.UniformGrid;

namespace BackupManager.Desktop;

public partial class MainWindow : Window
{
    private readonly string _dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupManager");
    private readonly string _configPath;
    private DesktopConfig _config;
    private readonly Dictionary<string, string> _sessionMySqlPasswords = new();
    private bool _backupInProgress;
    private readonly DispatcherTimer _dashboardRefreshTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    public MainWindow()
    {
        InitializeComponent(); Directory.CreateDirectory(_dataPath); _configPath = Path.Combine(_dataPath, "desktop.json");
        _config = Load(); MigrateSecretsForService(); PauseButton.Content = _config.Paused ? "Resume" : "Pause"; ShowPage("Dashboard");
        _dashboardRefreshTimer.Tick += (_, _) => RefreshDashboardFromDisk(); _dashboardRefreshTimer.Start();
    }
    private void RefreshDashboardFromDisk()
    {
        if (_backupInProgress || !string.Equals(PageTitle.Text, "Dashboard", StringComparison.OrdinalIgnoreCase)) return;
        try { var latest = Load(); _config = latest; PauseButton.Content = _config.Paused ? "Resume" : "Pause"; ShowPage("Dashboard"); } catch { }
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
    private DesktopConfig Load() => File.Exists(_configPath) ? JsonSerializer.Deserialize<DesktopConfig>(File.ReadAllText(_configPath)) ?? new() : new();
    private void Save() => File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
    private void MigrateSecretsForService()
    {
        var changed = false;
        var mysql = GetSavedMySqlPassword(); if (!string.IsNullOrEmpty(mysql)) { var protectedValue = Protect(mysql); if (protectedValue != _config.EncryptedMySqlPassword) { _config.EncryptedMySqlPassword = protectedValue; changed = true; } }
        if (_config.RemoteFtp is { EncryptedPassword: not null } ftp) { var remote = Unprotect(ftp.EncryptedPassword); if (!string.IsNullOrEmpty(remote)) { var protectedValue = Protect(remote); if (protectedValue != ftp.EncryptedPassword) { ftp.EncryptedPassword = protectedValue; changed = true; } } }
        if (changed) Save();
    }
    private int _busyDepth;
    private void SetBusy(bool busy, string message = "Processing…")
    {
        if (busy) { _busyDepth++; BusyMessage.Text = message; BusyOverlay.Visibility = Visibility.Visible; }
        else if (_busyDepth > 0 && --_busyDepth == 0) BusyOverlay.Visibility = Visibility.Collapsed;
    }
    private void UpdateBusy(string message) { if (_busyDepth > 0) BusyMessage.Text = message; }
    private static TextBlock Text(string text, int size = 14) => new() { Text = text, FontSize = size, Margin = new Thickness(0, 4, 0, 4), TextWrapping = TextWrapping.Wrap };
    private static System.Windows.Controls.Button Action(string label, RoutedEventHandler handler) { var b = new System.Windows.Controls.Button { Content = label, MinWidth = 130, Margin = new Thickness(4), Padding = new Thickness(12, 8, 12, 8) }; b.Click += handler; return b; }
    private void Navigate(object sender, RoutedEventArgs e) => ShowPage(((System.Windows.Controls.Button)sender).Tag.ToString()!);
    private void ShowPage(string page)
    {
        PageTitle.Text = page switch { "Sources" or "Folders" or "MySql" => "Backup Sources", _ => page }; PageSubtitle.Text = page switch { "Dashboard" => "Monitor and control protection for this server", "Jobs" => "Create and manage independent backup jobs", "Sources" or "Folders" or "MySql" => "Manage folders and MySQL databases in one backup workspace", "Remote" => "Configure remote FTP/SFTP delivery", "History" => "Review completed, failed, and pending backup runs", _ => "Backup Manager administration" };
        PageContent.Content = page switch { "Dashboard" => Dashboard(), "Jobs" => Jobs(), "Sources" or "Folders" or "MySql" => Sources(), "Remote" => Remote(), "History" => History(), "Schedule" => SchedulePage(), "Restore" => Restore(), "Logs" => Logs(), _ => Settings() };
    }
    private UIElement Dashboard()
    {
        var g = new Grid(); g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(125) }); g.RowDefinitions.Add(new RowDefinition());
        var backupFailed = string.Equals(_config.LastBackupStatus, "Failed", StringComparison.OrdinalIgnoreCase);
        var jobFrequency = _config.Jobs.Count == 0 ? "No schedules configured" : string.Join(" • ", _config.Jobs.Select(x => $"{x.Name}: {FormatSchedule(x.Schedule)}")); var cards = new UniformGrid { Columns = 4 }; cards.Children.Add(Card("SERVICE", _config.Paused ? "PAUSED" : (backupFailed ? "ERROR" : "READY"), _config.Paused ? "Scheduling suspended" : (backupFailed ? $"Failed {_config.LastBackupAttempt?.ToLocalTime():dd MMM HH:mm}" : "Scheduling enabled"))); cards.Children.Add(Card("JOBS", _config.Jobs.Count.ToString(), jobFrequency)); cards.Children.Add(Card("LAST BACKUP", _config.LastBackup?.ToLocalTime().ToString("dd MMM yyyy HH:mm") ?? "Not run", backupFailed ? (_config.LastBackupError ?? "Scheduled backup failed") : "Latest completed backup")); cards.Children.Add(Card("NEXT BACKUP", _config.Jobs.Select(x => x.NextRun).Where(x => x.HasValue).Order().FirstOrDefault()?.ToLocalTime().ToString("dd MMM HH:mm") ?? "Not scheduled", "Next eligible job")); g.Children.Add(cards);
        var section = new StackPanel { Margin = new Thickness(0, 20, 0, 0) }; Grid.SetRow(section, 1); section.Children.Add(Text("Recent activity", 18)); var list = new ListBox { Height = 310 }; foreach (var item in _config.History.OrderByDescending(x => x.CompletedAt).Take(10)) list.Items.Add($"{item.CompletedAt.ToLocalTime():g}  |  {item.JobName}  |  {item.Status}  |  {item.ArchivePath}"); if (list.Items.Count == 0) list.Items.Add("No backups have been run yet."); section.Children.Add(list); g.Children.Add(section); return g;
    }
    private Border Card(string heading, string value, string detail) => new() { Background = System.Windows.Media.Brushes.White, BorderBrush = System.Windows.Media.Brushes.LightGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(5), Padding = new Thickness(16), Child = new StackPanel { Children = { Text(heading, 11), new TextBlock { Text = value, FontSize = 23, FontWeight = FontWeights.SemiBold }, Text(detail, 12) } } };
    private static string FormatSchedule(Schedule schedule) => schedule.Kind switch { "EveryMinutes" => $"Every {schedule.EveryMinutes} minute(s)", "EveryHours" => $"Every {schedule.EveryHours} hour(s)", "Daily" => "Daily", "Weekly" => $"Weekly ({schedule.Day})", "Monthly" => "Monthly", _ => "Manual" };
    private UIElement Jobs()
    {
        var p = new StackPanel(); var bar = new WrapPanel(); bar.Children.Add(Action("New backup job", (_, _) => CreateJob())); bar.Children.Add(Action("Run selected", (_, _) => RunSelected())); p.Children.Add(bar);
        var list = new ListBox { Name = "JobsList", Height = 250, Margin = new Thickness(0, 8, 0, 8) }; foreach (var job in _config.Jobs) list.Items.Add(job); list.DisplayMemberPath = "Name"; p.Children.Add(list);
        var cards = new WrapPanel(); foreach (var job in _config.Jobs) { var size = job.Sources.Sum(x => FolderSize(x.Path)); var last = _config.History.Where(x => x.JobName == job.Name && !string.IsNullOrWhiteSpace(x.RemotePath)).OrderByDescending(x => x.CompletedAt).FirstOrDefault()?.CompletedAt.ToLocalTime().ToString("dd MMM yyyy HH:mm") ?? "Never"; cards.Children.Add(Card(job.Name, $"{job.Sources.Count} folders  •  {FormatSize(size)}", $"Last backup sent: {last}")); } p.Children.Add(cards);
        p.Children.Add(Action("Delete selected backup job", (_, _) => { if (list.SelectedItem is not BackupJob job) { MessageBox.Show("Select a backup job first."); return; } if (MessageBox.Show($"Delete '{job.Name}' and its configured sources?", "Delete backup job", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; _config.Jobs.Remove(job); Save(); ShowPage("Jobs"); })); return p;
    }
    private UIElement Sources()
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.38, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.62, GridUnitType.Star) });
        var folders = new Border { BorderBrush = System.Windows.Media.Brushes.LightGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Child = Folders() };
        var databases = new Border { BorderBrush = System.Windows.Media.Brushes.LightGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Child = MySql() };
        Grid.SetColumn(folders, 0); Grid.SetColumn(databases, 2); grid.Children.Add(folders); grid.Children.Add(databases); return grid;
    }
    private UIElement Folders()
    {
        var p = new StackPanel(); p.Children.Add(Text("Folder sources", 18)); p.Children.Add(Text("Choose one or more folders to include in backups.", 12)); p.Children.Add(Action("Add folder", (_, _) => AddFolder()));
        var list = new ListBox { Height = 330, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var job in _config.Jobs) foreach (var source in job.Sources) list.Items.Add(new SelectedFolderView(job.Id, source.Id, job.Name, source.Path));
        list.DisplayMemberPath = "Display";
        if (list.Items.Count == 0) list.Items.Add("No folders selected for backup."); p.Children.Add(list);
        p.Children.Add(Action("Remove selected folder", (_, _) => { if (list.SelectedItem is not SelectedFolderView item) { MessageBox.Show("Select a saved folder first."); return; } var job = _config.Jobs.FirstOrDefault(x => x.Id == item.JobId); job?.Sources.RemoveAll(x => x.Id == item.SourceId); Save(); ShowPage("Sources"); })); return p;
    }
    private UIElement MySql()
    {
        var panel = new StackPanel(); panel.Children.Add(Text("MySQL database sources", 18)); panel.Children.Add(Text("Connections automatically discovered from MySQL Workbench", 12));
        var profiles = new MySqlDiscovery().FindWorkbenchProfiles(); var selector = new System.Windows.Controls.ComboBox { DisplayMemberPath = "Name", Height = 34, Margin = new Thickness(0, 12, 0, 8) };
        foreach (var profile in profiles) selector.Items.Add(profile); if (selector.Items.Count > 0) selector.SelectedIndex = 0; panel.Children.Add(selector);
        var databases = new ListBox { Height = 160, SelectionMode = System.Windows.Controls.SelectionMode.Multiple }; panel.Children.Add(databases);
        var includeUsers = new System.Windows.Controls.CheckBox { Content = "Include MySQL users and privileges", IsChecked = _config.Jobs.FirstOrDefault()?.IncludeMySqlUsersAndPrivileges == true, Margin = new Thickness(0, 10, 0, 2) }; panel.Children.Add(includeUsers);
        panel.Children.Add(Text("Adds users-and-privileges.sql to the MySQL ZIP. This can include credential hashes and requires a MySQL administrative account.", 12));
        MySqlConnectionOptions? activeConnection = null; string? activePassword = null;
        panel.Children.Add(Action("Scan databases", async (_, _) =>
        {
            if (selector.SelectedItem is not MySqlWorkbenchProfile profile) { MessageBox.Show("No MySQL Workbench connection was found. Open Workbench once and save a connection profile."); return; }
            var password = GetSavedMySqlPassword() ?? AskPassword(profile.Name); if (password is null) return;
            SetBusy(true, "Scanning MySQL databases…");
            try { databases.Items.Clear(); var connection = new MySqlConnectionOptions { Host = profile.Host, Port = profile.Port, UserName = profile.UserName }; foreach (var database in await new MySqlDiscovery().ListDatabasesAsync(connection, password, CancellationToken.None)) databases.Items.Add(database); activeConnection = connection; activePassword = password; _sessionMySqlPasswords[$"{connection.Host}:{connection.Port}:{connection.UserName}"] = password; }
            catch (Exception ex) { MessageBox.Show(ex.Message, "MySQL discovery", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { SetBusy(false); }
        }));
        panel.Children.Add(Action("Add selected databases to backup", (_, _) =>
        {
            if (activeConnection is null || databases.SelectedItems.Count == 0) { MessageBox.Show("Scan the Workbench connection and select one or more databases first."); return; }
            var job = _config.Jobs.FirstOrDefault();
            if (job is null) { job = new BackupJob { Name = "MySQL Backup", DestinationPath = Path.Combine(_dataPath, "Backups"), Schedule = new Schedule("Manual") }; Directory.CreateDirectory(job.DestinationPath); _config.Jobs.Add(job); }
            job.MySqlConnection = activeConnection;
            job.IncludeMySqlUsersAndPrivileges = includeUsers.IsChecked == true;
            foreach (var item in databases.SelectedItems.Cast<string>()) if (!job.Databases.Any(x => x.DatabaseName.Equals(item, StringComparison.OrdinalIgnoreCase))) job.Databases.Add(new MySqlSource(Guid.NewGuid(), item));
            Save(); MessageBox.Show($"Added {databases.SelectedItems.Count} database(s) to '{job.Name}'. They will be exported as standard .sql files inside the ZIP backup." + (job.IncludeMySqlUsersAndPrivileges ? " User and grant export is enabled." : "")); ShowPage("Sources");
        }));
        panel.Children.Add(Text("Selected for backup", 18));
        var selected = new ListBox { Height = 100, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var job in _config.Jobs)
            foreach (var database in job.Databases)
                selected.Items.Add(new SelectedDatabaseView(job.Id, database.Id, job.Name, database.DatabaseName));
        if (selected.Items.Count == 0) selected.Items.Add("No databases have been selected for backup yet.");
        panel.Children.Add(selected);
        panel.Children.Add(Action("Remove selected database", (_, _) =>
        {
            if (selected.SelectedItem is not SelectedDatabaseView item) { MessageBox.Show("Select a database from the saved backup list first."); return; }
            var job = _config.Jobs.FirstOrDefault(x => x.Id == item.JobId);
            if (job is null) return;
            job.Databases.RemoveAll(x => x.Id == item.DatabaseId); Save(); ShowPage("Sources");
        }));
        if (profiles.Count == 0) panel.Children.Add(Text("No MySQL Workbench profiles found in %AppData%\\MySQL\\Workbench. MySQL Workbench must be installed and have at least one saved connection."));
        return panel;
    }

    private UIElement SchedulePage()
    {
        var panel = new StackPanel();
        panel.Children.Add(Text("Backup frequency", 18));
        panel.Children.Add(Text("Choose when Backup Manager should run each configured backup job."));
        panel.Children.Add(Text("Keep remote backups for (days)", 13));
        var retention = new TextBox { Text = _config.RetentionDays.ToString(), Height = 34, Margin = new Thickness(0, 4, 0, 8) }; panel.Children.Add(retention);
        panel.Children.Add(Action("Save retention", (_, _) =>
        {
            if (!int.TryParse(retention.Text.Trim(), out var retentionDays) || retentionDays < 0) { MessageBox.Show("Retention days must be 0 or a positive whole number.", "Retention"); return; }
            _config.RetentionDays = retentionDays; Save(); MessageBox.Show(retentionDays == 0 ? "Remote backup deletion is disabled." : $"Remote backups older than {retentionDays} days will be deleted during sync/upload.", "Retention saved");
        }));
        if (_config.Jobs.Count == 0) { panel.Children.Add(Text("Create a backup job first, then return here to assign its schedule.")); return panel; }

        var jobSelector = new System.Windows.Controls.ComboBox { DisplayMemberPath = "Name", Height = 34, Margin = new Thickness(0, 14, 0, 8) };
        foreach (var job in _config.Jobs) jobSelector.Items.Add(job);
        jobSelector.SelectedIndex = 0; panel.Children.Add(jobSelector);

        var choices = new[] { "Every 5 minutes", "Every 10 minutes", "Every 15 minutes", "Every 30 minutes", "Every 1 hour", "Every 2 hours", "Every 4 hours", "Every 6 hours", "Every 8 hours", "Every 12 hours", "Every 16 hours", "Daily", "Weekly", "Monthly" };
        var frequency = new System.Windows.Controls.ComboBox { Height = 34, Margin = new Thickness(0, 4, 0, 8) };
        foreach (var choice in choices) frequency.Items.Add(choice);
        var savedSchedule = ((BackupJob)jobSelector.SelectedItem).Schedule;
        var savedChoice = savedSchedule.Kind == "EveryMinutes" ? $"Every {savedSchedule.EveryMinutes} minutes" : savedSchedule.Kind == "EveryHours" ? $"Every {savedSchedule.EveryHours} hour" + (savedSchedule.EveryHours == 1 ? "" : "s") : savedSchedule.Kind;
        frequency.SelectedIndex = Math.Max(0, Array.FindIndex(choices, x => string.Equals(x, savedChoice, StringComparison.OrdinalIgnoreCase))); panel.Children.Add(frequency);

        var weekday = new System.Windows.Controls.ComboBox { Height = 34, Margin = new Thickness(0, 4, 0, 8), Visibility = Visibility.Collapsed };
        foreach (var day in Enum.GetValues<DayOfWeek>()) weekday.Items.Add(day);
        weekday.SelectedItem = DayOfWeek.Sunday; panel.Children.Add(weekday);
        frequency.SelectionChanged += (_, _) => weekday.Visibility = frequency.SelectedItem?.ToString() == "Weekly" ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.Add(Action("Save frequency", (_, _) =>
        {
            if (jobSelector.SelectedItem is not BackupJob job || frequency.SelectedItem is not string selected) return;
            job.Schedule = selected switch
            {
                "Daily" => new Schedule("Daily", Time: new TimeOnly(2, 0)),
                "Weekly" => new Schedule("Weekly", Day: (DayOfWeek)weekday.SelectedItem!, Time: new TimeOnly(2, 0)),
                "Monthly" => new Schedule("Monthly", Time: new TimeOnly(2, 0)),
                _ when selected.Contains("minutes", StringComparison.OrdinalIgnoreCase) => new Schedule("EveryMinutes", EveryMinutes: int.Parse(selected.Split(' ')[1])),
                _ => new Schedule("EveryHours", int.Parse(selected.Split(' ')[1]))
            };
            job.NextRun = ScheduleCalculator.Next(job.Schedule, DateTimeOffset.Now);
            if (!int.TryParse(retention.Text.Trim(), out var retentionDays) || retentionDays < 0) { MessageBox.Show("Retention days must be 0 or a positive whole number.", "Frequency"); return; }
            _config.RetentionDays = retentionDays;
            Save();
            MessageBox.Show($"{job.Name} is scheduled: {selected}.\nNext backup: {job.NextRun?.ToLocalTime():dddd, dd MMM yyyy HH:mm}", "Frequency saved");
            ShowPage("Dashboard");
        }));
        panel.Children.Add(Text("Daily, weekly, and monthly backups run at 02:00 local time. Weekly backups use the selected day."));
        return panel;
    }
    private static string? AskPassword(string profileName)
    {
        var window = new Window { Title = "MySQL authentication", Width = 360, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(20) }; panel.Children.Add(new TextBlock { Text = $"Password for {profileName}" }); var box = new PasswordBox { Margin = new Thickness(0, 8, 0, 12) }; panel.Children.Add(box); var ok = new System.Windows.Controls.Button { Content = "Connect", IsDefault = true, Width = 100, HorizontalAlignment = System.Windows.HorizontalAlignment.Right }; ok.Click += (_, _) => window.DialogResult = true; panel.Children.Add(ok); window.Content = panel;
        return window.ShowDialog() == true ? box.Password : null;
    }
    private UIElement Remote()
    {
        var p = new StackPanel();
        p.Children.Add(Text("Remote FTP storage", 18));
        p.Children.Add(Text("Backups are stored under the remote folder using date-based backup folders."));
        var saved = _config.RemoteFtp ?? new RemoteFtpConfig();
        var host = new TextBox { Text = saved.Host, Height = 36, Margin = new Thickness(0, 3, 0, 10) };
        var port = new TextBox { Text = saved.Port.ToString(), Height = 36, Margin = new Thickness(0, 3, 0, 10) };
        var ftps = new System.Windows.Controls.CheckBox { Content = "Use Explicit FTPS (TLS encryption)", IsChecked = saved.UseFtps, Margin = new Thickness(0, 0, 0, 10) };
        var trustCertificate = new System.Windows.Controls.CheckBox { Content = "Trust this server certificate (required for the current self-signed certificate)", IsChecked = saved.TrustInvalidCertificate, Margin = new Thickness(0, 0, 0, 10) };
        var user = new TextBox { Text = saved.UserName, Height = 36, Margin = new Thickness(0, 3, 0, 10) };
        var password = new PasswordBox { Height = 36, Margin = new Thickness(0, 3, 0, 10) };
        var folder = new TextBox { Text = saved.RemoteFolder, Height = 36, Margin = new Thickness(0, 3, 0, 14) };
        p.Children.Add(Text("FTP server address")); p.Children.Add(host);
        p.Children.Add(Text("Port")); p.Children.Add(port);
        p.Children.Add(ftps);
        p.Children.Add(trustCertificate);
        p.Children.Add(Text("FTP username")); p.Children.Add(user);
        p.Children.Add(Text("FTP password")); p.Children.Add(password);
        p.Children.Add(Text("Remote backup folder")); p.Children.Add(folder);
        var actions = new WrapPanel();
        actions.Children.Add(Action("Save FTP settings", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(host.Text) || string.IsNullOrWhiteSpace(user.Text) || !int.TryParse(port.Text, out var number) || number is < 1 or > 65535) { MessageBox.Show("Enter a server address, username, and a valid port."); return; }
            _config.RemoteFtp = new RemoteFtpConfig { Host = host.Text.Trim(), Port = number, UserName = user.Text.Trim(), RemoteFolder = folder.Text.Trim(), UseFtps = ftps.IsChecked == true, TrustInvalidCertificate = trustCertificate.IsChecked == true, EncryptedPassword = password.Password.Length > 0 ? Protect(password.Password) : _config.RemoteFtp?.EncryptedPassword };
            Save(); password.Clear(); MessageBox.Show("FTP settings saved securely.");
        }));
        actions.Children.Add(Action("Test FTP connection", async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(host.Text) || string.IsNullOrWhiteSpace(user.Text) || !int.TryParse(port.Text, out var number)) { MessageBox.Show("Enter the FTP server, port, and username first."); return; }
            var secret = password.Password.Length > 0 ? password.Password : Unprotect(_config.RemoteFtp?.EncryptedPassword);
            if (string.IsNullOrEmpty(secret)) { MessageBox.Show("Enter and save the FTP password before testing."); return; }
            SetBusy(true, "Testing secure FTP connection…");
            try { await TestFtpAsync(host.Text.Trim(), number, user.Text.Trim(), secret, folder.Text.Trim(), ftps.IsChecked == true, trustCertificate.IsChecked == true); MessageBox.Show("FTP connection successful.", "FTP test"); }
            catch (Exception ex) { MessageBox.Show($"FTP connection failed.\n{ex.Message}", "FTP test", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { SetBusy(false); }
        }));
        p.Children.Add(actions);
        p.Children.Add(Text("The saved password is protected using Windows Data Protection and is not displayed after saving.", 12));
        return p;
    }
    // LocalMachine scope lets the Windows service (running as LocalSystem) use the saved secret.
    private static string Protect(string value) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.LocalMachine));
    private static string? Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.LocalMachine)); }
        catch { try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); } catch { return null; } }
    }
    private static async Task TestFtpAsync(string host, int port, string user, string password, string folder, bool useFtps, bool trustInvalidCertificate)
    {
        var config = new RemoteFtpConfig { Host = host, Port = port, UserName = user, RemoteFolder = folder, UseFtps = useFtps, TrustInvalidCertificate = trustInvalidCertificate };
        var result = await RunCurlAsync(config, password, ["--list-only", FtpUri(config, folder.Trim('/') + "/").ToString()]); if (result.ExitCode != 0) throw new InvalidOperationException(result.Error.Trim());
    }
    private static System.Net.Security.RemoteCertificateValidationCallback? SetCertificatePolicy(bool trustInvalidCertificate)
    {
        var previous = ServicePointManager.ServerCertificateValidationCallback;
        if (trustInvalidCertificate) ServicePointManager.ServerCertificateValidationCallback = (_, _, _, _) => true;
        return previous;
    }
    private static void RestoreCertificatePolicy(System.Net.Security.RemoteCertificateValidationCallback? previous) => ServicePointManager.ServerCertificateValidationCallback = previous;
    private static Uri FtpUri(RemoteFtpConfig config, string remotePath)
    {
        var endpoint = config.Host.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ? config.Host : "ftp://" + config.Host;
        var builder = new UriBuilder(new Uri(endpoint)) { Port = config.Port, Path = remotePath.TrimStart('/') };
        return builder.Uri;
    }
    private static async Task EnsureRemoteDirectoryAsync(RemoteFtpConfig config, string password, string remotePath)
    {
#pragma warning disable SYSLIB0014 // FTP/FTPS is the selected remote storage protocol.
        var request = (FtpWebRequest)WebRequest.Create(FtpUri(config, remotePath)); request.Method = WebRequestMethods.Ftp.MakeDirectory; request.Credentials = new NetworkCredential(config.UserName, password); request.EnableSsl = config.UseFtps; request.UsePassive = true; request.KeepAlive = false; request.Timeout = 20000;
        var previousPolicy = SetCertificatePolicy(config.TrustInvalidCertificate);
        try { using var response = (FtpWebResponse)await request.GetResponseAsync(); }
        catch (WebException ex) when (ex.Response is FtpWebResponse ftp && (ftp.StatusCode == FtpStatusCode.ActionNotTakenFileUnavailable || (int)ftp.StatusCode == 451 || (int)ftp.StatusCode == 500)) { ftp.Dispose(); }
        finally { RestoreCertificatePolicy(previousPolicy); }
#pragma warning restore SYSLIB0014
    }
    private async Task<string> UploadBackupAsync(BackupJob job, BackupArchive archive, DateTimeOffset backupStartedAt, IProgress<BackupProgress> progress)
    {
        var config = _config.RemoteFtp ?? throw new InvalidOperationException("Remote FTP settings have not been configured.");
        if (string.IsNullOrWhiteSpace(config.Host) || string.IsNullOrWhiteSpace(config.UserName)) throw new InvalidOperationException("Remote FTP server and username are required.");
        var password = Unprotect(config.EncryptedPassword); if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("Save the remote FTP password before running a backup.");
        var category = archive.Category.Equals("MySql", StringComparison.OrdinalIgnoreCase) ? "mysql" : "files";
        var stamp = backupStartedAt.ToLocalTime().ToString("dd_MMM_yyyy_hh_mm_tt", System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant();
        var remotePath = config.RemoteFolder.Trim('/') + $"/{category}/backup_{category}_{stamp}{ScheduleCalculator.FrequencySuffix(job.Schedule)}.zip"; progress.Report(new(job.Id, BackupRunState.Uploading, $"Uploading {category} ZIP to remote FTP storage"));
        await UploadWithCurlAsync(config, password, archive.ArchivePath, remotePath);
        var record = new RemoteBackupRecord(Path.GetFileName(remotePath), category, backupStartedAt, archive.Sha256, job.Name);
        UpdateLocalRemoteIndex(record);
        // Index maintenance must never turn a successful archive transfer into a failed backup.
        try { await UpdateRemoteIndexAsync(config, password, record); } catch { }
        try { File.Delete(archive.ArchivePath); } catch { }
        return remotePath;
    }
    private static async Task UploadWithCurlAsync(RemoteFtpConfig config, string password, string localPath, string remotePath)
    {
        var result = await RunCurlAsync(config, password, ["--upload-file", localPath, FtpUri(config, remotePath).ToString()]);
        if (result.ExitCode != 0) throw new InvalidOperationException($"FTP upload failed (curl exit {result.ExitCode}): {result.Error.Trim()}");
    }
    private static async Task<(int ExitCode, string Output, string Error)> RunCurlAsync(RemoteFtpConfig config, string password, IEnumerable<string> requestArguments)
    {
        var result = await new FtpTransferService().ExecuteAsync(new RemoteFtpOptions(config.Host, config.Port, config.UserName, config.UseFtps, config.TrustInvalidCertificate), password, requestArguments);
        return (result.ExitCode, result.Output, result.Error);
    }
    private async Task<string> UploadBackupAsync(BackupJob job, string primaryArchivePath, IProgress<BackupProgress> progress)
    {
        var category = primaryArchivePath.EndsWith("_mysql.zip", StringComparison.OrdinalIgnoreCase) ? "MySql" : "Files";
        var archives = new List<BackupArchive> { new(category, primaryArchivePath, "") };
        var pairedArchive = category == "Files" ? primaryArchivePath[..^"_files.zip".Length] + "_mysql.zip" : primaryArchivePath[..^"_mysql.zip".Length] + "_files.zip";
        if (File.Exists(pairedArchive)) archives.Add(new BackupArchive(category == "Files" ? "MySql" : "Files", pairedArchive, ""));
        var uploaded = new List<string>(); var errors = new List<string>();
        foreach (var archive in archives) { try { uploaded.Add($"{archive.Category}: {await UploadBackupAsync(job, archive, DateTimeOffset.Now, progress)}"); } catch (Exception ex) { errors.Add($"{archive.Category}: {ex.Message}"); } }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
        return string.Join("\n", uploaded);
    }
    private async Task UpdateRemoteIndexAsync(RemoteFtpConfig config, string password, RemoteBackupRecord item)
    {
        var indexPath = config.RemoteFolder.Trim('/') + "/backup-index.json";
        var entries = new List<RemoteBackupRecord>();
        try { var bytes = await DownloadRemoteBytesAsync(config, password, indexPath); entries = JsonSerializer.Deserialize<List<RemoteBackupRecord>>(bytes) ?? []; } catch { }
        entries.RemoveAll(x => string.Equals(x.FileName, item.FileName, StringComparison.OrdinalIgnoreCase)); entries.Add(item);
        var payload = JsonSerializer.SerializeToUtf8Bytes(entries.OrderByDescending(x => x.CompletedAt));
        await UploadRemoteBytesAsync(config, password, indexPath, payload);
        SaveLocalRemoteIndex(entries);
        await ApplyRemoteRetentionAsync(config, password, entries);
    }
    private async Task ApplyRemoteRetentionAsync(RemoteFtpConfig config, string password, List<RemoteBackupRecord> entries)
    {
        if (_config.RetentionDays <= 0) return;
        var cutoff = DateTimeOffset.Now.AddDays(-_config.RetentionDays);
        var expired = entries.Where(x => x.CompletedAt < cutoff).ToList();
        foreach (var item in expired)
        {
            try { await RunCurlAsync(config, password, ["--quote", "DELE " + item.FileName, FtpUri(config, config.RemoteFolder.Trim('/') + "/" + item.Category.ToLowerInvariant() + "/").ToString()]); } catch { }
            entries.Remove(item);
        }
        if (expired.Count > 0) { var payload = JsonSerializer.SerializeToUtf8Bytes(entries.OrderByDescending(x => x.CompletedAt)); try { await UploadRemoteBytesAsync(config, password, config.RemoteFolder.Trim('/') + "/backup-index.json", payload); } catch { } }
        SaveLocalRemoteIndex(entries);
    }
    private string LocalRemoteIndexPath => Path.Combine(_dataPath, "remote-backup-index.json");
    private List<RemoteBackupRecord> LoadLocalRemoteIndex()
    {
        try { return File.Exists(LocalRemoteIndexPath) ? JsonSerializer.Deserialize<List<RemoteBackupRecord>>(File.ReadAllText(LocalRemoteIndexPath)) ?? [] : []; } catch { return []; }
    }
    private void SaveLocalRemoteIndex(IEnumerable<RemoteBackupRecord> records) => File.WriteAllText(LocalRemoteIndexPath, JsonSerializer.Serialize(records.OrderByDescending(x => x.CompletedAt), new JsonSerializerOptions { WriteIndented = true }));
    private void UpdateLocalRemoteIndex(RemoteBackupRecord item) { var records = LoadLocalRemoteIndex(); records.RemoveAll(x => x.FileName.Equals(item.FileName, StringComparison.OrdinalIgnoreCase)); records.Add(item); SaveLocalRemoteIndex(records); }
    private async Task<byte[]> DownloadRemoteBytesAsync(RemoteFtpConfig config, string password, string remotePath)
    {
        var temp = Path.GetTempFileName(); try { var result = await RunCurlAsync(config, password, ["--output", temp, FtpUri(config, remotePath).ToString()]); if (result.ExitCode != 0) throw new InvalidOperationException(result.Error.Trim()); return await File.ReadAllBytesAsync(temp); } finally { try { File.Delete(temp); } catch { } }
    }
    private async Task UploadRemoteBytesAsync(RemoteFtpConfig config, string password, string remotePath, byte[] data)
    {
        var temp = Path.GetTempFileName(); try { await File.WriteAllBytesAsync(temp, data); await UploadWithCurlAsync(config, password, temp, remotePath); } finally { try { File.Delete(temp); } catch { } }
    }
    private async Task<List<RemoteBackupRecord>> GetRemoteIndexAsync()
    {
        var config = _config.RemoteFtp; if (config is null) return [];
        var password = Unprotect(config.EncryptedPassword); if (string.IsNullOrEmpty(password)) return [];
        var records = new List<RemoteBackupRecord>(); var indexedRecords = new List<RemoteBackupRecord>();
        try { var indexBytes = await DownloadRemoteBytesAsync(config, password, config.RemoteFolder.Trim('/') + "/backup-index.json"); indexedRecords = JsonSerializer.Deserialize<List<RemoteBackupRecord>>(indexBytes) ?? []; } catch { }
        var listed = 0;
        try
        {
            foreach (var category in new[] { "files", "mysql" })
            {
                var root = config.RemoteFolder.Trim('/'); var paths = new[] { root + "/" + category, root + "/" + root + "/" + category, category }.Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var remoteFolder in paths)
                {
                    var result = await RunCurlAsync(config, password, ["--list-only", FtpUri(config, remoteFolder + "/").ToString()]); if (result.ExitCode != 0) continue;
                    listed++;
                    foreach (var name in result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { if (!name.StartsWith("backup_", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue; var stem = name[7..^4]; var split = stem.IndexOf('_'); if (split < 0) continue; var stamp = stem[(split + 1)..]; var suffix = stamp.LastIndexOf('_'); if (suffix > 0 && (stamp[(suffix + 1)..].EndsWith("hr", StringComparison.OrdinalIgnoreCase) || stamp[(suffix + 1)..].EndsWith("min", StringComparison.OrdinalIgnoreCase) || stamp[(suffix + 1)..].Equals("daily", StringComparison.OrdinalIgnoreCase) || stamp[(suffix + 1)..].Equals("weekly", StringComparison.OrdinalIgnoreCase) || stamp[(suffix + 1)..].Equals("monthly", StringComparison.OrdinalIgnoreCase) || stamp[(suffix + 1)..].Equals("manual", StringComparison.OrdinalIgnoreCase))) stamp = stamp[..suffix]; var completed = DateTimeOffset.Now; DateTimeOffset.TryParseExact(stamp, "dd_MMM_yyyy_hh_mm_tt", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out completed); if (!records.Any(x => x.FileName.Equals(name, StringComparison.OrdinalIgnoreCase) && x.Category.Equals(category, StringComparison.OrdinalIgnoreCase))) records.Add(new RemoteBackupRecord(name, category, completed, "", "")); }
                    if (records.Any(x => x.Category.Equals(category, StringComparison.OrdinalIgnoreCase))) break;
                }
            }
        }
        catch { }
        if (listed == 0 && indexedRecords.Count == 0) throw new InvalidOperationException("Unable to list the remote files/mysql backup folders. The remote index was not changed.");
        if (records.Count == 0) { records = indexedRecords; SaveLocalRemoteIndex(records); return records.OrderByDescending(x => x.CompletedAt).ToList(); }
        records = records.OrderByDescending(x => x.CompletedAt).ToList();
        await ApplyRemoteRetentionAsync(config, password, records);
        var freshPayload = JsonSerializer.SerializeToUtf8Bytes(records); await UploadRemoteBytesAsync(config, password, config.RemoteFolder.Trim('/') + "/backup-index.json", freshPayload);
        var fetched = await DownloadRemoteBytesAsync(config, password, config.RemoteFolder.Trim('/') + "/backup-index.json");
        records = JsonSerializer.Deserialize<List<RemoteBackupRecord>>(fetched) ?? records; SaveLocalRemoteIndex(records); return records;
    }
    private UIElement History()
    {
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headingText = new StackPanel(); headingText.Children.Add(Text("Remote archive history", 20)); headingText.Children.Add(new TextBlock { Text = "Every completed FTP upload is available here for download or restore.", Foreground = System.Windows.Media.Brushes.SlateGray, Margin = new Thickness(0, 2, 0, 0) }); heading.Children.Add(headingText);
        var summary = new TextBlock { Text = "Loading remote archives…", Foreground = System.Windows.Media.Brushes.SteelBlue, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumn(summary, 1); heading.Children.Add(summary); layout.Children.Add(heading);
        var frame = new Border { Margin = new Thickness(0, 18, 0, 0), BorderBrush = System.Windows.Media.Brushes.LightSteelBlue, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(1) }; Grid.SetRow(frame, 1); layout.Children.Add(frame);
        var inside = new Grid(); inside.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); inside.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); frame.Child = inside;
        var toolbar = new DockPanel { Background = System.Windows.Media.Brushes.WhiteSmoke, LastChildFill = false, Margin = new Thickness(0, 0, 0, 1), Height = 54 }; var note = new TextBlock { Text = "Files and database exports are stored separately for targeted restores.", Foreground = System.Windows.Media.Brushes.SlateGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) }; DockPanel.SetDock(note, Dock.Left); toolbar.Children.Add(note);
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, Margin = new Thickness(10), SelectionMode = DataGridSelectionMode.Single };
        grid.Columns.Add(new DataGridTextColumn { Header = "Completed", Width = new DataGridLength(170), Binding = new System.Windows.Data.Binding("CompletedAt") { StringFormat = "dd MMM yyyy  HH:mm" } });
        grid.Columns.Add(new DataGridTextColumn { Header = "Type", Width = new DataGridLength(85), Binding = new System.Windows.Data.Binding("Category") });
        grid.Columns.Add(new DataGridTextColumn { Header = "Archive file", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new System.Windows.Data.Binding("FileName") });
        grid.Columns.Add(new DataGridTextColumn { Header = "Backup job", Width = new DataGridLength(150), Binding = new System.Windows.Data.Binding("JobName") });
        grid.Columns.Add(new DataGridTemplateColumn { Header = "Save / restore", Width = new DataGridLength(132), CellTemplate = ActionTemplate() }); Grid.SetRow(grid, 1); inside.Children.Add(grid);
        void LoadCachedHistory() { var records = LoadLocalRemoteIndex(); grid.ItemsSource = records; summary.Text = records.Count == 0 ? "No cached archives - sync with remote" : $"{records.Count} cached archive{(records.Count == 1 ? "" : "s")}"; }
        async Task SyncRemoteAsync() { SetBusy(true, "Syncing backup history with remote server…"); summary.Text = "Syncing remote history…"; try { var records = await GetRemoteIndexAsync(); grid.ItemsSource = records; summary.Text = records.Count == 0 ? "No remote archives" : $"{records.Count} remote archive{(records.Count == 1 ? "" : "s")}"; } catch (Exception ex) { LoadCachedHistory(); MessageBox.Show(ex.Message, "Backup history sync", MessageBoxButton.OK, MessageBoxImage.Error); } finally { SetBusy(false); } }
        var refresh = Action("Sync with remote", async (_, _) => await SyncRemoteAsync()); refresh.Style = (Style)FindResource("PrimaryButton"); refresh.Margin = new Thickness(8, 0, 12, 0); refresh.MinWidth = 144; DockPanel.SetDock(refresh, Dock.Right); toolbar.Children.Add(refresh); inside.Children.Add(toolbar);
        LoadCachedHistory(); return layout;
    }
    private DataTemplate ActionTemplate()
    {
        var template = new DataTemplate(); var factory = new FrameworkElementFactory(typeof(Button)); factory.SetBinding(Button.ContentProperty, new System.Windows.Data.Binding("ActionLabel")); factory.SetValue(Button.MinWidthProperty, 105d); factory.SetValue(Button.HeightProperty, 30d); factory.SetValue(Button.FontSizeProperty, 12d); factory.SetValue(Button.PaddingProperty, new Thickness(8, 4, 8, 4)); factory.SetValue(Button.MarginProperty, new Thickness(6, 0, 6, 0)); factory.AddHandler(Button.ClickEvent, new RoutedEventHandler(async (s, e) => { if (s is Button b && b.DataContext is RemoteBackupRecord item) { if (item.Category.Equals("mysql", StringComparison.OrdinalIgnoreCase)) await RestoreMySqlArchiveAsync(item); else await DownloadAndRestoreAsync(item); } })); template.VisualTree = factory; return template;
    }
    private async Task RestoreMySqlArchiveAsync(RemoteBackupRecord item)
    {
        var config = _config.RemoteFtp; var ftpPassword = config is null ? null : Unprotect(config.EncryptedPassword); if (config is null || string.IsNullOrEmpty(ftpPassword)) { MessageBox.Show("Configure and save FTP settings first.", "Restore database"); return; }
        var root = Path.Combine(Path.GetTempPath(), "BackupManager-Restore-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var archivePath = Path.Combine(root, item.FileName);
        MySqlRestoreLogEntry? restoreLog = null;
        try
        {
            SetBusy(true, "Downloading MySQL backup archive…"); var bytes = await DownloadRemoteBytesAsync(config, ftpPassword, RemoteArchivePath(config, item)); await File.WriteAllBytesAsync(archivePath, bytes); ZipFile.ExtractToDirectory(archivePath, root, true);
            var sqlFiles = Directory.EnumerateFiles(root, "*.sql", SearchOption.TopDirectoryOnly).Where(x => !Path.GetFileName(x).Equals("users-and-privileges.sql", StringComparison.OrdinalIgnoreCase)).ToList(); if (sqlFiles.Count == 0) throw new InvalidOperationException("This MySQL archive does not contain database SQL files.");
            SetBusy(false); var request = ShowMySqlRestoreDialog(sqlFiles, Path.Combine(root, "users-and-privileges.sql")); if (request is null) return;
            var targets = sqlFiles.Select(x => (SqlFile: x, DatabaseName: sqlFiles.Count == 1 ? request.TargetDatabase : Path.GetFileNameWithoutExtension(x))).ToList(); var targetText = string.Join(", ", targets.Select(x => x.DatabaseName));
            var impact = request.ReplaceExisting ? "Existing target database data will be deleted and replaced." : "Existing target databases will be kept; the SQL will be imported into them."; if (MessageBox.Show($"Restore this archive to local MySQL ({request.Connection.Host}:{request.Connection.Port})?\n\nDatabases: {targetText}\n{impact}\n\nContinue?", "Confirm database restore", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            restoreLog = new MySqlRestoreLogEntry(DateTimeOffset.Now, item.FileName, RemoteArchivePath(config, item), request.Connection.Host, request.Connection.Port, request.Connection.UserName, targets.Select(x => x.DatabaseName).ToList(), request.ReplaceExisting, request.RestoreUsersAndPrivileges, "Running", null, null);
            AppendMySqlRestoreLog(restoreLog);
            SetBusy(true, "Restoring MySQL database locally…"); await new MySqlRestoreService().RestoreAsync(request.Connection, request.Password, targets, request.ReplaceExisting, request.UsersAndPrivilegesFile, request.RestoreUsersAndPrivileges, CancellationToken.None); MessageBox.Show("MySQL restore completed successfully.", "Restore database");
            if (restoreLog is not null) CompleteMySqlRestoreLog(restoreLog, "Completed", null);
        }
        catch (Exception ex) { if (restoreLog is not null) CompleteMySqlRestoreLog(restoreLog, "Failed", ex.Message); MessageBox.Show(ex.Message, "Database restore failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { while (_busyDepth > 0) SetBusy(false); try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { } }
    }
    private string MySqlRestoreLogPath => Path.Combine(_dataPath, "mysql-restore-history.json");
    private void AppendMySqlRestoreLog(MySqlRestoreLogEntry entry) { var entries = LoadMySqlRestoreLog(); entries.Add(entry); SaveMySqlRestoreLog(entries); }
    private void CompleteMySqlRestoreLog(MySqlRestoreLogEntry entry, string status, string? error)
    {
        var entries = LoadMySqlRestoreLog(); var index = entries.FindIndex(x => x.Id == entry.Id);
        if (index < 0) entries.Add(entry with { Status = status, CompletedAt = DateTimeOffset.Now, Error = error });
        else entries[index] = entries[index] with { Status = status, CompletedAt = DateTimeOffset.Now, Error = error };
        SaveMySqlRestoreLog(entries);
    }
    private List<MySqlRestoreLogEntry> LoadMySqlRestoreLog()
    {
        try { return File.Exists(MySqlRestoreLogPath) ? JsonSerializer.Deserialize<List<MySqlRestoreLogEntry>>(File.ReadAllText(MySqlRestoreLogPath)) ?? [] : []; }
        catch { return []; }
    }
    private void SaveMySqlRestoreLog(List<MySqlRestoreLogEntry> entries) => File.WriteAllText(MySqlRestoreLogPath, JsonSerializer.Serialize(entries.OrderByDescending(x => x.StartedAt).Take(500), new JsonSerializerOptions { WriteIndented = true }));
    private MySqlRestoreRequest? ShowMySqlRestoreDialog(IReadOnlyList<string> sqlFiles, string usersAndPrivilegesFile)
    {
        var profiles = new MySqlDiscovery().FindWorkbenchProfiles(); if (profiles.Count == 0) { MessageBox.Show("No saved MySQL Workbench connection was found.", "Restore database"); return null; }
        var dialog = new Window { Title = "Restore MySQL backup", Width = 680, Height = 560, MinWidth = 680, MinHeight = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize, Background = System.Windows.Media.Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(30) }; panel.Children.Add(Text("Restore database backup", 22)); panel.Children.Add(Text("Select the local MySQL server where this archive should be restored.", 13));
        var profile = new System.Windows.Controls.ComboBox { DisplayMemberPath = "Name", Height = 34, Margin = new Thickness(0, 12, 0, 8) }; foreach (var item in profiles) profile.Items.Add(item); profile.SelectedIndex = 0; panel.Children.Add(profile);
        var savedPassword = GetSavedMySqlPassword();
        panel.Children.Add(Text("Local MySQL password", 13));
        var password = new PasswordBox { Height = 34, Margin = new Thickness(0, 4, 0, 8), Visibility = string.IsNullOrEmpty(savedPassword) ? Visibility.Visible : Visibility.Collapsed };
        panel.Children.Add(password);
        panel.Children.Add(Text(string.IsNullOrEmpty(savedPassword) ? "No saved password found. Enter it to continue." : "Saved MySQL password will be used automatically. Enter a password only if the saved one is rejected.", 12));
        var target = new TextBox { Text = Path.GetFileNameWithoutExtension(sqlFiles[0]), Height = 34, Margin = new Thickness(0, 4, 0, 6), IsEnabled = sqlFiles.Count == 1 }; panel.Children.Add(Text(sqlFiles.Count == 1 ? "Target database name" : "This archive contains multiple databases; their original names will be restored.")); panel.Children.Add(target);
        var replace = new System.Windows.Controls.CheckBox { Content = "Erase and recreate target database before restore", Margin = new Thickness(0, 10, 0, 4) }; panel.Children.Add(replace);
        var users = new System.Windows.Controls.CheckBox { Content = "Also restore users and privileges", IsChecked = false, IsEnabled = File.Exists(usersAndPrivilegesFile), Margin = new Thickness(0, 4, 0, 8) }; panel.Children.Add(users);
        panel.Children.Add(Text("Restoring users requires a MySQL administrative account. This action is confirmed before any database data is changed.", 12));
        MySqlRestoreRequest? result = null; var actions = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) }; var cancel = Action("Cancel", (_, _) => dialog.Close()); var restore = Action("Restore now", (_, _) => { if (profile.SelectedItem is not MySqlWorkbenchProfile selected) return; var secret = password.Password.Length > 0 ? password.Password : savedPassword; if (string.IsNullOrEmpty(secret)) { MessageBox.Show("Enter the local MySQL password.", "Restore database"); return; } if (sqlFiles.Count == 1 && string.IsNullOrWhiteSpace(target.Text)) { MessageBox.Show("Enter a target database name.", "Restore database"); return; } result = new MySqlRestoreRequest(new MySqlConnectionOptions { Host = selected.Host, Port = selected.Port, UserName = selected.UserName }, secret, target.Text.Trim(), replace.IsChecked == true, users.IsChecked == true, usersAndPrivilegesFile); dialog.DialogResult = true; }); restore.Style = (Style)FindResource("PrimaryButton"); actions.Children.Add(cancel); actions.Children.Add(restore); panel.Children.Add(actions); dialog.Content = panel; dialog.ShowDialog(); return result;
    }
    private static string RemoteArchivePath(RemoteFtpConfig config, RemoteBackupRecord item) => config.RemoteFolder.Trim('/') + "/" + item.Category.ToLowerInvariant() + "/" + item.FileName;
    private async Task DownloadAndRestoreAsync(RemoteBackupRecord item)
    {
        var config = _config.RemoteFtp; var password = config is null ? null : Unprotect(config.EncryptedPassword); if (config is null || string.IsNullOrEmpty(password)) { MessageBox.Show("Configure and save FTP settings first.", "Restore"); return; }
        using var dialog = new Forms.FolderBrowserDialog { Description = "Choose where to save the backup archive", UseDescriptionForTitle = true }; if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        var target = Path.Combine(dialog.SelectedPath, item.FileName); if (File.Exists(target) && MessageBox.Show($"{item.FileName} already exists in this location. Replace it?", "Save archive", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SetBusy(true, $"Downloading {item.FileName}…");
        try { var bytes = await DownloadRemoteBytesAsync(config, password, RemoteArchivePath(config, item)); await File.WriteAllBytesAsync(target, bytes); MessageBox.Show($"Downloaded backup to:\n{target}", "Restore"); Process.Start("explorer.exe", "/select," + target); } catch (Exception ex) { MessageBox.Show(ex.Message, "Restore failed", MessageBoxButton.OK, MessageBoxImage.Error); } finally { SetBusy(false); }
    }
    private UIElement Restore() { var p = new StackPanel(); p.Children.Add(Text("Restore wizard", 18)); p.Children.Add(Text("Select a successful backup in Backup History, verify its checksum, select files/databases, choose a destination, and confirm overwrite impact.")); p.Children.Add(Action("Open backup location", (_, _) => Process.Start("explorer.exe", _dataPath))); return p; }
    private UIElement Logs() { var p = new StackPanel(); p.Children.Add(Text("Logs", 18)); p.Children.Add(Text("Service and backup logs are stored in ProgramData\\BackupManager\\logs. MySQL restore history is retained locally without passwords.")); p.Children.Add(Action("Open logs folder", (_, _) => { var x = Path.Combine(_dataPath, "logs"); Directory.CreateDirectory(x); Process.Start("explorer.exe", x); })); p.Children.Add(Action("Open MySQL restore history", (_, _) => { if (!File.Exists(MySqlRestoreLogPath)) File.WriteAllText(MySqlRestoreLogPath, "[]"); Process.Start("notepad.exe", MySqlRestoreLogPath); })); return p; }
    private UIElement Settings()
    {
        var p = new StackPanel(); p.Children.Add(Text("Settings", 18));
        p.Children.Add(Text("MySQL Workbench password"));
        p.Children.Add(Text("Save the password once for automatic database discovery and scheduled SQL exports. It is encrypted using Windows Data Protection for this Windows user."));
        var passwordBox = new PasswordBox { Height = 36, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, Margin = new Thickness(0, 12, 0, 8) };
        p.Children.Add(passwordBox);
        p.Children.Add(Action("Save password", (_, _) =>
        {
            if (passwordBox.Password.Length == 0) { MessageBox.Show("Enter the MySQL Workbench password first."); return; }
            _config.EncryptedMySqlPassword = Protect(passwordBox.Password);
            Save(); MessageBox.Show("MySQL password saved securely for this Windows user.");
        }));
        return p;
    }
    private string? GetSavedMySqlPassword()
    {
        if (string.IsNullOrWhiteSpace(_config.EncryptedMySqlPassword)) return null;
        return Unprotect(_config.EncryptedMySqlPassword);
    }
    private void AddFolder() { using var d = new Forms.FolderBrowserDialog { Description = "Select a folder to include in backups" }; if (d.ShowDialog() != Forms.DialogResult.OK) return; if (_config.Jobs.Count == 0) CreateJob(d.SelectedPath); else { _config.Jobs[0].Sources.Add(new BackupSource(Guid.NewGuid(), d.SelectedPath)); Save(); ShowPage("Sources"); } }
    private void CreateJob(string? initialFolder = null) { var destination = Path.Combine(_dataPath, "Backups"); Directory.CreateDirectory(destination); var job = new BackupJob { Name = $"Backup {DateTime.Now:HHmmss}", DestinationPath = destination, Schedule = new Schedule("Manual") }; if (initialFolder is not null) job.Sources.Add(new BackupSource(Guid.NewGuid(), initialFolder)); _config.Jobs.Add(job); Save(); ShowPage("Jobs"); }
    private async void RunNow(object sender, RoutedEventArgs e) { if (_config.Jobs.Count == 0) { MessageBox.Show("Create a backup job first."); return; } await Run(_config.Jobs[0]); }
    private async void RunSelected() { if (_config.Jobs.Count == 0) { MessageBox.Show("Create a backup job first."); return; } await Run(_config.Jobs[0]); }
    private async Task Run(BackupJob job)
    {
        if (_backupInProgress) { MessageBox.Show("A backup is already running. Its current step is shown beneath the page title."); return; }
        SetBusy(true, $"Preparing {job.Name} backup…");
        _backupInProgress = true; RunBackupButton.IsEnabled = false; RunBackupButton.Content = "Creating backup…"; PageSubtitle.Text = $"Backup in progress — preparing {job.Name}";
        var progress = new Progress<BackupProgress>(update => { PageSubtitle.Text = $"Backup in progress — {update.Message}"; if (update.State == BackupRunState.Uploading) RunBackupButton.Content = "Uploading to server…"; });
        try { string? password = null; if (job.MySqlConnection is { } connection) { _sessionMySqlPasswords.TryGetValue($"{connection.Host}:{connection.Port}:{connection.UserName}", out password); password ??= GetSavedMySqlPassword(); } var result = await new ArchiveService().CreateAsync(job, progress, CancellationToken.None, password); _config.LastBackup = result.CompletedAt; job.LastRun = result.CompletedAt; job.NextRun = ScheduleCalculator.Next(job.Schedule, result.CompletedAt); string? remotePath = null; string? warning = null; var status = result.State.ToString(); try { remotePath = await UploadBackupAsync(job, result.ArchivePath, progress); } catch (Exception uploadError) { status = BackupRunState.CompletedWithWarnings.ToString(); warning = $"Local ZIP created, but remote upload failed: {uploadError.Message}"; } _config.History.Add(new RunRecord(job.Name, result.CompletedAt, status, result.ArchivePath, result.Sha256, remotePath, warning)); Save(); MessageBox.Show(warning is null ? $"Backup completed and uploaded.\nLocal: {result.ArchivePath}\nRemote: {remotePath}" : $"{warning}\n\nLocal backup is safe at:\n{result.ArchivePath}", warning is null ? "Backup Manager" : "Backup completed with warning", MessageBoxButton.OK, warning is null ? MessageBoxImage.Information : MessageBoxImage.Warning); ShowPage("Dashboard"); } catch (Exception ex) { PageSubtitle.Text = "Backup failed — review the error message"; MessageBox.Show(ex.Message, "Backup failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SetBusy(false); _backupInProgress = false; RunBackupButton.IsEnabled = true; RunBackupButton.Content = "Run Backup and Upload Now"; }
    }
    private void TogglePause(object sender, RoutedEventArgs e) { _config.Paused = !_config.Paused; PauseButton.Content = _config.Paused ? "Resume" : "Pause"; Save(); ShowPage("Dashboard"); }
    private static long FolderSize(string path) { try { return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0; } }) : 0; } catch { return 0; } }
    private static string FormatSize(long bytes) { string[] units = ["B", "KB", "MB", "GB", "TB"]; var value = (double)bytes; var i = 0; while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; } return $"{value:0.##} {units[i]}"; }
}
public sealed class DesktopConfig { public bool Paused { get; set; } public int RetentionDays { get; set; } = 7; public List<BackupJob> Jobs { get; set; } = []; public List<RunRecord> History { get; set; } = []; public DateTimeOffset? LastBackup { get; set; } public DateTimeOffset? LastBackupAttempt { get; set; } public string? LastBackupStatus { get; set; } public string? LastBackupError { get; set; } public string? EncryptedMySqlPassword { get; set; } public RemoteFtpConfig? RemoteFtp { get; set; } }
public sealed class RemoteFtpConfig { public string Host { get; set; } = ""; public int Port { get; set; } = 21; public string UserName { get; set; } = ""; public string RemoteFolder { get; set; } = "/backups"; public bool UseFtps { get; set; } = true; public bool TrustInvalidCertificate { get; set; } = true; public string? EncryptedPassword { get; set; } }
public sealed record RemoteBackupRecord(string FileName, string Category, DateTimeOffset CompletedAt, string Sha256, string JobName) { public string ActionLabel => Category.Equals("mysql", StringComparison.OrdinalIgnoreCase) ? "Restore database" : "Save archive"; }
public sealed record MySqlRestoreRequest(MySqlConnectionOptions Connection, string Password, string TargetDatabase, bool ReplaceExisting, bool RestoreUsersAndPrivileges, string UsersAndPrivilegesFile);
public sealed record MySqlRestoreLogEntry(DateTimeOffset StartedAt, string ArchiveFile, string RemotePath, string ServerHost, int ServerPort, string UserName, List<string> Databases, bool ReplacedExisting, bool RestoredUsersAndPrivileges, string Status, DateTimeOffset? CompletedAt, string? Error)
{
    public Guid Id { get; init; } = Guid.NewGuid();
}
public sealed record SelectedFolderView(Guid JobId, Guid SourceId, string JobName, string Path) { public string Display => $"{Path}  —  {JobName}"; }
public sealed record SelectedDatabaseView(Guid JobId, Guid DatabaseId, string JobName, string DatabaseName)
{
    public override string ToString() => $"{DatabaseName}   —   {JobName}";
}
public sealed record RunRecord(string JobName, DateTimeOffset CompletedAt, string Status, string ArchivePath, string Sha256, string? RemotePath = null, string? Warning = null);
