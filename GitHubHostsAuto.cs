using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
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
                ServicePointManager.DefaultConnectionLimit = 32;

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

        private readonly NotifyIcon _notify;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Form _form;
        private readonly Label _lblIp, _lblHealth, _lblMeta, _lblHint;
        private readonly Panel _badge;
        private readonly Button _btnRefresh, _btnLog, _btnHide;
        private readonly ToolStripMenuItem _miStatus, _miAutoStart;
        private readonly string _logPath, _statePath, _dataDir;
        private readonly Icon _appIcon;
        private bool _checking, _exit;

        // colors
        private static readonly Color CBg = Color.FromArgb(250, 250, 251);
        private static readonly Color CHeader = Color.FromArgb(36, 41, 47);
        private static readonly Color CText = Color.FromArgb(36, 41, 47);
        private static readonly Color CMuted = Color.FromArgb(110, 118, 129);
        private static readonly Color COk = Color.FromArgb(26, 127, 55);
        private static readonly Color CBad = Color.FromArgb(207, 34, 46);
        private static readonly Color CBtn = Color.FromArgb(36, 41, 47);
        private static readonly Color CBlue = Color.FromArgb(88, 166, 255);
        private static readonly Color CCard = Color.White;
        private static readonly Color CBorder = Color.FromArgb(209, 217, 224);

        public TrayApp()
        {
            _dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GitHubHostsAuto");
            Directory.CreateDirectory(_dataDir);
            _logPath = Path.Combine(_dataDir, "github-hosts.log");
            _statePath = Path.Combine(_dataDir, "state.json");

            _appIcon = CreateIcon();
            _form = BuildForm();
            _lblIp = FindLabel(_form, "lblIp");
            _lblHealth = FindLabel(_form, "lblHealth");
            _lblMeta = FindLabel(_form, "lblMeta");
            _lblHint = FindLabel(_form, "lblHint");
            _badge = FindPanel(_form, "badge");
            _btnRefresh = FindButton(_form, "btnRefresh");
            _btnLog = FindButton(_form, "btnLog");
            _btnHide = FindButton(_form, "btnHide");

            ContextMenuStrip menu = BuildMenu(out _miStatus, out _miAutoStart);

            _notify = new NotifyIcon
            {
                Icon = _appIcon,
                Text = AppTitle,
                Visible = true,
                ContextMenuStrip = menu
            };
            _notify.DoubleClick += (s, e) => ToggleForm();
            _notify.BalloonTipClicked += (s, e) => ShowForm();

            _btnHide.Click += (s, e) => _form.Hide();
            _btnLog.Click += (s, e) => OpenLog();
            _btnRefresh.Click += async (s, e) => await RefreshAsync(false);

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
            _timer.Tick += async (s, e) => await TickAsync();
            _timer.Start();

            if (!IsAdmin())
                Balloon("未以管理员运行，无法自动修改 hosts。", ToolTipIcon.Warning);

            Log("started admin=" + IsAdmin() + " curl=" + File.Exists(CurlPath));
            var st = CaptureStatus();
            UpdateUi(st);
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

        // ---------- find controls ----------

        private static Label FindLabel(Control root, string tag)
        {
            foreach (Control c in root.Controls)
            {
                if (c.Name == tag && c is Label) return (Label)c;
                var sub = FindLabel(c, tag);
                if (sub != null) return sub;
            }
            return null;
        }

        private static Button FindButton(Control root, string tag)
        {
            foreach (Control c in root.Controls)
            {
                if (c.Name == tag && c is Button) return (Button)c;
                var sub = FindButton(c, tag);
                if (sub != null) return sub;
            }
            return null;
        }

        private static Panel FindPanel(Control root, string tag)
        {
            foreach (Control c in root.Controls)
            {
                if (c.Name == tag && c is Panel) return (Panel)c;
                var sub = FindPanel(c, tag);
                if (sub != null) return sub;
            }
            return null;
        }

        // ---------- icon ----------

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

        // ---------- UI ----------

        private Form BuildForm()
        {
            int W = 560;
            int H = 500;
            var f = new Form
            {
                Text = AppTitle,
                ClientSize = new Size(W, H),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedSingle,
                MaximizeBox = false,
                BackColor = CBg,
                Font = new Font("Microsoft YaHei UI", 9.5f),
                Icon = _appIcon,
                ShowInTaskbar = true
            };

            // header
            var header = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(W, 78),
                BackColor = CHeader
            };
            header.Controls.Add(new Label
            {
                Text = "GitHub Hosts 自动刷新",
                AutoSize = true,
                Location = new Point(24, 16),
                Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold),
                ForeColor = Color.White
            });
            header.Controls.Add(new Label
            {
                Text = "自动探测可用 IP · 失效自动切换 · 托盘常驻",
                AutoSize = true,
                Location = new Point(24, 48),
                Font = new Font("Microsoft YaHei UI", 9f),
                ForeColor = Color.FromArgb(170, 178, 190)
            });
            f.Controls.Add(header);

            int left = 24;
            int cardW = W - 48;

            // status card
            var card = new Panel
            {
                Location = new Point(left, 98),
                Size = new Size(cardW, 160),
                BackColor = CCard
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
                Font = new Font("Microsoft YaHei UI", 9f),
                ForeColor = CMuted
            });
            var lblIp = new Label
            {
                Name = "lblIp",
                Text = "检测中…",
                AutoSize = true,
                Location = new Point(16, 36),
                Font = new Font("Consolas", 16f, FontStyle.Bold),
                ForeColor = CText
            };
            card.Controls.Add(lblIp);

            var badge = new Panel
            {
                Name = "badge",
                Location = new Point(16, 84),
                Size = new Size(cardW - 32, 58),
                BackColor = Color.FromArgb(246, 248, 250)
            };
            badge.Paint += (s, e) =>
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
                Location = new Point(14, 18),
                Font = new Font("Segoe UI", 12f),
                ForeColor = CMuted
            };
            badge.Controls.Add(dot);
            var lblHealth = new Label
            {
                Name = "lblHealth",
                Text = "正在检测…",
                AutoSize = true,
                MaximumSize = new Size(cardW - 70, 44),
                Location = new Point(40, 14),
                Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
                ForeColor = CMuted
            };
            badge.Controls.Add(lblHealth);
            card.Controls.Add(badge);
            f.Controls.Add(card);

            // meta
            var lblMeta = new Label
            {
                Name = "lblMeta",
                Text = "",
                Location = new Point(left, 272),
                Size = new Size(cardW, 70),
                Font = new Font("Microsoft YaHei UI", 9.5f),
                ForeColor = CMuted
            };
            f.Controls.Add(lblMeta);

            // buttons
            _btnRefreshFlat = MakeBtn("立即刷新", true);
            _btnRefreshFlat.Name = "btnRefresh";
            _btnRefreshFlat.Location = new Point(left, 356);
            _btnRefreshFlat.Size = new Size(150, 40);

            _btnLogFlat = MakeBtn("打开日志", false);
            _btnLogFlat.Name = "btnLog";
            _btnLogFlat.Location = new Point(left + 166, 356);
            _btnLogFlat.Size = new Size(150, 40);

            _btnHideFlat = MakeBtn("隐藏到托盘", false);
            _btnHideFlat.Name = "btnHide";
            _btnHideFlat.Location = new Point(left + 332, 356);
            _btnHideFlat.Size = new Size(150, 40);

            f.Controls.Add(_btnRefreshFlat);
            f.Controls.Add(_btnLogFlat);
            f.Controls.Add(_btnHideFlat);

            var hint = new Label
            {
                Name = "lblHint",
                Text = "关闭窗口不会退出，会缩到右下角托盘继续监控。\r\n退出请用托盘菜单「退出」。如需开机自启，在托盘菜单勾选。",
                Location = new Point(left, 414),
                Size = new Size(cardW, 56),
                Font = new Font("Microsoft YaHei UI", 9f),
                ForeColor = CMuted
            };
            f.Controls.Add(hint);

            return f;
        }

        // temp holders so BuildForm can set fields before we assign private readonly
        private Button _btnRefreshFlat, _btnLogFlat, _btnHideFlat;

        private static Button MakeBtn(string text, bool primary)
        {
            var b = new Button
            {
                Text = text,
                Height = 36,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
                BackColor = primary ? CBtn : Color.White,
                ForeColor = primary ? Color.White : CText
            };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = CBorder;
            b.FlatAppearance.MouseOverBackColor = primary
                ? Color.FromArgb(56, 62, 70)
                : Color.FromArgb(246, 248, 250);
            return b;
        }

        private ContextMenuStrip BuildMenu(out ToolStripMenuItem miStatus, out ToolStripMenuItem miAutoStart)
        {
            var menu = new ContextMenuStrip { BackColor = Color.White, Font = new Font("Microsoft YaHei UI", 9f) };

            var miShow = new ToolStripMenuItem("打开面板");
            miShow.Click += (s, e) => ShowForm();
            menu.Items.Add(miShow);

            var miRefresh = new ToolStripMenuItem("立即刷新 IP");
            miRefresh.Click += async (s, e) => await RefreshAsync(false);
            menu.Items.Add(miRefresh);

            menu.Items.Add(new ToolStripSeparator());
            miStatus = new ToolStripMenuItem("状态: 检测中…") { Enabled = false };
            menu.Items.Add(miStatus);

            miAutoStart = new ToolStripMenuItem("开机自动启动") { CheckOnClick = true };
            miAutoStart.Checked = AutostartEnabled();
            miAutoStart.Click += (s, e) => ToggleAutostart();
            menu.Items.Add(miAutoStart);

            var miLog = new ToolStripMenuItem("打开日志");
            miLog.Click += (s, e) => OpenLog();
            menu.Items.Add(miLog);

            menu.Items.Add(new ToolStripSeparator());
            var miExit = new ToolStripMenuItem("退出");
            miExit.Click += (s, e) => ExitApp();
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

        // ---------- network ----------

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

        /// <summary>System health: real HTTPS to github.com via hosts/DNS (most reliable).</summary>
        private static bool TryHttpGet(string url, int timeoutMs, out int status, out int length)
        {
            status = 0;
            length = 0;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.AllowAutoRedirect = true;
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) GitHubHostsAuto/1.0";
                req.Accept = "*/*";
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    status = (int)resp.StatusCode;
                    using (var rs = resp.GetResponseStream())
                    {
                        if (rs != null)
                        {
                            var buf = new byte[8192];
                            int read;
                            while ((read = rs.Read(buf, 0, buf.Length)) > 0)
                            {
                                length += read;
                                if (length > 400000) break;
                            }
                        }
                    }
                    return status >= 200 && status < 400;
                }
            }
            catch (WebException wex)
            {
                try
                {
                    var hr = wex.Response as HttpWebResponse;
                    if (hr != null)
                    {
                        status = (int)hr.StatusCode;
                        return status >= 200 && status < 400;
                    }
                }
                catch { }
                return false;
            }
            catch { return false; }
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
            catch
            {
                return null;
            }
        }

        private static bool TestGitReal(string ip)
        {
            // Prefer curl --resolve for IP probe
            string body = Curl(
                "-sS --http1.1 --connect-timeout 3 --max-time 6 " +
                "-A \"git/2.22.0.windows.1\" " +
                "--resolve github.com:443:" + ip + " " +
                "\"https://github.com/git/git.git/info/refs?service=git-upload-pack\"",
                10000);
            if (!string.IsNullOrEmpty(body) && body.Contains("service=git-upload-pack"))
                return true;

            // Fallback: if probing the currently resolved IP, use normal HTTP
            return false;
        }

        private static bool TestHtmlReal(string ip)
        {
            string body = Curl(
                "-sS --http1.1 --connect-timeout 3 --max-time 8 " +
                "-A \"Mozilla/5.0\" " +
                "--resolve github.com:443:" + ip + " " +
                "https://github.com/",
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

        /// <summary>Health check via curl (hosts/DNS). HttpWebRequest often fails on this network.</summary>
        private bool IsSystemHealthy(out string detail)
        {
            string htmlCode = Curl("-sS --http1.1 --connect-timeout 4 --max-time 10 -A \"Mozilla/5.0\" -o NUL -w %{http_code} https://github.com/", 12000);
            htmlCode = (htmlCode ?? "").Trim();
            if (htmlCode != "200" && htmlCode != "301" && htmlCode != "302")
            {
                detail = "网页 HTTPS 失败 (http=" + (htmlCode.Length == 0 ? "timeout" : htmlCode) + ")";
                return false;
            }
            string apiCode = Curl("-sS --http1.1 --connect-timeout 3 --max-time 8 -o NUL -w %{http_code} https://api.github.com/", 10000);
            apiCode = (apiCode ?? "").Trim();
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
                if (ip == exclude) continue;
                // allow web-only if git probe flaky but web is real HTML
                if (TestHtmlReal(ip) && TestGitReal(ip)) return ip;
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
                "权限：{0}    自动切换：{1} 次    间隔：{2} 秒{3}最后检查：{4:HH:mm:ss}",
                st.Admin ? "管理员" : "非管理员",
                st.RefreshCount,
                IntervalSec,
                Environment.NewLine,
                st.Time);

            if (_miStatus != null && !_miStatus.IsDisposed)
                _miStatus.Text = st.Healthy ? "状态: 正常 " + st.Ip : "状态: 异常";
        }

        private async Task RefreshAsync(bool silent)
        {
            if (_checking) return;
            _checking = true;
            try
            {
                if (_btnRefresh != null && !_btnRefresh.IsDisposed)
                    _btnRefresh.Enabled = false;

                bool wrote = false;
                string newIp = null;
                await Task.Run(() =>
                {
                    if (!IsAdmin())
                    {
                        if (!silent)
                            MessageBox.Show("写入 hosts 需要管理员权限。请右键 exe →「以管理员身份运行」。",
                                "权限不足", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    string detail;
                    if (IsSystemHealthy(out detail))
                    {
                        Log("still healthy, skip refresh");
                        return;
                    }

                    string cur = CurrentIp();
                    Log("unhealthy " + (cur ?? "null") + " (" + detail + "), probing...");
                    newIp = FindGitHubIp(cur);
                    if (newIp == null)
                    {
                        Log("no working IP");
                        if (!silent)
                            MessageBox.Show("未能找到可用的 github.com IP。\n请检查网络或稍后重试。",
                                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    string raw = FindRawIp();
                    wrote = WriteHosts(newIp, raw);
                    if (wrote)
                    {
                        IncRefreshCount();
                        Log("switched " + cur + " -> " + newIp);
                    }
                });

                var st = CaptureStatus();
                UpdateUi(st);
                if (!silent)
                {
                    if (st.Healthy)
                        Balloon(string.IsNullOrEmpty(newIp) ? "当前已正常" : "已切换 IP: " + newIp, ToolTipIcon.Info);
                    else
                        Balloon("刷新后仍异常，请打开日志查看", ToolTipIcon.Warning);
                }
            }
            finally
            {
                if (_btnRefresh != null && !_btnRefresh.IsDisposed)
                    _btnRefresh.Enabled = true;
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
                    _checking = false;
                    await RefreshAsync(true);
                    return;
                }
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

        // ---------- autostart / state / log ----------

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

        private bool AutostartEnabled()
        {
            return File.Exists(StartupLink());
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
