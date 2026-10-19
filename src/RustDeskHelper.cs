// =====================================================================
//  RustDesk 一键配置助手 v5.1  (C# / WinForms · 原生 exe · 秒开 · 全中文)
//  前提：已安装官方 RustDesk 1.5.x；不修改原生程序文件
//  说明：本工具只是“外挂面板”，本身不实现远程控制功能
//  编译：见同目录 build.ps1（使用系统自带 csc.exe，无需 Visual Studio）
// =====================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Pipes;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RdHelper
{
    // =================================================================
    //  常量、颜色、字体、通用小工具
    // =================================================================
    static class U
    {
        // 本仓库是公开版本，不内置任何私人服务器信息（地址、Key、API 全部留空）。
        // 首次使用时请点界面上的【服务器设置】，填入你自己搭建的 ID 服务器 / 中继服务器 / Key。
        public const string DefServer = "";
        public const string DefKey    = "";
        public const string DefApi    = "";
        public const string TaskLogon = "RustDeskHelper";
        public const string TaskBoot  = "RustDeskHelper-Boot";
        public const string SvcName   = "RustDesk";
        public const string Version   = "v5.1";

        public static Stopwatch T0 = Stopwatch.StartNew();   // 启动计时（用于日志输出“秒开”耗时）

        public static readonly string CfgDir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RustDesk");
        public static readonly string CfgCfg  = Path.Combine(CfgDir, "config");
        public static readonly string Cfg1    = Path.Combine(CfgCfg, "RustDesk.toml");
        public static readonly string Cfg2    = Path.Combine(CfgCfg, "RustDesk2.toml");
        public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RustDeskHelper");
        public static readonly string LogFile = Path.Combine(DataDir, "helper.log");
        public static readonly string PwdFile = Path.Combine(DataDir, "fixed-password.txt");
        public static readonly string AppFile = Path.Combine(DataDir, "fixed-password.applied.txt");
        public static readonly UTF8Encoding NoBom = new UTF8Encoding(false);

        // ---------------- 配色 ----------------
        public static readonly Color ClrPage    = Color.FromArgb(243, 245, 248);
        public static readonly Color ClrBorder  = Color.FromArgb(226, 230, 236);
        public static readonly Color ClrText    = Color.FromArgb(31, 35, 41);
        public static readonly Color ClrSub     = Color.FromArgb(107, 114, 128);
        public static readonly Color ClrAccent  = Color.FromArgb(37, 99, 235);
        public static readonly Color ClrAccentD = Color.FromArgb(29, 78, 216);
        public static readonly Color ClrAccentH = Color.FromArgb(59, 130, 246);
        public static readonly Color ClrGreen   = Color.FromArgb(22, 163, 74);
        public static readonly Color ClrRed     = Color.FromArgb(220, 38, 38);
        public static readonly Color ClrAmber   = Color.FromArgb(217, 119, 6);
        public static readonly Color ClrHeaderA = Color.FromArgb(30, 64, 175);
        public static readonly Color ClrHeaderB = Color.FromArgb(37, 99, 235);

        // ---------------- 字体 ----------------
        public static readonly Font FTitle   = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
        public static readonly Font FUI      = new Font("Microsoft YaHei UI", 9F);
        public static readonly Font FSm      = new Font("Microsoft YaHei UI", 8F);
        public static readonly Font FCap     = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        public static readonly Font FPill    = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
        public static readonly Font FMonoBig = new Font("Consolas", 14F, FontStyle.Bold);
        public static readonly Font FMono    = new Font("Consolas", 10F, FontStyle.Bold);
        public static readonly Font FMonoSm  = new Font("Consolas", 9F);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        // ---------------- 圆角路径 ----------------
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 2) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------------- 日志 ----------------
        public static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(LogFile, DateTime.Now.ToString("MM-dd HH:mm:ss") + "  " + msg + "\r\n", NoBom);
            }
            catch { }
        }

        // ---------------- 权限 ----------------
        public static bool IsAdmin()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // ================= TOML 读写 =================
        public static string ReadCfg()
        {
            try { return File.Exists(Cfg2) ? File.ReadAllText(Cfg2, Encoding.UTF8) : ""; }
            catch { return ""; }
        }
        public static void WriteCfg(string t)
        {
            try { File.WriteAllText(Cfg2, t, NoBom); } catch { }
        }
        public static string ReadCfg1()
        {
            try { return File.Exists(Cfg1) ? File.ReadAllText(Cfg1, Encoding.UTF8) : ""; }
            catch { return ""; }
        }

        public static string GetToml(string text, string key)
        {
            if (text == null) return "";
            Match m = Regex.Match(text, "(?m)^\\s*" + Regex.Escape(key) + "\\s*=\\s*'(.*)'\\s*$");
            if (m.Success) return m.Groups[1].Value;
            Match m2 = Regex.Match(text, "(?m)^\\s*" + Regex.Escape(key) + "\\s*=\\s*\"(.*)\"\\s*$");
            if (m2.Success) return m2.Groups[1].Value;
            return "";
        }

        public static string SetToml(string text, string key, string value)
        {
            string line = key + " = '" + value + "'";
            string pat = "(?m)^\\s*" + Regex.Escape(key) + "\\s*=\\s*.*$";
            if (Regex.IsMatch(text, pat))
                return new Regex(pat).Replace(text, line.Replace("$", "$$"), 1);
            if (!text.EndsWith("\n")) text += "\r\n";
            return text + line + "\r\n";
        }

        public static string RemoveTomlKey(string text, string key)
        {
            string pat = "(?m)^[ \t]*" + Regex.Escape(key) + "[ \t]*=[^\\r\\n]*(?:\\r?\\n|$)";
            return Regex.Replace(text, pat, "");
        }

        // 写入服务器配置。绝不写 password 选项（RustDesk 不识别，这正是旧版“固定密码没生效”的原因）
        public static void EnsureConfig(string srv, string key, string api)
        {
            if (string.IsNullOrEmpty(srv)) return;   // 没有服务器地址时不写任何东西，避免把配置写空
            try
            {
                Directory.CreateDirectory(CfgCfg);
                if (!File.Exists(Cfg2)) File.WriteAllText(Cfg2, "", Encoding.ASCII);
                string t = ReadCfg();
                t = RemoveTomlKey(t, "password");
                if (!Regex.IsMatch(t, "(?m)^\\s*\\[options\\]"))
                {
                    if (!t.EndsWith("\n")) t += "\r\n";
                    t += "\r\n[options]\r\n";
                }
                t = SetToml(t, "custom-rendezvous-server", srv);
                t = SetToml(t, "relay-server", srv);
                t = SetToml(t, "key", key);
                if (!string.IsNullOrEmpty(api)) t = SetToml(t, "api-server", api);
                t = SetToml(t, "disable-udp", "N");
                t = SetToml(t, "allow-websocket", "N");
                t = SetToml(t, "enable-lan-discovery", "N");
                t = SetToml(t, "direct-access-port", "21118");
                WriteCfg(t);
            }
            catch { }
        }

        // 该服务器密钥是否已被确认（等于“已成功注册到服务器”）
        public static bool KeyConfirmed(string srv)
        {
            string t = ReadCfg1();
            if (string.IsNullOrEmpty(t)) return false;
            if (Regex.IsMatch(t, "(?m)^\\s*key_confirmed\\s*=\\s*true\\s*$")) return true;
            if (!string.IsNullOrEmpty(srv))
            {
                string pat = "(?m)^\\s*\"" + Regex.Escape(srv) + "[^\"]*\"\\s*=\\s*true";
                if (Regex.IsMatch(t, pat)) return true;
            }
            return false;
        }

        // ================= 密码 =================
        public static string NewRandomPassword(int len)
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
            Random rnd = new Random();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < len; i++) sb.Append(chars[rnd.Next(chars.Length)]);
            return sb.ToString();
        }
        public static string ReadTextFileOrEmpty(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : ""; }
            catch { return ""; }
        }
        public static void WriteTextFile(string path, string s)
        {
            try { File.WriteAllText(path, s, NoBom); } catch { }
        }
        public static string GetStoredPwd() { return ReadTextFileOrEmpty(PwdFile); }
        public static void SaveStoredPwd(string p) { WriteTextFile(PwdFile, p); }
        public static string GetAppliedPwd() { return ReadTextFileOrEmpty(AppFile); }
        public static void SaveAppliedPwd(string p) { WriteTextFile(AppFile, p); }
        public static string PwdAppliedText()
        {
            string s = GetStoredPwd();
            if (s.Length == 0) return "尚未设置固定密码";
            string a = GetAppliedPwd();
            if (a.Length > 0 && a == s) return "已写入客户端，立即生效";
            return "尚未写入客户端，请点【设置】生效";
        }

        // ================= 找客户端 =================
        public static string FindExe()
        {
            List<string> cands = new List<string>();
            string[] keys = new string[] {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RustDesk",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\RustDesk"
            };
            foreach (string k in keys)
            {
                try
                {
                    using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(k))
                    {
                        if (rk != null)
                        {
                            object loc = rk.GetValue("InstallLocation");
                            if (loc != null) cands.Add(Path.Combine(loc.ToString(), "rustdesk.exe"));
                        }
                    }
                }
                catch { }
                try
                {
                    using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(k))
                    {
                        if (rk != null)
                        {
                            object loc = rk.GetValue("InstallLocation");
                            if (loc != null) cands.Add(Path.Combine(loc.ToString(), "rustdesk.exe"));
                        }
                    }
                }
                catch { }
            }
            cands.Add(@"C:\Program Files\RustDesk\rustdesk.exe");
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"rustdesk\rustdesk.exe"));
            foreach (string c in cands)
            {
                try { if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c; }
                catch { }
            }
            return "";
        }

        // ================= 安装最新版 RustDesk（需求6） =================
        // 从 GitHub 取最新 Release，挑出 Windows x64 安装包直链
        public static string LatestInstallerUrl()
        {
            try
            {
                using (System.Net.WebClient wc = new System.Net.WebClient())
                {
                    wc.Headers.Add("User-Agent", "RustDeskHelper");
                    wc.Encoding = Encoding.UTF8;
                    string json = wc.DownloadString("https://api.github.com/repos/rustdesk/rustdesk/releases/latest");
                    MatchCollection ms = Regex.Matches(json,
                        "\"browser_download_url\"\\s*:\\s*\"([^\"]*x86_64\\.exe)\"");
                    foreach (Match m in ms)
                    {
                        string u = m.Groups[1].Value.Replace("\\/", "/");
                        if (u.IndexOf("aarch64", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (u.IndexOf("sciter", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        return u;
                    }
                }
            }
            catch { }
            return "";
        }

        // ================= 调用 rustdesk 命令行 =================
        public static string Cli(string exe, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                Process p = Process.Start(psi);
                string outp = "";
                try { outp = p.StandardOutput.ReadToEnd(); } catch { }
                try { p.StandardError.ReadToEnd(); } catch { }
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                return outp;
            }
            catch { return ""; }
        }

        // 以管理员身份运行一次命令行，输出重定向到临时文件再读回（用于需要 UAC 的设置固定密码）
        public static string CliElevated(string exe, string args)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "rd_pwd_out_" + Guid.NewGuid().ToString("N") + ".txt");
            string bat = Path.Combine(Path.GetTempPath(), "rd_pwd_run_" + Guid.NewGuid().ToString("N") + ".cmd");
            string body = "@echo off\r\n\"" + exe + "\" " + args + " > \"" + tmp + "\" 2>&1\r\nexit /b\r\n";
            try { File.WriteAllText(bat, body, Encoding.ASCII); } catch { return ""; }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(bat);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process p = Process.Start(psi);
                p.WaitForExit();
            }
            catch
            {
                TryDelete(bat); TryDelete(tmp);
                return "ELEVATION_CANCELLED";
            }
            string outp = "";
            for (int i = 0; i < 20; i++)
            {
                if (File.Exists(tmp)) { try { outp = File.ReadAllText(tmp); } catch { } break; }
                Thread.Sleep(200);
            }
            TryDelete(bat); TryDelete(tmp);
            return outp;
        }
        static void TryDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        public static string DeviceIdOnce(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return "";
            string v = "";
            try { v = Cli(exe, "--get-id", 4000).Trim(); } catch { }
            if (Regex.IsMatch(v, "^\\d{6,}$")) return v;
            return "";
        }

        // ================= 进程 / 服务 =================
        // RustDesk 运行时由三类进程组成，必须区分清楚，否则“客户端进程”的启停判断会出错：
        //   --service  服务宿主进程（由 Windows 服务托管，不能随意结束）
        //   --server   由 --service 自动拉起的子进程，结束它会立刻自动重生
        //   其余进程（含 --tray 托盘/客户端）才是真正意义上的“客户端进程”，结束不会重生
        // 所以这里只统计第三类。早先的版本把 --server 也算作客户端，
        // 导致状态永远显示“运行中”、点【停止】后按钮不会变回【启动】，这就是那个 bug 的根因。
        static int[] _cliPids = new int[0];
        static DateTime _cliPidsTime = DateTime.MinValue;

        public static int[] ClientPids()
        {
            if ((DateTime.Now - _cliPidsTime).TotalMilliseconds < 700) return _cliPids;
            List<int> list = new List<int>();
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                           "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='rustdesk.exe'"))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementBaseObject o in col)
                    {
                        int pid = 0;
                        string cmd = "";
                        try { pid = Convert.ToInt32(o["ProcessId"]); } catch { }
                        try { cmd = Convert.ToString(o["CommandLine"]); } catch { }
                        if (pid <= 0) continue;
                        if (cmd == null) cmd = "";
                        if (cmd.IndexOf("--service", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (cmd.IndexOf("--server", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        list.Add(pid);
                    }
                }
            }
            catch { }
            _cliPids = list.ToArray();
            _cliPidsTime = DateTime.Now;
            return _cliPids;
        }

        public static bool ClientRunning() { return ClientPids().Length > 0; }

        // 启动客户端进程：用 --tray 让 RustDesk 常驻后台（不弹主窗口、不打扰用户）
        public static bool StartClient(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            try
            {
                Process.Start(new ProcessStartInfo(exe, "--tray") { UseShellExecute = true });
                _cliPidsTime = DateTime.MinValue;   // 立刻重新枚举，状态刷新更快
                return true;
            }
            catch { return false; }
        }

        // 停止客户端进程：只结束客户端自身，不动 --service / --server，因此不影响“与服务器的连接”
        public static void StopClient()
        {
            int[] pids = ClientPids();
            foreach (int id in pids)
            {
                try { using (Process p = Process.GetProcessById(id)) p.Kill(); } catch { }
            }
            _cliPidsTime = DateTime.MinValue;   // 立刻重新枚举
        }

        public static string SvcStatus()
        {
            try
            {
                using (ServiceController sc = new ServiceController(SvcName))
                    return sc.Status.ToString();
            }
            catch { return "NotFound"; }
        }

        public static string SvcStatusCn(string s)
        {
            switch (s)
            {
                case "Running": return "运行中";
                case "Stopped": return "已停止";
                case "StartPending": return "正在启动";
                case "StopPending": return "正在停止";
                case "Paused": return "已暂停";
                case "NotFound": return "未安装";
                default: return "未知";
            }
        }

        public static Res StartSvc()
        {
            if (!IsAdmin()) return new Res(false, "需要管理员权限才能启动服务器连接");
            try
            {
                using (ServiceController sc = new ServiceController(SvcName)) sc.Start();
                return new Res(true, "正在建立与服务器的连接…");
            }
            catch (Exception ex) { return new Res(false, "启动失败：" + ex.Message); }
        }

        public static Res StopSvc()
        {
            if (!IsAdmin()) return new Res(false, "需要管理员权限才能断开服务器连接");
            try
            {
                using (ServiceController sc = new ServiceController(SvcName)) sc.Stop();
                return new Res(true, "正在断开与服务器的连接…");
            }
            catch (Exception ex) { return new Res(false, "断开失败：" + ex.Message); }
        }

        // ================= 客户端版本 =================
        // 直接读 exe 的版本资源，不用启动进程，毫秒级返回
        public static string RdVersion(string exe)
        {
            try
            {
                FileVersionInfo vi = FileVersionInfo.GetVersionInfo(exe);
                if (vi.ProductVersion != null && vi.ProductVersion.Length > 0) return vi.ProductVersion;
                if (vi.FileVersion != null && vi.FileVersion.Length > 0) return vi.FileVersion;
            }
            catch { }
            return "";
        }

        // ================= 开机自启（计划任务） =================
        public static bool TaskExists(string name)
        {
            // 快速路径：任务计划程序把每个任务保存为 System32\Tasks\<名称> 的 XML 文件，存在即为“已设置”，
            // 避免每次启动都去拉起 schtasks.exe（约 30~40ms/次），显著加快界面出现速度。
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
                if (File.Exists(Path.Combine(dir, name))) return true;
            }
            catch { }
            // 回退：用命令精确查询（首次运行、尚无任务时走这里）
            return RunSchtasks("/Query /TN \"" + name + "\"") == 0;
        }

        static int RunSchtasks(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                Process p = Process.Start(psi);
                try { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); } catch { }
                if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return 1; }
                return p.ExitCode;
            }
            catch { return 1; }
        }

        public static Res SetAuto(bool logon, bool boot, string exe)
        {
            if (logon)
            {
                int r = RunSchtasks("/Create /TN \"" + TaskLogon + "\" /TR \"\\\"" + exe + "\\\"\" /SC ONLOGON /F");
                if (r != 0) return new Res(false, "登录自启失败：需要管理员权限");
            }
            else RunSchtasks("/Delete /TN \"" + TaskLogon + "\" /F");

            if (boot)
            {
                if (!IsAdmin()) return new Res(false, "开机自启失败：请以管理员身份运行本程序");
                int r2 = RunSchtasks("/Create /TN \"" + TaskBoot + "\" /TR \"\\\"" + exe + "\\\" --service\" /SC ONSTART /RU SYSTEM /RL HIGHEST /F");
                if (r2 != 0) return new Res(false, "开机自启失败：权限不足");
            }
            else RunSchtasks("/Delete /TN \"" + TaskBoot + "\" /F");

            return new Res(true, "自启设置已保存");
        }
    }

    // 统一的返回结果
    class Res
    {
        public bool Ok;
        public string Msg;
        public Res(bool ok, string msg) { Ok = ok; Msg = msg; }
    }

    // =================================================================
    //  自绘控件
    // =================================================================
    class Card : Panel
    {
        public int Radius = 10;
        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = U.ClrPage;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(U.ClrPage);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 3; i >= 1; i--)
            {
                Rectangle rs = new Rectangle(0, i + 1, Width - 1, Height - 1 - i);
                using (GraphicsPath sp = U.Round(rs, Radius))
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(9, 90, 100, 120)))
                    g.FillPath(sb, sp);
            }
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = U.Round(r, Radius))
            {
                using (SolidBrush b = new SolidBrush(Color.White)) g.FillPath(b, p);
                using (Pen pen = new Pen(U.ClrBorder)) g.DrawPath(pen, p);
            }
        }
    }

    class RButton : Button
    {
        public int Radius = 7;
        public bool Primary = false;
        public bool Selected = false;
        bool _hover = false, _down = false;

        public RButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = U.FUI;
            Cursor = Cursors.Hand;
            BackColor = U.ClrPage;
            MouseEnter += delegate { _hover = true; Invalidate(); };
            MouseLeave += delegate { _hover = false; _down = false; Invalidate(); };
        }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill, text, border;
            if (Selected)
            {
                fill = U.ClrAccent; border = U.ClrAccent; text = Color.White;
            }
            else if (Primary)
            {
                fill = _down ? U.ClrAccentD : (_hover ? U.ClrAccentH : U.ClrAccent);
                border = fill; text = Color.White;
            }
            else
            {
                fill = _down ? Color.FromArgb(233, 236, 241) : (_hover ? Color.FromArgb(246, 248, 251) : Color.White);
                border = U.ClrBorder; text = U.ClrText;
            }
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = U.Round(r, Radius))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                using (Pen pen = new Pen(border)) g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, Text, Font, r, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    class HeaderPanel : Panel
    {
        public HeaderPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = U.ClrPage;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = new Rectangle(0, 0, Width, Height);
            using (LinearGradientBrush b = new LinearGradientBrush(r, U.ClrHeaderA, U.ClrHeaderB, LinearGradientMode.Horizontal))
            {
                ColorBlend cb = new ColorBlend(3);
                cb.Colors = new Color[] { U.ClrHeaderA, U.ClrHeaderB, U.ClrHeaderB };
                cb.Positions = new float[] { 0f, 0.42f, 1f };
                b.InterpolationColors = cb;
                g.FillRectangle(b, r);
            }
        }
    }

    class Pill : Control
    {
        public Color Dot = U.ClrGreen;
        public Pill()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = U.FPill;
            ForeColor = Color.White;
            BackColor = U.ClrHeaderB;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = U.Round(r, r.Height / 2))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(48, 255, 255, 255)))
                g.FillPath(b, p);
            using (SolidBrush b = new SolidBrush(Dot))
                g.FillEllipse(b, 11, (Height - 8) / 2, 8, 8);
            Rectangle tr = new Rectangle(25, 0, Width - 31, Height);
            TextRenderer.DrawText(g, Text, Font, tr, ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    // =================================================================
    //  密码框内嵌的“眼睛”按钮：点击切换显示 / 隐藏
    // =================================================================
    class EyeToggle : Control
    {
        public bool Revealed = false;      // true = 明文显示
        bool _hover = false;

        public EyeToggle()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            Cursor = Cursors.Hand;
            Size = new Size(24, 24);
            MouseEnter += delegate { _hover = true; Invalidate(); };
            MouseLeave += delegate { _hover = false; Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color c = _hover ? U.ClrAccent : U.ClrSub;
            int cx = Width / 2, cy = Height / 2;
            using (Pen p = new Pen(c, 1.4f))
            using (SolidBrush b = new SolidBrush(c))
            {
                // 眼眶
                g.DrawEllipse(p, cx - 7, cy - 5, 14, 10);
                // 瞳孔
                g.FillEllipse(b, cx - 2, cy - 2, 4, 4);
                // 隐藏状态：加一道斜线
                if (!Revealed) g.DrawLine(p, cx - 8, cy + 7, cx + 8, cy - 7);
            }
        }
    }

    // =================================================================
    //  底部状态行：支持分段着色（正常=绿，异常=红）
    // =================================================================
    class StatusLine : Control
    {
        class Run { public string Text; public Color Color; }
        List<Run> _runs = new List<Run>();

        public StatusLine()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = U.FSm;
            BackColor = U.ClrPage;
        }

        void Add(string t, Color c) { Run r = new Run(); r.Text = t; r.Color = c; _runs.Add(r); }

        public void SetMessage(string t, Color c)
        {
            _runs.Clear(); Add(t, c); Invalidate();
        }

        public void SetStatus(bool cliRun, string svcCn, bool confirmed)
        {
            _runs.Clear();
            Add("客户端：", U.ClrSub);
            Add(cliRun ? "运行中" : "未运行", cliRun ? U.ClrGreen : U.ClrRed);
            Add("　｜　与服务器连接：", U.ClrSub);
            Add(svcCn, svcCn == "运行中" ? U.ClrGreen : U.ClrRed);
            Add("　｜　服务器密钥：", U.ClrSub);
            Add(confirmed ? "已确认（已注册）" : "未确认", confirmed ? U.ClrGreen : U.ClrRed);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            int x = 0;
            foreach (Run r in _runs)
            {
                Size sz = TextRenderer.MeasureText(g, r.Text, Font);
                int y = (Height - sz.Height) / 2;
                if (y < 0) y = 0;
                TextRenderer.DrawText(g, r.Text, Font, new Point(x, y), r.Color);
                x += sz.Width;
            }
        }
    }

    // =================================================================
    //  下载进度窗口（需求6：自动安装最新版时显示）
    // =================================================================
    class InstallForm : Form
    {
        public bool Success = false;
        public string Error = "";
        Label _lbl;
        ProgressBar _bar;
        string _url, _file;
        System.Net.WebClient _wc;

        public InstallForm(string url, string file)
        {
            _url = url; _file = file;
            Text = "正在下载 RustDesk 最新版";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false; MaximizeBox = false;
            ClientSize = new Size(400, 108);
            Font = U.FUI;
            BackColor = Color.White;

            _lbl = new Label();
            _lbl.Text = "正在获取最新版 RustDesk…";
            _lbl.Location = new Point(16, 14);
            _lbl.Size = new Size(368, 20);
            Controls.Add(_lbl);

            _bar = new ProgressBar();
            _bar.Location = new Point(16, 40);
            _bar.Size = new Size(368, 18);
            _bar.Maximum = 100;
            Controls.Add(_bar);

            Label h = new Label();
            h.Text = "下载完成后会自动静默安装，请稍候。";
            h.ForeColor = U.ClrSub;
            h.Location = new Point(16, 66);
            h.Size = new Size(368, 18);
            Controls.Add(h);

            FormClosing += delegate { try { if (_wc != null && !Success) _wc.CancelAsync(); } catch { } };
            Shown += delegate { BeginDownload(); };
        }

        void BeginDownload()
        {
            try
            {
                _wc = new System.Net.WebClient();
                _wc.Headers.Add("User-Agent", "RustDeskHelper");
                _wc.DownloadProgressChanged += delegate(object s, System.Net.DownloadProgressChangedEventArgs e)
                {
                    try
                    {
                        _bar.Value = e.ProgressPercentage;
                        _lbl.Text = "正在下载 RustDesk 最新版… " + e.ProgressPercentage + "%";
                    }
                    catch { }
                };
                _wc.DownloadFileCompleted += delegate(object s, System.ComponentModel.AsyncCompletedEventArgs e)
                {
                    if (e.Error != null) Error = e.Error.Message;
                    else if (e.Cancelled) Error = "下载已取消";
                    else Success = true;
                    Close();
                };
                _wc.DownloadFileAsync(new Uri(_url), _file);
            }
            catch (Exception ex) { Error = ex.Message; Close(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { if (_wc != null) _wc.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }

    // =================================================================
    //  主窗体
    // =================================================================
    class MainForm : Form
    {
        // ---- 运行时状态 ----
        string _exe = "";
        string _rdVer = "";
        string _fixedPwd = "";
        bool _isAdmin = false;
        bool _autoLogon = false, _autoBoot = false;
        bool _pwdVisible = false;
        bool _pwdCueOn = false;      // 未设置固定密码时，框内显示提示文字

        bool _busyPoll = false;
        int _poll = 0;
        bool _idFetched = false;
        bool _lastClient = false;
        bool _repairWait = false;
        string _msgText = "";
        Color _msgColor = U.ClrSub;
        DateTime _msgUntil = DateTime.MinValue;

        // ---- 控件 ----
        Pill _pill = null;
        Label _lblPwdMsg, _lblSrvMsg, _lblFoot, _lblVer;
        StatusLine _lblMsg;
        Label _lblCliState, _lblSvcState, _lblAutoLogon, _lblAutoBoot;
        TextBox _txtId, _txtFixed, _txtSrv, _txtRelay, _txtKey;
        EyeToggle _eye;
        RButton _btnCopyId, _btnCopyPwd, _btnSetPwd;
        RButton _btnCli, _btnSvc, _btnSaveAuto, _btnSync;
        RButton _btnOpen, _btnFix, _btnCfg, _btnLog;
        RButton _seg1, _seg2;
        CheckBox _chkLogon, _chkBoot;
        Panel _panelStatus, _panelSetup;
        System.Windows.Forms.Timer _timer;

        const int PageW = 520;
        const int CardX = 16;
        const int CardW = 488;

        public MainForm()
        {
            // ---------------- 初始化数据（轻量，先让界面出来） ----------------
            U.Log("================ RustDesk 一键配置助手 " + U.Version + " 启动 ================");
            _exe = U.FindExe();
            if (_exe.Length == 0)
            {
                // 需求6：没检测到客户端时，询问是否自动安装当前最新版（默认安装）
                DialogResult dr = MessageBox.Show(
                    "没有检测到已安装的 RustDesk 客户端。\r\n\r\n是否现在自动下载并安装当前最新版？（安装过程会弹出“用户账户控制”，请选择“是”）",
                    "RustDesk 一键配置助手", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (dr == DialogResult.Yes) DoInstallLatest();
                if (_exe.Length == 0) _exe = U.FindExe();
                if (_exe.Length == 0)
                {
                    MessageBox.Show("仍未检测到 RustDesk，请手动安装后重新打开本工具。\r\n下载地址：https://github.com/rustdesk/rustdesk/releases/latest",
                        "RustDesk 一键配置助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    Environment.Exit(1);
                }
            }
            U.Log("已找到客户端：" + _exe);

            string cfg = U.ReadCfg();
            string curServer = U.GetToml(cfg, "custom-rendezvous-server");
            string curKey = U.GetToml(cfg, "key");
            string curApi = U.GetToml(cfg, "api-server");
            string curRelay = U.GetToml(cfg, "relay-server");
            // 公开版不内置服务器信息：配置里为空时不再“自动补全”，交给用户在【服务器设置】里填写
            if (curServer.Length == 0) curServer = U.DefServer;
            if (curKey.Length == 0) curKey = U.DefKey;
            if (curApi.Length == 0) curApi = U.DefApi;
            if (curRelay.Length == 0) curRelay = curServer;

            string legacyPwd = U.GetToml(cfg, "password");
            bool hadBogus = Regex.IsMatch(cfg, "(?m)^[ \t]*password[ \t]*=");
            if (hadBogus && curServer.Length > 0)
            {
                U.EnsureConfig(curServer, curKey, curApi);
                U.Log("已清理无效的 password 选项（旧版写错位置）");
            }

            _fixedPwd = U.GetStoredPwd();
            if (_fixedPwd.Length == 0 && legacyPwd.Length >= 6 && !Regex.IsMatch(legacyPwd, "\\s"))
            {
                _fixedPwd = legacyPwd;
                U.SaveStoredPwd(_fixedPwd);
                U.Log("已沿用旧版配置里的固定密码");
            }
            // 不再自动生成：没有固定密码时，密码框内给出「点设置自动生成」的提示
            _rdVer = U.RdVersion(_exe);
            _autoLogon = U.TaskExists(U.TaskLogon);
            _autoBoot = U.TaskExists(U.TaskBoot);
            _isAdmin = U.IsAdmin();

            // ---------------- 窗口 ----------------
            Text = "RustDesk 一键配置助手";
            ClientSize = new Size(PageW, 482);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(140, 10);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Font = U.FUI;
            BackColor = U.ClrPage;
            TopMost = false;            // 需求1：不再永远置顶
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi(curServer, curRelay, curKey, curApi);

            Shown += delegate
            {
                Activate();
                U.Log("界面已显示，进入实时刷新（启动耗时 " + U.T0.ElapsedMilliseconds + " 毫秒）");
                // 需求6：界面先出来，再去启动客户端；不阻塞界面
                StartClientIfNeeded();
                RefreshUi();
                _timer.Start();
            };
        }

        // =================================================================
        //  构建界面
        // =================================================================
        void BuildUi(string curServer, string curRelay, string curKey, string curApi)
        {
            // ---------------- 顶部色带 ----------------
            HeaderPanel head = new HeaderPanel();
            head.Location = new Point(0, 0);
            head.Size = new Size(PageW, 52);
            Controls.Add(head);

            Label title = MkLabel("RustDesk 一键配置助手", U.FTitle, Color.White, 20, 11, 300, 30);
            title.BackColor = Color.Transparent;
            head.Controls.Add(title);

            _pill = new Pill();
            _pill.Location = new Point(306, 13);
            _pill.Size = new Size(198, 26);
            _pill.Text = "正在读取状态…";
            head.Controls.Add(_pill);

            // ---------------- 分段按钮 ----------------
            _seg1 = new RButton();
            _seg1.Text = "连接状态";
            _seg1.Location = new Point(16, 60);
            _seg1.Size = new Size(120, 28);
            _seg1.Selected = true;
            _seg1.Click += delegate { SwitchPanel(0); };
            Controls.Add(_seg1);

            _seg2 = new RButton();
            _seg2.Text = "服务器设置";
            _seg2.Location = new Point(140, 60);
            _seg2.Size = new Size(120, 28);
            _seg2.Click += delegate { SwitchPanel(1); };
            Controls.Add(_seg2);

            // ---------------- 两个内容面板 ----------------
            _panelStatus = new Panel();
            _panelStatus.Location = new Point(0, 94);
            _panelStatus.Size = new Size(PageW, 296);
            _panelStatus.BackColor = U.ClrPage;
            Controls.Add(_panelStatus);

            _panelSetup = new Panel();
            _panelSetup.Location = new Point(0, 94);
            _panelSetup.Size = new Size(PageW, 296);
            _panelSetup.BackColor = U.ClrPage;
            _panelSetup.Visible = false;
            Controls.Add(_panelSetup);

            BuildStatusPanel(curServer);
            BuildSetupPanel(curServer, curRelay, curKey);

            // ---------------- 底部按钮 ----------------
            _btnOpen = MkButton("打开客户端", 16, 398, 116, 32, false, false);
            _btnOpen.Click += delegate { if (_exe.Length > 0) Process.Start(new ProcessStartInfo(_exe) { UseShellExecute = true }); };
            _btnFix = MkButton("修复连接", 140, 398, 116, 32, false, false);
            _btnFix.Click += delegate { DoRepair(); };
            _btnCfg = MkButton("配置目录", 264, 398, 116, 32, false, false);
            _btnCfg.Click += delegate { try { Process.Start("explorer.exe", U.CfgCfg); } catch { } };
            _btnLog = MkButton("查看日志", 388, 398, 116, 32, false, false);
            _btnLog.Click += delegate
            {
                if (File.Exists(U.LogFile)) Process.Start("notepad.exe", U.LogFile);
                else SetMsg("暂无日志文件", U.ClrAmber, 4);
            };
            Controls.Add(_btnOpen); Controls.Add(_btnFix);
            Controls.Add(_btnCfg); Controls.Add(_btnLog);

            _lblMsg = new StatusLine();
            _lblMsg.Location = new Point(18, 438);
            _lblMsg.Size = new Size(484, 18);
            _lblMsg.SetMessage("正在读取状态…", U.ClrSub);
            Controls.Add(_lblMsg);

            // 需求4：左侧显示运行身份，右侧显示作者信息
            _lblFoot = MkLabel(
                _isAdmin ? "当前以管理员身份运行"
                         : "未以管理员身份运行，部分功能受限",
                U.FSm, U.ClrSub, 18, 460, 296, 16);
            Controls.Add(_lblFoot);

            Label author = MkLabel("作者：九头鱼　pmzyc@qq.com", U.FSm, U.ClrSub, 318, 460, 184, 16);
            author.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(author);
        }

        // ---- 面板一：连接状态 ----
        void BuildStatusPanel(string curServer)
        {
            // ---------------- 卡片1：设备 ID ----------------
            Card c1 = MkCard(0, 0, 62);
            c1.Controls.Add(MkCap("设备 ID", 14, 6, 140));
            c1.Controls.Add(MkHelp(58, "本机在 RustDesk 网络里的 ID，对方凭这个号连过来。"));

            _txtId = MkText(14, 28, 350, 26, true);
            _txtId.Font = U.FMonoBig;
            _txtId.Text = "正在读取…";
            _txtId.TextAlign = HorizontalAlignment.Center;
            c1.Controls.Add(_txtId);

            _btnCopyId = MkButton("复制", 372, 28, 102, 26, false, false);
            _btnCopyId.BackColor = Color.White;
            _btnCopyId.Click += delegate
            {
                if (Regex.IsMatch(_txtId.Text, "^\\d{6,}$"))
                {
                    try { Clipboard.SetText(_txtId.Text); SetMsg("✓ 设备 ID 已复制到剪贴板", U.ClrGreen, 4); } catch { }
                }
                else SetMsg("设备 ID 还没读到，请稍候", U.ClrAmber, 4);
            };
            c1.Controls.Add(_btnCopyId);
            _panelStatus.Controls.Add(c1);

            // ---------------- 卡片2：固定密码（框内小眼睛切换显示/隐藏） ----------------
            Card c2 = MkCard(0, 70, 76);
            c2.Controls.Add(MkCap("固定密码", 14, 5, 140));
            c2.Controls.Add(MkHelp(70, "固定密码长期有效，自己人日常用这个。点【设置】后由本工具调用官方命令写入客户端，立即生效、无需重启；如果弹出“用户账户控制”，请选择“是”。\r\n\r\n点密码框右侧的小眼睛可以临时查看明文。未设置时框里会给出提示。"));

            _txtFixed = MkText(14, 28, 308, 26, false);
            _txtFixed.Font = U.FMono;
            _txtFixed.TextAlign = HorizontalAlignment.Left;
            c2.Controls.Add(_txtFixed);

            _eye = new EyeToggle();
            _eye.Location = new Point(14 + 308 - 26, 29);
            _eye.Click += delegate
            {
                _pwdVisible = !_pwdVisible;
                _eye.Revealed = _pwdVisible;
                if (!_pwdCueOn) _txtFixed.PasswordChar = _pwdVisible ? '\0' : '\u25CF';
                _eye.Invalidate();
            };
            c2.Controls.Add(_eye);
            _eye.BringToFront();   // 文本框是原生控件，必须显式置顶，否则会盖住眼睛图标

            _btnCopyPwd = MkButton("复制", 330, 28, 60, 26, false, false);
            _btnCopyPwd.BackColor = Color.White;
            _btnCopyPwd.Click += delegate
            {
                string p = _pwdCueOn ? "" : _txtFixed.Text.Trim();
                if (p.Length > 0) { try { Clipboard.SetText(p); SetPwdMsg("✓ 已复制到剪贴板", U.ClrGreen); } catch { } }
                else SetPwdMsg("✗ 还没有固定密码，先点【设置】", U.ClrRed);
            };
            c2.Controls.Add(_btnCopyPwd);

            _btnSetPwd = MkButton("设置", 398, 28, 76, 26, true, false);
            _btnSetPwd.BackColor = Color.White;
            _btnSetPwd.Click += delegate { DoSetPassword(); };
            c2.Controls.Add(_btnSetPwd);

            _lblPwdMsg = MkLabel("", U.FSm, U.ClrSub, 14, 57, 460, 17);
            c2.Controls.Add(_lblPwdMsg);
            _panelStatus.Controls.Add(c2);

            // 密码框：提示文字进出 / 掩码与明文切换
            _txtFixed.GotFocus += delegate
            {
                if (_pwdCueOn)
                {
                    _pwdCueOn = false;
                    _txtFixed.Text = "";
                    _txtFixed.ForeColor = U.ClrText;
                    _txtFixed.PasswordChar = _pwdVisible ? '\0' : '\u25CF';
                }
            };
            _txtFixed.LostFocus += delegate
            {
                if (!_pwdCueOn && _fixedPwd.Length == 0 && _txtFixed.Text.Trim().Length == 0) ShowPwdCue();
            };

            // ---------------- 卡片3：开机自启动 ----------------
            Card c3 = MkCard(0, 154, 70);
            c3.Controls.Add(MkCap("开机自启动", 14, 4, 140));
            c3.Controls.Add(MkHelp(82, "登录后启动：开机进桌面后 RustDesk 自动运行，普通电脑用这个。\r\n开机即启动：以 SYSTEM 身份在登录前启动，适合 W2022 这类服务器；未登录时没有桌面会话，无法采集屏幕。"));

            _chkLogon = MkCheck("登录 Windows 后自动启动", 14, 26, 246, _autoLogon);
            c3.Controls.Add(_chkLogon);
            _chkBoot = MkCheck("开机即启动（服务器，需管理员）", 14, 46, 246, _autoBoot);
            c3.Controls.Add(_chkBoot);

            // 需求2：把“是否已经设置成功”直接显示出来（绿✓ / 红✗）
            _lblAutoLogon = MkLabel("", U.FSm, U.ClrSub, 262, 26, 100, 20);
            c3.Controls.Add(_lblAutoLogon);
            _lblAutoBoot = MkLabel("", U.FSm, U.ClrSub, 262, 46, 100, 20);
            c3.Controls.Add(_lblAutoBoot);
            RefreshAutoLabels();

            _btnSaveAuto = MkButton("保存", 366, 24, 108, 44, false, false);
            _btnSaveAuto.BackColor = Color.White;
            _btnSaveAuto.Click += delegate
            {
                Res r = U.SetAuto(_chkLogon.Checked, _chkBoot.Checked, _exe);
                // 保存后按计划任务的真实存在情况刷新，避免“界面提示成功、实际没建上”
                _autoLogon = U.TaskExists(U.TaskLogon);
                _autoBoot = U.TaskExists(U.TaskBoot);
                _chkLogon.Checked = _autoLogon;
                _chkBoot.Checked = _autoBoot;
                RefreshAutoLabels();
                if (r.Ok) SetMsg("✓ " + r.Msg, U.ClrGreen, 5);
                else SetMsg("✗ " + r.Msg, U.ClrRed, 5);
            };
            c3.Controls.Add(_btnSaveAuto);
            _panelStatus.Controls.Add(c3);

            // ---------------- 卡片4：客户端状态（单行：进程 + 服务器连接） ----------------
            Card c4 = MkCard(0, 232, 64);
            c4.Controls.Add(MkCap("客户端状态", 14, 5, 140));
            c4.Controls.Add(MkHelp(82, "“客户端进程”是你能看到界面的那个 RustDesk 程序；“与服务器的连接”由 RustDesk 后台服务负责，决定这台机器是否挂到 ID 服务器上。\r\n\r\n两者是两回事，可以分别启动或停止。"));

            _lblVer = MkLabel(_rdVer.Length > 0 ? "RustDesk " + _rdVer : "", U.FSm, U.ClrSub, 240, 5, 234, 20);
            _lblVer.TextAlign = ContentAlignment.MiddleRight;
            c4.Controls.Add(_lblVer);

            c4.Controls.Add(MkLabel("客户端进程", U.FSm, U.ClrSub, 14, 29, 62, 22));
            _lblCliState = MkLabel("● 检测中…", U.FSm, U.ClrSub, 78, 29, 74, 22);
            c4.Controls.Add(_lblCliState);

            c4.Controls.Add(MkLabel("与服务器连接", U.FSm, U.ClrSub, 154, 29, 80, 22));
            _lblSvcState = MkLabel("● 检测中…", U.FSm, U.ClrSub, 236, 29, 84, 22);
            c4.Controls.Add(_lblSvcState);

            _btnCli = MkButton("启动", 330, 26, 74, 28, false, false);
            _btnCli.BackColor = Color.White;
            _btnCli.Click += delegate
            {
                if (U.ClientRunning())
                {
                    U.StopClient();
                    SetMsg("正在停止 RustDesk 客户端进程（不影响服务器连接）…", U.ClrAccent, 5);
                }
                else
                {
                    if (U.StartClient(_exe)) SetMsg("正在启动 RustDesk 客户端进程…", U.ClrAccent, 5);
                    else SetMsg("✗ 启动失败：找不到客户端程序", U.ClrRed, 5);
                }
            };
            c4.Controls.Add(_btnCli);

            _btnSvc = MkButton("连接", 410, 26, 74, 28, false, false);
            _btnSvc.BackColor = Color.White;
            _btnSvc.Click += delegate
            {
                Res r = (U.SvcStatus() == "Running") ? U.StopSvc() : U.StartSvc();
                if (r.Ok) SetMsg(r.Msg, U.ClrAccent, 6);
                else SetMsg("✗ " + r.Msg, U.ClrRed, 6);
            };
            c4.Controls.Add(_btnSvc);
            _panelStatus.Controls.Add(c4);

            // 初始化密码框：有密码 → 掩码显示；没密码 → 框内给出提示
            if (_fixedPwd.Length > 0)
            {
                _txtFixed.Text = _fixedPwd;
                _txtFixed.ForeColor = U.ClrText;
                _txtFixed.PasswordChar = '\u25CF';
                _lblPwdMsg.Text = U.PwdAppliedText();
                _lblPwdMsg.ForeColor = U.ClrSub;
            }
            else ShowPwdCue();
        }

        // 未设置固定密码时，密码框内显示提示文字（不是真实密码）
        void ShowPwdCue()
        {
            _pwdCueOn = true;
            _txtFixed.PasswordChar = '\0';
            _txtFixed.ForeColor = U.ClrSub;
            _txtFixed.Text = "未设置 · 点右侧【设置】自动生成";
            _lblPwdMsg.Text = "尚未设置固定密码：点【设置】会自动生成一个并写入客户端";
            _lblPwdMsg.ForeColor = U.ClrAmber;
        }

        // ---- 面板二：服务器设置 ----
        void BuildSetupPanel(string curServer, string curRelay, string curKey)
        {
            Card c4 = MkCard(0, 0, 150);
            c4.Controls.Add(MkCap("服务器设置", 14, 8, 160));
            Label q4 = MkHelp(82, "本工具不内置任何服务器地址，请填你自己搭建的 RustDesk 服务端：\r\n\r\nID 服务器：hbbs 的地址，例如 192.168.1.10 或 你的域名。\r\n中继服务器：hbbr 的地址（留空则与 ID 服务器相同）。\r\nKey：必须与服务端 id_ed25519.pub 文件内容完全一致，否则客户端永远注册不上。\r\n\r\n填好点【同步到客户端】，再点【修复连接】让它重新注册。");
            c4.Controls.Add(q4);

            c4.Controls.Add(MkLabel("ID 服务器", U.FSm, U.ClrSub, 14, 40, 68, 18));
            _txtSrv = MkText(86, 37, 150, 26, false);
            _txtSrv.Font = U.FMonoSm;
            _txtSrv.Text = curServer;
            c4.Controls.Add(_txtSrv);

            c4.Controls.Add(MkLabel("中继服务器", U.FSm, U.ClrSub, 248, 40, 68, 18));
            _txtRelay = MkText(320, 37, 154, 26, false);
            _txtRelay.Font = U.FMonoSm;
            _txtRelay.Text = curRelay;
            c4.Controls.Add(_txtRelay);

            c4.Controls.Add(MkLabel("Key", U.FSm, U.ClrSub, 14, 78, 68, 18));
            _txtKey = MkText(86, 75, 388, 26, false);
            _txtKey.Font = new Font("Consolas", 8F);
            _txtKey.Text = curKey;
            c4.Controls.Add(_txtKey);

            _btnSync = MkButton("同步到客户端", 86, 110, 130, 30, true, false);
            _btnSync.BackColor = Color.White;
            _btnSync.Click += delegate { DoSync(); };
            c4.Controls.Add(_btnSync);

            _lblSrvMsg = MkLabel("当前配置正常", U.FSm, U.ClrGreen, 224, 116, 250, 18);
            c4.Controls.Add(_lblSrvMsg);
            _panelSetup.Controls.Add(c4);
        }

        // =================================================================
        //  控件工厂
        // =================================================================
        Card MkCard(int x, int y, int h)
        {
            Card c = new Card();
            c.Location = new Point(CardX + x, y);
            c.Size = new Size(CardW, h);
            return c;
        }
        Label MkLabel(string text, Font f, Color fore, int x, int y, int w, int h)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = f;
            l.ForeColor = fore;
            l.BackColor = Color.Transparent;
            l.Location = new Point(x, y);
            l.Size = new Size(w, h);
            l.TextAlign = ContentAlignment.MiddleLeft;
            return l;
        }
        Label MkCap(string text, int x, int y, int w)
        {
            Label l = MkLabel(text, U.FCap, U.ClrSub, x, y, w, 20);
            l.TextAlign = ContentAlignment.MiddleLeft;
            return l;
        }
        Label MkHelp(int x, string tip)
        {
            Label q = new Label();
            q.Text = "?";
            q.Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold);
            q.ForeColor = Color.White;
            q.BackColor = U.ClrSub;
            q.TextAlign = ContentAlignment.MiddleCenter;
            q.Location = new Point(x, 7);
            q.Size = new Size(16, 16);
            q.Cursor = Cursors.Help;
            q.Click += delegate { MessageBox.Show(tip, "说明", MessageBoxButtons.OK, MessageBoxIcon.Information); };
            return q;
        }
        TextBox MkText(int x, int y, int w, int h, bool readOnly)
        {
            TextBox t = new TextBox();
            t.Location = new Point(x, y);
            t.Size = new Size(w, h);
            t.ReadOnly = readOnly;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = Color.White;
            t.ForeColor = U.ClrText;
            return t;
        }
        CheckBox MkCheck(string text, int x, int y, int w, bool isChecked)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Font = U.FUI;
            c.ForeColor = U.ClrText;
            c.BackColor = Color.White;
            c.Location = new Point(x, y);
            c.Size = new Size(w, 22);
            c.Checked = isChecked;
            return c;
        }
        RButton MkButton(string text, int x, int y, int w, int h, bool primary, bool selected)
        {
            RButton b = new RButton();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, h);
            b.Primary = primary;
            b.Selected = selected;
            if (primary) b.BackColor = Color.White;
            return b;
        }

        void SwitchPanel(int idx)
        {
            _panelStatus.Visible = (idx == 0);
            _panelSetup.Visible = (idx == 1);
            _seg1.Selected = (idx == 0);
            _seg2.Selected = (idx == 1);
            _seg1.Invalidate();
            _seg2.Invalidate();
        }

        // =================================================================
        //  消息
        // =================================================================
        void SetMsg(string t, Color c, int seconds)
        {
            _msgText = t; _msgColor = c; _msgUntil = DateTime.Now.AddSeconds(seconds);
            if (_lblMsg != null) _lblMsg.SetMessage(t, c);   // 界面尚未构建时（如自动安装阶段）忽略
        }
        // 需求2：开机自启动“是否已设置”的显示
        void RefreshAutoLabels()
        {
            _lblAutoLogon.Text = _autoLogon ? "✓ 已设置" : "✗ 未设置";
            _lblAutoLogon.ForeColor = _autoLogon ? U.ClrGreen : U.ClrRed;
            _lblAutoBoot.Text = _autoBoot ? "✓ 已设置" : "✗ 未设置";
            _lblAutoBoot.ForeColor = _autoBoot ? U.ClrGreen : U.ClrRed;
        }
        void SetPwdMsg(string t, Color c)
        {
            _lblPwdMsg.Text = t; _lblPwdMsg.ForeColor = c;
        }

        // =================================================================
        //  实时刷新（需求6：先出界面，再异步刷新，不卡界面）
        // =================================================================
        void StartClientIfNeeded()
        {
            if (!U.ClientRunning())
            {
                SetMsg("正在启动 RustDesk 客户端…", U.ClrAccent, 6);
                U.StartClient(_exe);
            }
        }

        void RefreshUi()
        {
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 1500;
            _timer.Tick += delegate { PollAsync(); };
            _timer.Start();
        }

        void PollAsync()
        {
            if (_busyPoll) return;
            _busyPoll = true;
            Thread th = new Thread(new ThreadStart(PollWork));
            th.IsBackground = true;
            th.Start();
        }

        void PollWork()
        {
            Snap s = new Snap();
            try
            {
                _poll++;
                bool cliRun = U.ClientRunning();
                string svcRaw = U.SvcStatus();
                s.CliRun = cliRun;
                s.SvcRaw = svcRaw;
                s.SvcCn = U.SvcStatusCn(svcRaw);

                string srv = U.GetToml(U.ReadCfg(), "custom-rendezvous-server");
                if (srv.Length == 0) srv = U.DefServer;
                s.Srv = srv;
                s.Confirmed = U.KeyConfirmed(srv);
                s.AutoLogon = U.TaskExists(U.TaskLogon);
                s.AutoBoot = U.TaskExists(U.TaskBoot);

                // 设备 ID：客户端运行后读取；客户端重启后刷新；平时每 15 次轮询刷新一次
                bool needId = false;
                if (cliRun && !_idFetched) needId = true;
                else if (_idFetched && cliRun && !_lastClient) needId = true;
                else if (_idFetched && cliRun && (_poll % 15 == 0)) needId = true;
                if (needId)
                {
                    string id = U.DeviceIdOnce(_exe);
                    if (id.Length > 0) { s.Id = id; s.HasId = true; _idFetched = true; }
                }
                _lastClient = cliRun;
            }
            catch { }
            try { BeginInvoke(new MethodInvoker(delegate { Apply(s); _busyPoll = false; })); }
            catch { _busyPoll = false; }
        }

        class Snap
        {
            public bool CliRun;
            public string SvcRaw = "";
            public string SvcCn = "";
            public string Srv = "";
            public bool Confirmed;
            public bool AutoLogon;
            public bool AutoBoot;
            public string Id = "";
            public bool HasId;
        }

        // =================================================================
        //  把后台采集到的状态刷到界面上
        // =================================================================
        void Apply(Snap s)
        {
            try
            {
                // 客户端进程本身
                if (s.CliRun)
                {
                    _lblCliState.Text = "● 运行中"; _lblCliState.ForeColor = U.ClrGreen; _btnCli.Text = "停止";
                }
                else
                {
                    _lblCliState.Text = "● 未运行"; _lblCliState.ForeColor = U.ClrRed; _btnCli.Text = "启动";
                }

                // 与服务器的连接（后台服务）
                if (s.SvcRaw == "Running")
                {
                    _lblSvcState.Text = "● 已连接"; _lblSvcState.ForeColor = U.ClrGreen; _btnSvc.Text = "断开";
                }
                else if (s.SvcRaw == "NotFound")
                {
                    _lblSvcState.Text = "● 未安装"; _lblSvcState.ForeColor = U.ClrRed; _btnSvc.Text = "连接";
                }
                else
                {
                    _lblSvcState.Text = "● 未连接"; _lblSvcState.ForeColor = U.ClrRed; _btnSvc.Text = "连接";
                }

                // 顶部状态胶囊
                if (s.CliRun && s.Confirmed) { _pill.Text = "已连接 " + s.Srv; _pill.Dot = U.ClrGreen; }
                else if (s.CliRun) { _pill.Text = "注册中… " + s.Srv; _pill.Dot = U.ClrAmber; }
                else { _pill.Text = "客户端未运行"; _pill.Dot = U.ClrRed; }
                _pill.Invalidate();

                // 设备 ID
                if (s.HasId) { _txtId.Text = s.Id; _txtId.ForeColor = U.ClrText; }

                // 修复后的结果校验（需求5）
                if (_repairWait)
                {
                    if (s.CliRun && s.Confirmed)
                    {
                        _repairWait = false;
                        SetMsg("✓ 修复完成：客户端已重新注册到 " + s.Srv, U.ClrGreen, 8);
                    }
                    else if (_poll % 5 == 0) SetMsg("正在校验注册结果… 服务：" + s.SvcCn, U.ClrAccent, 5);
                }

                // 需求2：开机自启动状态（外部改动也能同步显示）
                if (s.AutoLogon != _autoLogon || s.AutoBoot != _autoBoot)
                {
                    _autoLogon = s.AutoLogon; _autoBoot = s.AutoBoot;
                    _chkLogon.Checked = _autoLogon; _chkBoot.Checked = _autoBoot;
                }
                RefreshAutoLabels();

                // 底部状态行（消息未过期则显示消息）
                if (DateTime.Now < _msgUntil) _lblMsg.SetMessage(_msgText, _msgColor);
                else _lblMsg.SetStatus(s.CliRun, s.SvcCn, s.Confirmed);
            }
            catch { }
        }

        // =================================================================
        //  需求2：固定密码走官方命令写入客户端（不是写进没用的配置项）
        // =================================================================
        void DoSetPassword()
        {
            // 还没设置过（框里只是提示文字）或用户把框清空了 → 自动生成一个
            string np = _pwdCueOn ? "" : _txtFixed.Text.Trim();
            if (np.Length == 0)
            {
                np = U.NewRandomPassword(10);
                _fixedPwd = np;
                _pwdCueOn = false;
                _txtFixed.Text = np;
                _txtFixed.ForeColor = U.ClrText;
                _txtFixed.PasswordChar = _pwdVisible ? '\0' : '\u25CF';
                U.Log("已生成新的固定密码（本工具自己保存明文副本）");
            }
            if (np.Length < 6) { SetPwdMsg("✗ 保存失败：密码至少 6 位", U.ClrRed); _txtFixed.Focus(); return; }
            if (Regex.IsMatch(np, "\\s")) { SetPwdMsg("✗ 保存失败：不能包含空格", U.ClrRed); return; }

            U.SaveStoredPwd(np);
            SetPwdMsg("正在写入客户端，请稍候…（如弹出“用户账户控制”，请选择“是”）", U.ClrAccent);
            Refresh();

            string argLine = "--password \"" + np + "\"";
            if (!U.ClientRunning())
            {
                U.StartClient(_exe);
                for (int i = 0; i < 16; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(500);
                    if (U.ClientRunning()) break;
                }
            }
            string outp = U.IsAdmin() ? U.Cli(_exe, argLine, 8000) : U.CliElevated(_exe, argLine);
            string o = (outp == null) ? "" : outp.Trim();

            if (o.Contains("Done"))
            {
                U.SaveAppliedPwd(np);
                SetPwdMsg("✓ 设置成功：已写入客户端，立即生效（无需重启）", U.ClrGreen);
                U.Log("固定密码已成功写入客户端");
            }
            else if (o.Contains("ELEVATION_CANCELLED")) SetPwdMsg("✗ 已取消管理员授权，密码未写入客户端", U.ClrRed);
            else if (o.Contains("administrative privileges") || o.Contains("Installation")) SetPwdMsg("✗ 需要管理员权限和“已安装”的客户端（授权弹窗请选“是”）", U.ClrRed);
            else if (o.Contains("disabled")) SetPwdMsg("✗ 客户端策略已禁止修改固定密码", U.ClrRed);
            else if (o.Length == 0) SetPwdMsg("✗ 客户端没有返回结果，请确认 RustDesk 正在运行后重试", U.ClrRed);
            else SetPwdMsg("✗ 写入失败：" + o, U.ClrRed);
        }

        // =================================================================
        //  服务器设置同步
        // =================================================================
        void DoSync()
        {
            string s = _txtSrv.Text.Trim();
            string r = _txtRelay.Text.Trim();
            string k = _txtKey.Text.Trim();
            if (s.Length == 0) { _lblSrvMsg.Text = "✗ 请填写 ID 服务器"; _lblSrvMsg.ForeColor = U.ClrRed; return; }
            if (r.Length == 0) r = s;
            if (k.Length == 0) { _lblSrvMsg.Text = "✗ 请填写 Key"; _lblSrvMsg.ForeColor = U.ClrRed; return; }
            try
            {
                U.EnsureConfig(s, k, U.DefApi);
                string t = U.ReadCfg();
                t = U.SetToml(t, "relay-server", r);
                U.WriteCfg(t);
                _lblSrvMsg.Text = "✓ 已同步，重启客户端后生效";
                _lblSrvMsg.ForeColor = U.ClrGreen;
                SetMsg("服务器已设置为 " + s + "，可点【修复连接】让客户端重新注册", U.ClrAccent, 8);
            }
            catch (Exception ex)
            {
                _lblSrvMsg.Text = "✗ 同步失败：" + ex.Message;
                _lblSrvMsg.ForeColor = U.ClrRed;
            }
        }

        // =================================================================
        //  需求5：安全的“修复连接”——不删除任何系统目录，只重置注册状态并重写配置，然后自动校验
        // =================================================================
        void DoRepair()
        {
            SetMsg("正在修复：备份配置 → 重置注册状态 → 重写服务器配置 → 重启客户端…", U.ClrAccent, 30);
            Refresh();
            if (_exe.Length == 0) { SetMsg("✗ 未找到客户端程序", U.ClrRed, 6); return; }
            if (_txtSrv.Text.Trim().Length == 0 || _txtKey.Text.Trim().Length == 0)
            {
                SetMsg("✗ 请先切到【服务器设置】填写你自己的 ID 服务器与 Key", U.ClrRed, 8);
                return;
            }

            try
            {
                if (File.Exists(U.Cfg1))
                    File.Copy(U.Cfg1, U.Cfg1 + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
            }
            catch { }

            try
            {
                string c = U.ReadCfg1();
                if (c.Length > 0)
                {
                    c = new Regex("(?m)^([ \\t]*key_confirmed[ \\t]*=[ \\t]*)(true|false)").Replace(c, "${1}false");
                    c = new Regex("(?m)^([ \\t]*\"[^\"]+\"[ \\t]*=[ \\t]*)(true|false)").Replace(c, "${1}false");
                    File.WriteAllText(U.Cfg1, c, U.NoBom);
                }
            }
            catch { }

            string s = _txtSrv.Text.Trim(); if (s.Length == 0) s = U.DefServer;
            string k = _txtKey.Text.Trim(); if (k.Length == 0) k = U.DefKey;
            string r = _txtRelay.Text.Trim(); if (r.Length == 0) r = s;
            U.EnsureConfig(s, k, U.DefApi);
            string t2 = U.ReadCfg();
            t2 = U.SetToml(t2, "relay-server", r);
            U.WriteCfg(t2);
            U.Log("修复连接：已重置注册状态并重写服务器配置");

            U.StopClient();
            for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(250); }
            U.StartClient(_exe);

            _repairWait = true;
            _idFetched = false;
            SetMsg("客户端已重启，正在重新注册，请稍候…", U.ClrAccent, 12);
        }

        // =================================================================
        //  需求6：自动下载并静默安装当前最新版 RustDesk
        // =================================================================
        void DoInstallLatest()
        {
            SetMsg("正在获取最新版 RustDesk 下载地址…", U.ClrAccent, 10);
            string url = U.LatestInstallerUrl();
            if (url.Length == 0)
            {
                MessageBox.Show("获取最新版下载地址失败（可能是网络问题）。\r\n请手动下载安装：https://github.com/rustdesk/rustdesk/releases/latest",
                    "RustDesk 一键配置助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            U.Log("准备安装最新版：" + url);

            string file = Path.Combine(Path.GetTempPath(), "rustdesk-setup-" + Guid.NewGuid().ToString("N") + ".exe");
            bool ok = false;
            InstallForm f = new InstallForm(url, file);
            f.ShowDialog();
            ok = f.Success;
            if (!ok)
            {
                string err = (f.Error == null) ? "" : f.Error;
                MessageBox.Show("下载失败：" + err + "\r\n\r\n可手动下载安装：https://github.com/rustdesk/rustdesk/releases/latest",
                    "RustDesk 一键配置助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 静默安装：安装包自带管理员清单，会弹出“用户账户控制”，请选择“是”
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, "--silent-install");
                psi.UseShellExecute = true;
                psi.Verb = "runas";      // 静默安装需要管理员权限，主动请求提权
                Process p = Process.Start(psi);
                for (int i = 0; i < 20 && !p.HasExited; i++) Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动安装程序失败：" + ex.Message, "RustDesk 一键配置助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 等待安装落地（最多 4 分钟），期间轮询客户端是否已出现
            string found = "";
            for (int i = 0; i < 240; i++)
            {
                found = U.FindExe();
                if (found.Length > 0) break;
                Thread.Sleep(1000);
            }
            try { if (File.Exists(file)) File.Delete(file); } catch { }
            if (found.Length > 0)
            {
                _exe = found;
                U.Log("已自动安装 RustDesk：" + _exe);
                _rdVer = U.RdVersion(_exe);
            }
        }
    }

    // =================================================================
    //  入口
    // =================================================================
    static class Program
    {
        [STAThread]
        static void Main()
        {
            U.T0.Restart();
            try { U.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Directory.CreateDirectory(U.DataDir); Directory.CreateDirectory(U.CfgCfg); } catch { }
            try { Application.Run(new MainForm()); }
            catch (Exception ex) { U.Log("界面异常：" + ex.Message); }
            U.Log("================ 助手 " + U.Version + " 退出 ================");
        }
    }
}