using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GitHubHostsAuto
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, @"Global\GitHubHostsAutoExe", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("GitHub 自动刷新已在后台运行。", "GitHub 自动刷新",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (Environment.OSVersion.Version.Major >= 6) SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                using (var app = new TrayApp())
                {
                    Application.Run();
                }
            }
        }
    }

    internal sealed class StatusSnap
    {
        public string Ip;
        public bool Healthy;
        public string Detail;
        public int RefreshCount;
        public bool Admin;
        public DateTime Time;
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private const string BeginTag = "# BEGIN GITHUB FIX";
        private const string EndTag = "# END GITHUB FIX";
        private const string HostsPath = @"C:\Windows\System32\drivers\etc\hosts";
        private const string AppTitle = "GitHub 自动刷新";
        private const string CurlPath = @"C:\Windows\System32\curl.exe";
        private const string Version = "1.2.0";
        private const int IntervalSec = 180;

        private static readonly string[] ProbeIps =
        {
            "20.87.245.0","4.208.26.197","20.27.177.113","20.233.83.145",
            "140.82.121.4","20.200.245.247","20.201.28.151","20.207.73.82",
            "140.82.113.22","140.82.114.22","20.201.28.150","20.207.73.84",
            "20.205.243.166","20.205.243.164"
        };

        private static readonly string[] RawIps =
        {
            "185.199.108.133","185.199.109.133","185.199.110.133","185.199.111.133"
        };

        private static readonly Color CBg = Color.FromArgb(250, 250, 251);
        private static readonly Color CHeader = Color.FromArgb(36, 41, 47);
        private static readonly Color CText = Color.FromArgb(36, 41, 47);
        private static readonly Color CMuted = Color.FromArgb(110, 118, 129);
        private static readonly Color COk = Color.FromArgb(26, 127, 55);
        private static readonly Color CBad = Color.FromArgb(207, 34, 46);
        private static readonly Color CWarn = Color.FromArgb(130, 100, 0);
        private static readonly Color CBlue = Color.FromArgb(88, 166, 255);
        private static readonly Color CBorder = Color.FromArgb(209, 217, 224);

        private readonly NotifyIcon _notify;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Form _form;
        private readonly TabControl _tabs;
        private readonly Label _lblIp, _lblHealth, _lblMeta, _lblProgress;
        private readonly Panel _badge;
        private readonly Button _btnRefresh, _btnLog, _btnHide;
        private readonly ToolStripMenuItem _miStatus, _miAutoStart;
        private readonly string _logPath, _statePath, _dataDir;
        private readonly Icon _appIcon;
        private bool _checking, _exit;

        public TrayApp()
        {
            _dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GitHubHostsAuto");
            Directory.CreateDirectory(_dataDir);
            _logPath = Path.Combine(_dataDir, "github-hosts.log");
            _statePath = Path.Combine(_dataDir, "state.json");
            _appIcon = CreateIcon();

            Button btnRefresh, btnLog, btnHide;
            Label lblIp, lblHealth, lblMeta, lblProgress;
            Panel badge;
            TabControl tabs;
            _form = BuildForm(out tabs, out lblIp, out lblHealth, out lblMeta,
                out lblProgress, out badge, out btnRefresh, out btnLog, out btnHide);
            _tabs = tabs;
            _lblIp = lblIp;
            _lblHealth = lblHealth;
            _lblMeta = lblMeta;
            _lblProgress = lblProgress;
            _badge = badge;
            _btnRefresh = btnRefresh;
            _btnLog = btnLog;
            _btnHide = btnHide;

            ContextMenuStrip menu = BuildMenu(out _miStatus, out _miAutoStart);

            _notify = new NotifyIcon
            {
                Icon = _appIcon,
                Text = AppTitle + " v" + Version,
                Visible = true,
                ContextMenuStrip = menu
            };
            _notify.DoubleClick += delegate { ToggleForm(); };
            _notify.BalloonTipClicked += delegate { ShowForm(); };

            _btnHide.Click += delegate { _form.Hide(); };
            _btnLog.Click += delegate { OpenLog(); };
            _btnRefresh.Click += async delegate { await RefreshAsync(false); };

            _form.FormClosing += (s, e) =>
            {
                if (_exit) return;
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    _form.Hide();
                    Balloon("已最小化到托盘，后台继续监控", ToolTipIcon.Info);
                }
            };

            _timer = new System.Windows.Forms.Timer { Interval = IntervalSec * 1000 };
            _timer.Tick += async delegate { await TickAsync(); };
            _timer.Start();

            if (!IsAdmin())
                Balloon("未以管理员运行，无法自动修改 hosts。", ToolTipIcon.Warning);

            Log("started v" + Version + " admin=" + IsAdmin() + " curl=" + File.Exists(CurlPath));
            var st0 = CaptureStatus();
            UpdateUi(st0);
            _form.Show();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _timer.Stop(); _timer.Dispose(); } catch { }
                try { _notify.Visible = false; _notify.Dispose(); } catch { }
                try { _form.Dispose(); } catch { }
                try { _appIcon.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        private static Icon CreateIcon()
        {
            var bmp = new Bitmap(64, 64);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var path = RoundRect(new Rectangle(2, 2, 60, 60), 14))
                using (var b = new SolidBrush(Color.FromArgb(24, 26, 32)))
                    g.FillPath(b, path);
                using (var b = new SolidBrush(CBlue))
                    g.FillEllipse(b, 14, 14, 36, 36);
                TextRenderer.DrawText(g, "G", new Font("Segoe UI", 18f, FontStyle.Bold),
                    new Rectangle(0, 0, 64, 64), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            IntPtr h = bmp.GetHicon();
            var ic = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            bmp.Dispose();
            return ic;
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static Button MakeBtn(string text, bool primary)
        {
            var b = new Button
            {
                Text = text,
                Height = 40,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
                BackColor = primary ? CHeader : Color.White,
                ForeColor = primary ? Color.White : CText
            };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = CBorder;
            b.FlatAppearance.MouseOverBackColor = primary
                ? Color.FromArgb(56, 62, 70)
                : Color.FromArgb(240, 243, 246);
            return b;
        }

        private Form BuildForm(
            out TabControl tabs,
            out Label lblIp, out Label lblHealth, out Label lblMeta, out Label lblProgress,
            out Panel badge,
            out Button btnRefresh, out Button btnLog, out Button btnHide)
        {
            // client area large enough for header + tabs + padding; no overlap
            var f = new Form
            {
                Text = AppTitle + " v" + Version,
                ClientSize = new Size(580, 560),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedSingle,
                MaximizeBox = false,
                BackColor = CBg,
                Font = new Font("Microsoft YaHei UI", 9.5f),
                Icon = _appIcon,
                ShowInTaskbar = true
            };

            // header (absolute)
            var header = new Panel
            {
                Bounds = new Rectangle(0, 0, 580, 80),
                BackColor = CHeader
            };
            var t1 = new Label
            {
                Text = "GitHub Hosts 自动刷新",
                AutoSize = true,
                Location = new Point(24, 14),
                Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold),
                ForeColor = Color.White
            };
            var t2 = new Label
            {
                Text = "v" + Version + "  ·  自动探测 IP  ·  失效自动切换  ·  托盘常驻",
                AutoSize = true,
                Location = new Point(24, 48),
                Font = new Font("Microsoft YaHei UI", 9f),
                ForeColor = Color.FromArgb(170, 178, 190)
            };
            header.Controls.Add(t1);
            header.Controls.Add(t2);
            f.Controls.Add(header);

            // tabs
            tabs = new TabControl
            {
                Bounds = new Rectangle(20, 96, 540, 440),
                Font = new Font("Microsoft YaHei UI", 10f)
            };

            // ---- tab 状态 ----
            var tabStatus = new TabPage("状态");
            tabStatus.BackColor = CBg;
            tabStatus.Padding = new Padding(12);

            var card = new Panel
            {
                Bounds = new Rectangle(16, 16, 490, 170),
                BackColor = Color.White
            };
            card.Paint += (s, e) =>
            {
                using (var pen = new Pen(CBorder))
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };
            card.Controls.Add(new Label
            {
                Text = "当前 github.com",
                AutoSize = true,
                Location = new Point(16, 12),
                ForeColor = CMuted
            });
            lblIp = new Label
            {
                Text = "检测中…",
                AutoSize = true,
                Location = new Point(16, 36),
                Font = new Font("Consolas", 16f, FontStyle.Bold),
                ForeColor = CText
            };
            card.Controls.Add(lblIp);

            Panel badgeLocal = new Panel
            {
                Bounds = new Rectangle(16, 90, 458, 62),
                BackColor = Color.FromArgb(246, 248, 250)
            };
            badgeLocal.Paint += (s, e) =>
            {
                var p = (Panel)s;
                using (var path = RoundRect(new Rectangle(0, 0, p.Width - 1, p.Height - 1), 8))
                using (var pen = new Pen(CBorder))
                    e.Graphics.DrawPath(pen, path);
            };
            var dot = new Label
            {
                Name = "dot",
                Text = "●",
                AutoSize = true,
                Location = new Point(16, 20),
                Font = new Font("Segoe UI", 12f),
                ForeColor = CMuted
            };
            badgeLocal.Controls.Add(dot);
            lblHealth = new Label
            {
                Text = "正在检测…",
                AutoSize = true,
                MaximumSize = new Size(400, 40),
                Location = new Point(42, 16),
                Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
                ForeColor = CMuted
            };
            badgeLocal.Controls.Add(lblHealth);
            card.Controls.Add(badgeLocal);
            badge = badgeLocal;
            tabStatus.Controls.Add(card);

            lblMeta = new Label
            {
                Bounds = new Rectangle(16, 200, 490, 72),
                Font = new Font("Microsoft YaHei UI", 9.5f),
                ForeColor = CMuted,
                Text = ""
            };
            tabStatus.Controls.Add(lblMeta);

            btnRefresh = MakeBtn("立即刷新", true);
            btnRefresh.Bounds = new Rectangle(16, 286, 150, 42);
            btnLog = MakeBtn("打开日志", false);
            btnLog.Bounds = new Rectangle(180, 286, 150, 42);
            btnHide = MakeBtn("隐藏到托盘", false);
            btnHide.Bounds = new Rectangle(344, 286, 150, 42);
            tabStatus.Controls.Add(btnRefresh);
            tabStatus.Controls.Add(btnLog);
            tabStatus.Controls.Add(btnHide);

            lblProgress = new Label
            {
                Bounds = new Rectangle(16, 340, 490, 56),
                Font = new Font("Microsoft YaHei UI", 9.5f),
                ForeColor = CBlue,
                Text = "提示：关闭窗口会缩到托盘，不会退出。"
            };
            tabStatus.Controls.Add(lblProgress);

            var hint = new Label
            {
                Bounds = new Rectangle(16, 400, 490, 36),
                Font = new Font("Microsoft YaHei UI", 8.5f),
                ForeColor = CMuted,
                Text = "退出请用托盘菜单「退出」。开机自启：托盘菜单勾选「开机自动启动」。"
            };
            tabStatus.Controls.Add(hint);

            // ---- tab 版本说明 ----
            var tabAbout = new TabPage("版本说明");
            tabAbout.BackColor = CBg;
            var about = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                BackColor = CBg,
                ForeColor = CText,
                Font = new Font("Microsoft YaHei UI", 10f),
                Bounds = new Rectangle(20, 16, 490, 380),
                Text =
                    "当前版本：v" + Version + "\r\n\r\n" +
                    "【v1.2.0】\r\n" +
                    "· 界面改为「状态 / 版本说明」双页签，修复遮挡\r\n" +
                    "· 「立即刷新」增加进度提示，避免看起来像没反应\r\n" +
                    "· 健康检查全面改用 curl，减少误报\r\n" +
                    "· 窗口加高，按钮不再被裁切\r\n\r\n" +
                    "【v1.1.0】\r\n" +
                    "· 现代化深色顶栏界面\r\n" +
                    "· 自动探测可用 GitHub IP 并写入 hosts\r\n" +
                    "· 托盘常驻，关闭窗口不退出\r\n" +
                    "· 支持开机自启\r\n\r\n" +
                    "【v1.0.0】\r\n" +
                    "· 首个单文件桌面版\r\n\r\n" +
                    "【工作原理】\r\n" +
                    "1. 用 curl 探测候选 IP（网页 + git 协议）\r\n" +
                    "2. 拒绝返回 200 但内容是假 OK 的中间盒\r\n" +
                    "3. 写入 hosts 的 # BEGIN GITHUB FIX 段\r\n" +
                    "4. flushdns 后确认 git ls-remote 可用\r\n\r\n" +
                    "【说明】\r\n" +
                    "· 修改 hosts 需要管理员权限\r\n" +
                    "· IP 会被网络间歇干扰，不是永久方案\r\n" +
                    "· 长期稳定建议使用代理\r\n"
            };
            tabAbout.Controls.Add(about);

            tabs.TabPages.Add(tabStatus);
            tabs.TabPages.Add(tabAbout);
            f.Controls.Add(tabs);

            return f;
        }

        private ContextMenuStrip BuildMenu(out ToolStripMenuItem miStatus, out ToolStripMenuItem miAutoStart)
        {
            var menu = new ContextMenuStrip
            {
                BackColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9f)
            };

            var miShow = new ToolStripMenuItem("打开面板");
            miShow.Click += delegate { ShowForm(); };
            menu.Items.Add(miShow);

            var miRefresh = new ToolStripMenuItem("立即刷新 IP");
            miRefresh.Click += async delegate { await RefreshAsync(false); };
            menu.Items.Add(miRefresh);

            menu.Items.Add(new ToolStripSeparator());
            miStatus = new ToolStripMenuItem("状态: 检测中…") { Enabled = false };
            menu.Items.Add(miStatus);

            miAutoStart = new ToolStripMenuItem("开机自动启动") { CheckOnClick = true };
            miAutoStart.Checked = File.Exists(StartupLink());
            miAutoStart.Click += delegate { ToggleAutostart(); };
            menu.Items.Add(miAutoStart);

            var miAbout = new ToolStripMenuItem("版本 v" + Version);
            miAbout.Click += delegate
            {
                ShowForm();
                try { _tabs.SelectedIndex = 1; } catch { }
            };
            menu.Items.Add(miAbout);

            var miLog = new ToolStripMenuItem("打开日志");
            miLog.Click += delegate { OpenLog(); };
            menu.Items.Add(miLog);

            menu.Items.Add(new ToolStripSeparator());
            var miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { ExitApp(); };
            menu.Items.Add(miExit);
            return menu;
        }

        private void ShowForm()
        {
            _form.Show();
            _form.WindowState = FormWindowState.Normal;
            _form.Activate();
        }

        private void ToggleForm()
        {
            if (_form.Visible) _form.Hide();
            else ShowForm();
        }

        private void ExitApp()
        {
            _exit = true;
            Log("exit");
            try { _timer.Stop(); } catch { }
            try { _notify.Visible = false; } catch { }
            ExitThread();
        }

        private void Balloon(string text, ToolTipIcon icon)
        {
            try
            {
                _notify.BalloonTipTitle = AppTitle;
                _notify.BalloonTipText = text;
                _notify.BalloonTipIcon = icon;
                _notify.ShowBalloonTip(3500);
            }
            catch { }
        }

        private void OpenLog()
        {
            if (File.Exists(_logPath))
                Process.Start("notepad.exe", "\"" + _logPath + "\"");
            else
                MessageBox.Show("日志还不存在。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SetProgress(string text, Color color)
        {
            if (_lblProgress == null || _lblProgress.IsDisposed) return;
            if (_lblProgress.InvokeRequired)
            {
                try { _lblProgress.BeginInvoke(new Action(() => SetProgress(text, color))); } catch { }
                return;
            }
            _lblProgress.Text = text;
            _lblProgress.ForeColor = color;
        }

        private void SetBusy(bool busy, string btnText)
        {
            if (_btnRefresh == null || _btnRefresh.IsDisposed) return;
            if (_btnRefresh.InvokeRequired)
            {
                try { _btnRefresh.BeginInvoke(new Action(() => SetBusy(busy, btnText))); } catch { }
                return;
            }
            _btnRefresh.Enabled = !busy;
            _btnRefresh.Text = btnText;
            _btnRefresh.BackColor = busy ? Color.FromArgb(120, 125, 135) : CHeader;
        }

        private static bool IsAdmin()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private string CurrentIp()
        {
            try
            {
                var addrs = Dns.GetHostAddresses("github.com");
                var ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                return ip == null ? null : ip.ToString();
            }
            catch { return null; }
        }

        private static string Curl(string args, int timeoutMs)
        {
            if (!File.Exists(CurlPath)) return null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = CurlPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return null;
                    string stdout = p.StandardOutput.ReadToEnd();
                    try { p.StandardError.ReadToEnd(); } catch { }
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                    }
                    return stdout;
                }
            }
            catch { return null; }
        }

        private static bool TestGitReal(string ip)
        {
            string body = Curl(
                "-sS --http1.1 --connect-timeout 3 --max-time 6 " +
                "-A \"git/2.22.0.windows.1\" " +
                "--resolve github.com:443:" + ip + " " +
                "\"https://github.com/git/git.git/info/refs?service=git-upload-pack\"",
                10000);
            return !string.IsNullOrEmpty(body) && body.Contains("service=git-upload-pack");
        }

        private static bool TestHtmlReal(string ip)
        {
            string body = Curl(
                "-sS --http1.1 --connect-timeout 3 --max-time 8 -A \"Mozilla/5.0\" " +
                "--resolve github.com:443:" + ip + " https://github.com/",
                12000);
            return !string.IsNullOrEmpty(body) && body.Length > 4000;
        }

        private static bool TestRawReal(string ip)
        {
            string body = Curl(
                "-sS --http1.1 --connect-timeout 3 --max-time 6 " +
                "--resolve raw.githubusercontent.com:443:" + ip + " " +
                "https://raw.githubusercontent.com/git/git/master/README.md",
                10000);
            return !string.IsNullOrEmpty(body) && body.Length > 50;
        }

        private bool IsSystemHealthy(out string detail)
        {
            string htmlCode = (Curl(
                "-sS --http1.1 --connect-timeout 4 --max-time 10 -A \"Mozilla/5.0\" -o NUL -w %{http_code} https://github.com/",
                12000) ?? "").Trim();
            if (htmlCode != "200" && htmlCode != "301" && htmlCode != "302")
            {
                detail = "网页 HTTPS 失败 (http=" + (htmlCode.Length == 0 ? "timeout" : htmlCode) + ")";
                return false;
            }
            string apiCode = (Curl(
                "-sS --http1.1 --connect-timeout 3 --max-time 8 -o NUL -w %{http_code} https://api.github.com/",
                10000) ?? "").Trim();
            if (apiCode != "200")
            {
                detail = "API 失败 (http=" + (apiCode.Length == 0 ? "timeout" : apiCode) + ")";
                return false;
            }
            string ip = CurrentIp();
            if (!string.IsNullOrEmpty(ip) && TestGitReal(ip))
            {
                detail = "网页 / API / git 协议均正常";
                return true;
            }
            detail = "网页与 API 正常（git 探测超时）";
            return true;
        }

        private string FindGitHubIp(string exclude)
        {
            foreach (var ip in ProbeIps)
            {
                if (ip == exclude) continue;
                if (TestGitReal(ip) && TestHtmlReal(ip)) return ip;
            }
            foreach (var ip in ProbeIps)
            {
                if (TestGitReal(ip)) return ip;
            }
            return null;
        }

        private string FindRawIp()
        {
            foreach (var ip in RawIps)
                if (TestRawReal(ip)) return ip;
            return "185.199.108.133";
        }

        private bool WriteHosts(string githubIp, string rawIp)
        {
            if (!IsAdmin()) return false;
            string text;
            try { text = File.ReadAllText(HostsPath); }
            catch (Exception ex) { Log("read hosts: " + ex.Message); return false; }

            string pattern = Regex.Escape(BeginTag) + ".*?" + Regex.Escape(EndTag) + @"\r?\n?";
            text = Regex.Replace(text, pattern, "", RegexOptions.Singleline);

            var sb = new StringBuilder(text.TrimEnd());
            sb.Append("\r\n\r\n");
            sb.AppendLine(BeginTag);
            sb.AppendLine(githubIp + " github.com");
            sb.AppendLine(githubIp + " www.github.com");
            sb.AppendLine("20.205.243.168 api.github.com");
            sb.AppendLine("20.205.243.165 codeload.github.com");
            sb.AppendLine(rawIp + " raw.githubusercontent.com");
            sb.AppendLine("185.199.109.215 github.githubassets.com");
            sb.AppendLine(rawIp + " objects.githubusercontent.com");
            sb.AppendLine(rawIp + " avatars.githubusercontent.com");
            sb.AppendLine(rawIp + " avatars0.githubusercontent.com");
            sb.AppendLine(rawIp + " avatars1.githubusercontent.com");
            sb.AppendLine(rawIp + " avatars2.githubusercontent.com");
            sb.AppendLine(rawIp + " avatars3.githubusercontent.com");
            sb.AppendLine(rawIp + " avatars4.githubusercontent.com");
            sb.AppendLine(rawIp + " avatars5.githubusercontent.com");
            sb.AppendLine(rawIp + " gist.githubusercontent.com");
            sb.AppendLine(rawIp + " user-images.githubusercontent.com");
            sb.AppendLine(rawIp + " media.githubusercontent.com");
            sb.AppendLine(rawIp + " camo.githubusercontent.com");
            sb.AppendLine(rawIp + " cloud.githubusercontent.com");
            sb.AppendLine(rawIp + " private-user-images.githubusercontent.com");
            sb.AppendLine(EndTag);

            try
            {
                File.WriteAllText(HostsPath, sb.ToString(), new UTF8Encoding(false));
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "ipconfig.exe",
                    Arguments = "/flushdns",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (p != null) p.WaitForExit(5000);
                Thread.Sleep(400);
            }
            catch (Exception ex)
            {
                Log("write hosts fail: " + ex.Message);
                return false;
            }

            try { return File.ReadAllText(HostsPath).Contains(githubIp); }
            catch { return false; }
        }

        private StatusSnap CaptureStatus()
        {
            var st = new StatusSnap
            {
                Time = DateTime.Now,
                Admin = IsAdmin(),
                Ip = CurrentIp()
            };
            try
            {
                string detail;
                st.Healthy = IsSystemHealthy(out detail);
                st.Detail = detail;
            }
            catch (Exception ex)
            {
                st.Healthy = false;
                st.Detail = ex.Message;
            }
            st.RefreshCount = LoadRefreshCount();
            return st;
        }

        private void UpdateUi(StatusSnap st)
        {
            if (_form == null || _form.IsDisposed) return;
            if (_form.InvokeRequired)
            {
                try { _form.BeginInvoke(new Action(() => UpdateUi(st))); } catch { }
                return;
            }

            _lblIp.Text = string.IsNullOrEmpty(st.Ip) ? "(无法解析)" : st.Ip;
            if (st.Healthy)
            {
                _lblHealth.Text = "正常 · " + st.Detail;
                _lblHealth.ForeColor = COk;
                _badge.BackColor = Color.FromArgb(240, 248, 242);
                var dot = _badge.Controls.OfType<Label>().FirstOrDefault(l => l.Name == "dot");
                if (dot != null) dot.ForeColor = COk;
            }
            else
            {
                _lblHealth.Text = "异常 · " + st.Detail;
                _lblHealth.ForeColor = CBad;
                _badge.BackColor = Color.FromArgb(255, 245, 245);
                var dot = _badge.Controls.OfType<Label>().FirstOrDefault(l => l.Name == "dot");
                if (dot != null) dot.ForeColor = CBad;
            }

            _lblMeta.Text = string.Format(
                "权限：{0}    自动切换：{1} 次    检查间隔：{2} 秒{3}最后检查：{4:HH:mm:ss}    版本：v{5}",
                st.Admin ? "管理员" : "非管理员",
                st.RefreshCount,
                IntervalSec,
                Environment.NewLine,
                st.Time,
                Version);

            if (_miStatus != null && !_miStatus.IsDisposed)
                _miStatus.Text = st.Healthy ? "状态: 正常 " + st.Ip : "状态: 异常";
        }

        private async Task RefreshAsync(bool silent)
        {
            if (_checking) return;
            _checking = true;
            try
            {
                SetBusy(true, "刷新中…");
                SetProgress("正在探测可用 GitHub IP，请稍候…", CBlue);
                if (!silent)
                {
                    try { _tabs.SelectedIndex = 0; } catch { }
                }

                string newIp = null;
                string msg = null;
                await Task.Run(() =>
                {
                    if (!IsAdmin())
                    {
                        msg = "需要管理员权限才能写入 hosts，请右键「以管理员身份运行」。";
                        Log("refresh denied: not admin");
                        return;
                    }

                    string detail;
                    if (IsSystemHealthy(out detail))
                    {
                        msg = "当前已正常（" + CurrentIp() + "），无需切换。";
                        Log("already healthy, skip");
                        return;
                    }

                    string cur = CurrentIp();
                    Log("unhealthy " + (cur ?? "null") + " (" + detail + "), probing...");
                    newIp = FindGitHubIp(cur);
                    if (newIp == null)
                    {
                        msg = "未能找到可用 IP，请检查网络后重试。";
                        Log("no working IP");
                        return;
                    }
                    string raw = FindRawIp();
                    if (WriteHosts(newIp, raw))
                    {
                        IncRefreshCount();
                        msg = "已切换 IP：" + cur + " → " + newIp;
                        Log("switched " + cur + " -> " + newIp);
                    }
                    else
                    {
                        msg = "写入 hosts 失败，请检查安全软件是否拦截。";
                        Log("write hosts failed");
                    }
                });

                var st = CaptureStatus();
                UpdateUi(st);

                if (st.Healthy)
                    SetProgress(msg ?? ("完成。当前 IP：" + st.Ip), COk);
                else
                    SetProgress(msg ?? ("仍异常：" + st.Detail), CBad);

                if (!silent)
                {
                    if (st.Healthy)
                        Balloon(msg ?? "刷新完成", ToolTipIcon.Info);
                    else
                        Balloon(msg ?? "刷新后仍异常，请看进度提示", ToolTipIcon.Warning);
                }
            }
            finally
            {
                SetBusy(false, "立即刷新");
                _checking = false;
            }
        }

        private async Task TickAsync()
        {
            if (_checking) return;
            _checking = true;
            try
            {
                var st = await Task.Run(() => CaptureStatus());
                UpdateUi(st);
                if (!st.Healthy)
                {
                    Log("tick fail (" + st.Detail + "), auto refresh", "WARN");
                    SetProgress("定时检查异常，正在自动切换…", CWarn);
                    _checking = false;
                    await RefreshAsync(true);
                    return;
                }
                SetProgress("提示：关闭窗口会缩到托盘，不会退出。", CBlue);
            }
            catch (Exception ex)
            {
                Log("tick error: " + ex.Message, "ERROR");
            }
            finally
            {
                _checking = false;
            }
        }

        private string ExePath()
        {
            return Application.ExecutablePath;
        }

        private string StartupLink()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "GitHubHostsAuto.lnk");
        }

        private void ToggleAutostart()
        {
            try
            {
                string lnk = StartupLink();
                if (File.Exists(lnk))
                {
                    File.Delete(lnk);
                    _miAutoStart.Checked = false;
                    Log("autostart removed");
                }
                else
                {
                    Type t = Type.GetTypeFromProgID("WScript.Shell");
                    dynamic shell = Activator.CreateInstance(t);
                    dynamic sc = shell.CreateShortcut(lnk);
                    sc.TargetPath = ExePath();
                    sc.WorkingDirectory = Path.GetDirectoryName(ExePath());
                    sc.IconLocation = ExePath() + ",0";
                    sc.Description = AppTitle;
                    sc.Save();
                    _miAutoStart.Checked = true;
                    Log("autostart added");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("开机自启设置失败: " + ex.Message, AppTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private int LoadRefreshCount()
        {
            try
            {
                if (!File.Exists(_statePath)) return 0;
                var m = Regex.Match(File.ReadAllText(_statePath), "\"RefreshCount\"\\s*:\\s*(\\d+)");
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
            catch { }
            return 0;
        }

        private void IncRefreshCount()
        {
            try
            {
                int c = LoadRefreshCount() + 1;
                File.WriteAllText(_statePath,
                    string.Format("{{\"RefreshCount\":{0},\"LastOk\":\"{1:o}\"}}", c, DateTime.Now),
                    Encoding.UTF8);
            }
            catch { }
        }

        private void Log(string msg, string level = "INFO")
        {
            try
            {
                File.AppendAllText(_logPath,
                    string.Format("[{0:yyyy-MM-dd HH:mm:ss}] [{1}] {2}{3}",
                        DateTime.Now, level, msg, Environment.NewLine),
                    Encoding.UTF8);
            }
            catch { }
        }
    }
}
