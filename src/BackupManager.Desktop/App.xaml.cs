using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
namespace BackupManager.Desktop;
public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _tray;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); _tray = new Forms.NotifyIcon { Icon = SystemIcons.Shield, Text = "Backup Manager", Visible = true };
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("Open Backup Manager", null, (_, _) => OpenMainWindow()); menu.Items.Add("Pause Backups", null, (_, _) => MessageBox.Show("Pause is available from the desktop dashboard.")); menu.Items.Add("Exit UI", null, (_, _) => { _tray.Visible = false; Shutdown(); }); _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) OpenMainWindow(); };
        _tray.DoubleClick += (_, _) => OpenMainWindow();
    }
    protected override void OnExit(ExitEventArgs e) { _tray?.Dispose(); base.OnExit(e); }
    private void OpenMainWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (Current.MainWindow is not { } window) return;
            if (!window.IsVisible) window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
            window.Topmost = true; window.Topmost = false;
            window.Focus();
        });
    }
}
