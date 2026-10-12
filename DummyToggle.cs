// DummyToggle - attaches/detaches one monitor (the dummy HDMI plug) to the Windows desktop,
// the same thing "Extend" / "Disconnect this display" in Display settings do. No admin needed.
//
//   DummyToggle.exe            first run: setup. Afterwards: toggle.
//   DummyToggle.exe on|off     silent, for scripts (Sunshine do/undo). Errors go to DummyToggle.log
//   DummyToggle.exe toggle     toggle and flash the new state on screen
//   DummyToggle.exe setup      pick the dummy monitor again and recreate the shortcut
//   DummyToggle.exe uninstall  remove the desktop shortcut
//
// Written against C# 5 so the csc.exe that ships with Windows can build it (see build.cmd).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    const string ShortcutName = "Dummy HDMI Toggle.lnk";

    static readonly string ExePath = Assembly.GetExecutingAssembly().Location;
    static readonly string ExeDir = Path.GetDirectoryName(ExePath);
    static readonly string ConfigPath = Path.Combine(ExeDir, "dummy-monitor.txt");
    static readonly string LogPath = Path.Combine(ExeDir, "DummyToggle.log");

    class Mon
    {
        public string Id;
        public string Name;

        public override string ToString()
        {
            return Name + "   -   " + Id;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public int dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE mode, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string device, IntPtr mode, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(uint numPaths, IntPtr paths, uint numModes, IntPtr modes, uint flags);

    const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x1;
    const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1; // on an output
    const int DISPLAY_DEVICE_ACTIVE = 0x1;              // on a monitor
    const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    const int DM_POSITION = 0x20, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000;
    const uint CDS_UPDATEREGISTRY = 0x1, CDS_NORESET = 0x10000000;
    const uint SDC_TOPOLOGY_EXTEND = 0x00000004;
    const uint SDC_APPLY = 0x00000080;

    [STAThread]
    static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant().TrimStart('-', '/') : "";

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
            "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Monitor'"))
        {
            foreach (ManagementObject o in s.Get())
            {
                Mon m = new Mon();
                m.Id = o["PNPDeviceID"] as string;
                if (m.Id == null) continue;
                m.Name = (o["Name"] as string) ?? "Monitor";
                string nice;
                if (friendly.TryGetValue(m.Id, out nice)) m.Name = nice;
                list.Add(m);
            }
        }
        return list;
    }

    // Looks up the output (\\.\DISPLAYn) the dummy hangs off. Returns false if Windows doesn't list it.
    static bool FindOutput(string id, out string output, out bool attached, out bool primary)
    {
        output = null;
        attached = primary = false;
        bool found = false;

        DISPLAY_DEVICE adapter = new DISPLAY_DEVICE();
        adapter.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
        for (uint i = 0; EnumDisplayDevices(null, i, ref adapter, 0); i++)
        {
            DISPLAY_DEVICE mon = new DISPLAY_DEVICE();
            mon.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            for (uint j = 0; EnumDisplayDevices(adapter.DeviceName, j, ref mon, EDD_GET_DEVICE_INTERFACE_NAME); j++)
            {
                // DeviceID is \\?\DISPLAY#GSM5B7F#5&...#{guid}; the saved id is DISPLAY\GSM5B7F\5&...
                string devId = (mon.DeviceID ?? "").Replace('#', '\\');
                if (devId.IndexOf(id, StringComparison.OrdinalIgnoreCase) < 0) continue;

                // A detached monitor can be listed under another monitor's output, so only trust
                // the output when the dummy itself is flagged active on it.
                found = true;
                if ((mon.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0 &&
                    (adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
                {
                    output = adapter.DeviceName;
                    attached = true;
                    primary = (adapter.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
                    return true;
                }
            }
        }
        return found;
    }

    // Returns the new state. want == null means toggle.
    static bool Set(bool? want)
    {
        if (!File.Exists(ConfigPath))
            throw new Exception("No dummy monitor chosen yet. Run DummyToggle.exe with no arguments first.");
        string id = File.ReadAllText(ConfigPath).Trim();

        string output;
        bool attached, primary;
        bool found = FindOutput(id, out output, out attached, out primary);

        bool on = want ?? !attached;
        if (found && attached == on) return on;

        if (on)
        {
            int rc = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
            if (rc != 0)
                throw new Exception("Windows refused to extend the desktop (error " + rc + ").");
        }
        else
        {
            if (!attached) return false;
            if (primary)
                throw new Exception("The dummy is currently the main display. Switch back to your real monitor first.");

            DEVMODE dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT; // 0x0 at 0,0 = detach
            int rc = ChangeDisplaySettingsEx(output, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            if (rc == 0)
                rc = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            if (rc != 0)
                throw new Exception("Windows refused to detach the dummy (error " + rc + ").");
        }

        for (int i = 0; i < 20; i++) // up to 5 s
        {
            Thread.Sleep(250);
            if (FindOutput(id, out output, out attached, out primary) ? attached == on : !on)
                return on;
        }
        throw new Exception("The dummy monitor did not turn " + (on ? "on" : "off") +
            ".\n\nIf it is disabled in Device Manager, enable it there, then run \"DummyToggle.exe setup\" again.");
    }

    // ---- setup -----------------------------------------------------------

    static int Setup()
    {
        string id = Pick();
        if (id == null) return 1;
        File.WriteAllText(ConfigPath, id);

        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic lnk = shell.CreateShortcut(ShortcutPath());
        lnk.TargetPath = ExePath;
        lnk.Arguments = "toggle";
        lnk.IconLocation = Path.Combine(Environment.SystemDirectory, "shell32.dll") + ",15";
        lnk.Description = "Turn the dummy HDMI monitor on/off";
        lnk.Save();

        MessageBox.Show("Done.\n\nA shortcut \"Dummy HDMI Toggle\" is on your desktop. Double-click it to turn the " +
            "dummy monitor on or off. You can move it or pin it wherever you like.\n\n" +
            "Don't move DummyToggle.exe afterwards - if you do, run \"DummyToggle.exe setup\" again.",
            "DummyToggle", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    static void Uninstall()
    {
        if (File.Exists(ShortcutPath())) File.Delete(ShortcutPath());
        MessageBox.Show("Desktop shortcut removed.", "DummyToggle");
    }

    static string ShortcutPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);
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
