using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Audio Bridge Receiver")]
[assembly: AssemblyProduct("Audio Bridge")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace AudioBridge
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "AudioBridgeReceiver.SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("Audio Bridge is already running. Look for it in the system tray.", "Audio Bridge");
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private static readonly int[] LatencyMs = { 30, 60, 120 };
        private static readonly string[] LatencyNames = { "Low (30 ms)", "Normal (60 ms)", "Stable (120 ms)" };

        private readonly PcmBuffer buffer = new PcmBuffer();
        private UdpAudioReceiver receiver;
        private WaveOutPlayer player;

        private readonly Label ipLabel = new Label();
        private readonly Label statusLabel = new Label();
        private readonly Label statsLabel = new Label();
        private readonly ProgressBar level = new ProgressBar();
        private readonly ComboBox deviceBox = new ComboBox();
        private readonly ComboBox latencyBox = new ComboBox();
        private readonly TrackBar volume = new TrackBar();
        private readonly Label volumeLabel = new Label();
        private readonly CheckBox trayBox = new CheckBox();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly Dictionary<string, string> settings = new Dictionary<string, string>();
        private bool trayHintShown;

        private static string SettingsPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioBridge", "settings.ini");
            }
        }

        public MainForm()
        {
            LoadSettings();
            Text = "Audio Bridge";
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var grid = new TableLayoutPanel();
            grid.ColumnCount = 2;
            grid.AutoSize = true;
            grid.Padding = new Padding(14);
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));

            var title = new Label();
            title.Text = "Android → PC audio receiver";
            title.Font = new Font("Segoe UI Semibold", 13f);
            title.AutoSize = true;
            title.Margin = new Padding(3, 0, 3, 10);
            grid.Controls.Add(title, 0, 0);
            grid.SetColumnSpan(title, 2);

            AddRow(grid, 1, "PC address", ipLabel);
            ipLabel.Font = new Font("Consolas", 11f, FontStyle.Bold);
            ipLabel.AutoSize = true;
            ipLabel.MaximumSize = new Size(300, 0);

            AddRow(grid, 2, "Status", statusLabel);
            statusLabel.AutoSize = true;
            statusLabel.MaximumSize = new Size(300, 0);

            AddRow(grid, 3, "Stream", statsLabel);
            statsLabel.AutoSize = true;
            statsLabel.ForeColor = SystemColors.GrayText;

            level.Maximum = 100;
            level.Size = new Size(290, 10);
            AddRow(grid, 4, "Level", level);

            deviceBox.DropDownStyle = ComboBoxStyle.DropDownList;
            deviceBox.Width = 290;
            deviceBox.Items.Add("Default output device");
            foreach (string d in WinMM.OutputDevices()) deviceBox.Items.Add(d);
            deviceBox.SelectedIndex = Math.Max(0, deviceBox.Items.IndexOf(Get("device", "")));
            deviceBox.SelectedIndexChanged += delegate { Set("device", deviceBox.SelectedIndex == 0 ? "" : (string)deviceBox.SelectedItem); RestartPlayer(); };
            AddRow(grid, 5, "Output", deviceBox);

            latencyBox.DropDownStyle = ComboBoxStyle.DropDownList;
            latencyBox.Width = 290;
            latencyBox.Items.AddRange(LatencyNames);
            latencyBox.SelectedIndex = Clamp(ParseInt(Get("latency", "1"), 1), 0, 2);
            latencyBox.SelectedIndexChanged += delegate { ApplyLatency(); Set("latency", latencyBox.SelectedIndex.ToString()); };
            AddRow(grid, 6, "Buffer", latencyBox);

            var volPanel = new FlowLayoutPanel();
            volPanel.AutoSize = true;
            volPanel.WrapContents = false;
            volPanel.Margin = Padding.Empty;
            volume.Minimum = 0;
            volume.Maximum = 150;
            volume.TickFrequency = 25;
            volume.Width = 230;
            volume.Value = Clamp(ParseInt(Get("volume", "100"), 100), 0, 150);
            volume.ValueChanged += delegate { ApplyVolume(); Set("volume", volume.Value.ToString()); };
            volumeLabel.AutoSize = true;
            volumeLabel.Margin = new Padding(3, 8, 3, 0);
            volPanel.Controls.Add(volume);
            volPanel.Controls.Add(volumeLabel);
            AddRow(grid, 7, "Volume", volPanel);

            trayBox.Text = "Minimize to system tray";
            trayBox.AutoSize = true;
            trayBox.Checked = Get("tray", "1") == "1";
            trayBox.CheckedChanged += delegate { Set("tray", trayBox.Checked ? "1" : "0"); };
            grid.Controls.Add(trayBox, 1, 8);

            var help = new Label();
            help.Text = "Open Audio Bridge on the phone, tap \"Find PC\" (or type the address above) and press Start. " +
                        "Phone and PC must be on the same network. Allow the app through Windows Firewall (Private networks) when asked.";
            help.ForeColor = SystemColors.GrayText;
            help.AutoSize = true;
            help.MaximumSize = new Size(390, 0);
            help.Margin = new Padding(3, 10, 3, 0);
            grid.Controls.Add(help, 0, 9);
            grid.SetColumnSpan(help, 2);

            Controls.Add(grid);

            var menu = new ContextMenuStrip();
            menu.Items.Add("Show", null, delegate { ShowFromTray(); });
            menu.Items.Add("Exit", null, delegate { Close(); });
            tray.ContextMenuStrip = menu;
            tray.Icon = Icon ?? SystemIcons.Application;
            tray.Text = "Audio Bridge";
            tray.DoubleClick += delegate { ShowFromTray(); };

            ApplyLatency();
            ApplyVolume();
            ipLabel.Text = LocalAddresses();

            receiver = new UdpAudioReceiver(buffer);
            try { receiver.Start(); }
            catch (Exception ex)
            {
                receiver.Error = "Cannot listen on UDP port " + UdpAudioReceiver.Port + ": " + ex.Message;
            }
            RestartPlayer();

            timer.Interval = 200;
            timer.Tick += delegate { RefreshStatus(); };
            timer.Start();
            NetworkChange.NetworkAddressChanged += delegate
            {
                try { BeginInvoke((MethodInvoker)delegate { ipLabel.Text = LocalAddresses(); }); } catch { }
            };
            RefreshStatus();
        }

        private static void AddRow(TableLayoutPanel grid, int row, string caption, Control value)
        {
            var l = new Label();
            l.Text = caption;
            l.AutoSize = true;
            l.ForeColor = SystemColors.GrayText;
            l.Margin = new Padding(3, 6, 12, 6);
            value.Margin = new Padding(3, 6, 3, 6);
            grid.Controls.Add(l, 0, row);
            grid.Controls.Add(value, 1, row);
        }

        private void ApplyLatency() { buffer.TargetMs = LatencyMs[latencyBox.SelectedIndex]; }

        private void ApplyVolume()
        {
            buffer.VolumePercent = volume.Value;
            volumeLabel.Text = volume.Value + "%";
        }

        private void RestartPlayer()
        {
            if (player != null) { player.Dispose(); player = null; }
            int id = deviceBox.SelectedIndex <= 0 ? WinMM.WAVE_MAPPER : deviceBox.SelectedIndex - 1;
            player = new WaveOutPlayer(buffer, id, 10);
            player.Start();
        }

        private void RefreshStatus()
        {
            string err = (receiver != null ? receiver.Error : null) ?? (player != null ? player.Error : null);
            string from = receiver != null ? receiver.CurrentSender : null;
            if (err != null)
            {
                statusLabel.Text = err;
                statusLabel.ForeColor = Color.Firebrick;
            }
            else if (from != null)
            {
                statusLabel.Text = "Playing audio from " + from;
                statusLabel.ForeColor = Color.SeaGreen;
            }
            else
            {
                statusLabel.Text = "Waiting for phone on UDP port " + UdpAudioReceiver.Port + "…";
                statusLabel.ForeColor = SystemColors.ControlText;
            }
            statsLabel.Text = string.Format("buffer {0} ms · lost {1} · underruns {2} · skips {3}",
                from != null ? buffer.BufferedMs : 0,
                receiver != null ? receiver.Lost : 0, buffer.Underruns, buffer.Skips);
            int peak = from != null ? buffer.Peak : 0;
            level.Value = Clamp((int)(Math.Sqrt(peak / 32767.0) * 100), 0, 100);
            tray.Text = from != null ? "Audio Bridge – playing from " + from : "Audio Bridge – waiting";
        }

        private static string LocalAddresses()
        {
            try
            {
                var ips = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                                !n.Description.ToLowerInvariant().Contains("virtual") &&
                                !n.Description.ToLowerInvariant().Contains("hyper-v"))
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                                !a.Address.ToString().StartsWith("169.254."))
                    .Select(a => a.Address.ToString())
                    .Distinct().ToArray();
                return ips.Length == 0 ? "not connected to a network" : string.Join("\n", ips);
            }
            catch { return "run ipconfig to find it"; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && trayBox.Checked)
            {
                Hide();
                tray.Visible = true;
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(2500, "Audio Bridge", "Still receiving in the background. Double-click to open.", ToolTipIcon.Info);
                }
            }
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            tray.Visible = false;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            timer.Stop();
            tray.Visible = false;
            if (receiver != null) { receiver.Dispose(); receiver = null; }
            if (player != null) { player.Dispose(); player = null; }
            SaveSettings();
            base.OnFormClosing(e);
        }

        // ---- tiny settings store: %APPDATA%\AudioBridge\settings.ini ----
        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) settings[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllLines(SettingsPath, settings.Select(kv => kv.Key + "=" + kv.Value).ToArray());
            }
            catch { }
        }

        private string Get(string key, string fallback)
        {
            string v;
            return settings.TryGetValue(key, out v) ? v : fallback;
        }

        private void Set(string key, string value) { settings[key] = value; }

        private static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, out v) ? v : fallback;
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }
}
