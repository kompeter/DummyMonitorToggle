// DummyToggle - enables/disables one monitor device (the dummy HDMI plug),
// the same thing Device Manager's "Disable device" does.
//
//   DummyToggle.exe            first run: setup. Afterwards: toggle.
//   DummyToggle.exe on|off     silent, for scripts (Sunshine do/undo). Errors go to DummyToggle.log
//   DummyToggle.exe toggle     toggle and flash the new state on screen
//   DummyToggle.exe setup      pick the dummy monitor again and recreate the shortcut
//   DummyToggle.exe uninstall  remove the scheduled task and desktop shortcut
//
// Written against C# 5 so the csc.exe that ships with Windows can build it (see build.cmd).

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    const string TaskName = "DummyMonitorToggle";
    const string ShortcutName = "Dummy HDMI Toggle.lnk";

    static readonly string ExePath = Assembly.GetExecutingAssembly().Location;
    static readonly string ExeDir = Path.GetDirectoryName(ExePath);
    static readonly string ConfigPath = Path.Combine(ExeDir, "dummy-monitor.txt");
    static readonly string LogPath = Path.Combine(ExeDir, "DummyToggle.log");

    class Mon
    {
        public string Id;
        public string Name;
        public bool Enabled;

        public override string ToString()
        {
            return (Enabled ? "[on]   " : "[off]  ") + Name + "   -   " + Id;
        }
    }

    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(uint numPaths, IntPtr paths, uint numModes, IntPtr modes, uint flags);
    const uint SDC_TOPOLOGY_EXTEND = 0x00000004;
    const uint SDC_APPLY = 0x00000080;

    [STAThread]
    static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant().TrimStart('-', '/') : "";

        // Enabling/disabling a device needs admin rights.
        if (!IsAdmin())
            return Elevate(args);

        bool silent = cmd == "on" || cmd == "off";
        Application.EnableVisualStyles();

        try
        {
            if (cmd == "")
                cmd = File.Exists(ConfigPath) ? "toggle" : "setup";

            switch (cmd)
            {
                case "setup":
                    return Setup();
                case "uninstall":
                    Uninstall();
                    return 0;
                case "on":
                    Set(true);
                    return 0;
                case "off":
                    Set(false);
                    return 0;
                case "toggle":
                    bool now = Set(null);
                    Flash(now ? "Dummy HDMI: ON" : "Dummy HDMI: OFF", now);
                    return 0;
                default:
                    throw new Exception("Unknown command: " + cmd + "\nUse: on, off, toggle, setup, uninstall");
            }
        }
        catch (Exception ex)
        {
            if (silent)
                Log(cmd + ": " + ex.Message);
            else
                MessageBox.Show(ex.Message, "DummyToggle", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    // ---- elevation -------------------------------------------------------

    static bool IsAdmin()
    {
        using (WindowsIdentity id = WindowsIdentity.GetCurrent())
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static int Elevate(string[] args)
    {
        ProcessStartInfo psi = new ProcessStartInfo(ExePath, string.Join(" ", args));
        psi.UseShellExecute = true;
        psi.Verb = "runas";
        try
        {
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }
        catch (Win32Exception)
        {
            return 1; // UAC prompt declined
        }
    }

    // ---- monitors --------------------------------------------------------

    static List<Mon> GetMonitors()
    {
        // Real model names ("LG ULTRAGEAR") are only available for monitors that are currently on.
        Dictionary<string, string> friendly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\wmi",
                "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID"))
            {
                foreach (ManagementObject o in s.Get())
                {
                    string inst = o["InstanceName"] as string;
                    ushort[] raw = o["UserFriendlyName"] as ushort[];
                    if (inst == null || raw == null) continue;

                    StringBuilder sb = new StringBuilder();
                    foreach (ushort c in raw)
                        if (c != 0) sb.Append((char)c);

                    int cut = inst.LastIndexOf('_'); // InstanceName is the device id plus "_0"
                    if (cut > 0) inst = inst.Substring(0, cut);
                    if (sb.Length > 0) friendly[inst] = sb.ToString();
                }
            }
        }
        catch (Exception)
        {
            // Not fatal, we just fall back to the generic device names.
        }

        List<Mon> list = new List<Mon>();
        using (ManagementObjectSearcher s = new ManagementObjectSearcher(
            "SELECT Name, PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass = 'Monitor'"))
        {
            foreach (ManagementObject o in s.Get())
            {
                Mon m = new Mon();
                m.Id = o["PNPDeviceID"] as string;
                if (m.Id == null) continue;
                m.Name = (o["Name"] as string) ?? "Monitor";
                string nice;
                if (friendly.TryGetValue(m.Id, out nice)) m.Name = nice;
                m.Enabled = Convert.ToInt32(o["ConfigManagerErrorCode"] ?? 0) != 22; // 22 = disabled
                list.Add(m);
            }
        }
        return list;
    }

    static Mon Find(List<Mon> list, string id)
    {
        foreach (Mon m in list)
            if (string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) return m;
        return null;
    }

    // Returns the new state. want == null means toggle.
    static bool Set(bool? want)
    {
        if (!File.Exists(ConfigPath))
            throw new Exception("No dummy monitor chosen yet. Run DummyToggle.exe with no arguments first.");
        string id = File.ReadAllText(ConfigPath).Trim();

        List<Mon> all = GetMonitors();
        Mon dummy = Find(all, id);
        if (dummy == null)
            throw new Exception("The saved dummy monitor was not found:\n" + id +
                "\n\nIs the plug still in the same port? Run \"DummyToggle.exe setup\" to pick it again.");

        bool on = want ?? !dummy.Enabled;
        if (dummy.Enabled == on) return on;

        if (!on)
        {
            bool otherOn = false;
            foreach (Mon m in all)
                if (m != dummy && m.Enabled) otherOn = true;
            if (!otherOn)
                throw new Exception("Refusing to disable the dummy: no other monitor is enabled.");
        }

        string output = PnpUtil(on, id);

        for (int i = 0; i < 40; i++) // up to 10 s
        {
            Thread.Sleep(250);
            Mon m = Find(GetMonitors(), id);
            if ((m == null && !on) || (m != null && m.Enabled == on))
            {
                if (on)
                {
                    // Give Windows a moment to see the display, then make sure it is part of the desktop.
                    Thread.Sleep(1000);
                    SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
                }
                return on;
            }
        }
        throw new Exception("Could not " + (on ? "enable" : "disable") + " the dummy monitor.\n\npnputil said:\n" + output);
    }

    static string PnpUtil(bool enable, string id)
    {
        ProcessStartInfo psi = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
            (enable ? "/enable-device" : "/disable-device") + " \"" + id + "\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        using (Process p = Process.Start(psi))
        {
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output.Trim();
        }
    }

    // ---- setup -----------------------------------------------------------

    static int Setup()
    {
        string id = Pick();
        if (id == null) return 1;
        File.WriteAllText(ConfigPath, id);

        // A scheduled task with "highest privileges" lets the shortcut toggle without a UAC prompt.
        string err = Run("schtasks.exe", "/create /f /tn \"" + TaskName + "\" /tr \"\\\"" + ExePath +
            "\\\" toggle\" /sc once /st 00:00 /rl highest");
        if (err != null)
            throw new Exception("Could not create the scheduled task:\n" + err);

        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic lnk = shell.CreateShortcut(ShortcutPath());
        lnk.TargetPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        lnk.Arguments = "/run /tn \"" + TaskName + "\"";
        lnk.WindowStyle = 7; // minimized, so no console window pops up
        lnk.IconLocation = Path.Combine(Environment.SystemDirectory, "shell32.dll") + ",15";
        lnk.Description = "Turn the dummy HDMI monitor on/off";
        lnk.Save();

        MessageBox.Show("Done.\n\nA shortcut \"Dummy HDMI Toggle\" is on your desktop. Double-click it to turn the " +
            "dummy monitor on or off (no admin prompt). You can move it or pin it wherever you like.\n\n" +
            "Don't move DummyToggle.exe afterwards - if you do, run \"DummyToggle.exe setup\" again.",
            "DummyToggle", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    static void Uninstall()
    {
        Run("schtasks.exe", "/delete /f /tn \"" + TaskName + "\"");
        if (File.Exists(ShortcutPath())) File.Delete(ShortcutPath());
        MessageBox.Show("Scheduled task and desktop shortcut removed.", "DummyToggle");
    }

    static string ShortcutPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);
    }

    // Returns null on success, otherwise the tool's output.
    static string Run(string exe, string arguments)
    {
        ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), arguments);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using (Process p = Process.Start(psi))
        {
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? null : output.Trim();
        }
    }

    static string Pick()
    {
        List<Mon> mons = GetMonitors();
        if (mons.Count == 0)
            throw new Exception("No monitor devices found.");

        using (Form f = new Form())
        {
            f.Text = "DummyToggle - which one is the dummy HDMI?";
            f.ClientSize = new Size(640, 230);
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false;
            f.MinimizeBox = false;
            f.StartPosition = FormStartPosition.CenterScreen;

            Label l = new Label();
            l.Text = "Select the monitor this tool should turn on and off. NOT your LG.";
            l.SetBounds(12, 10, 616, 20);

            ListBox lb = new ListBox();
            lb.SetBounds(12, 34, 616, 140);
            lb.HorizontalScrollbar = true;
            foreach (Mon m in mons) lb.Items.Add(m);

            Button ok = new Button();
            ok.Text = "Use this one";
            ok.SetBounds(508, 186, 120, 30);
            ok.DialogResult = DialogResult.OK;
            ok.Enabled = false;
            lb.SelectedIndexChanged += delegate { ok.Enabled = lb.SelectedItem != null; };

            f.Controls.Add(l);
            f.Controls.Add(lb);
            f.Controls.Add(ok);
            f.AcceptButton = ok;

            if (f.ShowDialog() != DialogResult.OK || lb.SelectedItem == null) return null;
            return ((Mon)lb.SelectedItem).Id;
        }
    }

    // ---- feedback --------------------------------------------------------

    static void Flash(string text, bool on)
    {
        using (Form f = new Form())
        {
            f.FormBorderStyle = FormBorderStyle.None;
            f.ShowInTaskbar = false;
            f.TopMost = true;
            f.BackColor = on ? Color.FromArgb(30, 120, 60) : Color.FromArgb(60, 60, 60);
            f.ClientSize = new Size(320, 70);
            f.StartPosition = FormStartPosition.Manual;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            f.Location = new Point(wa.Left + (wa.Width - f.Width) / 2, wa.Bottom - f.Height - 60);

            Label l = new Label();
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.ForeColor = Color.White;
            l.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
            l.Text = text;
            f.Controls.Add(l);

            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 1400;
            t.Tick += delegate { t.Stop(); f.Close(); };
            f.Shown += delegate { t.Start(); };
            Application.Run(f);
        }
    }

    static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
        }
        catch (Exception)
        {
        }
    }
}
