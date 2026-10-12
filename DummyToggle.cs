// DummyToggle - attaches/detaches one monitor (typically a dummy HDMI/DP plug) to the Windows
// desktop, the same thing "Extend" / "Disconnect this display" in Display settings do. No admin needed.
//
//   DummyToggle.exe            first run: setup. Afterwards: toggle.
//   DummyToggle.exe on|off     silent, for scripts (Sunshine do/undo). Errors go to %APPDATA%\DummyMonitorToggle\DummyToggle.log
//   DummyToggle.exe toggle     toggle and flash the new state on screen
//   DummyToggle.exe setup      pick the monitor again and recreate the shortcut
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
    const string ShortcutName = "Monitor Toggle.lnk";
    const string OldShortcutName = "Dummy HDMI Toggle.lnk";

    static readonly string ExePath = Assembly.GetExecutingAssembly().Location;
    static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DummyMonitorToggle");
    static readonly string ConfigPath = Path.Combine(DataDir, "monitor.txt"); // line 1: device id, line 2: name
    static readonly string OldConfigPath = Path.Combine(Path.GetDirectoryName(ExePath), "dummy-monitor.txt");
    static readonly string LogPath = Path.Combine(DataDir, "DummyToggle.log");

    static string monitorId;
    static string monitorName;

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

    [StructLayout(LayoutKind.Sequential)]
    struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator, refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [In, Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint numModes, IntPtr modes, IntPtr currentTopologyId);
    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);
    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(uint numPaths, [In] DISPLAYCONFIG_PATH_INFO[] paths, uint numModes, IntPtr modes, uint flags);

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
    const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
    const uint SDC_APPLY = 0x00000080;
    const uint SDC_SAVE_TO_DATABASE = 0x00000200;
    const uint SDC_ALLOW_CHANGES = 0x00000400;
    const uint QDC_ALL_PATHS = 0x1;
    const uint DISPLAYCONFIG_PATH_ACTIVE = 0x1;
    const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xffffffff;
    const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    const int DISPLAYCONFIG_MODE_INFO_SIZE = 64;

    [STAThread]
    static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant().TrimStart('-', '/') : "";

        bool silent = cmd == "on" || cmd == "off";
        Application.EnableVisualStyles();

        try
        {
            bool configured = LoadConfig();
            if (cmd == "")
                cmd = configured ? "toggle" : "setup";

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
                    Flash(monitorName + (now ? ": ON" : ": OFF"), now);
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

    // ---- settings --------------------------------------------------------

    static bool LoadConfig()
    {
        if (File.Exists(ConfigPath))
        {
            string[] lines = File.ReadAllLines(ConfigPath);
            if (lines.Length == 0 || lines[0].Trim() == "") return false;
            monitorId = lines[0].Trim();
            monitorName = lines.Length > 1 && lines[1].Trim() != "" ? lines[1].Trim() : "Monitor";
            return true;
        }

        // Older versions kept the choice next to the exe.
        if (File.Exists(OldConfigPath))
        {
            string id = File.ReadAllText(OldConfigPath).Trim();
            if (id == "") return false;
            string name = "Monitor";
            try
            {
                foreach (Mon m in GetMonitors())
                    if (string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) name = m.Name;
            }
            catch (Exception)
            {
            }
            SaveConfig(id, name);
            return true;
        }
        return false;
    }

    static void SaveConfig(string id, string name)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllLines(ConfigPath, new string[] { id, name });
        monitorId = id;
        monitorName = name;
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

    // A device path is \\?\DISPLAY#GSM5B7F#5&...#{guid}; the saved id is DISPLAY\GSM5B7F\5&...
    static bool Matches(string devicePath, string id)
    {
        return (devicePath ?? "").Replace('#', '\\').IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Looks up the output (\\.\DISPLAYn) the monitor hangs off. Returns false if Windows doesn't list it.
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
                if (!Matches(mon.DeviceID, id)) continue;

                // A detached monitor can be listed under another monitor's output, so only trust
                // the output when the monitor itself is flagged active on it.
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

    static int CountMonitors()
    {
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DISPLAY_DEVICE adapter = new DISPLAY_DEVICE();
        adapter.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
        for (uint i = 0; EnumDisplayDevices(null, i, ref adapter, 0); i++)
        {
            DISPLAY_DEVICE mon = new DISPLAY_DEVICE();
            mon.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            for (uint j = 0; EnumDisplayDevices(adapter.DeviceName, j, ref mon, EDD_GET_DEVICE_INTERFACE_NAME); j++)
                if (!string.IsNullOrEmpty(mon.DeviceID)) seen.Add(mon.DeviceID);
        }
        return seen.Count;
    }

    // Adds just this monitor to the current layout, leaving other inactive displays alone.
    // Returns false if it could not be done; the caller then falls back to a plain "extend".
    static bool AttachOnly(string id)
    {
        uint numPaths, numModes;
        if (GetDisplayConfigBufferSizes(QDC_ALL_PATHS, out numPaths, out numModes) != 0) return false;

        DISPLAYCONFIG_PATH_INFO[] paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
        IntPtr modes = Marshal.AllocHGlobal((int)numModes * DISPLAYCONFIG_MODE_INFO_SIZE);
        try
        {
            if (QueryDisplayConfig(QDC_ALL_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero) != 0) return false;

            List<DISPLAYCONFIG_PATH_INFO> layout = new List<DISPLAYCONFIG_PATH_INFO>();
            HashSet<string> usedSources = new HashSet<string>();
            for (int i = 0; i < numPaths; i++)
            {
                if ((paths[i].flags & DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;
                layout.Add(paths[i]);
                usedSources.Add(SourceKey(paths[i].sourceInfo));
            }

            for (int i = 0; i < numPaths; i++)
            {
                DISPLAYCONFIG_PATH_INFO p = paths[i];
                if ((p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0 || p.targetInfo.targetAvailable == 0) continue;
                if (usedSources.Contains(SourceKey(p.sourceInfo))) continue; // would clone an existing screen

                DISPLAYCONFIG_TARGET_DEVICE_NAME name = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                name.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                name.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_TARGET_DEVICE_NAME));
                name.adapterId = p.targetInfo.adapterId;
                name.id = p.targetInfo.id;
                if (DisplayConfigGetDeviceInfo(ref name) != 0 || !Matches(name.monitorDevicePath, id)) continue;

                // Let Windows pick the resolution and position.
                p.flags |= DISPLAYCONFIG_PATH_ACTIVE;
                p.sourceInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                p.targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                layout.Add(p);

                return SetDisplayConfig((uint)layout.Count, layout.ToArray(), numModes, modes,
                    SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE) == 0;
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(modes);
        }
    }

    static string SourceKey(DISPLAYCONFIG_PATH_SOURCE_INFO s)
    {
        return s.adapterId.HighPart + ":" + s.adapterId.LowPart + ":" + s.id;
    }

    static void Extend()
    {
        int rc = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
        if (rc != 0)
            throw new Exception("Windows refused to extend the desktop (error " + rc + ").");
    }

    static bool WaitFor(string id, bool on, int seconds)
    {
        string output;
        bool attached, primary;
        for (int i = 0; i < seconds * 4; i++)
        {
            Thread.Sleep(250);
            if (FindOutput(id, out output, out attached, out primary) ? attached == on : !on)
                return true;
        }
        return false;
    }

    // Returns the new state. want == null means toggle.
    static bool Set(bool? want)
    {
        if (monitorId == null)
            throw new Exception("No monitor chosen yet. Run DummyToggle.exe with no arguments first.");
        string id = monitorId;

        string output;
        bool attached, primary;
        bool found = FindOutput(id, out output, out attached, out primary);

        bool on = want ?? !attached;
        if (found && attached == on) return on;

        if (on)
        {
            // With two monitors "extend" is exactly right. With more, it would also switch on
            // every other inactive display, so try to add only ours first.
            bool targeted = false;
            if (CountMonitors() > 2)
            {
                try
                {
                    targeted = AttachOnly(id) && WaitFor(id, true, 3);
                }
                catch (Exception)
                {
                }
            }
            if (targeted) return true;
            Extend();
        }
        else
        {
            if (!attached) return false;
            if (primary)
                throw new Exception(monitorName + " is currently the main display. Make another monitor the main display first.");

            DEVMODE dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT; // 0x0 at 0,0 = detach
            int rc = ChangeDisplaySettingsEx(output, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            if (rc == 0)
                rc = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            if (rc != 0)
                throw new Exception("Windows refused to detach " + monitorName + " (error " + rc + ").");
        }

        if (WaitFor(id, on, 5)) return on;
        throw new Exception(monitorName + " did not turn " + (on ? "on" : "off") +
            ".\n\nCheck that it is plugged in and not disabled in Device Manager, then run \"DummyToggle.exe setup\" again.");
    }

    // ---- setup -----------------------------------------------------------

    static int Setup()
    {
        Mon picked = Pick();
        if (picked == null) return 1;
        SaveConfig(picked.Id, picked.Name);

        string old = DesktopPath(OldShortcutName);
        if (File.Exists(old)) File.Delete(old);

        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic lnk = shell.CreateShortcut(DesktopPath(ShortcutName));
        lnk.TargetPath = ExePath;
        lnk.Arguments = "toggle";
        lnk.IconLocation = Path.Combine(Environment.SystemDirectory, "shell32.dll") + ",15";
        lnk.Description = "Turn " + picked.Name + " on/off";
        lnk.Save();

        MessageBox.Show("Done.\n\nA shortcut \"Monitor Toggle\" is on your desktop. Double-click it to turn " +
            picked.Name + " on or off. You can rename it, move it or pin it wherever you like.\n\n" +
            "Don't move DummyToggle.exe afterwards - if you do, run \"DummyToggle.exe setup\" again.",
            "DummyToggle", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    static void Uninstall()
    {
        foreach (string path in new string[] { DesktopPath(ShortcutName), DesktopPath(OldShortcutName), ConfigPath, OldConfigPath, LogPath })
            if (File.Exists(path)) File.Delete(path);
        if (Directory.Exists(DataDir) && Directory.GetFileSystemEntries(DataDir).Length == 0) Directory.Delete(DataDir);
        MessageBox.Show("Desktop shortcut and saved settings removed. You can now delete DummyToggle.exe.", "DummyToggle");
    }

    static string DesktopPath(string name)
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name);
    }

    static Mon Pick()
    {
        List<Mon> mons = GetMonitors();
        if (mons.Count == 0)
            throw new Exception("No monitor devices found.");

        using (Form f = new Form())
        {
            f.Text = "DummyToggle - which monitor should be toggled?";
            f.ClientSize = new Size(640, 230);
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false;
            f.MinimizeBox = false;
            f.StartPosition = FormStartPosition.CenterScreen;

            Label l = new Label();
            l.Text = "Select the monitor this tool should turn on and off (your dummy plug, not your main screen).";
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
            return (Mon)lb.SelectedItem;
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
            Font font = new Font("Segoe UI", 16f, FontStyle.Bold);
            f.ClientSize = new Size(Math.Max(320, TextRenderer.MeasureText(text, font).Width + 60), 70);
            f.StartPosition = FormStartPosition.Manual;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            f.Location = new Point(wa.Left + (wa.Width - f.Width) / 2, wa.Bottom - f.Height - 60);

            Label l = new Label();
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.ForeColor = Color.White;
            l.Font = font;
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
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
        }
        catch (Exception)
        {
        }
    }
}
