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
        private const string Version = "1.3.6";
        private const int IntervalSec = 2;

        private static readonly string[] ProbeIps =
        {
            "140.82.112.4","140.82.113.4","140.82.114.3","140.82.114.4",
            "140.82.121.3","140.82.121.4","140.82.112.3","140.82.113.3",
            "20.87.245.0","4.208.26.197","20.27.177.113","20.233.83.145",
            "20.200.245.247","20.201.28.151","20.207.73.82","20.205.243.166"
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
        private DateTime _nextAutoRefresh = DateTime.MinValue;

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
                using (var path = RoundRect(new Rectangle(2, 2, 60, 60), 12))
                using (var b = new SolidBrush(Color.FromArgb(24, 26, 32)))
                    g.FillPath(b, path);
                using (var b = new SolidBrush(CBlue))
                    g.FillEllipse(b, 13, 13, 38, 38);
                TextRenderer.DrawText(
                    g, "G",
                    new Font("Segoe UI", 16f, FontStyle.Bold),
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

            int W = 700;

            int H = 780;

            var f = new Form

            {

                Text = AppTitle + " v" + Version,

                ClientSize = new Size(W, H),

                StartPosition = FormStartPosition.CenterScreen,

                FormBorderStyle = FormBorderStyle.FixedSingle,

                MaximizeBox = false,

                BackColor = CBg,

                Font = new Font("Microsoft YaHei UI", 10f),

                Icon = _appIcon,

                ShowInTaskbar = true

            };



            var header = new Panel

            {

                Bounds = new Rectangle(0, 0, W, 118),

                BackColor = CHeader

            };

            header.Controls.Add(new Label

            {

                Text = "GitHub Hosts 自动刷新",

                AutoSize = true,

                Location = new Point(28, 22),

                Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold),

                ForeColor = Color.White

            });

            header.Controls.Add(new Label

            {

                Text = "v" + Version + "   ·   每 " + IntervalSec + " 秒自动检查   ·   失效自动切换",

                AutoSize = true,

                Location = new Point(28, 68),

                Font = new Font("Microsoft YaHei UI", 9.5f),

                ForeColor = Color.FromArgb(180, 188, 200)

            });

            f.Controls.Add(header);



            tabs = new TabControl

            {

                Bounds = new Rectangle(20, 134, W - 40, H - 154),

                Font = new Font("Microsoft YaHei UI", 10.5f),

                Padding = new Point(18, 8)

            };



            var tabStatus = new TabPage("状态");

            tabStatus.BackColor = CBg;



            int pageW = tabs.Width - 12;

            int pageH = tabs.Height - 40;

            int pad = 18;



            var card = new Panel

            {

                Bounds = new Rectangle(pad, pad, pageW - pad * 2, 210),

                BackColor = Color.White

            };

            card.Paint += (s, e) =>

            {

                var p = (Panel)s;

                using (var pen = new Pen(CBorder))

                    e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);

            };

            card.Controls.Add(new Label

            {

                Text = "当前 github.com",

                AutoSize = true,

                Location = new Point(22, 18),

                Font = new Font("Microsoft YaHei UI", 9.5f),

                ForeColor = CMuted

            });

            lblIp = new Label

            {

                Text = "检测中…",

                AutoSize = true,

                Location = new Point(22, 46),

                Font = new Font("Consolas", 18f, FontStyle.Bold),

                ForeColor = CText

            };

            card.Controls.Add(lblIp);



            Panel badgeLocal = new Panel

            {

                Bounds = new Rectangle(22, 110, card.Width - 44, 78),

                BackColor = Color.FromArgb(246, 248, 250)

            };

            badgeLocal.Paint += (s, e) =>

            {

                var p = (Panel)s;

                using (var path = RoundRect(new Rectangle(0, 0, p.Width - 1, p.Height - 1), 10))

                using (var pen = new Pen(CBorder))

                    e.Graphics.DrawPath(pen, path);

            };

            var dot = new Label

            {

                Name = "dot",

                Text = "●",

                AutoSize = true,

                Location = new Point(16, 18),

                Font = new Font("Segoe UI", 11f),

                ForeColor = CMuted

            };

            badgeLocal.Controls.Add(dot);

            lblHealth = new Label

            {

                Text = "正在检测…",

                AutoSize = false,

                Bounds = new Rectangle(40, 14, badgeLocal.Width - 54, 52),

                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),

                ForeColor = CMuted

            };

            badgeLocal.Controls.Add(lblHealth);

            card.Controls.Add(badgeLocal);

            badge = badgeLocal;

            tabStatus.Controls.Add(card);



            lblMeta = new Label

            {

                Bounds = new Rectangle(pad, 246, pageW - pad * 2, 78),

                Font = new Font("Microsoft YaHei UI", 10f),

                ForeColor = CMuted,

                Text = ""

            };

            tabStatus.Controls.Add(lblMeta);



            int btnY = 336;

            int totalBtnW = pageW - pad * 2;

            int gap = 10;

            int btnW = (totalBtnW - gap * 2) / 3;

            btnRefresh = MakeBtn("立即刷新", true);

            btnRefresh.Bounds = new Rectangle(pad, btnY, btnW, 46);

            btnLog = MakeBtn("日志", false);

            btnLog.Bounds = new Rectangle(pad + btnW + gap, btnY, btnW, 46);

            btnHide = MakeBtn("隐藏到托盘", false);

            btnHide.Bounds = new Rectangle(pad + (btnW + gap) * 2, btnY, btnW, 46);

            tabStatus.Controls.Add(btnRefresh);

            tabStatus.Controls.Add(btnLog);

            tabStatus.Controls.Add(btnHide);



            lblProgress = new Label

            {

                Bounds = new Rectangle(pad, btnY + 64, totalBtnW, 100),

                Font = new Font("Microsoft YaHei UI", 10f),

                ForeColor = CBlue,

                Text = "提示：关闭窗口会缩到托盘，不会退出。"

            };

            tabStatus.Controls.Add(lblProgress);



            var hint = new Label

            {

                Bounds = new Rectangle(pad, btnY + 176, totalBtnW, 56),

                Font = new Font("Microsoft YaHei UI", 9f),

                ForeColor = CMuted,

                Text = "退出：托盘菜单「退出」。开机自启：托盘菜单「开机自动启动」。"

            };

            tabStatus.Controls.Add(hint);



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

                Font = new Font("Microsoft YaHei UI", 10.5f),

                Bounds = new Rectangle(18, 12, pageW - 24, pageH - 20),

                Text =

                    "当前版本：v" + Version + "\r\n\r\n" +

                    "【v1.3.1】\r\n" +

                    "· 统一修复顶栏、状态徽章、底部进度文字被裁切\r\n" +

                    "· 窗口加大到 700x780，间距统一\r\n\r\n" +

                    "【v1.3.0】\r\n" +

                    "· 强制刷新切换 IP\r\n" +

                    "· 自动检查间隔 30 秒\r\n\r\n" +

                    "【v1.2.0】状态 / 版本说明双页签\r\n" +

                    "【v1.1.0】托盘常驻、自动探测 IP\r\n" +

                    "【v1.0.0】首个单文件桌面版\r\n"

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
                "-sS --http1.1 --connect-timeout 2 --max-time 4 " +
                "-A \"git/2.22.0.windows.1\" " +
                "--resolve github.com:443:" + ip + " " +
                "\"https://github.com/git/git.git/info/refs?service=git-upload-pack\"",
                10000);
            return !string.IsNullOrEmpty(body) && body.Contains("service=git-upload-pack");
        }

        private static bool TestHtmlReal(string ip)
        {
            string body = Curl(
                "-sS --http1.1 --connect-timeout 2 --max-time 5 -A \"Mozilla/5.0\" " +
                "--resolve github.com:443:" + ip + " https://github.com/",
                12000);
            return !string.IsNullOrEmpty(body) && body.Length > 4000;
        }

        private static bool TestRawReal(string ip)
        {
            string body = Curl(
                "-sS --http1.1 --connect-timeout 2 --max-time 4 " +
                "--resolve raw.githubusercontent.com:443:" + ip + " " +
                "https://raw.githubusercontent.com/git/git/master/README.md",
                10000);
            return !string.IsNullOrEmpty(body) && body.Length > 50;
        }

        private bool IsSystemHealthy(out string detail)
        {
            string htmlCode = (Curl(
                "-sS --http1.1 --connect-timeout 1 --max-time 2 -A \"Mozilla/5.0\" -o NUL -w %{http_code} https://github.com/",
                3000) ?? "").Trim();
            if (htmlCode != "200" && htmlCode != "301" && htmlCode != "302")
            {
                detail = "网页失败 (http=" + (htmlCode.Length == 0 ? "超时" : htmlCode) + ")";
                return false;
            }
            string apiCode = (Curl(
                "-sS --http1.1 --connect-timeout 1 --max-time 2 -o NUL -w %{http_code} https://api.github.com/",
                3000) ?? "").Trim();
            if (apiCode != "200")
            {
                detail = "API 失败 (http=" + (apiCode.Length == 0 ? "超时" : apiCode) + ")";
                return false;
            }
            string ip = CurrentIp();
            if (!string.IsNullOrEmpty(ip) && TestGitReal(ip))
            {
                detail = "网页 / API / git 正常";
                return true;
            }
            detail = "网页与 API 正常";
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
            sb.AppendLine("140.82.112.6 api.github.com");
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
                "权限：{0}　切换：{1} 次　间隔：{2}s\r\n检查：{3:HH:mm:ss}　v{4}",
                st.Admin ? "管理员" : "非管理员",
                st.RefreshCount,
                IntervalSec,
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
                SetProgress("正在强制探测可用 GitHub IP，请稍候…", CBlue);
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

                    string cur = CurrentIp();
                    Log("force refresh, current=" + (cur ?? "null"));

                    // Always re-probe. Prefer a different IP so "立即刷新" visibly switches.
                    string found = FindGitHubIp(cur);
                    if (found == null)
                        found = FindGitHubIp(null);
                    if (found == null)
                    {
                        _nextAutoRefresh = DateTime.Now.AddSeconds(30);
                        msg = "当前网络下未找到可用 IP，30 秒后自动重试。";
                        Log("no working IP, backoff 30s");
                        return;
                    }

                    if (found == cur)
                    {
                        // rewrite same IP to confirm hosts is applied
                        string rawSame = FindRawIp();
                        if (WriteHosts(found, rawSame))
                        {
                            msg = "hosts 已确认写入：" + found + "（当前已是该 IP，无需切换）";
                            Log("hosts confirmed same ip " + found);
                        }
                        else
                        {
                            msg = "写入 hosts 失败。";
                        }
                        return;
                    }

                    string raw = FindRawIp();
                    if (WriteHosts(found, raw))
                    {
                        IncRefreshCount();
                        msg = "已切换 IP：" + cur + " → " + found;
                        Log("switched " + cur + " -> " + found);
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
                    SetProgress((msg ?? "刷新完成") + "\r\n当前解析：" + st.Ip + "  ·  " + st.Detail, COk);
                else
                    SetProgress(msg ?? ("仍异常：" + st.Detail), CBad);

                if (!silent)
                {
                    if (st.Healthy)
                        Balloon(msg ?? "刷新完成", ToolTipIcon.Info);
                    else
                        Balloon(msg ?? "刷新后仍异常", ToolTipIcon.Warning);
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
                    if (DateTime.Now < _nextAutoRefresh)
                    {
                        int wait = Math.Max(0, (int)(_nextAutoRefresh - DateTime.Now).TotalSeconds);
                        SetProgress("网络异常，等待可用 IP…（" + wait + " 秒后重试）", CWarn);
                        return;
                    }
                    Log("tick fail (" + st.Detail + "), auto refresh", "WARN");
                    SetProgress("定时检查异常，正在自动切换…", CWarn);
                    _checking = false;
                    await RefreshAsync(true);
                    return;
                }
                _nextAutoRefresh = DateTime.MinValue;
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
