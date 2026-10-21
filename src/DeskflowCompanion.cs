using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DeskflowCompanion
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    internal static class NativeMethods
    {
        internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("dxva2.dll", EntryPoint = "GetNumberOfPhysicalMonitorsFromHMONITOR", CallingConvention = CallingConvention.StdCall)]
        internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll", EntryPoint = "GetPhysicalMonitorsFromHMONITOR", CallingConvention = CallingConvention.StdCall)]
        internal static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", EntryPoint = "DestroyPhysicalMonitors", CallingConvention = CallingConvention.StdCall)]
        internal static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", EntryPoint = "GetVCPFeatureAndVCPFeatureReply", CallingConvention = CallingConvention.StdCall)]
        internal static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte bVCPCode, out uint pvct, out uint pvcp, out uint pcmax);

        [DllImport("dxva2.dll", EntryPoint = "SetVCPFeature", CallingConvention = CallingConvention.StdCall)]
        internal static extern bool SetVCPFeature(IntPtr hMonitor, byte bVCPCode, uint dwNewValue);

        [DllImport("dxva2.dll", EntryPoint = "GetCapabilitiesStringLength", CallingConvention = CallingConvention.StdCall)]
        internal static extern bool GetCapabilitiesStringLength(IntPtr hMonitor, out uint pdwCapabilitiesStringLengthInCharacters);

        [DllImport("dxva2.dll", EntryPoint = "CapabilitiesRequestAndCapabilitiesReply", CallingConvention = CallingConvention.StdCall)]
        internal static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr hMonitor, byte[] pszASCIICapabilitiesString, uint dwCapabilitiesStringLengthInCharacters);

        [DllImport("user32.dll")]
        internal static extern bool SetProcessDPIAware();
    }

    internal sealed class MonitorInfo : IDisposable
    {
        public IntPtr Handle;
        public string DeviceName;
        public string DeviceId;
        public string Description;
        public string Model;
        public Rectangle Bounds;
        public int DisplayIndex;
        public uint CurrentInput;
        public readonly List<byte> Inputs = new List<byte>();
        public string Capabilities = "";

        public string Key
        {
            get
            {
                if (!String.IsNullOrEmpty(DeviceId)) return DeviceId;
                return DeviceName + "|" + Bounds.ToString();
            }
        }

        public string InputNames
        {
            get { return String.Join(" / ", Inputs.Select(InputName).ToArray()); }
        }

        public string SideLabel
        {
            get { return Bounds.X <= 0 ? "左侧" : "右侧"; }
        }

        public string DisplayLabel
        {
            get
            {
                string model = String.IsNullOrEmpty(Model) ? Description : Model;
                return String.Format("{0}显示器  ·  {1}  ·  {2}x{3}  ·  {4}",
                    SideLabel, model, Bounds.Width, Bounds.Height, DeviceName);
            }
        }

        public static string InputName(byte value)
        {
            switch (value)
            {
                case 0x0F: return "DisplayPort";
                case 0x11: return "HDMI 1";
                case 0x12: return "HDMI 2";
                case 0x1B: return "USB-C";
                case 0x13: return "HDMI 3";
                case 0x14: return "HDMI 4";
                default: return String.Format("输入 0x{0:X2}", value);
            }
        }

        public void Dispose()
        {
        }
    }

    internal static class MonitorCatalog
    {
        private static readonly List<PHYSICAL_MONITOR[]> OpenBatches = new List<PHYSICAL_MONITOR[]>();

        public static List<MonitorInfo> Enumerate()
        {
            DestroyAll();
            var found = new List<Tuple<MonitorInfo, PHYSICAL_MONITOR[]>>();
            NativeMethods.MonitorEnumProc callback = delegate(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data)
            {
                var mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi)) return true;

                uint count;
                if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out count) || count == 0) return true;
                var batch = new PHYSICAL_MONITOR[count];
                if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, batch)) return true;

                for (int i = 0; i < batch.Length; i++)
                {
                    var info = new MonitorInfo();
                    info.Handle = batch[i].hPhysicalMonitor;
                    info.DeviceName = mi.szDevice;
                    info.Description = batch[i].szPhysicalMonitorDescription;
                    info.Bounds = Rectangle.FromLTRB(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom);
                    info.DeviceId = GetMonitorDeviceId(mi.szDevice);
                    ReadVcp(info);
                    found.Add(Tuple.Create(info, batch));
                }
                return true;
            };

            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

            var monitors = found.Select(x => x.Item1).OrderBy(x => x.Bounds.X).ThenBy(x => x.Bounds.Y).ToList();
            for (int i = 0; i < monitors.Count; i++) monitors[i].DisplayIndex = i + 1;
            foreach (var pair in found)
            {
                if (!OpenBatches.Contains(pair.Item2)) OpenBatches.Add(pair.Item2);
            }
            return monitors;
        }

        private static string GetMonitorDeviceId(string deviceName)
        {
            try
            {
                var dd = new DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (NativeMethods.EnumDisplayDevices(deviceName, 0, ref dd, 0)) return dd.DeviceID;
            }
            catch { }
            return deviceName;
        }

        private static void ReadVcp(MonitorInfo info)
        {
            uint type, current, max;
            if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(info.Handle, 0x60, out type, out current, out max))
            {
                info.CurrentInput = current & 0xFF;
            }

            try
            {
                uint length;
                if (NativeMethods.GetCapabilitiesStringLength(info.Handle, out length) && length > 1 && length < 65536)
                {
                    var buffer = new byte[length];
                    if (NativeMethods.CapabilitiesRequestAndCapabilitiesReply(info.Handle, buffer, length))
                    {
                        int zero = Array.IndexOf(buffer, (byte)0);
                        int actual = zero >= 0 ? zero : buffer.Length;
                        info.Capabilities = Encoding.ASCII.GetString(buffer, 0, actual);
                        var model = Regex.Match(info.Capabilities, @"model\(([^)]*)\)", RegexOptions.IgnoreCase);
                        if (model.Success) info.Model = model.Groups[1].Value.Trim();
                        var inputMatch = Regex.Match(info.Capabilities, @"60\(([^)]*)\)", RegexOptions.IgnoreCase);
                        if (inputMatch.Success)
                        {
                            foreach (var token in inputMatch.Groups[1].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                byte value;
                                if (Byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) && !info.Inputs.Contains(value))
                                    info.Inputs.Add(value);
                            }
                        }
                    }
                }
            }
            catch { }

            if (info.Inputs.Count == 0)
            {
                info.Inputs.AddRange(new byte[] { 0x0F, 0x11, 0x12, 0x1B });
            }
            info.Inputs.Sort();
        }

        public static bool SetInput(MonitorInfo monitor, byte input)
        {
            if (monitor == null) return false;
            // Some Windows 11 display drivers return a zero DXVA2 physical-monitor handle but still
            // accept DDC/CI calls through it. Do not treat zero as invalid here.
            return NativeMethods.SetVCPFeature(monitor.Handle, 0x60, input);
        }

        public static void DestroyAll()
        {
            foreach (var batch in OpenBatches)
            {
                try { NativeMethods.DestroyPhysicalMonitors((uint)batch.Length, batch); }
                catch { }
            }
            OpenBatches.Clear();
        }
    }

    internal sealed class AppConfig
    {
        public string MonitorKey = "";
        public int WindowsInput = 0x11;
        public int MacInput = 0x12;
        public bool AutoSwitch = true;
        public bool EdgeSwitch = true;
        public string MacSide = "Left";
        public int EdgeDwellMs = 550;
        public int MacSwitchHoldMs = 2400;
        public string ServerName = "Z790UD";
        public string ClientName = "MacBook";
        public string DeskflowLog = @"C:\ProgramData\Deskflow\deskflow-daemon.log";
        public string DeskflowConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Deskflow\Deskflow.conf");
        public bool StartWithWindows;
        public int TransferPort = 45873;
        public string TransferToken = "";

        public static string ConfigDirectory
        {
            get
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (String.IsNullOrEmpty(root)) root = Path.GetDirectoryName(Application.ExecutablePath);
                return Path.Combine(root, "DeskflowCompanion");
            }
        }

        public static string ConfigPath { get { return Path.Combine(ConfigDirectory, "config.ini"); } }

        public static AppConfig Load()
        {
            var cfg = new AppConfig();
            try
            {
                if (!File.Exists(ConfigPath)) return cfg;
                foreach (var raw in File.ReadAllLines(ConfigPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int split = line.IndexOf('=');
                    if (split <= 0) continue;
                    string key = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();
                    switch (key.ToLowerInvariant())
                    {
                        case "monitorkey": cfg.MonitorKey = value; break;
                        case "windowsinput": cfg.WindowsInput = ParseInt(value, cfg.WindowsInput); break;
                        case "macinput": cfg.MacInput = ParseInt(value, cfg.MacInput); break;
                        case "autoswitch": cfg.AutoSwitch = ParseBool(value, cfg.AutoSwitch); break;
                        case "edgeswitch": cfg.EdgeSwitch = ParseBool(value, cfg.EdgeSwitch); break;
                        case "macside": cfg.MacSide = ParseSide(value); break;
                        case "edgedwellms": cfg.EdgeDwellMs = ParseInt(value, cfg.EdgeDwellMs); break;
                        case "macswitchholdms": cfg.MacSwitchHoldMs = ParseInt(value, cfg.MacSwitchHoldMs); break;
                        case "servername": cfg.ServerName = value; break;
                        case "clientname": cfg.ClientName = value; break;
                        case "deskflowlog": cfg.DeskflowLog = value; break;
                        case "deskflowconfig": cfg.DeskflowConfig = value; break;
                        case "startwithwindows": cfg.StartWithWindows = ParseBool(value, cfg.StartWithWindows); break;
                        case "transferport": cfg.TransferPort = ParseInt(value, cfg.TransferPort); break;
                        case "transfertoken": cfg.TransferToken = value; break;
                    }
                }
            }
            catch { }
            if (NormalizeInputs(cfg))
            {
                try { cfg.Save(); } catch { }
            }
            return cfg;
        }

        public void Save()
        {
            Directory.CreateDirectory(ConfigDirectory);
            var lines = new[]
            {
                "# Deskflow Companion configuration",
                "monitorKey=" + MonitorKey,
                "windowsInput=" + WindowsInput.ToString(CultureInfo.InvariantCulture),
                "macInput=" + MacInput.ToString(CultureInfo.InvariantCulture),
                "autoSwitch=" + AutoSwitch,
                "edgeSwitch=" + EdgeSwitch,
                "macSide=" + MacSide,
                "edgeDwellMs=" + EdgeDwellMs.ToString(CultureInfo.InvariantCulture),
                "macSwitchHoldMs=" + MacSwitchHoldMs.ToString(CultureInfo.InvariantCulture),
                "serverName=" + ServerName,
                "clientName=" + ClientName,
                "deskflowLog=" + DeskflowLog,
                "deskflowConfig=" + DeskflowConfig,
                "startWithWindows=" + StartWithWindows,
                "transferPort=" + TransferPort.ToString(CultureInfo.InvariantCulture),
                "transferToken=" + TransferToken
            };
            File.WriteAllLines(ConfigPath, lines, Encoding.UTF8);
        }

        public void EnsureTransferToken()
        {
            if (TransferPort < 1024 || TransferPort > 65000) TransferPort = 45873;
            if (String.IsNullOrEmpty(TransferToken) || TransferToken.Length < 8)
                TransferToken = Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        private static bool NormalizeInputs(AppConfig cfg)
        {
            bool changed = false;
            if (cfg.WindowsInput < 0 || cfg.WindowsInput > 0xFF) { cfg.WindowsInput = 0x11; changed = true; }
            if (cfg.MacInput < 0 || cfg.MacInput > 0xFF) { cfg.MacInput = 0x12; changed = true; }
            if (cfg.MacSwitchHoldMs < 1000) { cfg.MacSwitchHoldMs = 1000; changed = true; }
            if (cfg.MacSwitchHoldMs > 10000) { cfg.MacSwitchHoldMs = 10000; changed = true; }
            if (cfg.WindowsInput == cfg.MacInput)
            {
                cfg.MacInput = cfg.WindowsInput == 0x12 ? 0x11 : 0x12;
                changed = true;
            }
            return changed;
        }

        private static int ParseInt(string value, int fallback)
        {
            int parsed;
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                Int32.TryParse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed)) return parsed;
            return Int32.TryParse(value, out parsed) ? parsed : fallback;
        }

        private static bool ParseBool(string value, bool fallback)
        {
            bool parsed;
            return Boolean.TryParse(value, out parsed) ? parsed : fallback;
        }

        private static string ParseSide(string value)
        {
            return String.Equals(value, "Right", StringComparison.OrdinalIgnoreCase) ? "Right" : "Left";
        }
    }

    internal sealed class DeskflowLogReader
    {
        private readonly string path;
        private long position;
        private string carry = "";

        public DeskflowLogReader(string path)
        {
            this.path = path;
            ResetToEnd();
        }

        public void ResetToEnd()
        {
            carry = "";
            position = 0;
            try { if (File.Exists(path)) position = new FileInfo(path).Length; }
            catch { position = 0; }
        }

        public List<string> ReadNewLines()
        {
            var result = new List<string>();
            try
            {
                if (!File.Exists(path)) return result;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length < position) { position = 0; carry = ""; }
                    if (stream.Length == position) return result;
                    stream.Seek(position, SeekOrigin.Begin);
                    int available = (int)Math.Min(stream.Length - position, 1024 * 1024);
                    var bytes = new byte[available];
                    int read = stream.Read(bytes, 0, available);
                    position += read;
                    string text = carry + Encoding.UTF8.GetString(bytes, 0, read);
                    string[] parts = text.Split('\n');
                    carry = parts[parts.Length - 1];
                    for (int i = 0; i < parts.Length - 1; i++)
                    {
                        string line = parts[i].TrimEnd('\r');
                        if (line.Length > 0) result.Add(line);
                    }
                }
            }
            catch { }
            return result;
        }
    }

    internal sealed class InputChoice
    {
        public byte Value;
        public string Text;
        public override string ToString() { return Text; }
    }

    internal sealed class MainForm : Form
    {
        private readonly AppConfig config;
        private List<MonitorInfo> monitors = new List<MonitorInfo>();
        private MonitorInfo selectedMonitor;
        private DeskflowLogReader logReader;

        private ComboBox monitorCombo;
        private ComboBox windowsInputCombo;
        private ComboBox macInputCombo;
        private CheckBox autoCheckBox;
        private CheckBox edgeCheckBox;
        private CheckBox startupCheckBox;
        private ComboBox sideCombo;
        private ListBox pendingFilesList;
        private Label dropFilesLabel;
        private Label monitorInfoLabel;
        private Label statusLabel;
        private Label deskflowStatusLabel;
        private Label clipboardStatusLabel;
        private Label autoStatusLabel;
        private Panel homePanel;
        private Panel advancedPanel;
        private Button advancedToggleButton;
        private Label headerTitle;
        private Label headerSubtitle;
        private Label homeMonitorDetailLabel;
        private Label homeWindowsInputLabel;
        private Label homeMacInputLabel;
        private NotifyIcon tray;
        private ToolStripMenuItem autoTrayItem;
        private ToolStripMenuItem edgeTrayItem;
        private System.Windows.Forms.Timer logTimer;
        private System.Windows.Forms.Timer pendingTimer;
        private System.Windows.Forms.Timer cursorTimer;
        private System.Windows.Forms.Timer testRestoreTimer;
        private LanTransferServer transferServer;
        private Label transferUrlLabel;
        private Label transferStatusLabel;
        private Label receivedFilesLabel;
        private readonly List<Label> transferUrlLabels = new List<Label>();
        internal static bool PreviewMode;

        private string pendingTarget = "";
        private DateTime pendingSince = DateTime.MinValue;
        private DateTime lastSwitchTime = DateTime.MinValue;
        private DateTime lastMacActivation = DateTime.MinValue;
        private DateTime edgeEnteredAt = DateTime.MinValue;
        private DateTime suppressAutoUntil = DateTime.MinValue;
        private Rectangle edgeBounds = Rectangle.Empty;
        private bool edgeArmed = true;
        private string activeSide = "Windows";
        private bool testInProgress;
        private int testOriginalInput = -1;
        private bool exiting;
        private bool loadingUi;

        private static readonly Regex SwitchRegex = new Regex(
            "switch from \"(?<from>[^\"]+)\" to \"(?<to>[^\"]+)\" at",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public MainForm()
        {
            bool configExists = File.Exists(AppConfig.ConfigPath);
            config = AppConfig.Load();
            config.EnsureTransferToken();
            config.Save();
            BuildUi();
            StartTransferServer();
            LoadMonitors();
            InitializeLogReader();
            InitializeActiveSide();
            RefreshStatus();
            ApplyStartupState();
            if (!configExists && selectedMonitor != null) SaveConfig();
        }

        private void BuildUi()
        {
            Text = "Deskflow 增强控制台";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(980, 640);
            Size = new Size(1100, 720);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(243, 246, 250);
            AutoScaleMode = AutoScaleMode.Dpi;

            var contentHost = new Panel();
            contentHost.Dock = DockStyle.Fill;
            contentHost.Padding = new Padding(22, 20, 22, 18);
            contentHost.BackColor = Color.FromArgb(243, 246, 250);
            Controls.Add(contentHost);

            var header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 104;
            header.BackColor = Color.FromArgb(22, 38, 58);
            Controls.Add(header);

            headerTitle = new Label();
            headerTitle.Text = "两台电脑，一套键鼠";
            headerTitle.ForeColor = Color.White;
            headerTitle.Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold, GraphicsUnit.Point);
            headerTitle.AutoSize = true;
            headerTitle.Location = new Point(28, 18);
            header.Controls.Add(headerTitle);

            headerSubtitle = new Label();
            headerSubtitle.Text = "键鼠共享 · 显示器切换 · 文件与剪贴板互传";
            headerSubtitle.ForeColor = Color.FromArgb(186, 205, 226);
            headerSubtitle.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
            headerSubtitle.AutoSize = true;
            headerSubtitle.Location = new Point(31, 62);
            header.Controls.Add(headerSubtitle);

            advancedToggleButton = new Button();
            advancedToggleButton.Text = "高级设置";
            advancedToggleButton.Size = new Size(116, 36);
            advancedToggleButton.Location = new Point(header.Width - 146, 34);
            advancedToggleButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            advancedToggleButton.FlatStyle = FlatStyle.Flat;
            advancedToggleButton.FlatAppearance.BorderColor = Color.FromArgb(91, 116, 146);
            advancedToggleButton.FlatAppearance.BorderSize = 1;
            advancedToggleButton.BackColor = Color.FromArgb(35, 55, 79);
            advancedToggleButton.ForeColor = Color.White;
            advancedToggleButton.Cursor = Cursors.Hand;
            advancedToggleButton.Click += delegate { ShowAdvancedPanel(!advancedPanel.Visible); };
            header.Controls.Add(advancedToggleButton);

            advancedPanel = new Panel();
            advancedPanel.Dock = DockStyle.Fill;
            advancedPanel.BackColor = Color.FromArgb(243, 246, 250);
            advancedPanel.Visible = false;
            contentHost.Controls.Add(advancedPanel);

            var tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Point(18, 8);
            advancedPanel.Controls.Add(tabs);

            var switchPage = new TabPage("显示器与切换");
            switchPage.BackColor = Color.White;
            tabs.TabPages.Add(switchPage);
            BuildSwitchPage(switchPage);

            var filesPage = new TabPage("文件传输");
            filesPage.BackColor = Color.White;
            tabs.TabPages.Add(filesPage);
            BuildFilesPage(filesPage);

            var clipboardPage = new TabPage("剪贴板");
            clipboardPage.BackColor = Color.White;
            tabs.TabPages.Add(clipboardPage);
            BuildClipboardPage(clipboardPage);

            var helpPage = new TabPage("诊断与说明");
            helpPage.BackColor = Color.White;
            tabs.TabPages.Add(helpPage);
            BuildHelpPage(helpPage);

            autoStatusLabel = NewStatusLabel(0, 0);
            autoStatusLabel.Visible = false;
            advancedPanel.Controls.Add(autoStatusLabel);

            homePanel = new Panel();
            homePanel.Dock = DockStyle.Fill;
            homePanel.BackColor = Color.FromArgb(243, 246, 250);
            contentHost.Controls.Add(homePanel);

            var cards = new TableLayoutPanel();
            cards.Dock = DockStyle.Fill;
            cards.ColumnCount = 3;
            cards.RowCount = 1;
            cards.Padding = new Padding(0);
            cards.Margin = new Padding(0);
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31F));
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38F));
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31F));
            cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            homePanel.Controls.Add(cards);

            var connectionCard = CreateSimpleCard(cards, 0, "1  键鼠连接", "一套键盘和鼠标，同时操作 Windows 和 Mac。", Color.FromArgb(37, 99, 235));

            deskflowStatusLabel = NewStatusLabel(24, 116);
            deskflowStatusLabel.AutoSize = false;
            deskflowStatusLabel.Size = new Size(280, 28);
            deskflowStatusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            deskflowStatusLabel.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold, GraphicsUnit.Point);
            connectionCard.Controls.Add(deskflowStatusLabel);

            clipboardStatusLabel = NewStatusLabel(24, 158);
            clipboardStatusLabel.AutoSize = false;
            clipboardStatusLabel.Size = new Size(280, 26);
            clipboardStatusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            clipboardStatusLabel.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point);
            connectionCard.Controls.Add(clipboardStatusLabel);

            var clipboardHelp = new Label();
            clipboardHelp.Text = "在任一电脑复制文字或图片，\r\n切到另一台电脑后直接粘贴。";
            clipboardHelp.ForeColor = Color.FromArgb(92, 106, 123);
            clipboardHelp.Location = new Point(24, 204);
            clipboardHelp.Size = new Size(280, 58);
            clipboardHelp.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            connectionCard.Controls.Add(clipboardHelp);

            var connectionAction = new Panel();
            connectionAction.Dock = DockStyle.Bottom;
            connectionAction.Height = 86;
            connectionAction.BackColor = Color.White;
            connectionCard.Controls.Add(connectionAction);

            var checkDeskflow = CreateActionButton("检查键鼠连接", Color.FromArgb(37, 99, 235), Color.White, true);
            checkDeskflow.Height = 50;
            checkDeskflow.Margin = new Padding(0);
            checkDeskflow.Click += delegate { CheckDeskflowConnection(); };
            connectionAction.Controls.Add(checkDeskflow);

            var monitorCard = CreateSimpleCard(cards, 1, "2  显示器", "共享显示器可以在 Windows 和 Mac 之间切换。", Color.FromArgb(14, 165, 164));

            var preview = new TableLayoutPanel();
            preview.Location = new Point(24, 112);
            preview.Size = new Size(330, 108);
            preview.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            preview.ColumnCount = 2;
            preview.RowCount = 1;
            preview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            preview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            preview.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            monitorCard.Controls.Add(preview);

            var windowsTile = new Panel();
            windowsTile.Dock = DockStyle.Fill;
            windowsTile.Margin = new Padding(0, 0, 5, 0);
            windowsTile.BackColor = Color.FromArgb(239, 246, 255);
            windowsTile.BorderStyle = BorderStyle.FixedSingle;
            preview.Controls.Add(windowsTile, 0, 0);

            var windowsTileTitle = new Label();
            windowsTileTitle.Text = "Windows";
            windowsTileTitle.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold, GraphicsUnit.Point);
            windowsTileTitle.ForeColor = Color.FromArgb(30, 64, 175);
            windowsTileTitle.AutoSize = true;
            windowsTileTitle.Location = new Point(14, 18);
            windowsTile.Controls.Add(windowsTileTitle);

            homeWindowsInputLabel = new Label();
            homeWindowsInputLabel.Text = "HDMI 1";
            homeWindowsInputLabel.ForeColor = Color.FromArgb(71, 85, 105);
            homeWindowsInputLabel.AutoSize = true;
            homeWindowsInputLabel.Location = new Point(15, 55);
            windowsTile.Controls.Add(homeWindowsInputLabel);

            var macTile = new Panel();
            macTile.Dock = DockStyle.Fill;
            macTile.Margin = new Padding(5, 0, 0, 0);
            macTile.BackColor = Color.FromArgb(255, 247, 237);
            macTile.BorderStyle = BorderStyle.FixedSingle;
            preview.Controls.Add(macTile, 1, 0);

            var macTileTitle = new Label();
            macTileTitle.Text = "Mac";
            macTileTitle.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold, GraphicsUnit.Point);
            macTileTitle.ForeColor = Color.FromArgb(154, 52, 18);
            macTileTitle.AutoSize = true;
            macTileTitle.Location = new Point(14, 18);
            macTile.Controls.Add(macTileTitle);

            homeMacInputLabel = new Label();
            homeMacInputLabel.Text = "HDMI 2";
            homeMacInputLabel.ForeColor = Color.FromArgb(71, 85, 105);
            homeMacInputLabel.AutoSize = true;
            homeMacInputLabel.Location = new Point(15, 55);
            macTile.Controls.Add(homeMacInputLabel);

            homeMonitorDetailLabel = new Label();
            homeMonitorDetailLabel.Text = "正在检测共享显示器…";
            homeMonitorDetailLabel.ForeColor = Color.FromArgb(62, 76, 94);
            homeMonitorDetailLabel.Location = new Point(24, 232);
            homeMonitorDetailLabel.Size = new Size(330, 44);
            homeMonitorDetailLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            monitorCard.Controls.Add(homeMonitorDetailLabel);

            var monitorActions = new TableLayoutPanel();
            monitorActions.Dock = DockStyle.Bottom;
            monitorActions.Height = 144;
            monitorActions.BackColor = Color.White;
            monitorActions.ColumnCount = 2;
            monitorActions.RowCount = 2;
            monitorActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            monitorActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            monitorActions.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            monitorActions.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            monitorCard.Controls.Add(monitorActions);

            var toWindows = CreateActionButton("切到 Windows", Color.White, Color.FromArgb(30, 64, 175), false);
            toWindows.Margin = new Padding(0, 2, 4, 4);
            toWindows.Click += delegate { ManualSwitchToWindows(); };
            monitorActions.Controls.Add(toWindows, 0, 0);

            var toMac = CreateActionButton("切到 Mac", Color.FromArgb(20, 45, 69), Color.White, true);
            toMac.Margin = new Padding(4, 2, 0, 4);
            toMac.Click += delegate { ManualSwitchToMac(); };
            monitorActions.Controls.Add(toMac, 1, 0);

            var chooseMonitor = CreateActionButton("选择共享显示器", Color.FromArgb(236, 242, 249), Color.FromArgb(35, 56, 82), false);
            chooseMonitor.Margin = new Padding(0, 4, 0, 0);
            chooseMonitor.Click += delegate { ChooseSharedMonitor(); };
            monitorActions.Controls.Add(chooseMonitor, 0, 1);
            monitorActions.SetColumnSpan(chooseMonitor, 2);

            var filesCard = CreateSimpleCard(cards, 2, "3  文件与剪贴板", "拖入文件，Mac 用浏览器打开地址即可下载或上传。", Color.FromArgb(234, 88, 12));

            var dropZone = new Panel();
            dropZone.Location = new Point(24, 112);
            dropZone.Size = new Size(280, 126);
            dropZone.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            dropZone.BackColor = Color.FromArgb(247, 250, 255);
            dropZone.BorderStyle = BorderStyle.FixedSingle;
            dropZone.AllowDrop = true;
            dropZone.DragEnter += FilesDragEnter;
            dropZone.DragDrop += FilesDragDrop;
            filesCard.Controls.Add(dropZone);

            var dropTitle = new Label();
            dropTitle.Text = "把文件拖到这里发送";
            dropTitle.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold, GraphicsUnit.Point);
            dropTitle.ForeColor = Color.FromArgb(56, 68, 84);
            dropTitle.TextAlign = ContentAlignment.MiddleCenter;
            dropTitle.Location = new Point(10, 31);
            dropTitle.Size = new Size(258, 28);
            dropTitle.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            dropZone.Controls.Add(dropTitle);

            var dropHint = new Label();
            dropHint.Text = "Mac 不需要安装额外软件";
            dropHint.ForeColor = Color.FromArgb(105, 118, 135);
            dropHint.TextAlign = ContentAlignment.MiddleCenter;
            dropHint.Location = new Point(10, 67);
            dropHint.Size = new Size(258, 24);
            dropHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            dropZone.Controls.Add(dropHint);

            transferUrlLabel = new Label();
            transferUrlLabel.Text = "正在启动局域网传输…";
            transferUrlLabel.ForeColor = Color.FromArgb(71, 85, 105);
            transferUrlLabel.Location = new Point(24, 248);
            transferUrlLabel.Size = new Size(280, 52);
            transferUrlLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            filesCard.Controls.Add(transferUrlLabel);
            transferUrlLabels.Add(transferUrlLabel);

            var fileAction = new TableLayoutPanel();
            fileAction.Dock = DockStyle.Bottom;
            fileAction.Height = 112;
            fileAction.BackColor = Color.White;
            fileAction.ColumnCount = 2;
            fileAction.RowCount = 2;
            fileAction.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            fileAction.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            fileAction.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            fileAction.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            filesCard.Controls.Add(fileAction);

            var addFiles = CreateActionButton("添加文件", Color.FromArgb(234, 88, 12), Color.White, true);
            addFiles.Margin = new Padding(0, 0, 4, 4);
            addFiles.Click += delegate { AddFilesFromDialog(); };
            fileAction.Controls.Add(addFiles, 0, 0);

            var copyUrl = CreateActionButton("复制 Mac 地址", Color.FromArgb(236, 242, 249), Color.FromArgb(35, 56, 82), false);
            copyUrl.Margin = new Padding(4, 0, 0, 4);
            copyUrl.Click += delegate { CopyTransferUrl(false); };
            fileAction.Controls.Add(copyUrl, 1, 0);

            var openBridge = CreateActionButton("打开文件与剪贴板页", Color.FromArgb(236, 242, 249), Color.FromArgb(35, 56, 82), false);
            openBridge.Margin = new Padding(0, 4, 0, 0);
            openBridge.Click += delegate { OpenTransferPage(""); };
            fileAction.Controls.Add(openBridge, 0, 1);
            fileAction.SetColumnSpan(openBridge, 2);

            var statusStrip = new Panel();
            statusStrip.Dock = DockStyle.Bottom;
            statusStrip.Height = 38;
            statusStrip.BackColor = Color.FromArgb(243, 246, 250);
            homePanel.Controls.Add(statusStrip);

            statusLabel = new Label();
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.ForeColor = Color.FromArgb(91, 104, 120);
            statusLabel.Padding = new Padding(10, 0, 10, 0);
            statusStrip.Controls.Add(statusLabel);

            homePanel.BringToFront();

            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Application;
            tray.Text = "Deskflow 增强控制台";
            tray.Visible = true;
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示控制台", null, delegate { ShowConsole(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("切到 Mac", null, delegate { ManualSwitchToMac(); });
            menu.Items.Add("切回 Windows", null, delegate { ManualSwitchToWindows(); });
            menu.Items.Add("选择共享显示器", null, delegate { ChooseSharedMonitor(); });
            menu.Items.Add("打开文件与剪贴板页", null, delegate { OpenTransferPage(""); });
            menu.Items.Add(new ToolStripSeparator());
            autoTrayItem = new ToolStripMenuItem("自动随 Deskflow 切换");
            autoTrayItem.CheckOnClick = true;
            autoTrayItem.Click += delegate
            {
                if (loadingUi) return;
                config.AutoSwitch = autoTrayItem.Checked;
                autoCheckBox.Checked = autoTrayItem.Checked;
                SaveConfig();
                RefreshStatus();
            };
            menu.Items.Add(autoTrayItem);
            edgeTrayItem = new ToolStripMenuItem("边缘自动切换");
            edgeTrayItem.CheckOnClick = true;
            edgeTrayItem.Click += delegate
            {
                if (loadingUi) return;
                config.EdgeSwitch = edgeTrayItem.Checked;
                edgeCheckBox.Checked = edgeTrayItem.Checked;
                SaveConfig();
                RefreshStatus();
            };
            menu.Items.Add(edgeTrayItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("高级设置", null, delegate { ShowAdvancedPanel(true); });
            menu.Items.Add("退出", null, delegate { ExitApplication(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowConsole(); };

            logTimer = new System.Windows.Forms.Timer();
            logTimer.Interval = 250;
            logTimer.Tick += delegate { ReadLog(); };
            logTimer.Start();

            pendingTimer = new System.Windows.Forms.Timer();
            pendingTimer.Interval = 150;
            pendingTimer.Tick += delegate { ApplyPendingTarget(); };
            pendingTimer.Start();

            cursorTimer = new System.Windows.Forms.Timer();
            cursorTimer.Interval = 180;
            cursorTimer.Tick += delegate { CheckCursorEdge(); };
            cursorTimer.Start();

            testRestoreTimer = new System.Windows.Forms.Timer();
            testRestoreTimer.Interval = 10000;
            testRestoreTimer.Tick += delegate { RestoreTestSwitch(); };

            FormClosing += OnFormClosing;
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized) HideToTray();
            };
        }

        private Panel CreateSimpleCard(TableLayoutPanel cards, int column, string title, string subtitle, Color accent)
        {
            var card = new Panel();
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(7);
            card.BackColor = Color.White;
            card.BorderStyle = BorderStyle.FixedSingle;
            cards.Controls.Add(card, column, 0);

            var stripe = new Panel();
            stripe.Location = new Point(0, 0);
            stripe.Size = new Size(300, 7);
            stripe.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            stripe.BackColor = accent;
            card.Controls.Add(stripe);

            var titleLabel = new Label();
            titleLabel.Text = title;
            titleLabel.Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold, GraphicsUnit.Point);
            titleLabel.ForeColor = Color.FromArgb(32, 46, 64);
            titleLabel.AutoSize = true;
            titleLabel.Location = new Point(24, 24);
            card.Controls.Add(titleLabel);

            var subtitleLabel = new Label();
            subtitleLabel.Text = subtitle;
            subtitleLabel.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            subtitleLabel.ForeColor = Color.FromArgb(100, 113, 130);
            subtitleLabel.Location = new Point(24, 63);
            subtitleLabel.Size = new Size(280, 42);
            subtitleLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            card.Controls.Add(subtitleLabel);
            return card;
        }

        private Button CreateActionButton(string text, Color background, Color foreground, bool filled)
        {
            var button = new Button();
            button.Text = text;
            button.Dock = DockStyle.Fill;
            button.BackColor = background;
            button.ForeColor = foreground;
            button.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = filled ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(184, 199, 216);
            button.Cursor = Cursors.Hand;
            button.Margin = new Padding(0);
            return button;
        }

        private void ShowAdvancedPanel(bool show)
        {
            if (advancedPanel == null || homePanel == null) return;
            advancedPanel.Visible = show;
            homePanel.Visible = !show;
            if (show)
            {
                advancedPanel.BringToFront();
                headerTitle.Text = "高级设置";
                headerSubtitle.Text = "显示器映射、自动切换与诊断，一般不需要修改";
                advancedToggleButton.Text = "返回简单模式";
            }
            else
            {
                homePanel.BringToFront();
                headerTitle.Text = "两台电脑，一套键鼠";
                headerSubtitle.Text = "键鼠共享 · 显示器切换 · 文件与剪贴板互传";
                advancedToggleButton.Text = "高级设置";
                RefreshStatus();
            }
        }

        private void RefreshHomeSummary()
        {
            if (homeMonitorDetailLabel == null) return;
            homeWindowsInputLabel.Text = MonitorInfo.InputName((byte)config.WindowsInput);
            homeMacInputLabel.Text = MonitorInfo.InputName((byte)config.MacInput);
            if (selectedMonitor == null)
            {
                homeMonitorDetailLabel.Text = "尚未检测到共享显示器。\r\n请点击下方按钮选择。";
                return;
            }

            string inputName = MonitorInfo.InputName((byte)selectedMonitor.CurrentInput);
            string source;
            if (selectedMonitor.CurrentInput == (uint)config.MacInput) source = "Mac（" + inputName + "）";
            else if (selectedMonitor.CurrentInput == (uint)config.WindowsInput) source = "Windows（" + inputName + "）";
            else source = inputName;

            string displayName = selectedMonitor.DisplayLabel;
            int split = displayName.IndexOf(" · ", StringComparison.Ordinal);
            if (split > 0) displayName = displayName.Substring(0, split);
            if (!String.IsNullOrEmpty(selectedMonitor.Model)) displayName += " · " + selectedMonitor.Model;

            homeMonitorDetailLabel.Text = "共享显示器：" + displayName + "\r\n当前显示：" + source;
        }

        private void CheckDeskflowConnection()
        {
            RefreshStatus();
            bool running = Process.GetProcessesByName("deskflow-core").Length > 0;
            bool logExists = File.Exists(config.DeskflowLog);
            if (running && logExists)
            {
                statusLabel.Text = "键鼠连接正常，可以直接使用。";
                return;
            }

            DialogResult answer = MessageBox.Show(this,
                "没有检测到完整的 Deskflow 服务。\r\n\r\n是否现在打开 Deskflow？打开后请确认服务已启动。",
                "检查键鼠连接", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes)
            {
                OpenDeskflow();
                RefreshStatus();
            }
        }

        private void ChooseSharedMonitor()
        {
            if (monitors == null || monitors.Count == 0) LoadMonitors();
            if (monitors == null || monitors.Count == 0)
            {
                MessageBox.Show(this, "没有检测到支持 DDC/CI 的显示器。\r\n请确认显示器已连接并启用 DDC/CI。",
                    "选择共享显示器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var dialog = new Form())
            {
                dialog.Text = "选择共享显示器";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.ClientSize = new Size(580, 292);
                dialog.BackColor = Color.FromArgb(247, 249, 252);
                dialog.Font = Font;

                var title = new Label();
                title.Text = "哪一台显示器同时连接了 Windows 和 Mac？";
                title.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold, GraphicsUnit.Point);
                title.ForeColor = Color.FromArgb(32, 46, 64);
                title.AutoSize = true;
                title.Location = new Point(24, 22);
                dialog.Controls.Add(title);

                var help = new Label();
                help.Text = "选中后，上方“切到 Windows / Mac”会控制这台显示器。";
                help.ForeColor = Color.FromArgb(94, 108, 125);
                help.Location = new Point(26, 56);
                help.Size = new Size(520, 24);
                dialog.Controls.Add(help);

                var combo = new ComboBox();
                combo.DropDownStyle = ComboBoxStyle.DropDownList;
                combo.Location = new Point(28, 92);
                combo.Size = new Size(524, 30);
                for (int i = 0; i < monitors.Count; i++)
                {
                    MonitorInfo monitor = monitors[i];
                    string model = String.IsNullOrEmpty(monitor.Model) ? monitor.Description : monitor.Model;
                    combo.Items.Add(String.Format("显示器 {0} · {1} · 当前 {2}",
                        monitor.DisplayIndex, model, MonitorInfo.InputName((byte)monitor.CurrentInput)));
                }
                int selectedIndex = 0;
                for (int i = 0; i < monitors.Count; i++)
                {
                    if (selectedMonitor != null && monitors[i].Key == selectedMonitor.Key) { selectedIndex = i; break; }
                }
                combo.SelectedIndex = selectedIndex;
                dialog.Controls.Add(combo);

                var detail = new Label();
                detail.Location = new Point(28, 134);
                detail.Size = new Size(524, 44);
                detail.ForeColor = Color.FromArgb(68, 82, 99);
                dialog.Controls.Add(detail);

                EventHandler refreshDetail = delegate
                {
                    int index = combo.SelectedIndex;
                    if (index < 0 || index >= monitors.Count) { detail.Text = ""; return; }
                    MonitorInfo monitor = monitors[index];
                    detail.Text = "Windows 输入：" + MonitorInfo.InputName((byte)config.WindowsInput) +
                                   "    Mac 输入：" + MonitorInfo.InputName((byte)config.MacInput) +
                                   "\r\n当前信号：" + MonitorInfo.InputName((byte)monitor.CurrentInput);
                };
                combo.SelectedIndexChanged += refreshDetail;
                refreshDetail(null, EventArgs.Empty);

                var cancel = new Button();
                cancel.Text = "取消";
                cancel.Location = new Point(348, 220);
                cancel.Size = new Size(96, 38);
                cancel.DialogResult = DialogResult.Cancel;
                dialog.Controls.Add(cancel);

                var confirm = new Button();
                confirm.Text = "使用这个显示器";
                confirm.Location = new Point(454, 220);
                confirm.Size = new Size(98, 38);
                confirm.BackColor = Color.FromArgb(37, 99, 235);
                confirm.ForeColor = Color.White;
                confirm.FlatStyle = FlatStyle.Flat;
                confirm.FlatAppearance.BorderSize = 0;
                confirm.Click += delegate
                {
                    int index = combo.SelectedIndex;
                    if (index < 0 || index >= monitors.Count) return;
                    selectedMonitor = monitors[index];
                    config.MonitorKey = selectedMonitor.Key;
                    edgeBounds = selectedMonitor.Bounds;
                    if (monitorCombo != null && index < monitorCombo.Items.Count) monitorCombo.SelectedIndex = index;
                    PopulateInputCombos(selectedMonitor);
                    RefreshMonitorInfo();
                    if (!SaveConfig())
                    {
                        MessageBox.Show(this, statusLabel.Text, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                };
                dialog.Controls.Add(confirm);
                dialog.AcceptButton = confirm;
                dialog.CancelButton = cancel;
                dialog.ShowDialog(this);
                RefreshStatus();
            }
        }

        private Label NewStatusLabel(int x, int y)
        {
            var label = new Label();
            label.Location = new Point(x, y);
            label.Size = new Size(270, 22);
            label.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
            return label;
        }

        private void BuildSwitchPage(TabPage page)
        {
            var title = new Label();
            title.Text = "共享显示器";
            title.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold, GraphicsUnit.Point);
            title.AutoSize = true;
            title.Location = new Point(24, 22);
            page.Controls.Add(title);

            var hint = new Label();
            hint.Text = "选择同时连接 Windows 和 Mac 的共享显示器。鼠标到达 Mac 侧边缘并停留约 0.55 秒会切到 Mac；从 Mac 返回 Windows 时会自动切回。";
            hint.ForeColor = Color.FromArgb(91, 104, 120);
            hint.Location = new Point(26, 56);
            hint.Size = new Size(790, 42);
            page.Controls.Add(hint);

            monitorCombo = new ComboBox();
            monitorCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            monitorCombo.Location = new Point(28, 105);
            monitorCombo.Size = new Size(560, 30);
            monitorCombo.SelectedIndexChanged += delegate { SelectMonitorFromCombo(); };
            page.Controls.Add(monitorCombo);

            var refresh = new Button();
            refresh.Text = "重新扫描";
            refresh.Location = new Point(600, 103);
            refresh.Size = new Size(98, 31);
            refresh.Click += delegate { LoadMonitors(); };
            page.Controls.Add(refresh);

            monitorInfoLabel = new Label();
            monitorInfoLabel.Location = new Point(28, 145);
            monitorInfoLabel.Size = new Size(780, 44);
            monitorInfoLabel.ForeColor = Color.FromArgb(56, 68, 84);
            page.Controls.Add(monitorInfoLabel);

            var windowsGroup = new GroupBox();
            windowsGroup.Text = "Windows 输入";
            windowsGroup.Location = new Point(28, 200);
            windowsGroup.Size = new Size(360, 100);
            page.Controls.Add(windowsGroup);

            windowsInputCombo = new ComboBox();
            windowsInputCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            windowsInputCombo.Location = new Point(20, 38);
            windowsInputCombo.Size = new Size(315, 28);
            windowsGroup.Controls.Add(windowsInputCombo);

            var captureWin = new Button();
            captureWin.Text = "当前输入设为 Windows";
            captureWin.Location = new Point(20, 70);
            captureWin.Size = new Size(170, 25);
            captureWin.Click += delegate { CaptureCurrentInput(true); };
            windowsGroup.Controls.Add(captureWin);

            var macGroup = new GroupBox();
            macGroup.Text = "Mac 输入";
            macGroup.Location = new Point(410, 200);
            macGroup.Size = new Size(360, 100);
            page.Controls.Add(macGroup);

            macInputCombo = new ComboBox();
            macInputCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            macInputCombo.Location = new Point(20, 38);
            macInputCombo.Size = new Size(315, 28);
            macGroup.Controls.Add(macInputCombo);

            var captureMac = new Button();
            captureMac.Text = "当前输入设为 Mac";
            captureMac.Location = new Point(20, 70);
            captureMac.Size = new Size(170, 25);
            captureMac.Click += delegate { CaptureCurrentInput(false); };
            macGroup.Controls.Add(captureMac);

            autoCheckBox = new CheckBox();
            autoCheckBox.Text = "启用自动显示器切换";
            autoCheckBox.Location = new Point(30, 314);
            autoCheckBox.Size = new Size(300, 26);
            autoCheckBox.CheckedChanged += delegate
            {
                if (loadingUi) return;
                config.AutoSwitch = autoCheckBox.Checked;
                if (autoTrayItem != null) autoTrayItem.Checked = autoCheckBox.Checked;
                SaveConfig();
                RefreshStatus();
            };
            page.Controls.Add(autoCheckBox);

            edgeCheckBox = new CheckBox();
            edgeCheckBox.Text = "鼠标到达边缘并停留后切换到 Mac";
            edgeCheckBox.Location = new Point(340, 314);
            edgeCheckBox.Size = new Size(320, 26);
            edgeCheckBox.CheckedChanged += delegate
            {
                if (loadingUi) return;
                config.EdgeSwitch = edgeCheckBox.Checked;
                if (edgeTrayItem != null) edgeTrayItem.Checked = edgeCheckBox.Checked;
                SaveConfig();
                RefreshStatus();
            };
            page.Controls.Add(edgeCheckBox);

            var sideLabel = new Label();
            sideLabel.Text = "Mac 相对 Windows 的位置：";
            sideLabel.Location = new Point(30, 351);
            sideLabel.Size = new Size(176, 24);
            page.Controls.Add(sideLabel);

            sideCombo = new ComboBox();
            sideCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            sideCombo.Items.Add("左侧（Mac 在 Windows 左边）");
            sideCombo.Items.Add("右侧");
            sideCombo.Location = new Point(205, 347);
            sideCombo.Size = new Size(250, 28);
            sideCombo.SelectedIndexChanged += delegate
            {
                if (loadingUi) return;
                config.MacSide = sideCombo.SelectedIndex == 1 ? "Right" : "Left";
                SaveConfig();
                RefreshStatus();
            };
            page.Controls.Add(sideCombo);

            var sideHint = new Label();
            sideHint.Text = "停留约 0.55 秒；返回时由 Deskflow 触发";
            sideHint.ForeColor = Color.FromArgb(91, 104, 120);
            sideHint.Location = new Point(474, 351);
            sideHint.Size = new Size(300, 24);
            page.Controls.Add(sideHint);

            var save = new Button();
            save.Text = "保存设置";
            save.Location = new Point(30, 392);
            save.Size = new Size(110, 34);
            save.Click += delegate
            {
                if (SaveConfig())
                {
                    RefreshStatus();
                    MessageBox.Show(this, "设置已保存。", "Deskflow 增强控制台", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(this, statusLabel.Text, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            page.Controls.Add(save);

            var toMac = new Button();
            toMac.Text = "立即切到 Mac";
            toMac.Location = new Point(154, 392);
            toMac.Size = new Size(145, 34);
            toMac.Click += delegate { ManualSwitchToMac(); };
            page.Controls.Add(toMac);

            var toWindows = new Button();
            toWindows.Text = "立即切回 Windows";
            toWindows.Location = new Point(313, 392);
            toWindows.Size = new Size(160, 34);
            toWindows.Click += delegate { ManualSwitchToWindows(); };
            page.Controls.Add(toWindows);

            var test = new Button();
            test.Text = "测试此显示器（10 秒后恢复）";
            test.Location = new Point(487, 392);
            test.Size = new Size(220, 34);
            test.Click += delegate { TestSelectedMonitor(); };
            page.Controls.Add(test);

            var note = new Label();
            note.Text = "工作原理：Deskflow 负责键鼠共享；本工具根据 Windows 光标的实际边缘位置切换显示器 DDC/CI 输入，并从 Deskflow 日志确认返回 Windows。显示器切换本身通常需要 1–3 秒。";
            note.ForeColor = Color.FromArgb(91, 104, 120);
            note.Location = new Point(30, 443);
            note.Size = new Size(810, 52);
            page.Controls.Add(note);
        }

        private void BuildFilesPage(TabPage page)
        {
            var title = new Label();
            title.Text = "Windows ↔ Mac 文件与剪贴板";
            title.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold, GraphicsUnit.Point);
            title.AutoSize = true;
            title.Location = new Point(24, 22);
            page.Controls.Add(title);

            var text = new Label();
            text.Text = "把文件拖到下面，Mac 用浏览器打开控制台显示的地址，即可下载 Windows 文件或把 Mac 文件上传回来。\r\n" +
                        "剪贴板页还提供文字手动桥接；Deskflow 的自动剪贴板同步仍然保留。";
            text.ForeColor = Color.FromArgb(56, 68, 84);
            text.Location = new Point(28, 58);
            text.Size = new Size(820, 58);
            page.Controls.Add(text);

            var dropPanel = new Panel();
            dropPanel.Location = new Point(30, 120);
            dropPanel.Size = new Size(820, 78);
            dropPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            dropPanel.BackColor = Color.FromArgb(247, 250, 255);
            dropPanel.BorderStyle = BorderStyle.FixedSingle;
            dropPanel.AllowDrop = true;
            dropPanel.DragEnter += FilesDragEnter;
            dropPanel.DragDrop += FilesDragDrop;
            page.Controls.Add(dropPanel);

            dropFilesLabel = new Label();
            dropFilesLabel.Text = "把文件拖到这里发送\r\n也可以点击“添加文件”";
            dropFilesLabel.ForeColor = Color.FromArgb(68, 85, 105);
            dropFilesLabel.TextAlign = ContentAlignment.MiddleCenter;
            dropFilesLabel.Dock = DockStyle.Fill;
            dropFilesLabel.AllowDrop = true;
            dropFilesLabel.DragEnter += FilesDragEnter;
            dropFilesLabel.DragDrop += FilesDragDrop;
            dropPanel.Controls.Add(dropFilesLabel);

            pendingFilesList = new ListBox();
            pendingFilesList.Location = new Point(30, 210);
            pendingFilesList.Size = new Size(820, 142);
            pendingFilesList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            pendingFilesList.HorizontalScrollbar = true;
            pendingFilesList.AllowDrop = true;
            pendingFilesList.DragEnter += FilesDragEnter;
            pendingFilesList.DragDrop += FilesDragDrop;
            page.Controls.Add(pendingFilesList);

            transferUrlLabel = new Label();
            transferUrlLabel.Text = "正在启动局域网传输…";
            transferUrlLabel.ForeColor = Color.FromArgb(30, 64, 175);
            transferUrlLabel.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point);
            transferUrlLabel.Location = new Point(30, 361);
            transferUrlLabel.Size = new Size(820, 42);
            transferUrlLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(transferUrlLabel);
            transferUrlLabels.Add(transferUrlLabel);

            var addFiles = new Button();
            addFiles.Text = "添加文件";
            addFiles.Location = new Point(30, 410);
            addFiles.Size = new Size(118, 36);
            addFiles.Click += delegate { AddFilesFromDialog(); };
            page.Controls.Add(addFiles);

            var addFolder = new Button();
            addFolder.Text = "添加文件夹";
            addFolder.Location = new Point(158, 410);
            addFolder.Size = new Size(118, 36);
            addFolder.Click += delegate { AddFolderFromDialog(); };
            page.Controls.Add(addFolder);

            var copyUrl = new Button();
            copyUrl.Text = "复制 Mac 地址";
            copyUrl.Location = new Point(286, 410);
            copyUrl.Size = new Size(145, 36);
            copyUrl.Click += delegate { CopyTransferUrl(false); };
            page.Controls.Add(copyUrl);

            var openPage = new Button();
            openPage.Text = "打开传输页";
            openPage.Location = new Point(441, 410);
            openPage.Size = new Size(135, 36);
            openPage.Click += delegate { OpenTransferPage(""); };
            page.Controls.Add(openPage);

            var incoming = new Button();
            incoming.Text = "打开接收文件夹";
            incoming.Location = new Point(586, 410);
            incoming.Size = new Size(150, 36);
            incoming.Click += delegate { OpenIncomingFolder(); };
            page.Controls.Add(incoming);

            var clear = new Button();
            clear.Text = "清空共享";
            clear.Location = new Point(746, 410);
            clear.Size = new Size(104, 36);
            clear.Click += delegate
            {
                if (transferServer != null) transferServer.ClearOutgoing();
                if (pendingFilesList != null) pendingFilesList.Items.Clear();
                UpdateDropFilesLabel();
                RefreshTransferStatus();
            };
            page.Controls.Add(clear);

            transferStatusLabel = new Label();
            transferStatusLabel.Text = "局域网传输正在启动…";
            transferStatusLabel.ForeColor = Color.FromArgb(91, 104, 120);
            transferStatusLabel.Location = new Point(30, 461);
            transferStatusLabel.Size = new Size(820, 24);
            transferStatusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(transferStatusLabel);

            receivedFilesLabel = new Label();
            receivedFilesLabel.Text = "尚未接收 Mac 文件。";
            receivedFilesLabel.ForeColor = Color.FromArgb(91, 104, 120);
            receivedFilesLabel.Location = new Point(30, 492);
            receivedFilesLabel.Size = new Size(820, 48);
            receivedFilesLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(receivedFilesLabel);

            var note = new Label();
            note.Text = "Mac 端不需要安装额外程序：用 Safari 或 Chrome 打开复制的地址即可。请仅在可信局域网内使用。";
            note.ForeColor = Color.FromArgb(120, 128, 140);
            note.Location = new Point(30, 545);
            note.Size = new Size(820, 30);
            note.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(note);
        }

        private void StartTransferServer()
        {
            try
            {
                if (transferServer != null) transferServer.Dispose();
                transferServer = new LanTransferServer(
                    config.TransferPort,
                    config.TransferToken,
                    Environment.MachineName,
                    ReadWindowsClipboardText,
                    WriteWindowsClipboardText);

                transferServer.FileReceived += delegate(string path)
                {
                    PostToUi(delegate
                    {
                        statusLabel.Text = "已接收 Mac 文件：" + Path.GetFileName(path);
                        RefreshTransferStatus();
                    });
                };
                transferServer.ClipboardReceived += delegate(string text)
                {
                    PostToUi(delegate
                    {
                        statusLabel.Text = "已把 Mac 发来的文字写入 Windows 剪贴板。";
                        RefreshTransferStatus();
                    });
                };
                transferServer.TransferMessage += delegate(string message)
                {
                    PostToUi(delegate
                    {
                        statusLabel.Text = message;
                        RefreshTransferStatus();
                    });
                };
                transferServer.Start();
            }
            catch (Exception ex)
            {
                statusLabel.Text = "局域网传输启动失败：" + ex.Message;
            }
            RefreshTransferStatus();
        }

        private void RefreshTransferStatus()
        {
            bool running = transferServer != null && transferServer.IsRunning;
            string url = running ? transferServer.BuildPrimaryUrl() : "";
            if (running && PreviewMode) url = "http://192.168.1.20:45873/previewonly1/";

            foreach (Label label in transferUrlLabels)
            {
                if (label == null || label.IsDisposed) continue;
                label.Text = running
                    ? "Mac 打开：" + url
                    : "局域网传输未启动" + (transferServer != null && transferServer.LastError.Length > 0 ? "：" + transferServer.LastError : "。");
            }

            if (transferStatusLabel != null && !transferStatusLabel.IsDisposed)
            {
                if (running)
                {
                    int outgoingCount = transferServer.OutgoingCount;
                    transferStatusLabel.Text = "● 传输服务已开启 · 端口 " + transferServer.Port + " · Windows 已共享 " + outgoingCount + " 个文件";
                    transferStatusLabel.ForeColor = Color.FromArgb(25, 135, 84);
                }
                else
                {
                    transferStatusLabel.Text = "● 传输服务未开启，请检查端口或防火墙设置。";
                    transferStatusLabel.ForeColor = Color.FromArgb(200, 92, 42);
                }
            }

            if (receivedFilesLabel != null && !receivedFilesLabel.IsDisposed)
            {
                TransferFileInfo[] received = transferServer == null ? new TransferFileInfo[0] : transferServer.GetIncomingSnapshot();
                if (received.Length == 0)
                {
                    receivedFilesLabel.Text = "尚未接收 Mac 文件。Mac 上传后会保存到“下载\\Deskflow Companion”。";
                }
                else
                {
                    string names = String.Join("、", received.Take(3).Select(x => x.Name).ToArray());
                    receivedFilesLabel.Text = "最近接收（" + received.Length + "）：" + names + (received.Length > 3 ? " …" : "");
                }
            }
        }

        private void AddFilesFromDialog()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "选择要发送到 Mac 的文件";
                dialog.Multiselect = true;
                dialog.CheckFileExists = true;
                dialog.Filter = "所有文件 (*.*)|*.*";
                if (dialog.ShowDialog(this) == DialogResult.OK) AddQueuedFiles(dialog.FileNames);
            }
        }

        private void AddFolderFromDialog()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择要发送到 Mac 的文件夹";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) == DialogResult.OK) AddQueuedFiles(new[] { dialog.SelectedPath });
            }
        }

        private void CopyTransferUrl(bool openAfterCopy)
        {
            if (transferServer == null || !transferServer.IsRunning)
            {
                MessageBox.Show(this, "局域网传输服务尚未启动。请先检查 Windows 防火墙或端口占用。", "文件传输", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string url = transferServer.BuildPrimaryUrl();
            try
            {
                Clipboard.SetText(url);
                statusLabel.Text = "Mac 访问地址已复制，请在同一局域网的 Mac 浏览器中打开。";
                if (openAfterCopy) OpenTransferPage("");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "复制地址失败：" + ex.Message + "\r\n\r\n请手动复制：" + url, "文件传输", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenTransferPage(string section)
        {
            if (transferServer == null || !transferServer.IsRunning)
            {
                MessageBox.Show(this, "局域网传输服务尚未启动。", "文件传输", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string url = transferServer.BuildPrimaryUrl();
            if (!String.IsNullOrEmpty(section)) url += "?section=" + Uri.EscapeDataString(section);
            OpenUrl(url);
        }

        private void OpenIncomingFolder()
        {
            try
            {
                string folder = transferServer != null
                    ? transferServer.IncomingDirectory
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Deskflow Companion");
                Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开接收文件夹失败：" + ex.Message, "文件传输", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string ReadWindowsClipboardText()
        {
            if (InvokeRequired)
            {
                try { return (string)Invoke(new Func<string>(ReadWindowsClipboardText)); }
                catch { return ""; }
            }
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch { return ""; }
        }

        private void WriteWindowsClipboardText(string text)
        {
            if (InvokeRequired)
            {
                try { Invoke(new Action<string>(WriteWindowsClipboardText), text ?? ""); }
                catch { }
                return;
            }
            try { Clipboard.SetText(text ?? ""); }
            catch (Exception ex)
            {
                PostToUi(delegate { statusLabel.Text = "写入 Windows 剪贴板失败：" + ex.Message; });
            }
        }

        private void PostToUi(Action action)
        {
            if (action == null || IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke(action); }
            catch { }
        }

        private void FilesDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void FilesDragDrop(object sender, DragEventArgs e)
        {
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null) return;
            AddQueuedFiles(paths);
        }

        private void AddQueuedFiles(IEnumerable<string> paths)
        {
            if (pendingFilesList == null) return;
            var existing = new HashSet<string>(pendingFilesList.Items.Cast<string>(), StringComparer.OrdinalIgnoreCase);
            var addedPaths = new List<string>();
            foreach (string path in paths)
            {
                if (String.IsNullOrEmpty(path)) continue;
                if (!File.Exists(path) && !Directory.Exists(path)) continue;
                if (existing.Add(path))
                {
                    pendingFilesList.Items.Add(path);
                    addedPaths.Add(path);
                }
            }
            UpdateDropFilesLabel();
            int shared = transferServer == null ? 0 : transferServer.SharePaths(addedPaths);
            statusLabel.Text = "已选择 " + pendingFilesList.Items.Count + " 项，本次共享 " + shared + " 个文件。Mac 打开传输地址即可下载。";
            RefreshTransferStatus();
        }

        private void UpdateDropFilesLabel()
        {
            if (dropFilesLabel == null || pendingFilesList == null) return;
            dropFilesLabel.Text = pendingFilesList.Items.Count == 0
                ? "把文件或文件夹拖到这里\r\n添加后会立即共享给 Mac"
                : "已选择 " + pendingFilesList.Items.Count + " 项\r\nMac 打开上方地址即可下载";
        }

        private void BuildClipboardPage(TabPage page)
        {
            var title = new Label();
            title.Text = "剪贴板共享";
            title.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold, GraphicsUnit.Point);
            title.AutoSize = true;
            title.Location = new Point(24, 22);
            page.Controls.Add(title);

            var text = new Label();
            text.Text = "Deskflow 正常时，会自动同步 Windows ↔ Mac 的文字、HTML 和图片剪贴板。\r\n\r\n" +
                        "在 Windows 复制：Ctrl+C，在 Mac 粘贴：⌘V\r\n" +
                        "在 Mac 复制：⌘C，在 Windows 粘贴：Ctrl+V\r\n\r\n" +
                        "如果自动同步没有生效，在 Mac 浏览器打开传输页，使用“文字剪贴板”区域的\r\n" +
                        "“取 Windows 剪贴板 / 发送到 Windows 剪贴板”按钮即可手动互传文字。";
            text.ForeColor = Color.FromArgb(56, 68, 84);
            text.Location = new Point(28, 62);
            text.Size = new Size(810, 160);
            page.Controls.Add(text);

            var test = new Button();
            test.Text = "复制测试文本";
            test.Location = new Point(30, 240);
            test.Size = new Size(150, 36);
            test.Click += delegate
            {
                try
                {
                    Clipboard.SetText("Deskflow 剪贴板测试：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    MessageBox.Show(this, "测试文本已复制。请把鼠标移到 Mac，然后按 ⌘V 粘贴。", "剪贴板测试", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "复制失败：" + ex.Message, "剪贴板测试", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            page.Controls.Add(test);

            var testImage = new Button();
            testImage.Text = "复制测试图片";
            testImage.Location = new Point(194, 240);
            testImage.Size = new Size(150, 36);
            testImage.Click += delegate
            {
                try
                {
                    using (var bitmap = new Bitmap(360, 160))
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(Color.FromArgb(24, 46, 72));
                        using (var font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold, GraphicsUnit.Point))
                        using (var brush = new SolidBrush(Color.White))
                        {
                            graphics.DrawString("Deskflow 剪贴板", font, brush, new PointF(22, 28));
                        }
                        using (var small = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point))
                        using (var brush = new SolidBrush(Color.FromArgb(198, 219, 240)))
                        {
                            graphics.DrawString("复制后到 Mac 按 ⌘V 粘贴", small, brush, new PointF(24, 92));
                        }
                        Clipboard.SetImage(bitmap);
                    }
                    MessageBox.Show(this, "测试图片已复制。请把鼠标移到 Mac，然后按 ⌘V 粘贴到备忘录或图像应用。", "剪贴板测试", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "复制图片失败：" + ex.Message, "剪贴板测试", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            page.Controls.Add(testImage);

            var refresh = new Button();
            refresh.Text = "重新检查 Deskflow 配置";
            refresh.Location = new Point(358, 240);
            refresh.Size = new Size(190, 36);
            refresh.Click += delegate { RefreshStatus(); };
            page.Controls.Add(refresh);

            var openBridge = new Button();
            openBridge.Text = "在浏览器中打开文字桥接";
            openBridge.Location = new Point(562, 240);
            openBridge.Size = new Size(190, 36);
            openBridge.Click += delegate { OpenTransferPage("clipboard"); };
            page.Controls.Add(openBridge);
        }

        private void BuildHelpPage(TabPage page)
        {
            var text = new Label();
            text.Text = "运行条件\r\n" +
                        "1. Deskflow Server 正常运行，后台服务和 deskflow-core 均已启动。\r\n" +
                        "2. 共享显示器支持 DDC/CI，并且同时连接 Windows 与 Mac。\r\n" +
                        "3. Mac 的 Deskflow 客户端名称与配置中的 MacBook 一致。\r\n" +
                        "4. Deskflow 布局中 Mac 位于 Windows 左侧（或在本工具中改成右侧）。\r\n\r\n" +
                        "自动切换逻辑\r\n" +
                        "• 切到 Mac：Windows 光标到达共享显示器靠 Mac 一侧的边缘，并停留约 0.55 秒。\r\n" +
                        "• 切回 Windows：Deskflow 日志检测到键盘鼠标回到 Z790UD 后，等待短暂静默再切换。\r\n" +
                        "• 切换显示器通常需要约 1–3 秒；连续快速划动不会反复发送 DDC/CI 命令。\r\n\r\n" +
                        "已知限制\r\n" +
                        "• 某些显示器在输入未选中时不响应 DDC/CI，此时只能手动切换。\r\n" +
                        "• 文件传输由本控制台内置的局域网页面完成，Mac 不需要安装额外程序。\r\n" +
                        "• 文字可通过内置页面手动互传；HTML 和图片仍依赖 Deskflow 自动剪贴板。\r\n" +
                        "• 本工具不修改 Deskflow 核心，升级 Deskflow 不会覆盖本工具。";
            text.ForeColor = Color.FromArgb(56, 68, 84);
            text.Location = new Point(28, 22);
            text.Size = new Size(820, 286);
            page.Controls.Add(text);

            var diag = new Button();
            diag.Text = "复制诊断信息";
            diag.Location = new Point(30, 322);
            diag.Size = new Size(140, 34);
            diag.Click += delegate { CopyDiagnostics(); };
            page.Controls.Add(diag);

            var openLog = new Button();
            openLog.Text = "打开 Deskflow 日志";
            openLog.Location = new Point(184, 322);
            openLog.Size = new Size(160, 34);
            openLog.Click += delegate { OpenPath(config.DeskflowLog, true); };
            page.Controls.Add(openLog);

            var openDeskflow = new Button();
            openDeskflow.Text = "打开 Deskflow";
            openDeskflow.Location = new Point(358, 322);
            openDeskflow.Size = new Size(140, 34);
            openDeskflow.Click += delegate { OpenDeskflow(); };
            page.Controls.Add(openDeskflow);

            var autoStart = new CheckBox();
            autoStart.Text = "随 Windows 登录自动启动增强控制台";
            autoStart.Location = new Point(31, 376);
            autoStart.Size = new Size(280, 28);
            autoStart.CheckedChanged += StartupCheckBoxChanged;
            startupCheckBox = autoStart;
            page.Controls.Add(autoStart);
        }

        private void LoadMonitors()
        {
            try
            {
                bool hadSavedMonitor = !String.IsNullOrEmpty(config.MonitorKey);
                monitors = MonitorCatalog.Enumerate();
                monitorCombo.Items.Clear();
                foreach (var monitor in monitors) monitorCombo.Items.Add(monitor.DisplayLabel);

                int selected = 0;
                for (int i = 0; i < monitors.Count; i++)
                {
                    if (String.Equals(config.MonitorKey, monitors[i].Key, StringComparison.OrdinalIgnoreCase)) { selected = i; break; }
                }

                bool migratedSharedMonitor = false;
                if (hadSavedMonitor && monitors.Count > 1 && selected >= 0 && selected < monitors.Count &&
                    monitors[selected].CurrentInput == (uint)config.WindowsInput)
                {
                    int macCandidate = -1;
                    for (int i = 0; i < monitors.Count; i++)
                    {
                        if (i == selected || monitors[i].CurrentInput != (uint)config.MacInput) continue;
                        if (macCandidate >= 0) { macCandidate = -1; break; }
                        macCandidate = i;
                    }
                    if (macCandidate >= 0)
                    {
                        selected = macCandidate;
                        migratedSharedMonitor = true;
                    }
                }

                if (monitors.Count > 0)
                {
                    monitorCombo.SelectedIndex = selected;
                    selectedMonitor = monitors[selected];
                    config.MonitorKey = selectedMonitor.Key;
                    edgeBounds = selectedMonitor.Bounds;
                    if (!hadSavedMonitor && selectedMonitor.CurrentInput != 0 && selectedMonitor.CurrentInput != (uint)config.MacInput)
                        config.WindowsInput = (int)selectedMonitor.CurrentInput;
                    PopulateInputCombos(selectedMonitor);
                    RefreshMonitorInfo();
                    if (migratedSharedMonitor) SaveConfig();
                }
                else
                {
                    selectedMonitor = null;
                    monitorInfoLabel.Text = "未检测到支持 DDC/CI 的显示器。";
                }
                RefreshStatus();
            }
            catch (Exception ex)
            {
                monitorInfoLabel.Text = "扫描显示器失败：" + ex.Message;
            }
        }

        private void SelectMonitorFromCombo()
        {
            int index = monitorCombo.SelectedIndex;
            if (index < 0 || index >= monitors.Count) return;
            selectedMonitor = monitors[index];
            config.MonitorKey = selectedMonitor.Key;
            edgeBounds = selectedMonitor.Bounds;
            PopulateInputCombos(selectedMonitor);
            RefreshMonitorInfo();
        }

        private void PopulateInputCombos(MonitorInfo monitor)
        {
            windowsInputCombo.Items.Clear();
            macInputCombo.Items.Clear();
            var values = monitor.Inputs.Count > 0 ? monitor.Inputs : new List<byte> { 0x0F, 0x11, 0x12, 0x1B };
            foreach (var value in values)
            {
                string text = MonitorInfo.InputName(value) + String.Format(" (0x{0:X2})", value);
                windowsInputCombo.Items.Add(new InputChoice { Value = value, Text = text });
                macInputCombo.Items.Add(new InputChoice { Value = value, Text = text });
            }

            SelectComboValue(windowsInputCombo, config.WindowsInput);
            SelectComboValue(macInputCombo, config.MacInput);
            if (windowsInputCombo.SelectedIndex < 0) windowsInputCombo.SelectedIndex = FindClosestInput(windowsInputCombo, 0x11);
            if (macInputCombo.SelectedIndex < 0) macInputCombo.SelectedIndex = FindClosestInput(macInputCombo, 0x1B);
        }

        private static int FindClosestInput(ComboBox combo, int preferred)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                var c = combo.Items[i] as InputChoice;
                if (c != null && c.Value == preferred) return i;
            }
            return 0;
        }

        private static void SelectComboValue(ComboBox combo, int value)
        {
            combo.SelectedIndex = -1;
            for (int i = 0; i < combo.Items.Count; i++)
            {
                var c = combo.Items[i] as InputChoice;
                if (c != null && c.Value == value) { combo.SelectedIndex = i; return; }
            }
        }

        private void RefreshMonitorInfo()
        {
            if (selectedMonitor != null && monitorInfoLabel != null)
            {
                monitorInfoLabel.Text = String.Format("当前输入：{0}（0x{1:X2}）    可用输入：{2}\r\n位置：{3},{4}    设备：{5}",
                    MonitorInfo.InputName((byte)selectedMonitor.CurrentInput), selectedMonitor.CurrentInput,
                    selectedMonitor.InputNames, selectedMonitor.Bounds.X, selectedMonitor.Bounds.Y, selectedMonitor.DeviceName);
            }
            RefreshHomeSummary();
        }

        private byte SelectedWindowsInput
        {
            get
            {
                var c = windowsInputCombo.SelectedItem as InputChoice;
                return c == null ? (byte)config.WindowsInput : c.Value;
            }
        }

        private byte SelectedMacInput
        {
            get
            {
                var c = macInputCombo.SelectedItem as InputChoice;
                return c == null ? (byte)config.MacInput : c.Value;
            }
        }

        private void CaptureCurrentInput(bool asWindows)
        {
            if (selectedMonitor == null) return;
            uint type, current, max;
            if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(selectedMonitor.Handle, 0x60, out type, out current, out max))
            {
                byte value = (byte)(current & 0xFF);
                if (asWindows) SelectComboValue(windowsInputCombo, value);
                else SelectComboValue(macInputCombo, value);
                bool saved = SaveConfig();
                if (!saved)
                {
                    SelectComboValue(windowsInputCombo, config.WindowsInput);
                    SelectComboValue(macInputCombo, config.MacInput);
                }
                RefreshMonitorInfo();
                if (saved) MessageBox.Show(this, "已记录当前输入：" + MonitorInfo.InputName(value), "Deskflow 增强控制台", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this, "无法读取当前输入。请确认显示器支持 DDC/CI。", "Deskflow 增强控制台", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private bool ApplySwitch(string target, bool automatic, bool quiet)
        {
            if (selectedMonitor == null)
            {
                if (!quiet) MessageBox.Show(this, "请先选择共享显示器。", "Deskflow 增强控制台", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            byte windowsInput = SelectedWindowsInput;
            byte macInput = SelectedMacInput;
            if (windowsInput == macInput)
            {
                statusLabel.Text = "Windows 输入和 Mac 输入不能相同，请重新设置后再切换。";
                if (!quiet) MessageBox.Show(this, "Windows 输入和 Mac 输入不能相同。\r\n\r\n请分别选择 HDMI 1（Windows）和 HDMI 2（Mac）。", "输入配置错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            bool toMac = String.Equals(target, "Mac", StringComparison.OrdinalIgnoreCase);
            byte input = toMac ? macInput : windowsInput;
            DateTime now = DateTime.UtcNow;
            if (selectedMonitor.CurrentInput == input)
            {
                activeSide = toMac ? "Mac" : "Windows";
                suppressAutoUntil = now.AddMilliseconds(900);
                edgeEnteredAt = DateTime.MinValue;
                if (toMac) { lastMacActivation = now; edgeArmed = false; }
                RefreshMonitorInfo();
                statusLabel.Text = String.Format("显示器已经显示 {0}（{1}）。", toMac ? "Mac" : "Windows", MonitorInfo.InputName(input));
                return true;
            }

            bool ok = MonitorCatalog.SetInput(selectedMonitor, input);
            lastSwitchTime = now;
            if (ok)
            {
                selectedMonitor.CurrentInput = input;
                activeSide = toMac ? "Mac" : "Windows";
                edgeEnteredAt = DateTime.MinValue;
                if (toMac) { lastMacActivation = now; edgeArmed = false; }
                suppressAutoUntil = now.AddMilliseconds(1800);
            }
            else
            {
                edgeArmed = false;
                suppressAutoUntil = now.AddMilliseconds(1200);
            }
            RefreshMonitorInfo();
            if (ok)
            {
                statusLabel.Text = String.Format("已切换到 {0}（{1}）。", toMac ? "Mac" : "Windows", MonitorInfo.InputName(input));
                if (!quiet) tray.ShowBalloonTip(1500, "显示器已切换", MonitorInfo.InputName(input), ToolTipIcon.Info);
            }
            else
            {
                statusLabel.Text = "显示器输入切换失败。请检查 DDC/CI 是否已启用。";
                if (!quiet) MessageBox.Show(this, "显示器没有接受切换命令。\r\n\r\n请在显示器 OSD 中启用 DDC/CI，或手动切换一次后再测试。", "切换失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return ok;
        }

        private void InitializeActiveSide()
        {
            if (selectedMonitor == null) return;
            activeSide = selectedMonitor.CurrentInput == (uint)config.MacInput ? "Mac" : "Windows";
            if (String.Equals(activeSide, "Mac", StringComparison.OrdinalIgnoreCase)) lastMacActivation = DateTime.UtcNow;
            // Require the cursor to move away from the edge once after startup. This prevents
            // an unexpected DDC/CI switch when the app is launched while the cursor is at x=0.
            edgeArmed = false;
        }

        private void CheckCursorEdge()
        {
            if (testInProgress || selectedMonitor == null || !config.AutoSwitch || !config.EdgeSwitch)
            {
                edgeEnteredAt = DateTime.MinValue;
                return;
            }
            if (edgeBounds.IsEmpty && selectedMonitor != null) edgeBounds = selectedMonitor.Bounds;
            Point cursor = Cursor.Position;
            bool atEdge;
            bool insideY = cursor.Y >= edgeBounds.Top && cursor.Y < edgeBounds.Bottom;
            int inwardDistance;
            bool macOnLeft = !String.Equals(config.MacSide, "Right", StringComparison.OrdinalIgnoreCase);
            if (macOnLeft)
            {
                atEdge = insideY && cursor.X <= edgeBounds.Left + 3;
                inwardDistance = cursor.X - edgeBounds.Left;
            }
            else
            {
                atEdge = insideY && cursor.X >= edgeBounds.Right - 4;
                inwardDistance = edgeBounds.Right - 1 - cursor.X;
            }

            if (!atEdge)
            {
                edgeEnteredAt = DateTime.MinValue;
                if (inwardDistance >= 14) edgeArmed = true;
                return;
            }
            if (!edgeArmed || String.Equals(activeSide, "Mac", StringComparison.OrdinalIgnoreCase)) return;
            if (DateTime.UtcNow < suppressAutoUntil) return;

            if (edgeEnteredAt == DateTime.MinValue)
            {
                edgeEnteredAt = DateTime.UtcNow;
                statusLabel.Text = String.Format("检测到 Mac 侧边缘，停留约 {0:0.0} 秒后切换显示器。", config.EdgeDwellMs / 1000.0);
                return;
            }
            if ((DateTime.UtcNow - edgeEnteredAt).TotalMilliseconds < config.EdgeDwellMs) return;

            edgeEnteredAt = DateTime.MinValue;
            edgeArmed = false;
            if (!ApplySwitch("Mac", true, true))
                statusLabel.Text = "检测到边缘，但显示器没有接受 DDC/CI 切换命令。";
        }

        private void ManualSwitchToMac()
        {
            if (ApplySwitch("Mac", false, false)) edgeArmed = false;
        }

        private void ManualSwitchToWindows()
        {
            if (ApplySwitch("Windows", false, false))
            {
                edgeArmed = false;
                edgeEnteredAt = DateTime.MinValue;
                pendingTarget = "";
            }
        }

        private void TestSelectedMonitor()
        {
            if (selectedMonitor == null) return;
            uint type, current, max;
            if (!NativeMethods.GetVCPFeatureAndVCPFeatureReply(selectedMonitor.Handle, 0x60, out type, out current, out max))
            {
                MessageBox.Show(this, "无法读取当前显示器输入。", "测试失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            testOriginalInput = (int)(current & 0xFF);
            testInProgress = true;
            ApplySwitch("Mac", false, true);
            testRestoreTimer.Stop();
            testRestoreTimer.Start();
            statusLabel.Text = "测试切换已执行。10 秒后会自动恢复到原输入。";
            MessageBox.Show(this, "已尝试把该显示器切到 Mac。\r\n\r\n如果 Mac 画面出现，说明这就是共享显示器；10 秒后会自动切回。\r\n如果屏幕无信号，等待自动恢复或用托盘菜单切回 Windows。",
                "显示器识别", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void RestoreTestSwitch()
        {
            testRestoreTimer.Stop();
            if (!testInProgress || selectedMonitor == null || testOriginalInput < 0) return;
            bool ok = MonitorCatalog.SetInput(selectedMonitor, (byte)testOriginalInput);
            if (ok) selectedMonitor.CurrentInput = (uint)testOriginalInput;
            activeSide = ok && testOriginalInput == config.MacInput ? "Mac" : "Windows";
            edgeArmed = !ok || activeSide == "Windows";
            testInProgress = false;
            suppressAutoUntil = DateTime.UtcNow.AddMilliseconds(1200);
            statusLabel.Text = ok ? "测试已结束，显示器已恢复到原输入。" : "恢复测试输入失败，请使用托盘菜单手动切回 Windows。";
            RefreshMonitorInfo();
        }

        private void InitializeLogReader()
        {
            logReader = new DeskflowLogReader(config.DeskflowLog);
        }

        private void ReadLog()
        {
            if (logReader == null) return;
            var lines = logReader.ReadNewLines();
            foreach (var line in lines)
            {
                var match = SwitchRegex.Match(line);
                if (!match.Success) continue;
                string to = match.Groups["to"].Value;
                string target = "";
                if (String.Equals(to, config.ClientName, StringComparison.OrdinalIgnoreCase)) target = "Mac";
                else if (String.Equals(to, config.ServerName, StringComparison.OrdinalIgnoreCase)) target = "Windows";
                if (target.Length == 0) continue;

                DateTime now = DateTime.UtcNow;
                if (target == "Windows")
                {
                    // Ignore a Windows-return marker while the switch to Mac is still settling,
                    // and require a quiet period before acting on it. Deskflow often emits
                    // Mac -> Windows -> Mac pairs within a few milliseconds near an edge.
                    if (String.Equals(activeSide, "Mac", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(pendingTarget, "Windows", StringComparison.OrdinalIgnoreCase))
                    {
                        if (lastMacActivation != DateTime.MinValue &&
                            (now - lastMacActivation).TotalMilliseconds < config.MacSwitchHoldMs)
                        {
                            pendingTarget = "";
                            if (config.AutoSwitch) statusLabel.Text = "已忽略刚切到 Mac 后产生的短暂回跳信号。";
                            continue;
                        }
                        pendingTarget = "Windows";
                        pendingSince = now;
                    }
                }
                else
                {
                    // Any return-to-Mac marker cancels a pending Windows switch. The mouse
                    // has crossed back, so switching the monitor to Windows would be wrong.
                    if (String.Equals(pendingTarget, "Windows", StringComparison.OrdinalIgnoreCase))
                    {
                        pendingTarget = "";
                        pendingSince = DateTime.MinValue;
                        if (config.AutoSwitch) statusLabel.Text = "已取消回切 Windows：Deskflow 已重新返回 Mac。";
                    }
                    else if (!config.EdgeSwitch && !String.Equals(activeSide, "Mac", StringComparison.OrdinalIgnoreCase))
                    {
                        pendingTarget = "Mac";
                        pendingSince = now;
                    }
                }
            }
        }

        private void ApplyPendingTarget()
        {
            if (testInProgress && pendingTarget.Length > 0) pendingTarget = "";
            if (pendingTarget.Length == 0 || testInProgress || !config.AutoSwitch) return;
            if (DateTime.UtcNow < suppressAutoUntil) return;

            double requiredDelay = String.Equals(pendingTarget, "Windows", StringComparison.OrdinalIgnoreCase) ? 1200.0 : 550.0;
            if ((DateTime.UtcNow - pendingSince).TotalMilliseconds < requiredDelay) return;

            string target = pendingTarget;
            pendingTarget = "";
            if (String.Equals(target, "Windows", StringComparison.OrdinalIgnoreCase) &&
                String.Equals(activeSide, "Windows", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (String.Equals(target, "Mac", StringComparison.OrdinalIgnoreCase) && config.EdgeSwitch) return;
            ApplySwitch(target, true, true);
        }

        private bool SaveConfig()
        {
            try
            {
                byte windowsInput = SelectedWindowsInput;
                byte macInput = SelectedMacInput;
                if (windowsInput == macInput)
                {
                    statusLabel.Text = "保存失败：Windows 输入和 Mac 输入不能相同。";
                    MessageBox.Show(this, "Windows 输入和 Mac 输入不能相同。\r\n\r\n请分别选择 HDMI 1（Windows）和 HDMI 2（Mac），否则自动切换会把左屏持续切回 Windows。", "输入配置错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                config.WindowsInput = windowsInput;
                config.MacInput = macInput;
                config.AutoSwitch = autoCheckBox.Checked;
                config.EdgeSwitch = edgeCheckBox.Checked;
                config.MacSide = sideCombo.SelectedIndex == 1 ? "Right" : "Left";
                if (selectedMonitor != null) config.MonitorKey = selectedMonitor.Key;
                config.Save();
                return true;
            }
            catch (Exception ex)
            {
                statusLabel.Text = "保存设置失败：" + ex.Message;
                return false;
            }
        }

        private static bool IsDeskflowClipboardEnabled(string path)
        {
            var candidates = new List<string>();
            if (!String.IsNullOrEmpty(path))
            {
                candidates.Add(path);
                try
                {
                    string directory = Path.GetDirectoryName(path);
                    if (!String.IsNullOrEmpty(directory) &&
                        Path.GetFileName(path).Equals("Deskflow-server.conf", StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(Path.Combine(directory, "Deskflow.conf"));
                    }
                }
                catch { }
            }

            foreach (string candidate in candidates)
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    foreach (string raw in File.ReadAllLines(candidate))
                    {
                        string line = raw.Trim();
                        int split = line.IndexOf('=');
                        if (split <= 0) continue;
                        if (!line.Substring(0, split).Trim().Equals("enableClipboard", StringComparison.OrdinalIgnoreCase)) continue;
                        bool enabled;
                        return Boolean.TryParse(line.Substring(split + 1).Trim(), out enabled) && enabled;
                    }
                }
                catch { }
            }
            return false;
        }

        private void RefreshStatus()
        {
            bool deskflowRunning = Process.GetProcessesByName("deskflow-core").Length > 0;
            bool logExists = File.Exists(config.DeskflowLog);
            deskflowStatusLabel.Text = deskflowRunning && logExists ? "● 键鼠连接正常" : "● 键鼠连接未就绪";
            deskflowStatusLabel.ForeColor = deskflowRunning && logExists ? Color.FromArgb(25, 135, 84) : Color.FromArgb(200, 92, 42);

            bool clipboard = IsDeskflowClipboardEnabled(config.DeskflowConfig);
            bool bridge = transferServer != null && transferServer.IsRunning;
            clipboardStatusLabel.Text = clipboard ? "● 自动剪贴板同步已开启" : (bridge ? "● 手动剪贴板桥接可用" : "● 剪贴板同步未确认");
            clipboardStatusLabel.ForeColor = clipboard || bridge ? Color.FromArgb(25, 135, 84) : Color.FromArgb(200, 92, 42);

            loadingUi = true;
            try
            {
                autoCheckBox.Checked = config.AutoSwitch;
                autoTrayItem.Checked = config.AutoSwitch;
                edgeCheckBox.Checked = config.EdgeSwitch;
                edgeTrayItem.Checked = config.EdgeSwitch;
                sideCombo.SelectedIndex = String.Equals(config.MacSide, "Right", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                edgeCheckBox.Enabled = config.AutoSwitch;
                sideCombo.Enabled = config.AutoSwitch && config.EdgeSwitch;
            }
            finally { loadingUi = false; }
            autoStatusLabel.Text = config.AutoSwitch
                ? (String.Equals(activeSide, "Mac", StringComparison.OrdinalIgnoreCase) ? "● 自动切换：开 · Mac" : "● 自动切换：开 · Windows")
                : "● 手动切换模式";
            autoStatusLabel.ForeColor = config.AutoSwitch ? Color.FromArgb(25, 135, 84) : Color.FromArgb(120, 128, 140);
            if (statusLabel.Text.Length == 0) statusLabel.Text = "使用顺序：确认键鼠连接 → 切换显示器 → 共享文件或互传文字。";
            RefreshHomeSummary();
            RefreshTransferStatus();
        }

        private void CopyDiagnostics()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Deskflow Companion diagnostics");
            sb.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("MonitorKey: " + config.MonitorKey);
            sb.AppendLine("Server: " + config.ServerName);
            sb.AppendLine("Client: " + config.ClientName);
            sb.AppendLine("AutoSwitch: " + config.AutoSwitch);
            sb.AppendLine("EdgeSwitch: " + config.EdgeSwitch);
            sb.AppendLine("MacSide: " + config.MacSide);
            sb.AppendLine("EdgeDwellMs: " + config.EdgeDwellMs);
            sb.AppendLine("ActiveSide: " + activeSide);
            sb.AppendLine("EdgeBounds: " + edgeBounds.ToString());
            sb.AppendLine("Cursor: " + Cursor.Position.ToString());
            sb.AppendLine("WindowsInput: 0x" + config.WindowsInput.ToString("X2"));
            sb.AppendLine("MacInput: 0x" + config.MacInput.ToString("X2"));
            sb.AppendLine("TransferPort: " + (transferServer == null ? config.TransferPort : transferServer.Port));
            sb.AppendLine("TransferRunning: " + (transferServer != null && transferServer.IsRunning));
            sb.AppendLine("TransferUrl: " + (transferServer != null && transferServer.IsRunning ? transferServer.BuildPrimaryUrl() : ""));
            sb.AppendLine("IncomingDirectory: " + (transferServer != null ? transferServer.IncomingDirectory : ""));
            sb.AppendLine();
            foreach (var monitor in monitors)
            {
                sb.AppendLine(monitor.DisplayLabel);
                sb.AppendLine("  Key: " + monitor.Key);
                sb.AppendLine("  Current: 0x" + monitor.CurrentInput.ToString("X2"));
                sb.AppendLine("  Inputs: " + monitor.InputNames);
                sb.AppendLine("  Caps: " + monitor.Capabilities);
            }
            try { Clipboard.SetText(sb.ToString()); statusLabel.Text = "诊断信息已复制。"; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "复制失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void OpenDeskflow()
        {
            string[] candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Deskflow", "deskflow.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Deskflow", "Deskflow.exe")
            };
            foreach (var path in candidates)
            {
                if (File.Exists(path)) { Process.Start(path); return; }
            }
            MessageBox.Show(this, "没有找到 Deskflow GUI。", "Deskflow 增强控制台", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开链接失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void OpenPath(string path, bool reveal)
        {
            try
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show(this, "路径不存在：" + path, "Deskflow 增强控制台", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (reveal) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else Process.Start(path);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void ApplyStartupState()
        {
            if (startupCheckBox == null) return;
            startupCheckBox.CheckedChanged -= StartupCheckBoxChanged;
            try { startupCheckBox.Checked = IsStartupEnabled(); }
            finally { startupCheckBox.CheckedChanged += StartupCheckBoxChanged; }
        }

        private void StartupCheckBoxChanged(object sender, EventArgs e)
        {
            try
            {
                SetStartup(startupCheckBox.Checked);
                config.StartWithWindows = startupCheckBox.Checked;
                SaveConfig();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "设置开机启动失败：" + ex.Message, "Deskflow 增强控制台", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static bool IsStartupEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    return key != null && key.GetValue("DeskflowCompanion") != null;
                }
            }
            catch { return false; }
        }

        private static void SetStartup(bool enabled)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key == null) throw new InvalidOperationException("无法打开启动项注册表。");
                if (enabled) key.SetValue("DeskflowCompanion", "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue("DeskflowCompanion", false);
            }
        }

        private void ShowConsole()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            RefreshStatus();
        }

        private void HideToTray()
        {
            Hide();
            tray.ShowBalloonTip(1200, "Deskflow 增强控制台仍在运行", "右键托盘图标可切换显示器或退出。", ToolTipIcon.Info);
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
            }
            else if (exiting)
            {
                if (transferServer != null) { transferServer.Dispose(); transferServer = null; }
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                MonitorCatalog.DestroyAll();
            }
        }

        private void ExitApplication()
        {
            exiting = true;
            Close();
            Application.Exit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (logTimer != null) logTimer.Stop();
                if (pendingTimer != null) pendingTimer.Stop();
                if (cursorTimer != null) cursorTimer.Stop();
                if (testRestoreTimer != null) testRestoreTimer.Stop();
                if (transferServer != null) { transferServer.Dispose(); transferServer = null; }
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                MonitorCatalog.DestroyAll();
            }
            base.Dispose(disposing);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && String.Equals(args[0], "--probe", StringComparison.OrdinalIgnoreCase))
            {
                Probe(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "deskflow-companion-probe.txt"));
                return;
            }
            if (args.Length > 0 && String.Equals(args[0], "--uicheck", StringComparison.OrdinalIgnoreCase))
            {
                string output = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "deskflow-companion-uicheck.txt");
                string directory = Path.GetDirectoryName(output);
                if (String.IsNullOrEmpty(directory)) directory = Path.GetTempPath();
                string screenshot = args.Length > 2 ? args[2] : Path.Combine(directory, "screen.png");
                try
                {
                    MainForm.PreviewMode = true;
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var form = new MainForm())
                    {
                        form.Show();
                        form.Refresh();
                        Application.DoEvents();
                        Thread.Sleep(300);
                        Application.DoEvents();

                        string advancedScreenshot = Path.Combine(directory, Path.GetFileNameWithoutExtension(screenshot) + "-advanced.png");
                        Button advancedButton = FindButtonByText(form, "高级设置");
                        if (advancedButton != null)
                        {
                            advancedButton.PerformClick();
                            Application.DoEvents();
                            Thread.Sleep(180);
                            Application.DoEvents();
                            using (var advancedBitmap = new Bitmap(form.Width, form.Height))
                            {
                                form.DrawToBitmap(advancedBitmap, new Rectangle(0, 0, form.Width, form.Height));
                                advancedBitmap.Save(advancedScreenshot, ImageFormat.Png);
                            }
                            advancedButton.PerformClick();
                            Application.DoEvents();
                            Thread.Sleep(80);
                            Application.DoEvents();
                        }

                        var tabs = FindControlOfType(form, typeof(TabControl)) as TabControl;
                        var captures = new List<string>();
                        int tabCount = tabs == null ? 1 : tabs.TabPages.Count;
                        for (int i = 0; i < tabCount; i++)
                        {
                            if (tabs != null)
                            {
                                tabs.SelectedIndex = i;
                                Application.DoEvents();
                                Thread.Sleep(80);
                                Application.DoEvents();
                            }
                            string current = i == 0 ? screenshot : Path.Combine(directory, Path.GetFileNameWithoutExtension(screenshot) + "-" + i + ".png");
                            using (var bitmap = new Bitmap(form.Width, form.Height))
                            {
                                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                                bitmap.Save(current, ImageFormat.Png);
                                int colors = 0;
                                var sampled = new HashSet<int>();
                                for (int y = 0; y < bitmap.Height; y += 8)
                                {
                                    for (int x = 0; x < bitmap.Width; x += 8)
                                        sampled.Add(bitmap.GetPixel(x, y).ToArgb());
                                }
                                colors = sampled.Count;
                                if (colors < 8) captures.Add(current + " (blank?)");
                                else captures.Add(current);
                            }
                        }
                        int controlCount = CountControls(form);
                        string result = String.Format("UI_OK Size={0} Controls={1} Screenshot={2}\r\nTabs={3} Buttons={4}\r\nAdvancedScreenshot={5}\r\nCaptured={6}",
                            form.Size, controlCount, screenshot, CountControlsOfType(form, typeof(TabControl)), CountControlsOfType(form, typeof(Button)), advancedScreenshot, String.Join("; ", captures.ToArray()));
                        File.WriteAllText(output, result, Encoding.UTF8);
                    }
                }
                catch (Exception ex)
                {
                    File.WriteAllText(output, "UI_ERROR " + ex.ToString(), Encoding.UTF8);
                }
                return;
            }
            if (args.Length > 0 && String.Equals(args[0], "--set", StringComparison.OrdinalIgnoreCase) && args.Length >= 3)
            {
                byte input;
                if (Byte.TryParse(args[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out input))
                {
                    var list = MonitorCatalog.Enumerate();
                    var target = list.FirstOrDefault(x => String.Equals(x.Key, args[1], StringComparison.OrdinalIgnoreCase) || String.Equals(x.DeviceName, args[1], StringComparison.OrdinalIgnoreCase));
                    Environment.ExitCode = target != null && MonitorCatalog.SetInput(target, input) ? 0 : 1;
                    MonitorCatalog.DestroyAll();
                    return;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        private static Control FindControlOfType(Control parent, Type type)
        {
            if (type.IsAssignableFrom(parent.GetType())) return parent;
            foreach (Control child in parent.Controls)
            {
                Control found = FindControlOfType(child, type);
                if (found != null) return found;
            }
            return null;
        }

        private static Button FindButtonByText(Control parent, string text)
        {
            Button button = parent as Button;
            if (button != null && button.Text == text) return button;
            foreach (Control child in parent.Controls)
            {
                Button found = FindButtonByText(child, text);
                if (found != null) return found;
            }
            return null;
        }

        private static int CountControls(Control parent)
        {
            int count = 1;
            foreach (Control child in parent.Controls) count += CountControls(child);
            return count;
        }

        private static int CountControlsOfType(Control parent, Type type)
        {
            int count = type.IsAssignableFrom(parent.GetType()) ? 1 : 0;
            foreach (Control child in parent.Controls) count += CountControlsOfType(child, type);
            return count;
        }

        private static void Probe(string path)
        {
            var sb = new StringBuilder();
            var monitors = MonitorCatalog.Enumerate();
            foreach (var monitor in monitors)
            {
                sb.AppendLine("Monitor: " + monitor.DisplayLabel);
                sb.AppendLine("Key: " + monitor.Key);
                sb.AppendLine("Device: " + monitor.DeviceName);
                sb.AppendLine("Bounds: " + monitor.Bounds.ToString());
                sb.AppendLine("Model: " + monitor.Model);
                sb.AppendLine("CurrentInput: 0x" + monitor.CurrentInput.ToString("X2"));
                sb.AppendLine("Inputs: " + monitor.InputNames);
                sb.AppendLine("Capabilities: " + monitor.Capabilities);
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            MonitorCatalog.DestroyAll();
        }
    }
}
