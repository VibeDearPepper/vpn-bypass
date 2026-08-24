using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VpnBypass
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
                return SelfTest.Run();

            int renderIndex = Array.FindIndex(args, a => a.Equals("--render-preview", StringComparison.OrdinalIgnoreCase));
            if (renderIndex >= 0)
            {
                string target = renderIndex + 1 < args.Length ? args[renderIndex + 1] : Path.Combine(Path.GetTempPath(), "vpn-bypass-preview.png");
                return RenderPreview(target);
            }

            bool preview = args.Any(a => a.Equals("--preview", StringComparison.OrdinalIgnoreCase));
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(preview));
            return 0;
        }

        private static int RenderPreview(string path)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new MainForm(true))
            {
                form.Show();
                Application.DoEvents();
                using (var image = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
                    image.Save(path, ImageFormat.Png);
                }
                form.Close();
            }
            return 0;
        }
    }

    internal sealed class SiteItem
    {
        public string Host = "";
        public bool Enabled = true;
        public List<string> Ips = new List<string>();
        public string Error = "";
        public DateTime UpdatedUtc = DateTime.MinValue;
    }

    internal sealed class OwnedRoute
    {
        public string Host = "";
        public string Ip = "";
        public string Gateway = "";
        public int InterfaceIndex;
    }

    internal sealed class AppState
    {
        public string PreferredGateway = "";
        public int PreferredInterfaceIndex;
        public bool AutoRepair = true;
        public readonly List<SiteItem> Sites = new List<SiteItem>();
        public readonly List<OwnedRoute> OwnedRoutes = new List<OwnedRoute>();
    }

    internal static class StateStore
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static AppState Load(string path)
        {
            var state = new AppState();
            if (!File.Exists(path)) return state;
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return state;
            var root = Json.DeserializeObject(text) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("Файл sites.json повреждён.");

            state.PreferredGateway = GetString(root, "preferredGateway", "");
            state.PreferredInterfaceIndex = GetInt(root, "preferredInterfaceIndex", 0);
            state.AutoRepair = GetBool(root, "autoRepair", true);

            object sites;
            if (root.TryGetValue("sites", out sites))
            {
                foreach (object raw in Items(sites))
                {
                    string legacy = raw as string;
                    if (legacy != null)
                    {
                        state.Sites.Add(new SiteItem { Host = NetUtil.NormalizeHost(legacy) });
                        continue;
                    }
                    var map = raw as Dictionary<string, object>;
                    if (map == null) continue;
                    var site = new SiteItem();
                    site.Host = NetUtil.NormalizeHost(GetString(map, "host", ""));
                    site.Enabled = GetBool(map, "enabled", true);
                    site.Error = GetString(map, "error", "");
                    DateTime date;
                    if (DateTime.TryParse(GetString(map, "updatedUtc", ""), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date)) site.UpdatedUtc = date;
                    object ips;
                    if (map.TryGetValue("ips", out ips))
                        site.Ips.AddRange(Items(ips).Select(Convert.ToString).Where(NetUtil.IsIPv4).Distinct());
                    state.Sites.Add(site);
                }
            }

            object owned;
            if (root.TryGetValue("ownedRoutes", out owned))
            {
                foreach (object raw in Items(owned))
                {
                    var map = raw as Dictionary<string, object>;
                    if (map == null) continue;
                    var route = new OwnedRoute
                    {
                        Host = GetString(map, "host", ""),
                        Ip = GetString(map, "ip", ""),
                        Gateway = GetString(map, "gateway", ""),
                        InterfaceIndex = GetInt(map, "interfaceIndex", 0)
                    };
                    if (route.Host.Length > 0 && NetUtil.IsIPv4(route.Ip) && NetUtil.IsIPv4(route.Gateway))
                        state.OwnedRoutes.Add(route);
                }
            }

            var distinct = state.Sites.GroupBy(s => s.Host, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(s => s.Host).ToList();
            state.Sites.Clear(); state.Sites.AddRange(distinct);
            return state;
        }

        public static void Save(string path, AppState state)
        {
            var root = new Dictionary<string, object>();
            root["version"] = 2;
            root["preferredGateway"] = state.PreferredGateway;
            root["preferredInterfaceIndex"] = state.PreferredInterfaceIndex;
            root["autoRepair"] = state.AutoRepair;
            root["sites"] = state.Sites.OrderBy(s => s.Host).Select(s => new Dictionary<string, object>
            {
                { "host", s.Host }, { "enabled", s.Enabled }, { "ips", s.Ips.ToArray() },
                { "error", s.Error }, { "updatedUtc", s.UpdatedUtc == DateTime.MinValue ? "" : s.UpdatedUtc.ToUniversalTime().ToString("o") }
            }).ToArray();
            root["ownedRoutes"] = state.OwnedRoutes.Select(r => new Dictionary<string, object>
            {
                { "host", r.Host }, { "ip", r.Ip }, { "gateway", r.Gateway }, { "interfaceIndex", r.InterfaceIndex }
            }).ToArray();

            string temp = path + ".tmp";
            File.WriteAllText(temp, Json.Serialize(root), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }

        private static IEnumerable<object> Items(object value)
        {
            var items = value as IEnumerable;
            if (items == null || value is string) yield break;
            foreach (object item in items) yield return item;
        }

        private static string GetString(Dictionary<string, object> map, string name, string fallback)
        {
            object value; return map.TryGetValue(name, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : fallback;
        }

        private static int GetInt(Dictionary<string, object> map, string name, int fallback)
        {
            object value; int result;
            return map.TryGetValue(name, out value) && value != null && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result) ? result : fallback;
        }

        private static bool GetBool(Dictionary<string, object> map, string name, bool fallback)
        {
            object value; bool result;
            return map.TryGetValue(name, out value) && value != null && bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result) ? result : fallback;
        }
    }

    internal sealed class GatewayItem
    {
        public string Gateway = "";
        public int InterfaceIndex;
        public string InterfaceName = "";
        public override string ToString() { return string.Format("{0} — {1} (интерфейс {2})", Gateway, InterfaceName, InterfaceIndex); }
    }

    internal sealed class RouteItem
    {
        public string Destination = "";
        public string Mask = "";
        public string Gateway = "";
        public string LocalAddress = "";
        public int Metric;
    }

    internal sealed class CommandResult
    {
        public int ExitCode;
        public string Output = "";
    }

    internal static class NetUtil
    {
        private static readonly Regex RouteLine = new Regex(
            @"^\s*(\d{1,3}(?:\.\d{1,3}){3})\s+(\d{1,3}(?:\.\d{1,3}){3})\s+(\S+)\s+(\d{1,3}(?:\.\d{1,3}){3})\s+(\d+)\s*$",
            RegexOptions.Compiled);

        public static string NormalizeHost(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Введите домен или URL.");
            string candidate = input.Trim();
            if (!Regex.IsMatch(candidate, @"^[a-zA-Z][a-zA-Z0-9+.-]*://")) candidate = "https://" + candidate;
            Uri uri;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("Некорректный HTTP/HTTPS-адрес.");
            string host = uri.DnsSafeHost.TrimEnd('.').ToLowerInvariant();
            IPAddress literal;
            if (IPAddress.TryParse(host, out literal))
            {
                if (literal.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("Пока поддерживается только IPv4.");
                return literal.ToString();
            }
            host = new IdnMapping().GetAscii(host).ToLowerInvariant();
            if (host.Length == 0 || host.Length > 253) throw new ArgumentException("Некорректное доменное имя.");
            foreach (string label in host.Split('.'))
                if (!Regex.IsMatch(label, @"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")) throw new ArgumentException("Некорректное доменное имя.");
            return host;
        }

        public static bool IsIPv4(string value)
        {
            IPAddress ip; return IPAddress.TryParse(value, out ip) && ip.AddressFamily == AddressFamily.InterNetwork;
        }

        public static List<string> ResolveIPv4(string host)
        {
            IPAddress literal;
            if (IPAddress.TryParse(host, out literal)) return new List<string> { literal.ToString() };
            var result = Dns.GetHostAddresses(host).Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString()).Distinct().OrderBy(a => a).ToList();
            if (result.Count == 0) throw new InvalidOperationException("Для домена не найдено IPv4-адресов.");
            return result;
        }

        public static List<GatewayItem> GetGateways()
        {
            const string blocked = "vpn|tap|tun|wireguard|wintun|tailscale|zerotier|openvpn|hidemy|hyper-v|vethernet|loopback";
            var result = new List<GatewayItem>();
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                if (Regex.IsMatch(nic.Name + " " + nic.Description, blocked, RegexOptions.IgnoreCase)) continue;
                try
                {
                    var props = nic.GetIPProperties();
                    var ipv4 = props.GetIPv4Properties();
                    if (ipv4 == null) continue;
                    foreach (var gateway in props.GatewayAddresses.Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                        result.Add(new GatewayItem { Gateway = gateway.Address.ToString(), InterfaceIndex = ipv4.Index, InterfaceName = nic.Name });
                }
                catch { }
            }
            return result.GroupBy(g => g.Gateway + "|" + g.InterfaceIndex).Select(g => g.First()).OrderBy(g => g.InterfaceName).ToList();
        }

        public static List<RouteItem> ReadRoutes()
        {
            CommandResult result = RunRoute("PRINT -4");
            if (result.ExitCode != 0) throw new InvalidOperationException("Не удалось прочитать маршруты: " + result.Output.Trim());
            return ParseRoutes(result.Output);
        }

        public static List<RouteItem> ParseRoutes(string text)
        {
            var routes = new List<RouteItem>();
            foreach (string line in text.Replace("\r", "").Split('\n'))
            {
                Match match = RouteLine.Match(line);
                int metric;
                if (!match.Success || !int.TryParse(match.Groups[5].Value, out metric)) continue;
                routes.Add(new RouteItem
                {
                    Destination = match.Groups[1].Value, Mask = match.Groups[2].Value,
                    Gateway = match.Groups[3].Value, LocalAddress = match.Groups[4].Value, Metric = metric
                });
            }
            return routes;
        }

        public static bool IsBypassActive(IEnumerable<RouteItem> routes, string ip, IEnumerable<string> regularGateways)
        {
            var gateways = new HashSet<string>(regularGateways, StringComparer.OrdinalIgnoreCase);
            return routes.Any(r => r.Destination == ip && r.Mask == "255.255.255.255" && gateways.Contains(r.Gateway));
        }

        public static void AddRoute(string ip, GatewayItem gateway)
        {
            CommandResult result = RunRoute(string.Format("ADD {0} MASK 255.255.255.255 {1} METRIC 1 IF {2}", ip, gateway.Gateway, gateway.InterfaceIndex));
            if (result.ExitCode != 0) throw new InvalidOperationException("Не удалось добавить маршрут " + ip + ": " + result.Output.Trim());
        }

        public static void DeleteRoute(string ip, string gateway, int interfaceIndex)
        {
            CommandResult result = RunRoute(string.Format("DELETE {0} MASK 255.255.255.255 {1} IF {2}", ip, gateway, interfaceIndex));
            if (result.ExitCode != 0 && ReadRoutes().Any(r => r.Destination == ip && r.Mask == "255.255.255.255" && r.Gateway == gateway))
                throw new InvalidOperationException("Не удалось удалить маршрут " + ip + ": " + result.Output.Trim());
        }

        private static CommandResult RunRoute(string arguments)
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "route.exe");
            var info = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
                StandardErrorEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
            };
            using (Process process = Process.Start(info))
            {
                string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                return new CommandResult { ExitCode = process.ExitCode, Output = output };
            }
        }
    }

    internal sealed class GradientPanel : Panel
    {
        public Color StartColor = Color.FromArgb(24, 32, 51);
        public Color EndColor = Color.FromArgb(37, 80, 146);

        public GradientPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            try
            {
                using (var brush = new LinearGradientBrush(ClientRectangle, StartColor, EndColor, LinearGradientMode.Horizontal))
                    e.Graphics.FillRectangle(brush, ClientRectangle);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                e.Graphics.Clear(StartColor);
            }
        }
    }

    internal sealed class RoundedPanel : Panel
    {
        public int Radius = 14;
        public Color BorderColor = Color.Transparent;

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            using (GraphicsPath path = Rounded(ClientRectangle, Radius))
            {
                Region previous = Region;
                Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (BorderColor == Color.Transparent) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Rounded(rect, Radius))
            using (var pen = new Pen(BorderColor)) e.Graphics.DrawPath(pen, path);
        }

        private static GraphicsPath Rounded(Rectangle rect, int radius)
        {
            int d = Math.Max(2, radius * 2);
            var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class ModernButton : Button
    {
        public Color BaseColor = Color.FromArgb(47, 112, 226);
        public int Radius = 10;
        private bool _hovered;

        public ModernButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            MouseEnter += delegate { _hovered = true; Invalidate(); };
            MouseLeave += delegate { _hovered = false; Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = Enabled ? (_hovered ? ControlPaint.Light(BaseColor, 0.08F) : BaseColor) : Color.FromArgb(184, 191, 202);
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Rounded(rect, Radius))
            using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
            TextRenderer.DrawText(e.Graphics, Text, Font, rect, Enabled ? ForeColor : Color.WhiteSmoke,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private static GraphicsPath Rounded(Rectangle rect, int radius)
        {
            int d = Math.Max(2, radius * 2);
            var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly bool _preview;
        private readonly string _configPath;
        private readonly AppState _state;
        private readonly DataGridView _grid = new DataGridView();
        private readonly TextBox _input = new TextBox();
        private readonly ComboBox _gateway = new ComboBox();
        private readonly CheckBox _autoRepair = new CheckBox();
        private readonly Label _summary = new Label();
        private readonly Label _activeCount = new Label();
        private readonly Label _inactiveCount = new Label();
        private readonly Label _totalCount = new Label();
        private readonly Label _status = new Label();
        private readonly Timer _timer = new Timer();
        private readonly List<Button> _buttons = new List<Button>();
        private bool _loading;
        private bool _busy;
        private int _ticks;

        public MainForm(bool preview)
        {
            _preview = preview;
            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sites.json");
            _state = StateStore.Load(_configPath);
            BuildUi();
            LoadGateways();
            RefreshGrid();
            _timer.Interval = 5000;
            _timer.Tick += TimerTick;
            _timer.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_preview) SetStatus("Предварительный просмотр — маршруты не изменяются", Color.DarkOrange);
            else RunBusy("Проверяю исключения…", delegate { SyncAll(true); });
        }

        private void BuildUi()
        {
            Text = "VPN Bypass — исключения для сайтов";
            Icon = SystemIcons.Shield;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(960, 660);
            Size = new Size(1120, 740);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            DoubleBuffered = true;
            Font = new Font("Segoe UI", 9.25F);
            BackColor = Color.FromArgb(244, 247, 251);

            var header = new GradientPanel { Dock = DockStyle.Top, Height = 96, StartColor = Color.FromArgb(24, 32, 51), EndColor = Color.FromArgb(37, 80, 146) };
            var brand = new RoundedPanel { Location = new Point(24, 20), Size = new Size(54, 54), Radius = 16, BackColor = Color.FromArgb(55, 198, 167) };
            brand.Controls.Add(new Label { Text = "↗", ForeColor = Color.White, BackColor = Color.Transparent, Font = new Font("Segoe UI Semibold", 21F), AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter });
            header.Controls.Add(brand);
            header.Controls.Add(new Label { Text = "VPN Bypass", ForeColor = Color.White, BackColor = Color.Transparent, Font = new Font("Segoe UI Semibold", 19F), AutoSize = true, Location = new Point(94, 18) });
            header.Controls.Add(new Label { Text = "Точный маршрут для выбранных сайтов — без отключения VPN", ForeColor = Color.FromArgb(205, 218, 238), BackColor = Color.Transparent, Font = new Font("Segoe UI", 9.5F), AutoSize = true, Location = new Point(97, 55) });
            _summary.ForeColor = Color.White; _summary.BackColor = Color.Transparent; _summary.Font = new Font("Segoe UI Semibold", 10F); _summary.AutoSize = true; _summary.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            header.Controls.Add(_summary);

            var stats = new Panel { Dock = DockStyle.Top, Height = 94, BackColor = BackColor, Padding = new Padding(20, 14, 20, 8) };
            stats.Controls.Add(MakeStatCard("АКТИВНЫЕ", _activeCount, Color.FromArgb(34, 166, 118), 20));
            stats.Controls.Add(MakeStatCard("НЕАКТИВНЫЕ", _inactiveCount, Color.FromArgb(235, 151, 55), 250));
            stats.Controls.Add(MakeStatCard("ВСЕГО САЙТОВ", _totalCount, Color.FromArgb(71, 113, 213), 480));
            var safeCard = new RoundedPanel { Location = new Point(710, 14), Size = new Size(365, 70), Radius = 14, BackColor = Color.FromArgb(235, 242, 253), BorderColor = Color.FromArgb(209, 222, 243) };
            safeCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            safeCard.Controls.Add(new Label { Text = "◉  Маршруты временные", ForeColor = Color.FromArgb(48, 85, 150), Font = new Font("Segoe UI Semibold", 10F), AutoSize = true, Location = new Point(18, 13) });
            safeCard.Controls.Add(new Label { Text = "После перезагрузки приложение восстановит их", ForeColor = Color.FromArgb(89, 105, 132), BackColor = Color.Transparent, AutoSize = true, Location = new Point(20, 39) });
            stats.Controls.Add(safeCard);

            var tools = new Panel { Dock = DockStyle.Top, Height = 116, BackColor = Color.White, Padding = new Padding(20, 0, 20, 0) };
            tools.Controls.Add(new Label { Text = "НОВОЕ ИСКЛЮЧЕНИЕ", AutoSize = true, Location = new Point(20, 15), ForeColor = Color.FromArgb(112, 122, 139), Font = new Font("Segoe UI Semibold", 8F) });
            _input.Location = new Point(20, 40); _input.Width = 350; _input.Height = 31; _input.Font = new Font("Segoe UI", 10F); _input.BorderStyle = BorderStyle.FixedSingle;
            _input.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { AddSite(); e.SuppressKeyPress = true; } };
            tools.Controls.Add(_input);
            Button add = MakeButton("＋  Добавить", Color.FromArgb(47, 112, 226), 125); add.Location = new Point(382, 38); add.Click += delegate { AddSite(); }; tools.Controls.Add(add);
            tools.Controls.Add(new Label { Text = "ОБЫЧНЫЙ ИНТЕРНЕТ-ШЛЮЗ", AutoSize = true, Location = new Point(535, 15), ForeColor = Color.FromArgb(112, 122, 139), Font = new Font("Segoe UI Semibold", 8F) });
            _gateway.DropDownStyle = ComboBoxStyle.DropDownList; _gateway.FlatStyle = FlatStyle.Flat; _gateway.Location = new Point(535, 40); _gateway.Width = 450; _gateway.Font = new Font("Segoe UI", 9.5F); _gateway.SelectedIndexChanged += GatewayChanged; tools.Controls.Add(_gateway);
            _autoRepair.Text = "Автовосстановление маршрутов"; _autoRepair.AutoSize = true; _autoRepair.Location = new Point(20, 84); _autoRepair.Checked = _state.AutoRepair;
            _autoRepair.CheckedChanged += delegate { _state.AutoRepair = _autoRepair.Checked; Save(); }; tools.Controls.Add(_autoRepair);
            tools.Controls.Add(new Label { Text = "●  Онлайн-проверка каждые 5 секунд", AutoSize = true, Location = new Point(280, 85), ForeColor = Color.FromArgb(39, 159, 112) });

            ConfigureGrid();

            var actions = new Panel { Dock = DockStyle.Bottom, Height = 78, BackColor = Color.White };
            Button toggle = MakeButton("◉  Включить / отключить", Color.FromArgb(66, 77, 97), 180); toggle.Location = new Point(20, 20); toggle.Click += delegate { ToggleSelected(); }; actions.Controls.Add(toggle);
            Button remove = MakeButton("×  Удалить", Color.FromArgb(202, 68, 77), 125); remove.Location = new Point(210, 20); remove.Click += delegate { RemoveSelected(); }; actions.Controls.Add(remove);
            Button refresh = MakeButton("↻  Обновить", Color.FromArgb(47, 112, 226), 135); refresh.Location = new Point(345, 20); refresh.Click += delegate { RunBusy("Обновляю DNS и маршруты…", delegate { SyncAll(true); }); }; actions.Controls.Add(refresh);
            Button disableAll = MakeButton("Отключить все", Color.FromArgb(113, 123, 140), 130); disableAll.Location = new Point(490, 20); disableAll.Click += delegate { DisableAll(); }; actions.Controls.Add(disableAll);
            _status.TextAlign = ContentAlignment.MiddleRight; _status.ForeColor = Color.Gray; _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; _status.Location = new Point(640, 20); _status.Size = new Size(430, 36); actions.Controls.Add(_status);

            Controls.Add(_grid); Controls.Add(actions); Controls.Add(tools); Controls.Add(stats); Controls.Add(header);
            Resize += delegate
            {
                _summary.Location = new Point(Math.Max(700, header.ClientSize.Width - _summary.Width - 28), 38);
                _status.Width = Math.Max(190, actions.ClientSize.Width - 665);
                safeCard.Width = Math.Max(230, stats.ClientSize.Width - 730);
            };
        }

        private Control MakeStatCard(string title, Label valueLabel, Color accent, int left)
        {
            var card = new RoundedPanel { Location = new Point(left, 14), Size = new Size(210, 70), Radius = 14, BackColor = Color.White, BorderColor = Color.FromArgb(227, 232, 240) };
            var bar = new Panel { BackColor = accent, Location = new Point(0, 0), Size = new Size(5, 70) };
            valueLabel.Text = "0"; valueLabel.ForeColor = Color.FromArgb(34, 42, 56); valueLabel.Font = new Font("Segoe UI Semibold", 20F); valueLabel.AutoSize = true; valueLabel.Location = new Point(20, 24);
            card.Controls.Add(bar); card.Controls.Add(valueLabel);
            card.Controls.Add(new Label { Text = title, ForeColor = Color.FromArgb(119, 129, 145), Font = new Font("Segoe UI Semibold", 7.5F), AutoSize = true, Location = new Point(21, 10) });
            return card;
        }

        private Button MakeButton(string text, Color color, int width)
        {
            var button = new ModernButton { Text = text, BaseColor = color, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Width = width, Height = 36, Cursor = Cursors.Hand, Font = new Font("Segoe UI Semibold", 9F), Radius = 10 };
            button.FlatAppearance.BorderSize = 0; _buttons.Add(button); return button;
        }

        private void ConfigureGrid()
        {
            _grid.Dock = DockStyle.Fill; _grid.BackgroundColor = BackColor; _grid.BorderStyle = BorderStyle.None; _grid.Margin = new Padding(20);
            _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false; _grid.AllowUserToResizeRows = false; _grid.ReadOnly = true; _grid.MultiSelect = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.RowHeadersVisible = false; _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            _grid.ColumnHeadersHeight = 42; _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(236, 240, 246); _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(78, 89, 106);
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 8.5F); _grid.EnableHeadersVisualStyles = false; _grid.DefaultCellStyle.Padding = new Padding(9, 8, 9, 8);
            _grid.DefaultCellStyle.BackColor = Color.White; _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 251, 254);
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(225, 235, 252); _grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(30, 38, 50);
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; _grid.GridColor = Color.FromArgb(232, 236, 243); _grid.RowTemplate.Height = 48;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Сайт", Width = 230 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Текущие IPv4", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 180 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Состояние", Width = 145 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Маршрут через", Width = 180 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Обновлено", Width = 125 });
        }

        private void LoadGateways()
        {
            _loading = true; _gateway.Items.Clear();
            List<GatewayItem> items = NetUtil.GetGateways();
            foreach (GatewayItem item in items) _gateway.Items.Add(item);
            int preferred = items.FindIndex(g => g.Gateway == _state.PreferredGateway && g.InterfaceIndex == _state.PreferredInterfaceIndex);
            if (items.Count > 0) _gateway.SelectedIndex = preferred >= 0 ? preferred : 0;
            _loading = false;
        }

        private GatewayItem SelectedGateway { get { return _gateway.SelectedItem as GatewayItem; } }

        private void GatewayChanged(object sender, EventArgs e)
        {
            if (_loading || SelectedGateway == null) return;
            _state.PreferredGateway = SelectedGateway.Gateway; _state.PreferredInterfaceIndex = SelectedGateway.InterfaceIndex; Save();
            SetStatus("Шлюз изменён — нажмите «Обновить сейчас»", Color.DarkOrange);
        }

        private SiteItem SelectedSite()
        {
            return _grid.SelectedRows.Count == 1 ? _grid.SelectedRows[0].Tag as SiteItem : null;
        }

        private void AddSite()
        {
            if (_preview) return;
            try
            {
                string host = NetUtil.NormalizeHost(_input.Text);
                SiteItem site = _state.Sites.FirstOrDefault(s => s.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
                if (site == null) { site = new SiteItem { Host = host }; _state.Sites.Add(site); }
                site.Enabled = true; _input.Clear();
                RunBusy("Добавляю исключение…", delegate { SyncSite(site, true); Save(); RefreshGrid(); SetStatus("Исключение добавлено", Color.SeaGreen); });
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось добавить сайт", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void ToggleSelected()
        {
            SiteItem site = SelectedSite(); if (site == null) { MessageBox.Show("Выберите сайт в таблице."); return; } if (_preview) return;
            if (site.Enabled && MessageBox.Show("Отключить маршруты для " + site.Host + "?", "VPN Bypass", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            RunBusy("Изменяю исключение…", delegate
            {
                if (site.Enabled) { RemoveRoutes(site, true); site.Enabled = false; }
                else { site.Enabled = true; SyncSite(site, true); }
                Save(); RefreshGrid();
            });
        }

        private void RemoveSelected()
        {
            SiteItem site = SelectedSite(); if (site == null) { MessageBox.Show("Выберите сайт в таблице."); return; } if (_preview) return;
            if (MessageBox.Show("Удалить " + site.Host + " и его активные маршруты?", "Удаление исключения", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            RunBusy("Удаляю исключение…", delegate { RemoveRoutes(site, true); _state.Sites.Remove(site); Save(); RefreshGrid(); });
        }

        private void DisableAll()
        {
            if (_preview) return;
            if (MessageBox.Show("Отключить все активные исключения? Сайты останутся в списке.", "VPN Bypass", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            RunBusy("Отключаю маршруты…", delegate { foreach (SiteItem site in _state.Sites.ToList()) { RemoveRoutes(site, true); site.Enabled = false; } Save(); RefreshGrid(); });
        }

        private void SyncAll(bool resolveDns)
        {
            if (SelectedGateway == null) throw new InvalidOperationException("Не найден обычный интернет-шлюз.");
            foreach (SiteItem site in _state.Sites.Where(s => s.Enabled).ToList()) SyncSite(site, resolveDns || site.Ips.Count == 0);
            Save(); RefreshGrid(); SetStatus("Проверено: " + DateTime.Now.ToString("HH:mm:ss"), Color.SeaGreen);
        }

        private void SyncSite(SiteItem site, bool resolveDns)
        {
            List<string> oldIps = site.Ips.ToList();
            if (resolveDns)
            {
                try { site.Ips = NetUtil.ResolveIPv4(site.Host); site.Error = ""; site.UpdatedUtc = DateTime.UtcNow; }
                catch (Exception ex) { site.Error = ex.Message; site.Ips = oldIps; }
            }
            foreach (OwnedRoute stale in _state.OwnedRoutes.Where(r => r.Host == site.Host && !site.Ips.Contains(r.Ip)).ToList())
            {
                TryDelete(stale); _state.OwnedRoutes.Remove(stale);
            }
            EnsureRoutes(site);
        }

        private void EnsureRoutes(SiteItem site)
        {
            GatewayItem gateway = SelectedGateway; if (gateway == null) throw new InvalidOperationException("Не выбран обычный шлюз.");
            List<RouteItem> routes = NetUtil.ReadRoutes();
            foreach (string ip in site.Ips)
            {
                if (NetUtil.IsBypassActive(routes, ip, new[] { gateway.Gateway })) continue;
                foreach (OwnedRoute old in _state.OwnedRoutes.Where(r => r.Host == site.Host && r.Ip == ip).ToList()) { TryDelete(old); _state.OwnedRoutes.Remove(old); }
                NetUtil.AddRoute(ip, gateway);
                _state.OwnedRoutes.Add(new OwnedRoute { Host = site.Host, Ip = ip, Gateway = gateway.Gateway, InterfaceIndex = gateway.InterfaceIndex });
                routes = NetUtil.ReadRoutes();
            }
        }

        private void RemoveRoutes(SiteItem site, bool includeMatchingRegularRoutes)
        {
            foreach (OwnedRoute owned in _state.OwnedRoutes.Where(r => r.Host == site.Host).ToList()) { TryDelete(owned); _state.OwnedRoutes.Remove(owned); }
            if (!includeMatchingRegularRoutes) return;
            List<RouteItem> routes = NetUtil.ReadRoutes();
            foreach (GatewayItem gateway in NetUtil.GetGateways())
                foreach (string ip in site.Ips.Where(ip => routes.Any(r => r.Destination == ip && r.Mask == "255.255.255.255" && r.Gateway == gateway.Gateway)).ToList())
                    NetUtil.DeleteRoute(ip, gateway.Gateway, gateway.InterfaceIndex);
        }

        private void TryDelete(OwnedRoute route)
        {
            try { NetUtil.DeleteRoute(route.Ip, route.Gateway, route.InterfaceIndex); }
            catch { if (NetUtil.ReadRoutes().Any(r => r.Destination == route.Ip && r.Mask == "255.255.255.255" && r.Gateway == route.Gateway)) throw; }
        }

        private void RefreshGrid()
        {
            List<RouteItem> routes; try { routes = NetUtil.ReadRoutes(); } catch { routes = new List<RouteItem>(); }
            List<string> regular = NetUtil.GetGateways().Select(g => g.Gateway).Distinct().ToList();
            string selected = SelectedSite() == null ? "" : SelectedSite().Host;
            _grid.Rows.Clear(); int activeSites = 0; int inactiveSites = 0;
            foreach (SiteItem site in _state.Sites.OrderBy(s => s.Host))
            {
                int active = site.Ips.Count(ip => NetUtil.IsBypassActive(routes, ip, regular));
                string status; Color color;
                if (!site.Enabled) { status = "○  Отключено"; color = Color.Gray; inactiveSites++; }
                else if (site.Ips.Count == 0 && site.Error.Length > 0) { status = "!  Ошибка DNS"; color = Color.Firebrick; inactiveSites++; }
                else if (site.Ips.Count > 0 && active == site.Ips.Count) { status = "●  Активно"; color = Color.SeaGreen; activeSites++; }
                else if (active > 0) { status = "◐  Частично"; color = Color.DarkOrange; activeSites++; }
                else { status = "○  Неактивно"; color = Color.DarkOrange; inactiveSites++; }
                string gateways = string.Join(", ", routes.Where(r => site.Ips.Contains(r.Destination) && r.Mask == "255.255.255.255" && regular.Contains(r.Gateway)).Select(r => r.Gateway).Distinct());
                int index = _grid.Rows.Add(site.Host, string.Join(", ", site.Ips), status, gateways, site.UpdatedUtc == DateTime.MinValue ? "—" : site.UpdatedUtc.ToLocalTime().ToString("dd.MM HH:mm"));
                DataGridViewRow row = _grid.Rows[index]; row.Tag = site; row.Cells[2].Style.ForeColor = color; row.Cells[2].Style.Font = new Font(_grid.Font, FontStyle.Bold);
                if (site.Host == selected) row.Selected = true;
            }
            _summary.Text = string.Format("Активно: {0} из {1}", activeSites, _state.Sites.Count);
            _activeCount.Text = activeSites.ToString(CultureInfo.InvariantCulture);
            _inactiveCount.Text = inactiveSites.ToString(CultureInfo.InvariantCulture);
            _totalCount.Text = _state.Sites.Count.ToString(CultureInfo.InvariantCulture);
            _summary.Location = new Point(Math.Max(650, ClientSize.Width - _summary.Width - 25), 29);
        }

        private void TimerTick(object sender, EventArgs e)
        {
            if (_busy) return; RefreshGrid(); _ticks++;
            if (!_preview && _state.AutoRepair && _ticks >= 12) { _ticks = 0; RunBusy("Автопроверка маршрутов…", delegate { SyncAll(false); }); }
        }

        private void RunBusy(string message, Action action)
        {
            if (_busy) return; _busy = true; UseWaitCursor = true; foreach (Button button in _buttons) button.Enabled = false; SetStatus(message, Color.DimGray);
            try { action(); }
            catch (Exception ex) { SetStatus("Ошибка", Color.Firebrick); MessageBox.Show(ex.Message, "VPN Bypass", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { foreach (Button button in _buttons) button.Enabled = true; UseWaitCursor = false; _busy = false; }
        }

        private void Save() { if (!_preview) StateStore.Save(_configPath, _state); }
        private void SetStatus(string text, Color color) { _status.Text = text; _status.ForeColor = color; }
    }

    internal static class SelfTest
    {
        public static int Run()
        {
            try
            {
                Check(NetUtil.NormalizeHost("https://Example.COM/path") == "example.com", "normalize host");
                Check(NetUtil.NormalizeHost("203.0.113.10") == "203.0.113.10", "normalize IPv4");
                var routes = NetUtil.ParseRoutes("  203.0.113.10  255.255.255.255  192.0.2.1  192.0.2.100  1\r\n");
                Check(routes.Count == 1 && NetUtil.IsBypassActive(routes, "203.0.113.10", new[] { "192.0.2.1" }), "parse route");
                string temp = Path.Combine(Path.GetTempPath(), "vpn-bypass-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    File.WriteAllText(temp, "{\"version\":1,\"sites\":[\"example.com\"]}", Encoding.UTF8);
                    AppState migrated = StateStore.Load(temp);
                    Check(migrated.Sites.Count == 1 && migrated.Sites[0].Host == "example.com", "legacy config migration");
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                Console.WriteLine("SELF_TEST=OK"); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("SELF_TEST=FAILED: " + ex.Message); return 1; }
        }
        private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
    }
}
