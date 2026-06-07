using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Win32;

namespace Gnip.Tray;

/// <summary>
/// System-tray controller for the gnip Windows service. Shows service status as a coloured dot
/// and offers start/stop/restart plus quick admin actions. Service control needs admin, so those
/// actions relaunch this exe elevated (<c>--svc &lt;action&gt;</c>) which performs the action via
/// <see cref="ServiceController"/> and exits.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    internal const string ServiceName = "gnip";
    internal const string DashboardUrl = "http://localhost:5099";
    internal const string DataDir = @"C:\ProgramData\gnip";

    [STAThread]
    private static int Main(string[] args)
    {
        // Elevated helper mode: perform one service action and exit (no UI).
        if (args.Length >= 2 && args[0] == "--svc")
            return RunServiceAction(args[1]);

        ApplicationConfiguration.Initialize();
        using var ctx = new TrayContext();
        Application.Run(ctx);
        return 0;
    }

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static int RunServiceAction(string action)
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            switch (action)
            {
                case "start":
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, Wait);
                    }
                    break;
                case "stop":
                    if (sc.CanStop)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, Wait);
                    }
                    break;
                case "restart":
                    sc.Refresh();
                    if (sc.Status == ServiceControllerStatus.StartPending)
                        sc.WaitForStatus(ServiceControllerStatus.Running, Wait);
                    if (sc.CanStop)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, Wait);
                    }
                    sc.Refresh();
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, Wait);
                    }
                    break;
            }
            return 0;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            MessageBox.Show($"The '{ServiceName}' service did not reach the expected state within {Wait.TotalSeconds:0}s.",
                "gnip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Service '{ServiceName}' {action} failed:\n\n{ex.Message}", "gnip",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _header;
    private readonly ToolStripMenuItem _start;
    private readonly ToolStripMenuItem _stop;
    private readonly ToolStripMenuItem _restart;
    private readonly ToolStripMenuItem _autostart;
    private Icon? _dotIcon;

    public TrayContext()
    {
        _header = new ToolStripMenuItem("gnip") { Enabled = false };
        var open = new ToolStripMenuItem("Open dashboard", null, (_, _) => OpenShell(Program.DashboardUrl));
        _start = new ToolStripMenuItem("Start service", null, (_, _) => Control("start"));
        _stop = new ToolStripMenuItem("Stop service", null, (_, _) => Control("stop"));
        _restart = new ToolStripMenuItem("Restart service", null, (_, _) => Control("restart"));
        var data = new ToolStripMenuItem("Open data folder", null, (_, _) => OpenDataFolder());
        var logs = new ToolStripMenuItem("View logs (Event Viewer)", null, (_, _) => OpenShell("eventvwr.msc"));
        _autostart = new ToolStripMenuItem("Start tray at login", null, (_, _) => ToggleAutostart());
        var exit = new ToolStripMenuItem("Exit", null, (_, _) => ExitThread());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _header,
            new ToolStripSeparator(),
            open,
            new ToolStripSeparator(),
            _start, _stop, _restart,
            new ToolStripSeparator(),
            data, logs, _autostart,
            new ToolStripSeparator(),
            exit,
        });
        menu.Opening += (_, _) => UpdateStatus(); // refresh right before it's shown

        _icon = new NotifyIcon { Visible = true, ContextMenuStrip = menu, Text = "gnip" };
        _icon.DoubleClick += (_, _) => OpenShell(Program.DashboardUrl);

        _timer = new System.Windows.Forms.Timer { Interval = 3000 };
        _timer.Tick += (_, _) => UpdateStatus();
        _timer.Start();

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var (text, color, running, installed) = QueryStatus();
        _header.Text = $"gnip — {text}";
        _start.Enabled = installed && !running;
        _stop.Enabled = installed && running;
        _restart.Enabled = installed;
        _autostart.Checked = IsAutostart();

        var tip = $"gnip — {text}";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
        SetIcon(color);
    }

    private static (string text, Color color, bool running, bool installed) QueryStatus()
    {
        try
        {
            using var sc = new ServiceController(Program.ServiceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => ("running", Color.FromArgb(102, 187, 106), true, true),
                ServiceControllerStatus.Stopped => ("stopped", Color.FromArgb(120, 130, 145), false, true),
                ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => ("starting…", Color.FromArgb(255, 167, 38), false, true),
                ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => ("stopping…", Color.FromArgb(255, 167, 38), true, true),
                ServiceControllerStatus.Paused => ("paused", Color.FromArgb(255, 167, 38), false, true),
                _ => ("unknown", Color.Gray, false, true),
            };
        }
        catch (InvalidOperationException)
        {
            return ("not installed", Color.FromArgb(239, 83, 80), false, false);
        }
        catch
        {
            return ("unavailable", Color.FromArgb(239, 83, 80), false, false);
        }
    }

    private void Control(string action)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = $"--svc {action}",
                UseShellExecute = true,
                Verb = "runas", // elevate: controlling a service needs admin
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // user dismissed the UAC prompt — nothing to do
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not {action} the service:\n\n{ex.Message}", "gnip",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        // the next timer tick reflects the new state
    }

    private static void OpenShell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private static void OpenDataFolder()
    {
        try { Directory.CreateDirectory(Program.DataDir); } catch { /* ignore */ }
        OpenShell(Program.DataDir);
    }

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "GnipTray";

    private static bool IsAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) != null;
        }
        catch { return false; }
    }

    private void ToggleAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (IsAutostart())
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            else
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not update startup setting:\n\n{ex.Message}", "gnip",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        UpdateStatus();
    }

    private void SetIcon(Color color)
    {
        var previous = _dotIcon;
        _dotIcon = MakeDotIcon(color);
        _icon.Icon = _dotIcon;
        previous?.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Icon MakeDotIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 2, 2, 11, 11);
        }
        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone(); // own copy so the handle can be released
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _icon.Visible = false;
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
            _dotIcon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
