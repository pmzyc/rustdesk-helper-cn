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
        // ===== 默认值：全部留空 = 使用 RustDesk 官方公共服务 =====
        // 不预设任何私有服务器地址/密钥，用户可在【服务器设置】里选择模式并填写。
        public const string DefServer = "";
        public const string DefKey    = "";
        public const string DefApi    = "";

        // ===== 三种使用模式 =====
        public const string ModeOfficial = "official";   // 官方公共服务：零配置，免费（中继限速）
        public const string ModeCustom   = "custom";     // 自建服务器：填地址 + Key
        public const string ModeLan      = "lan";        // 仅局域网：不开服务器，同网段发现

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
        static int _logCounter = 0;

        public static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                if (++_logCounter % 50 == 0) TrimLogIfNeeded();   // 每 50 条检查一次大小，避免每次 stat 文件
                File.AppendAllText(LogFile, DateTime.Now.ToString("MM-dd HH:mm:ss") + "  " + msg + "\r\n", NoBom);
            }
            catch { }
        }

        // 日志超过 2MB 时裁剪：只保留最近 90 天、且最多 3000 行
        public static void TrimLogIfNeeded()
        {
            try
            {
                FileInfo fi = new FileInfo(LogFile);
                if (!fi.Exists || fi.Length < 2 * 1024 * 1024) return;

                string[] lines = File.ReadAllLines(LogFile);
                DateTime cutoff = DateTime.Now.AddDays(-90);
                List<string> keep = new List<string>();
                foreach (string l in lines)
                {
                    if (l.Length >= 14)
                    {
                        DateTime t;
                        if (DateTime.TryParseExact(l.Substring(0, 14), "MM-dd HH:mm:ss",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out t))
                        {
                            // 日志里只有月日，补上年份再比较
                            if (t.Year < 2000) t = new DateTime(DateTime.Now.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second);
                            if (t < cutoff) continue;
                        }
                    }
                    keep.Add(l);
                }
                if (keep.Count > 3000) keep = keep.GetRange(keep.Count - 3000, 3000);
                File.WriteAllLines(LogFile, keep.ToArray(), NoBom);
            }
            catch { }
        }

        // ---------------- 日志模式 ----------------
        public static readonly string SettingsFile = Path.Combine(DataDir, "settings.txt");

        // persist = 持久累积（默认）；session = 仅本次会话
        public static string GetLogMode()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    foreach (string l in File.ReadAllLines(SettingsFile))
                    {
                        int i = l.IndexOf('=');
                        if (i > 0 && l.Substring(0, i).Trim() == "log-mode")
                            return l.Substring(i + 1).Trim();
                    }
                }
            }
            catch { }
            return "persist";
        }

        public static void SetLogMode(string mode)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                Dictionary<string, string> kv = new Dictionary<string, string>();
                if (File.Exists(SettingsFile))
                {
                    foreach (string l in File.ReadAllLines(SettingsFile))
                    {
                        int i = l.IndexOf('=');
                        if (i > 0) kv[l.Substring(0, i).Trim()] = l.Substring(i + 1).Trim();
                    }
                }
                kv["log-mode"] = mode;
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> p in kv) sb.AppendLine(p.Key + "=" + p.Value);
                WriteTextFile(SettingsFile, sb.ToString());
            }
            catch { }
        }

        // 日志行数 / 大小（给日志页显示）
        public static string LogSummary()
        {
            try
            {
                FileInfo fi = new FileInfo(LogFile);
                if (!fi.Exists) return "日志文件不存在";
                int n = 0;
                using (StreamReader sr = new StreamReader(LogFile)) { while (sr.ReadLine() != null) n++; }
                return string.Format("{0:0.0} KB，共 {1} 行，最后更新 {2}", fi.Length / 1024.0, n, fi.LastWriteTime.ToString("MM-dd HH:mm"));
            }
            catch { return "读取日志信息失败"; }
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

        // 写入服务器配置。
        // 注意：RustDesk2.toml 的 [options] password 就是固定密码的官方存储位置（明文），
        // 绝不能删除；旧版这里误删，是“设置成功但密码无效”的根源之一。
        public static void EnsureConfig(string srv, string key, string api)
        {
            try
            {
                Directory.CreateDirectory(CfgCfg);
                if (!File.Exists(Cfg2)) File.WriteAllText(Cfg2, "", Encoding.ASCII);
                string t = ReadCfg();
                if (!Regex.IsMatch(t, "(?m)^\\s*\\[options\\]"))
                {
                    if (!t.EndsWith("\n")) t += "\r\n";
                    t += "\r\n[options]\r\n";
                }
                // 空值 = 删除该键（官方/局域网模式下必须清掉，写成空字符串同样无效）
                t = SetOrRemove(t, "custom-rendezvous-server", srv);
                t = SetOrRemove(t, "relay-server", srv);
                t = SetOrRemove(t, "key", key);
                t = SetOrRemove(t, "api-server", api);
                t = SetToml(t, "disable-udp", "N");
                t = SetToml(t, "allow-websocket", "N");
                t = SetToml(t, "enable-lan-discovery", "N");
                t = SetToml(t, "direct-access-port", "21118");
                t = SetToml(t, "verification-method", "use-both-passwords");
                WriteCfg(t);
            }
            catch { }
        }

        // 空值 → 删除键；非空 → 写入
        public static string SetOrRemove(string text, string key, string value)
        {
            if (string.IsNullOrEmpty(value)) return RemoveTomlKey(text, key);
            return SetToml(text, key, value);
        }

        // 检测当前使用模式（读客户机器上已有的配置反推，兼容空值/空格/大小写）
        public static string DetectMode()
        {
            string cfg = ReadCfg();
            string srv = (GetToml(cfg, "custom-rendezvous-server") ?? "").Trim();
            string lan = (GetToml(cfg, "enable-lan-discovery") ?? "").Trim();
            bool lanOn = lan.Equals("Y", StringComparison.OrdinalIgnoreCase) || lan.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (srv.Length == 0) return lanOn ? ModeLan : ModeOfficial;
            return ModeCustom;
        }

        // 按模式写配置（差异只在 server / relay / key / api / lan-discovery）
        public static bool ApplyMode(string mode, string srv, string relay, string key, string api)
        {
            try
            {
                Directory.CreateDirectory(CfgCfg);
                if (!File.Exists(Cfg2)) File.WriteAllText(Cfg2, "", Encoding.ASCII);
                string t = ReadCfg();
                if (!Regex.IsMatch(t, "(?m)^\\s*\\[options\\]"))
                {
                    if (!t.EndsWith("\n")) t += "\r\n";
                    t += "\r\n[options]\r\n";
                }

                if (mode == ModeCustom)
                {
                    t = SetOrRemove(t, "custom-rendezvous-server", srv);
                    string r = string.IsNullOrEmpty(relay) ? srv : relay;
                    t = SetOrRemove(t, "relay-server", r);
                    t = SetOrRemove(t, "key", key);
                    t = SetOrRemove(t, "api-server", api);
                    t = SetToml(t, "enable-lan-discovery", "N");
                }
                else
                {
                    // 官方 / 局域网：清掉所有服务器相关键
                    t = RemoveTomlKey(t, "custom-rendezvous-server");
                    t = RemoveTomlKey(t, "relay-server");
                    t = RemoveTomlKey(t, "key");
                    t = RemoveTomlKey(t, "api-server");
                    t = SetToml(t, "enable-lan-discovery", (mode == ModeLan) ? "Y" : "N");
                }

                t = SetToml(t, "disable-udp", "N");
                t = SetToml(t, "allow-websocket", "N");
                t = SetToml(t, "direct-access-port", "21118");
                t = SetToml(t, "verification-method", "use-both-passwords");
                WriteCfg(t);
                _cmTime = DateTime.MinValue;   // 模式变了，连接方式缓存失效

                // 回读校验：写没写进去，别让用户以为设置成功了
                if (mode == ModeCustom)
                {
                    string chk = ReadCfg();
                    if (GetToml(chk, "custom-rendezvous-server").Length == 0) { Log("ApplyMode 校验失败：server 未写入"); return false; }
                    if (GetToml(chk, "key").Length == 0) { Log("ApplyMode 校验失败：key 未写入"); return false; }
                }
                Log("ApplyMode 完成：mode=" + mode + " srv=" + srv + " relay=" + relay);
                return true;
            }
            catch (Exception ex)
            {
                Log("ApplyMode 异常：" + ex.Message);
                return false;
            }
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

        // 客户端是否已设置固定密码（RustDesk 1.5.x 存在 RustDesk.toml 的顶层 password，且是加密的）。
        // 只能判断"有没有"，明文无法还原（这本来就是安全设计）。
        public static bool ClientHasPassword()
        {
            try
            {
                string p = GetToml(ReadTextFileOrEmpty(Cfg1), "password");
                return p.Length > 0;
            }
            catch { return false; }
        }

        public static string GetAppliedPwd() { return ReadTextFileOrEmpty(AppFile); }
        public static void SaveAppliedPwd(string p) { WriteTextFile(AppFile, p); }
        public static string PwdAppliedText()
        {
            string s = GetStoredPwd();
            if (s.Length == 0) return "尚未设置固定密码";
            // 以 Config 里真实的 password 为准（比 applied.txt 可靠：
            // 用户手改、或旧版本写入的都不会有 applied.txt）
            string inCfg = GetToml(ReadCfg(), "password");
            if (inCfg.Length > 0 && inCfg == s) return "已写入客户端并生效";
            if (inCfg.Length > 0) return "客户端里是另一个密码（" + inCfg + "），点【设置】可覆盖";
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

        // =============================================================
        //  RustDesk 1.5.x 的“服务模式”缺陷处理
        //  --service 拉起的 --server 不写日志、不注册、不心跳（事件日志可见
        //  librustdesk.dll 0xc0000409），导致 key_confirmed 永远为 false。
        //  解决办法：停服务 + 设为禁用，改用纯用户进程模式。
        // =============================================================
        public static Res DisableService()
        {
            if (!IsAdmin()) return new Res(false, "需要管理员权限才能停用系统服务");
            string st = SvcStatus();
            if (st == "NotFound") return new Res(true, "未安装系统服务，无需处理");
            try
            {
                using (ServiceController sc = new ServiceController(SvcName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        try
                        {
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                        }
                        catch { }
                    }
                }
            }
            catch { }
            // 开机不要再自动拉起（否则下次开机又变回服务模式）
            RunHidden("sc.exe", "config " + SvcName + " start= disabled");
            Log("服务已停止并设为禁用（切换为用户模式）");
            return new Res(true, "系统服务已停止并禁用");
        }

        // 确保服务启动类型可启动（曾被设为 Disabled 时恢复为 Automatic）
        public static void EnsureSvcEnabled()
        {
            try
            {
                RunHidden("sc.exe", "config " + SvcName + " start= auto");
                Log("服务启动类型已确保为自动");
            }
            catch { }
        }

        // 一键安装为 Windows 系统服务（官方 --silent-install）
        public static Res InstallService(string exe)
        {
            if (!IsAdmin()) return new Res(false, "需要管理员权限才能安装系统服务");
            Log("开始安装系统服务…");
            try { Cli(exe, "--silent-install", 25000); } catch { }
            for (int i = 0; i < 60; i++)
            {
                if (SvcStatus() != "NotFound") break;
                Thread.Sleep(500);
            }
            if (SvcStatus() == "NotFound") return new Res(false, "安装失败：服务未出现（UAC 弹窗是否点了“是”？）");
            EnsureSvcEnabled();
            KillAllRustDesk();
            Thread.Sleep(1500);
            try { using (ServiceController sc = new ServiceController(SvcName)) sc.Start(); } catch { }
            Log("系统服务安装完成");
            return new Res(true, "已安装为系统服务");
        }

        static void StopSvcWait(int sec)
        {
            try
            {
                using (ServiceController sc = new ServiceController(SvcName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(sec));
                    }
                }
            }
            catch { }
        }

        static void StartSvcWait(int sec)
        {
            try
            {
                using (ServiceController sc = new ServiceController(SvcName))
                {
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(sec));
                    }
                }
            }
            catch { }
        }

        // 配置变更后重启客户端。
        // 结论（多次实测）：RustDesk 1.5.0 的服务模式不可靠 —— --service 拉起的 --server
        // 经常不启动连接线程（日志连 start udp 都没有，key_confirmed 永远 false）。
        // 用户进程模式则在官方/自建两种配置下都稳定工作，所以统一走用户进程。
        public static void RestartClientForConfig(string exe)
        {
            bool hasSvc = (SvcStatus() != "NotFound");
            Log("重启客户端：mode=" + DetectMode() + " hasSvc=" + hasSvc + "（统一用户进程模式）");

            if (hasSvc) StopSvcWait(10);   // 停掉服务，避免与用户进程抢 IPC / 端口
            KillAllRustDesk();
            Thread.Sleep(1200);
            StartClientViaExplorer(exe);
        }

        static string _cmCache = "";
        static DateTime _cmTime = DateTime.MinValue;

        // 从客户端日志判断最近一次连接是直连还是中继（带 20 秒缓存，避免频繁读大文件）
        public static string LastConnMode()
        {
            if ((DateTime.Now - _cmTime).TotalSeconds < 20) return _cmCache;
            string result = "";
            try
            {
                string log = Path.Combine(CfgDir, "log", "rustdesk_rCURRENT.log");
                if (File.Exists(log))
                {
                    string[] lines = File.ReadAllLines(log);
                    int start = Math.Max(0, lines.Length - 600);
                    for (int i = lines.Length - 1; i >= start; i--)
                    {
                        string l = lines[i];
                        if (l.IndexOf("TCP Hole Punched", StringComparison.OrdinalIgnoreCase) >= 0) { result = "直连"; break; }
                        if (l.IndexOf("relay", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            l.IndexOf("connect", StringComparison.OrdinalIgnoreCase) >= 0) { result = "中继"; break; }
                    }
                }
            }
            catch { }
            _cmCache = result;
            _cmTime = DateTime.Now;
            return result;
        }

        // =============================================================
        //  残留检测 & 彻底卸载（逐项容错，任何一项失败都不阻塞）
        // =============================================================
        public class ResidueItem
        {
            public string Name = "";
            public bool Exists = false;
            public string Detail = "";
            public bool Info = false;      // true = 不是 RustDesk 残留，只是提示（如助手自身数据）
        }

        public static string[] ResidueDirs()
        {
            return new string[] {
                @"C:\Program Files\RustDesk",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rustdesk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RustDeskPortable"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RustDeskHelper"),
                CfgDir,
                @"C:\ProgramData\RustDesk",
                @"C:\Windows\System32\config\systemprofile\AppData\Roaming\RustDesk"
            };
        }

        static void AddReg(List<ResidueItem> list, RegistryKey root, string path, string display)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(path))
                    list.Add(new ResidueItem { Name = display, Exists = (k != null), Detail = (k != null) ? "存在" : "已清除" });
            }
            catch { list.Add(new ResidueItem { Name = display, Detail = "检测跳过" }); }
        }

        public static List<ResidueItem> ScanResidue()
        {
            List<ResidueItem> list = new List<ResidueItem>();

            // 进程
            try
            {
                int n = 0;
                foreach (Process p in Process.GetProcesses())
                {
                    try { if (string.Equals(p.ProcessName, "rustdesk", StringComparison.OrdinalIgnoreCase)) n++; } catch { }
                }
                list.Add(new ResidueItem { Name = "客户端进程", Exists = n > 0, Detail = n > 0 ? (n + " 个运行中") : "无" });
            }
            catch { list.Add(new ResidueItem { Name = "客户端进程", Detail = "检测跳过" }); }

            // 服务
            try
            {
                string sv = SvcStatus();
                list.Add(new ResidueItem { Name = "Windows 服务", Exists = sv != "NotFound", Detail = SvcStatusCn(sv) });
            }
            catch { list.Add(new ResidueItem { Name = "Windows 服务", Detail = "检测跳过" }); }

            // 目录
            foreach (string d in ResidueDirs())
            {
                try
                {
                    bool ex = Directory.Exists(d);
                    // RustDeskHelper 是本助手自己的数据（密码副本、日志），不属于 RustDesk 残留
                    bool isHelperData = d.IndexOf("RustDeskHelper", StringComparison.OrdinalIgnoreCase) >= 0;
                    list.Add(new ResidueItem
                    {
                        Name = d,
                        Exists = ex,
                        Info = isHelperData,
                        Detail = isHelperData ? (ex ? "存在（助手自身数据，可保留）" : "已清除")
                                              : (ex ? "存在" : "已清除")
                    });
                }
                catch { list.Add(new ResidueItem { Name = d, Detail = "检测跳过" }); }
            }

            // 注册表
            AddReg(list, Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RustDesk", "注册表 Uninstall");
            AddReg(list, Registry.LocalMachine, @"SOFTWARE\RustDesk", "注册表 SOFTWARE\\RustDesk");
            AddReg(list, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\RustDesk", "注册表 WOW6432Node");
            AddReg(list, Registry.ClassesRoot, "rustdesk", "URI 协议 rustdesk://");
            AddReg(list, Registry.CurrentUser, @"Software\RustDesk", "注册表 用户级 RustDesk");

            // 计划任务（读任务目录，不调外部命令，避免卡住）
            try
            {
                string tdir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
                int cnt = 0;
                if (Directory.Exists(tdir))
                    foreach (string f in Directory.GetFiles(tdir, "RustDesk*")) cnt++;
                list.Add(new ResidueItem { Name = "计划任务", Exists = cnt > 0, Detail = cnt > 0 ? (cnt + " 个") : "无" });
            }
            catch { list.Add(new ResidueItem { Name = "计划任务", Detail = "检测跳过" }); }

            // 快捷方式
            try
            {
                int cnt = 0;
                foreach (string sp in ShortcutSpots())
                {
                    try
                    {
                        if (!Directory.Exists(sp)) continue;
                        foreach (string f in Directory.GetFiles(sp, "*RustDesk*", SearchOption.AllDirectories)) cnt++;
                    }
                    catch { }
                }
                list.Add(new ResidueItem { Name = "快捷方式", Exists = cnt > 0, Detail = cnt > 0 ? (cnt + " 个") : "无" });
            }
            catch { list.Add(new ResidueItem { Name = "快捷方式", Detail = "检测跳过" }); }

            return list;
        }

        static string _lhCache = "";
        static DateTime _lhTime = DateTime.MinValue;

        // 日志健康度（带 10 秒缓存）：
        //   ok      最近有 start udp / register_pk —— 连接正常
        //   fail    出现 timed out / refused
        //   idle    日志长时间不更新（进程可能卡住）
        //   none    没有日志
        public static string LogHealth(int freshnessSec)
        {
            if ((DateTime.Now - _lhTime).TotalSeconds < 10) return _lhCache;
            string res = "none";
            try
            {
                string log = Path.Combine(CfgDir, "log", "rustdesk_rCURRENT.log");
                if (File.Exists(log))
                {
                    double age = (DateTime.Now - File.GetLastWriteTime(log)).TotalSeconds;
                    if (age > freshnessSec) res = "idle";
                    else
                    {
                        string[] lines = File.ReadAllLines(log);
                        int start = Math.Max(0, lines.Length - 500);
                        bool hasErr = false, hasReg = false;
                        for (int i = start; i < lines.Length; i++)
                        {
                            string l = lines[i];
                            if (l.IndexOf("start udp", StringComparison.OrdinalIgnoreCase) >= 0) hasReg = true;
                            else if (l.IndexOf("register_pk", StringComparison.OrdinalIgnoreCase) >= 0) hasReg = true;
                            else if (l.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0) hasErr = true;
                            else if (l.IndexOf("refused", StringComparison.OrdinalIgnoreCase) >= 0) hasErr = true;
                        }
                        res = hasReg ? "ok" : (hasErr ? "fail" : "unknown");
                    }
                }
            }
            catch { res = "unknown"; }
            _lhCache = res;
            _lhTime = DateTime.Now;
            return res;
        }

        static string[] ShortcutSpots()
        {
            // 注意：不要再加 CommonStartup —— 它是 CommonPrograms 的子目录，
            // 递归搜索 CommonPrograms 时已经覆盖，重复加入会导致同一个快捷方式被数两次。
            return new string[] {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
            };
        }

        // 彻底卸载：逐项执行，任何一步失败都继续，不抛异常给调用方
        public static void UninstallAll(Action<string> progress, Action<bool> done)
        {
            try
            {
                progress("[1/8] 停止 Windows 服务…");
                try
                {
                    using (ServiceController sc = new ServiceController(SvcName))
                    {
                        if (sc.Status != ServiceControllerStatus.Stopped)
                        {
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                        }
                    }
                }
                catch { progress("      服务不存在或已停止，继续"); }

                progress("[2/8] 删除服务…");
                try { RunHidden("sc.exe", "stop " + SvcName); } catch { }
                try { RunHidden("sc.exe", "delete " + SvcName); } catch { }

                progress("[3/8] 结束所有客户端进程…");
                try { KillAllRustDesk(); } catch { }

                progress("[4/8] 删除计划任务…");
                try { RunHidden("schtasks.exe", "/Delete /TN \"" + TaskLogon + "\" /F"); } catch { }
                try { RunHidden("schtasks.exe", "/Delete /TN \"" + TaskBoot + "\" /F"); } catch { }

                progress("[5/8] 删除目录…");
                foreach (string d in ResidueDirs())
                {
                    // 助手自身的数据（固定密码记录、日志）保留：
                    // 卸载 RustDesk 不代表要丢掉助手配置，重装后可继续用同一个密码。
                    if (d.IndexOf("RustDeskHelper", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        progress("      保留（助手自身数据）" + d);
                        continue;
                    }
                    try
                    {
                        if (Directory.Exists(d)) { Directory.Delete(d, true); progress("      已删除 " + d); }
                    }
                    catch { progress("      跳过（占用中）" + d); }
                }

                progress("[6/8] 清理注册表…");
                DelReg(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RustDesk", progress);
                DelReg(Registry.LocalMachine, @"SOFTWARE\RustDesk", progress);
                DelReg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\RustDesk", progress);
                DelReg(Registry.ClassesRoot, "rustdesk", progress);
                DelReg(Registry.CurrentUser, @"Software\RustDesk", progress);

                progress("[7/8] 删除快捷方式…");
                foreach (string sp in ShortcutSpots())
                {
                    try
                    {
                        if (!Directory.Exists(sp)) continue;
                        foreach (string f in Directory.GetFiles(sp, "*RustDesk*", SearchOption.AllDirectories))
                        {
                            try { File.Delete(f); progress("      已删除 " + Path.GetFileName(f)); } catch { }
                        }
                    }
                    catch { }
                }

                progress("[8/8] 清理临时文件…");
                try
                {
                    foreach (string f in Directory.GetFiles(Path.GetTempPath(), "*ustDesk*")) { try { File.Delete(f); } catch { } }
                }
                catch { }
                try
                {
                    foreach (string f in Directory.GetFiles(@"C:\Windows\Prefetch", "*RUSTDESK*")) { try { File.Delete(f); } catch { } }
                }
                catch { }

                progress("");
                progress("=== 卸载流程结束，正在复核残留 ===");
                done(true);
            }
            catch (Exception ex)
            {
                progress("卸载过程出错：" + ex.Message);
                done(false);
            }
        }

        static void DelReg(RegistryKey root, string path, Action<string> progress)
        {
            try
            {
                root.DeleteSubKeyTree(path, false);
                progress("      已清理 " + path);
            }
            catch { }
        }

        // 结束所有 rustdesk 进程，包括服务拉起的 --service / --server
        public static void KillAllRustDesk()
        {
            for (int round = 0; round < 2; round++)
            {
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        if (string.Equals(p.ProcessName, "rustdesk", StringComparison.OrdinalIgnoreCase))
                        {
                            p.Kill();
                            p.WaitForExit(3000);
                        }
                    }
                    catch { }
                }
                Thread.Sleep(400);
            }
            _cliPidsTime = DateTime.MinValue;
            Log("已结束所有 rustdesk 进程");
        }

        // 通过 explorer 启动客户端：助手本身是管理员，直接启动会让客户端也带上管理员令牌，
        // RustDesk 在该上下文里容易异常退出；经 explorer 拉起的子进程是普通用户权限。
        public static bool StartClientViaExplorer(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + exe + "\"") { UseShellExecute = true });
                _cliPidsTime = DateTime.MinValue;
                Log("经 explorer 启动客户端：" + exe);
                return true;
            }
            catch (Exception ex)
            {
                Log("explorer 启动失败：" + ex.Message);
                return StartClient(exe);
            }
        }

        static void RunHidden(string file, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                try { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); } catch { }
                if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } }
            }
            catch { }
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
            Add("　｜　连接：", U.ClrSub);
            if (confirmed) Add("已连接", U.ClrGreen);
            else if (cliRun) Add("未注册", U.ClrAmber);
            else Add("未连接", U.ClrRed);
            Add("　｜　运行方式：", U.ClrSub);
            // 服务停止时客户端由用户进程运行，这是正常状态，用灰色而不是红色
            if (svcCn == "运行中") Add("系统服务", U.ClrGreen);
            else Add("用户进程", U.ClrGreen);
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
    //  未安装客户端时的提示窗口：只给出可复制的下载地址，不自动下载安装
    // =================================================================
    class MissingClientForm : Form
    {
        public const string ReleasesUrl = "https://github.com/rustdesk/rustdesk/releases";

        public MissingClientForm()
        {
            Text = "RustDesk 一键配置助手";
            ClientSize = new Size(460, 208);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            Font = U.FUI;
            BackColor = Color.White;
            TopMost = true;

            Label t = new Label();
            t.Text = "未检测到已安装的 RustDesk";
            t.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
            t.ForeColor = U.ClrText;
            t.SetBounds(20, 18, 420, 26);
            Controls.Add(t);

            Label d = new Label();
            d.Text = "本助手只是 RustDesk 的配置工具，需要先安装客户端才能使用。\r\n请下载并安装后再打开本助手：";
            d.ForeColor = U.ClrSub;
            d.SetBounds(20, 52, 420, 38);
            Controls.Add(d);

            TextBox tb = new TextBox();
            tb.Text = ReleasesUrl;
            tb.ReadOnly = true;
            tb.SetBounds(20, 94, 420, 24);
            tb.Font = new Font("Consolas", 9F);
            tb.ForeColor = U.ClrAccent;
            tb.BackColor = Color.White;
            tb.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(tb);

            Button copy = new Button();
            copy.Text = "复制链接";
            copy.SetBounds(20, 132, 104, 32);
            copy.Click += delegate
            {
                try
                {
                    Clipboard.SetDataObject(tb.Text, true);
                    copy.Text = "已复制 ✓";
                }
                catch { }
            };
            Controls.Add(copy);

            Button open = new Button();
            open.Text = "打开下载页";
            open.SetBounds(132, 132, 116, 32);
            open.Click += delegate
            {
                try { Process.Start(new ProcessStartInfo(ReleasesUrl) { UseShellExecute = true }); } catch { }
            };
            Controls.Add(open);

            Button exit = new Button();
            exit.Text = "退出";
            exit.SetBounds(336, 132, 104, 32);
            exit.Click += delegate { Close(); };
            Controls.Add(exit);

            Label tip = new Label();
            tip.Text = "安装完成后重新打开本助手即可。";
            tip.ForeColor = U.ClrSub;
            tip.SetBounds(20, 174, 420, 20);
            Controls.Add(tip);
        }
    }

    // =================================================================
    //  下载进度窗口（已停用：本工具不再自动下载安装客户端）
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
        bool _clientHasPwd = false;    // 客户端已设固定密码但明文不可读（1.5.x 加密存储）
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
        Label _lblSvcInstall;          // 系统服务是否已安装
        Label _lblConnMode;            // 最近一次连接方式（直连 / 中继）
        RButton _btnInstallSvc;        // 一键安装系统服务
        StatusLine _lblMsg;
        Label _lblCliState, _lblSvcState, _lblAutoLogon, _lblAutoBoot;
        TextBox _txtId, _txtFixed, _txtSrv, _txtRelay, _txtKey;
        EyeToggle _eye;
        RButton _btnCopyId, _btnCopyPwd, _btnSetPwd;
        RButton _btnCli, _btnSvc, _btnSaveAuto, _btnSync;
        RButton _btnOpen, _btnFix, _btnCfg, _btnLog;
        RButton _seg1, _seg2, _seg3, _seg4;
        CheckBox _chkLogon, _chkBoot;
        CheckBox _chkNoRelay;          // 仅直连（禁用中继）
        RadioButton _rbOfficial, _rbCustom, _rbLan;   // 使用模式三选一
        Label _lblModeHint;            // 模式说明
        Label _lblIdSrv, _lblRelayCap, _lblKeyCap;    // 自建模式的字段标签（需随模式显示隐藏）
        bool _needRestartAfterInit = false;   // 启动时补写过配置 → 界面出来后重启客户端使其生效
        bool _installBusy = false;            // 正在安装系统服务（后台线程执行）
        bool _suppressRelayEvent = false;     // 程序内改 Checked 时抑制连锁事件
        Panel _panelStatus, _panelSetup, _panelUninstall, _panelLog;
        RadioButton _rbLogPersist, _rbLogSession;   // 日志记录方式
        Label _lblLogInfo;
        RButton _btnOpenLog, _btnTrimLog, _btnClearLog;
        TextBox _txtUninstallLog;              // 卸载页的检测/执行日志
        RButton _btnScan, _btnDoUninstall, _btnClearHelper;
        bool _uninstallBusy = false;
        System.Windows.Forms.Timer _timer;

        const int PageW = 520;
        const int CardX = 16;
        const int CardW = 488;

        public MainForm()
        {
            // ---------------- 初始化数据（轻量，先让界面出来） ----------------
            // 日志模式为"仅本次会话"时，启动先清空，保证日志只说本次的事
            if (U.GetLogMode() == "session")
            {
                try { if (File.Exists(U.LogFile)) File.Delete(U.LogFile); } catch { }
            }
            U.TrimLogIfNeeded();
            U.Log("================ RustDesk 一键配置助手 " + U.Version + " 启动 ================");
            _exe = U.FindExe();
            if (_exe.Length == 0)
            {
                // 不再自动下载安装：只提示用户手动安装（给可复制的下载地址）
                U.Log("未检测到已安装的 RustDesk，提示用户手动安装后退出");
                using (MissingClientForm mf = new MissingClientForm()) mf.ShowDialog();
                Environment.Exit(1);
            }
            U.Log("已找到客户端：" + _exe);

            string cfg = U.ReadCfg();
            string curServer = U.GetToml(cfg, "custom-rendezvous-server");
            string curKey = U.GetToml(cfg, "key");
            string curApi = U.GetToml(cfg, "api-server");
            string curRelay = U.GetToml(cfg, "relay-server");
            string curMode = U.DetectMode();

            string legacyPwd = U.GetToml(cfg, "password");
            // password 是固定密码的合法位置，不能清掉。
            // 服务器地址/Key 留空是合法状态（= 用官方公共服务 或 仅局域网），绝不强制填充。
            // 唯一必须补的是 verification-method，否则固定密码不生效。
            bool needVerify = U.GetToml(cfg, "verification-method").Length == 0;
            if (needVerify)
            {
                U.EnsureConfig(curServer, curKey, curApi);
                U.Log("已补写 verification-method（固定密码生效的必要项）");
                _needRestartAfterInit = true;
            }

            _fixedPwd = U.GetStoredPwd();
            // 助手没记录过、但客户端里已经有固定密码（用户可能在原生界面或用别的工具设过）
            // → 读出来并同步保存，避免助手显示"未设置"误导用户重新生成
            if (_fixedPwd.Length == 0 && legacyPwd.Length > 0 && !Regex.IsMatch(legacyPwd, "\\s"))
            {
                _fixedPwd = legacyPwd;
                U.SaveStoredPwd(_fixedPwd);
                U.Log("从 RustDesk2.toml 读取到明文固定密码，已同步到助手");
            }
            // 1.5.x 起密码加密存在 RustDesk.toml 顶层 password，明文无法还原，
            // 但可以识别"客户端已设置过"，避免用户误以为没设过而重设覆盖。
            if (_fixedPwd.Length == 0 && U.ClientHasPassword())
            {
                _clientHasPwd = true;
                U.Log("检测到客户端已设置固定密码（加密存储，明文不可读）");
            }
            // 不再自动生成：没有固定密码时，密码框内给出「点设置自动生成」的提示
            _rdVer = U.RdVersion(_exe);
            _autoLogon = U.TaskExists(U.TaskLogon);
            _autoBoot = U.TaskExists(U.TaskBoot);
            _isAdmin = U.IsAdmin();

            // ---------------- 窗口 ----------------
            Text = "RustDesk 一键配置助手";
            ClientSize = new Size(PageW, 516);
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

                // 自动识别客户机器上已有的配置，并明确告诉用户读到了什么
                string m = U.DetectMode();
                string info;
                if (m == U.ModeCustom)
                    info = "已识别现有配置：自建服务器 " + (curServer.Length > 0 ? curServer : "(地址为空)");
                else if (m == U.ModeLan)
                    info = "已识别现有配置：仅局域网模式";
                else
                    info = "已识别现有配置：官方公共服务（零配置）";
                U.Log(info);
                SetMsg(info, U.ClrSub, 8);

                if (_needRestartAfterInit)
                {
                    // 刚补写过配置：必须重启，否则客户端继续连默认公共服务器 rs-ny
                    _needRestartAfterInit = false;
                    SetMsg("配置已补全，正在重启客户端使其生效（约 10 秒）…", U.ClrAccent, 25);
                    Refresh();
                    U.RestartClientForConfig(_exe);
                    U.Log("已重启客户端，等待重新注册");
                }
                else
                {
                    StartClientIfNeeded();
                }

                RefreshUi();
                _timer.Start();

                // 首次引导：客户端以未安装模式运行且还没注册上 → 询问是否安装系统服务
                if (U.SvcStatus() == "NotFound" && !U.KeyConfirmed(curServer))
                {
                    DialogResult dr = MessageBox.Show(
                        "检测到 RustDesk 以【未安装模式】运行。\r\n\r\n" +
                        "在 Windows 锁屏界面或 UAC 提权弹窗出现时，将无法远程操作鼠标键盘、也无法录制屏幕。\r\n\r\n" +
                        "是否现在安装为系统服务？（客户端会迁移到 Program Files 并创建服务）",
                        "RustDesk 一键配置助手", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (dr == DialogResult.Yes) DoInstallService();
                }
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
            _seg1.Size = new Size(106, 28);
            _seg1.Selected = true;
            _seg1.Click += delegate { SwitchPanel(0); };
            Controls.Add(_seg1);

            _seg2 = new RButton();
            _seg2.Text = "服务器设置";
            _seg2.Location = new Point(126, 60);
            _seg2.Size = new Size(106, 28);
            _seg2.Click += delegate { SwitchPanel(1); };
            Controls.Add(_seg2);

            _seg3 = new RButton();
            _seg3.Text = "卸载";
            _seg3.Location = new Point(236, 60);
            _seg3.Size = new Size(84, 28);
            _seg3.Click += delegate { SwitchPanel(2); };
            Controls.Add(_seg3);

            _seg4 = new RButton();
            _seg4.Text = "日志";
            _seg4.Location = new Point(324, 60);
            _seg4.Size = new Size(84, 28);
            _seg4.Click += delegate { SwitchPanel(3); };
            Controls.Add(_seg4);

            // ---------------- 两个内容面板 ----------------
            _panelStatus = new Panel();
            _panelStatus.Location = new Point(0, 94);
            _panelStatus.Size = new Size(PageW, 330);
            _panelStatus.BackColor = U.ClrPage;
            Controls.Add(_panelStatus);

            _panelSetup = new Panel();
            _panelSetup.Location = new Point(0, 94);
            _panelSetup.Size = new Size(PageW, 330);
            _panelSetup.BackColor = U.ClrPage;
            _panelSetup.Visible = false;
            Controls.Add(_panelSetup);

            _panelUninstall = new Panel();
            _panelUninstall.Location = new Point(0, 94);
            _panelUninstall.Size = new Size(PageW, 330);
            _panelUninstall.BackColor = U.ClrPage;
            _panelUninstall.Visible = false;
            Controls.Add(_panelUninstall);

            _panelLog = new Panel();
            _panelLog.Location = new Point(0, 94);
            _panelLog.Size = new Size(PageW, 330);
            _panelLog.BackColor = U.ClrPage;
            _panelLog.Visible = false;
            Controls.Add(_panelLog);

            BuildUninstallPanel();
            BuildLogPanel();

            BuildStatusPanel(curServer);
            BuildSetupPanel(curServer, curRelay, curKey);

            // ---------------- 底部按钮 ----------------
            _btnOpen = MkButton("打开客户端", 16, 432, 116, 32, false, false);
            _btnOpen.Click += delegate { if (_exe.Length > 0) Process.Start(new ProcessStartInfo(_exe) { UseShellExecute = true }); };
            _btnFix = MkButton("修复连接", 140, 432, 116, 32, false, false);
            _btnFix.Click += delegate { DoRepair(); };
            _btnCfg = MkButton("配置目录", 264, 432, 116, 32, false, false);
            _btnCfg.Click += delegate { try { Process.Start("explorer.exe", U.CfgCfg); } catch { } };
            _btnLog = MkButton("查看日志", 388, 432, 116, 32, false, false);
            _btnLog.Click += delegate
            {
                if (File.Exists(U.LogFile)) Process.Start("notepad.exe", U.LogFile);
                else SetMsg("暂无日志文件", U.ClrAmber, 4);
            };
            Controls.Add(_btnOpen); Controls.Add(_btnFix);
            Controls.Add(_btnCfg); Controls.Add(_btnLog);

            _lblMsg = new StatusLine();
            _lblMsg.Location = new Point(18, 470);
            _lblMsg.Size = new Size(484, 18);
            _lblMsg.SetMessage("正在读取状态…", U.ClrSub);
            Controls.Add(_lblMsg);

            // 需求4：左侧显示运行身份，右侧显示作者 + 项目链接
            _lblFoot = MkLabel(
                _isAdmin ? "当前以管理员身份运行"
                         : "未以管理员身份运行，部分功能受限",
                U.FSm, U.ClrSub, 18, 492, 170, 16);
            Controls.Add(_lblFoot);

            Label author = MkLabel("作者：九头鱼  pmzyc@qq.com", U.FSm, U.ClrSub, 186, 492, 200, 16);
            author.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(author);

            // GitHub 项目链接
            Label ghLink = MkLabel("GitHub", U.FSm, U.ClrAccent, 392, 492, 50, 16);
            ghLink.Cursor = Cursors.Hand;
            ghLink.Font = new Font(ghLink.Font, FontStyle.Underline);
            ghLink.Click += delegate
            {
                try { Process.Start(new ProcessStartInfo("https://github.com/pmzyc/rustdesk-helper-cn") { UseShellExecute = true }); }
                catch { SetMsg("✗ 无法打开浏览器", U.ClrRed, 4); }
            };
            Controls.Add(ghLink);

            // 分享：复制项目地址
            Label shareLink = MkLabel("分享", U.FSm, U.ClrAccent, 448, 492, 44, 16);
            shareLink.Cursor = Cursors.Hand;
            shareLink.Font = new Font(shareLink.Font, FontStyle.Underline);
            shareLink.Click += delegate
            {
                try
                {
                    Clipboard.SetDataObject("https://github.com/pmzyc/rustdesk-helper-cn", true);
                    SetMsg("✓ 项目地址已复制到剪贴板", U.ClrGreen, 5);
                }
                catch { }
            };
            Controls.Add(shareLink);
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
            c3.Controls.Add(MkHelp(82, "登录后启动：开机进桌面后 RustDesk 自动运行，普通电脑用这个。\r\n开机即启动：以 SYSTEM 身份在登录前启动，适合服务器/无人值守机器；未登录时没有桌面会话，无法采集屏幕。"));

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
            Card c4 = MkCard(0, 232, 96);
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
                // 【断开 / 连接】= 结束 / 启动客户端进程
                // 服务模式实测不可靠，统一按进程管理
                U.Log("BTN-SVC clicked");
                if (U.ClientRunning())
                {
                    U.KillAllRustDesk();
                    SetMsg("✓ 已断开：客户端已停止，别人暂时连不上", U.ClrAccent, 6);
                }
                else
                {
                    if (U.StartClientViaExplorer(_exe)) SetMsg("正在连接服务器…（约 10 秒后就绪）", U.ClrAccent, 8);
                    else SetMsg("✗ 启动失败：找不到客户端程序", U.ClrRed, 5);
                }
            };
            c4.Controls.Add(_btnSvc);

            // 第二行：系统服务（UAC 服务）是否安装 —— 未安装时 UAC/锁屏下无法操作
            c4.Controls.Add(MkLabel("运行方式", U.FSm, U.ClrSub, 14, 62, 62, 22));
            _lblSvcInstall = MkLabel("● 检测中…", U.FSm, U.ClrSub, 78, 62, 160, 22);
            c4.Controls.Add(_lblSvcInstall);

            _lblConnMode = MkLabel("", U.FSm, U.ClrSub, 238, 62, 106, 22);
            c4.Controls.Add(_lblConnMode);

            _btnInstallSvc = MkButton("安装服务", 350, 60, 134, 28, true, false);
            _btnInstallSvc.BackColor = Color.White;
            _btnInstallSvc.Click += delegate { DoToggleRelay(); };
            c4.Controls.Add(_btnInstallSvc);

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
            if (_clientHasPwd)
            {
                // 客户端已设置过，但 1.5.x 加密存在 RustDesk.toml，读不出明文
                _txtFixed.ForeColor = U.ClrSub;
                _txtFixed.Text = "已设置（客户端加密存储，助手读不到明文）";
                _lblPwdMsg.Text = "客户端已设置过固定密码；RustDesk 1.5.x 采用加密存储，助手读不出明文。点【设置】可生成新密码并覆盖原密码。";
                _lblPwdMsg.ForeColor = U.ClrAmber;
            }
            else
            {
                _txtFixed.ForeColor = U.ClrSub;
                _txtFixed.Text = "未设置 · 点右侧【设置】自动生成";
                _lblPwdMsg.Text = "尚未设置固定密码：点【设置】会自动生成一个并写入客户端";
                _lblPwdMsg.ForeColor = U.ClrAmber;
            }
        }

        // ---- 面板二：服务器设置 ----
        void BuildSetupPanel(string curServer, string curRelay, string curKey)
        {
            Card c4 = MkCard(0, 0, 220);
            c4.Controls.Add(MkCap("服务器设置", 14, 8, 160));
            Label q4 = MkHelp(82,
                "三种使用模式：\r\n" +
                "· 官方公共服务：什么都不用填，走 RustDesk 官方服务器，免费（官方中继有速率限制）。\r\n" +
                "· 自建服务器：填你自己的 hbbs 地址与 Key，速度快、可控。\r\n" +
                "· 仅局域网：不连任何服务器，只与同一网段的设备互连。\r\n" +
                "改完点【同步并重启】，客户端会自动重启使配置生效。");
            c4.Controls.Add(q4);

            // ---- 使用模式三选一 ----
            c4.Controls.Add(MkLabel("使用模式", U.FSm, U.ClrSub, 14, 34, 68, 18));
            string curMode = U.DetectMode();
            _rbOfficial = MkRadio("官方公共服务", 86, 32, 100, curMode == U.ModeOfficial);
            _rbOfficial.CheckedChanged += delegate { ApplyModeUi(); };
            c4.Controls.Add(_rbOfficial);
            _rbCustom = MkRadio("自建服务器", 190, 32, 90, curMode == U.ModeCustom);
            _rbCustom.CheckedChanged += delegate { ApplyModeUi(); };
            c4.Controls.Add(_rbCustom);
            _rbLan = MkRadio("仅局域网", 284, 32, 84, curMode == U.ModeLan);
            _rbLan.CheckedChanged += delegate { ApplyModeUi(); };
            c4.Controls.Add(_rbLan);

            _lblModeHint = MkLabel("", U.FSm, U.ClrSub, 86, 56, 388, 18);
            c4.Controls.Add(_lblModeHint);

            // ---- 自建模式字段（其它模式隐藏）----
            _lblIdSrv = MkLabel("ID 服务器", U.FSm, U.ClrSub, 14, 86, 68, 18);
            c4.Controls.Add(_lblIdSrv);
            _txtSrv = MkText(86, 83, 150, 26, false);
            _txtSrv.Font = U.FMonoSm;
            _txtSrv.Text = curServer;
            c4.Controls.Add(_txtSrv);

            _lblRelayCap = MkLabel("中继服务器", U.FSm, U.ClrSub, 248, 86, 68, 18);
            c4.Controls.Add(_lblRelayCap);
            _txtRelay = MkText(320, 83, 154, 26, false);
            _txtRelay.Font = U.FMonoSm;
            _txtRelay.Text = string.IsNullOrEmpty(curRelay) ? curServer : curRelay;
            c4.Controls.Add(_txtRelay);

            _lblKeyCap = MkLabel("Key", U.FSm, U.ClrSub, 14, 122, 68, 18);
            c4.Controls.Add(_lblKeyCap);
            _txtKey = MkText(86, 119, 388, 26, false);
            _txtKey.Font = new Font("Consolas", 8F);
            _txtKey.Text = curKey;
            c4.Controls.Add(_txtKey);

            // 仅直连：把中继指向不可达地址，强制 P2P（打洞失败就连不上）
            bool noRelay = (curRelay != null && curRelay.IndexOf("127.0.0.1") >= 0);
            _chkNoRelay = MkCheck("仅直连（禁用中继）", 86, 154, 190, noRelay);
            _chkNoRelay.CheckedChanged += delegate
            {
                if (_suppressRelayEvent) return;
                bool want = _chkNoRelay.Checked;

                string title = want ? "启用【仅直连】" : "切回【中继模式】";
                string msg = want
                    ? "将禁用中继服务器：\r\n\r\n" +
                      "· 客户端只靠 P2P 打洞直连\r\n" +
                      "· 打洞失败时完全连不上（不会自动回退中继）\r\n" +
                      "· 两台机器在同一虚拟网段（EasyTier / ZeroTier 等）内通常可直连\r\n\r\n" +
                      "确定启用【仅直连】吗？"
                    : "将恢复中继服务器：\r\n\r\n" +
                      "· P2P 打洞成功时仍走直连，速度不受影响\r\n" +
                      "· 打洞失败时自动通过中继转发（兼容性更好，速度略慢）\r\n\r\n" +
                      "确定切回【中继模式】吗？";
                if (MessageBox.Show(msg, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                {
                    // 用户取消：把勾选状态还原
                    _suppressRelayEvent = true;
                    _chkNoRelay.Checked = !want;
                    _suppressRelayEvent = false;
                    return;
                }

                if (want)
                {
                    _txtRelay.Text = "127.0.0.1:1";
                    _txtRelay.ReadOnly = true;
                    _txtRelay.ForeColor = U.ClrSub;
                    SetSrvMsg("已切换为仅直连，正在应用…", U.ClrAccent);
                }
                else
                {
                    _txtRelay.ReadOnly = false;
                    _txtRelay.ForeColor = U.ClrText;
                    _txtRelay.Text = _txtSrv.Text.Trim();
                    SetSrvMsg("已切回中继，正在应用…", U.ClrAccent);
                }
                DoSync(true);      // 已确认过，直接同步并重启
            };
            c4.Controls.Add(_chkNoRelay);

            _btnSync = MkButton("同步并重启", 310, 180, 164, 28, true, false);
            _btnSync.BackColor = Color.White;
            _btnSync.Click += delegate { DoSync(); };
            c4.Controls.Add(_btnSync);

            _lblSrvMsg = MkLabel("", U.FSm, U.ClrSub, 86, 184, 216, 18);
            c4.Controls.Add(_lblSrvMsg);

            ApplyModeUi();
            _panelSetup.Controls.Add(c4);
        }

        // 模式切换：显示/隐藏自建字段 + 更新提示与按钮文案
        void ApplyModeUi()
        {
            if (_rbOfficial == null || _rbCustom == null || _rbLan == null) return;
            bool custom = _rbCustom.Checked;
            Control[] fields = new Control[] { _lblIdSrv, _txtSrv, _lblRelayCap, _txtRelay, _lblKeyCap, _txtKey, _chkNoRelay };
            foreach (Control c in fields) if (c != null) c.Visible = custom;

            if (_lblModeHint == null) return;
            if (_rbOfficial.Checked)
            {
                _lblModeHint.Text = "⚠ 走官方服务器：零配置可用，但官方中继有速率限制，商用建议自建";
                _lblModeHint.ForeColor = U.ClrAmber;
                if (_btnSync != null) _btnSync.Text = "应用并重启";
            }
            else if (_rbLan.Checked)
            {
                _lblModeHint.Text = "⚠ 仅局域网：只能与同一网段的设备互连，跨网络不可用";
                _lblModeHint.ForeColor = U.ClrAmber;
                if (_btnSync != null) _btnSync.Text = "应用并重启";
            }
            else
            {
                _lblModeHint.Text = "自建服务器：填地址与 Key；下方可勾选【仅直连】禁用中继（打洞失败将无法连接）";
                _lblModeHint.ForeColor = U.ClrSub;
                if (_btnSync != null) _btnSync.Text = "同步并重启";
            }
        }

        void SetSrvMsg(string text, Color c)
        {
            if (_lblSrvMsg == null) return;
            _lblSrvMsg.Text = text;
            _lblSrvMsg.ForeColor = c;
        }

        // =================================================================
        //  面板四：日志
        // =================================================================
        void BuildLogPanel()
        {
            Card cl = MkCard(0, 0, 250);
            cl.Controls.Add(MkCap("日志记录", 14, 8, 160));
            cl.Controls.Add(MkHelp(82,
                "日志记录助手做过的每一步操作（模式切换、重启客户端、安装服务、卸载等），用于排查问题。\r\n" +
                "· 持久记录：累积保存，超过 2MB 自动裁剪（保留最近 90 天、最多 3000 行）。\r\n" +
                "· 仅本次会话：每次启动助手时清空，只看这一次。\r\n" +
                "日志只在点【打开日志】时才读取，文件再大也不影响助手启动。"));

            cl.Controls.Add(MkLabel("记录方式", U.FSm, U.ClrSub, 14, 34, 68, 18));
            string mode = U.GetLogMode();

            _rbLogPersist = MkRadio("持久记录（推荐，可回溯历史）", 86, 32, 240, mode == "persist");
            _rbLogPersist.CheckedChanged += delegate
            {
                if (_rbLogPersist.Checked) { U.SetLogMode("persist"); U.Log("日志模式切换为：持久记录"); UpdateLogInfo(); }
            };
            cl.Controls.Add(_rbLogPersist);

            _rbLogSession = MkRadio("仅本次会话（每次启动清空）", 86, 56, 240, mode == "session");
            _rbLogSession.CheckedChanged += delegate
            {
                if (_rbLogSession.Checked) { U.SetLogMode("session"); U.Log("日志模式切换为：仅本次会话"); UpdateLogInfo(); }
            };
            cl.Controls.Add(_rbLogSession);

            _lblLogInfo = MkLabel("", U.FSm, U.ClrSub, 14, 86, 484, 18);
            cl.Controls.Add(_lblLogInfo);

            cl.Controls.Add(MkLabel("文件位置：" + U.LogFile, U.FSm, U.ClrSub, 14, 110, 484, 16));

            _btnOpenLog = MkButton("打开日志", 14, 138, 110, 30, true, false);
            _btnOpenLog.BackColor = Color.White;
            _btnOpenLog.Click += delegate
            {
                try
                {
                    if (File.Exists(U.LogFile)) Process.Start("notepad.exe", U.LogFile);
                    else SetMsg("暂无日志文件", U.ClrAmber, 4);
                }
                catch (Exception ex) { SetMsg("✗ 打开失败：" + ex.Message, U.ClrRed, 5); }
            };
            cl.Controls.Add(_btnOpenLog);

            _btnTrimLog = MkButton("立即裁剪", 134, 138, 110, 30, false, false);
            _btnTrimLog.BackColor = Color.White;
            _btnTrimLog.Click += delegate
            {
                U.TrimLogIfNeeded();
                U.Log("手动执行日志裁剪检查");
                UpdateLogInfo();
                SetMsg("已检查并裁剪日志（超过 2MB 才会真正裁剪）", U.ClrGreen, 6);
            };
            cl.Controls.Add(_btnTrimLog);

            _btnClearLog = MkButton("清空日志", 254, 138, 110, 30, false, false);
            _btnClearLog.BackColor = Color.White;
            _btnClearLog.ForeColor = U.ClrRed;
            _btnClearLog.Click += delegate
            {
                if (MessageBox.Show("确定清空日志文件吗？\r\n\r\n清空后无法恢复。", "清空日志",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                try
                {
                    if (File.Exists(U.LogFile)) File.Delete(U.LogFile);
                    U.Log("日志已手动清空");
                    UpdateLogInfo();
                    SetMsg("✓ 日志已清空", U.ClrGreen, 5);
                }
                catch (Exception ex) { SetMsg("✗ 清空失败：" + ex.Message, U.ClrRed, 6); }
            };
            cl.Controls.Add(_btnClearLog);

            Label note = MkLabel(
                "为什么限制大小：日志长期累积会变得很大，用记事本打开会卡死。\r\n" +
                "这里在写入时自动控制体积，打开时再一次性读取，两头都不会卡。",
                U.FSm, U.ClrSub, 14, 180, 484, 36);
            cl.Controls.Add(note);

            _panelLog.Controls.Add(cl);
            UpdateLogInfo();
        }

        void UpdateLogInfo()
        {
            if (_lblLogInfo == null) return;
            _lblLogInfo.Text = "当前日志：" + U.LogSummary();
        }

        // =================================================================
        //  面板三：卸载（检测与执行全部后台线程，单项失败自动跳过）
        // =================================================================
        void BuildUninstallPanel()
        {
            Card cu = MkCard(0, 0, 326);
            cu.Controls.Add(MkCap("卸载 RustDesk", 14, 8, 200));

            Label warn = MkLabel(
                "将删除：客户端程序、Windows 服务、配置与设备 ID、固定密码、日志、注册表、快捷方式、计划任务、临时文件。\r\n" +
                "助手自身的密码记录会保留，重新安装后可继续使用。此操作不可恢复。",
                U.FSm, U.ClrRed, 14, 36, 484, 36);
            cu.Controls.Add(warn);

            _btnScan = MkButton("检测残留", 14, 80, 110, 30, false, false);
            _btnScan.BackColor = Color.White;
            _btnScan.Click += delegate { DoScanResidue(); };
            cu.Controls.Add(_btnScan);

            _btnDoUninstall = MkButton("彻底卸载", 134, 80, 110, 30, true, false);
            _btnDoUninstall.BackColor = Color.White;
            _btnDoUninstall.Click += delegate { DoUninstall(); };
            cu.Controls.Add(_btnDoUninstall);

            // 默认不碰助手数据，只有点这个按钮才删
            _btnClearHelper = MkButton("清空助手数据", 254, 80, 134, 30, false, false);
            _btnClearHelper.BackColor = Color.White;
            _btnClearHelper.ForeColor = U.ClrRed;
            _btnClearHelper.Click += delegate { DoClearHelperData(); };
            cu.Controls.Add(_btnClearHelper);

            Label tip = MkLabel("检测与卸载都在后台执行，界面不会卡；单项失败自动跳过。助手自身数据默认保留。",
                U.FSm, U.ClrSub, 14, 116, 484, 16);
            cu.Controls.Add(tip);

            _txtUninstallLog = new TextBox();
            _txtUninstallLog.Multiline = true;
            _txtUninstallLog.ReadOnly = true;
            _txtUninstallLog.ScrollBars = ScrollBars.Vertical;
            _txtUninstallLog.Font = new Font("Consolas", 8.5F);
            _txtUninstallLog.BackColor = Color.White;
            _txtUninstallLog.ForeColor = U.ClrText;
            _txtUninstallLog.BorderStyle = BorderStyle.FixedSingle;
            _txtUninstallLog.SetBounds(14, 138, 484, 176);
            cu.Controls.Add(_txtUninstallLog);

            _panelUninstall.Controls.Add(cu);
        }

        void DoScanResidue()
        {
            if (_uninstallBusy) { SetMsg("正在处理中，请稍候…", U.ClrAccent, 4); return; }
            _uninstallBusy = true;
            _btnScan.Enabled = false;
            _btnDoUninstall.Enabled = false;
            _txtUninstallLog.Text = "正在检测残留…" + Environment.NewLine;
            SetMsg("正在检测残留…", U.ClrAccent, 10);

            ThreadPool.QueueUserWorkItem(delegate
            {
                List<U.ResidueItem> items = new List<U.ResidueItem>();
                try { items = U.ScanResidue(); } catch { }
                try
                {
                    if (!IsHandleCreated) return;
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        _uninstallBusy = false;
                        _btnScan.Enabled = true;
                        _btnDoUninstall.Enabled = true;
                        StringBuilder sb = new StringBuilder();
                        int bad = 0;
                        foreach (U.ResidueItem it in items)
                        {
                            string tag = it.Info ? "  [助手] " : (it.Exists ? "  [残留] " : "  [正常] ");
                            if (it.Exists && !it.Info) bad++;
                            sb.AppendLine(tag + it.Name + " —— " + it.Detail);
                        }
                        sb.AppendLine();
                        sb.AppendLine(bad == 0 ? "=== 没有检测到任何残留 ===" : "=== 发现 " + bad + " 项残留 ===");
                        _txtUninstallLog.Text = sb.ToString();
                        SetMsg(bad == 0 ? "✓ 无残留" : ("发现 " + bad + " 项残留"), bad == 0 ? U.ClrGreen : U.ClrAmber, 12);
                    }));
                }
                catch { }
            });
        }

        void DoUninstall()
        {
            if (_uninstallBusy) { SetMsg("正在处理中，请稍候…", U.ClrAccent, 4); return; }
            DialogResult dr = MessageBox.Show(
                "确定要彻底卸载 RustDesk 吗？\r\n\r\n" +
                "将删除程序、服务、配置、设备 ID、固定密码、注册表、快捷方式、计划任务等全部痕迹。\r\n" +
                "助手自身的密码记录会保留（重装后可继续使用）。\r\n\r\n" +
                "此操作不可恢复。",
                "确认卸载", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (dr != DialogResult.OK) return;

            _uninstallBusy = true;
            _btnScan.Enabled = false;
            _btnDoUninstall.Enabled = false;
            _txtUninstallLog.Text = "";
            SetMsg("正在卸载，请稍候…", U.ClrAccent, 180);

            ThreadPool.QueueUserWorkItem(delegate
            {
                U.UninstallAll(
                    delegate(string line)
                    {
                        try
                        {
                            if (IsHandleCreated)
                                BeginInvoke(new MethodInvoker(delegate { _txtUninstallLog.AppendText(line + Environment.NewLine); }));
                        }
                        catch { }
                    },
                    delegate(bool ok)
                    {
                        try
                        {
                            if (!IsHandleCreated) return;
                            BeginInvoke(new MethodInvoker(delegate
                            {
                                _uninstallBusy = false;
                                _btnScan.Enabled = true;
                                _btnDoUninstall.Enabled = true;
                                SetMsg(ok ? "✓ 卸载完成，正在复核残留…" : "✗ 卸载过程有异常，请查看日志", ok ? U.ClrGreen : U.ClrRed, 10);
                                DoScanResidue();
                            }));
                        }
                        catch { }
                    });
            });
        }

        // 清空助手自身数据（密码记录、日志）—— 默认不执行，只有点按钮才做
        void DoClearHelperData()
        {
            if (_uninstallBusy) { SetMsg("正在处理中，请稍候…", U.ClrAccent, 4); return; }
            DialogResult dr = MessageBox.Show(
                "确定要清空助手自身的数据吗？\r\n\r\n" +
                "将删除：固定密码记录、操作日志。\r\n" +
                "不影响已安装的 RustDesk 客户端。此操作不可恢复。",
                "清空助手数据", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (dr != DialogResult.OK) return;

            _uninstallBusy = true;
            _btnScan.Enabled = false;
            _btnDoUninstall.Enabled = false;
            _btnClearHelper.Enabled = false;
            SetMsg("正在清空助手数据…", U.ClrAccent, 30);

            ThreadPool.QueueUserWorkItem(delegate
            {
                int deleted = 0, skipped = 0;
                List<string> names = new List<string>();
                try
                {
                    if (Directory.Exists(U.DataDir))
                    {
                        foreach (string f in Directory.GetFiles(U.DataDir))
                        {
                            try { File.Delete(f); deleted++; names.Add(Path.GetFileName(f)); }
                            catch { skipped++; }
                        }
                        try
                        {
                            foreach (string sub in Directory.GetDirectories(U.DataDir))
                            {
                                try { Directory.Delete(sub, true); } catch { skipped++; }
                            }
                        }
                        catch { }
                        try { if (Directory.GetFiles(U.DataDir).Length == 0) Directory.Delete(U.DataDir, true); } catch { }
                    }
                }
                catch { }

                try
                {
                    if (!IsHandleCreated) return;
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        _uninstallBusy = false;
                        _btnScan.Enabled = true;
                        _btnDoUninstall.Enabled = true;
                        _btnClearHelper.Enabled = true;

                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine("=== 清空助手数据 ===");
                        if (names.Count == 0 && skipped == 0) sb.AppendLine("  目录不存在，无需清理");
                        foreach (string n in names) sb.AppendLine("  已删除 " + n);
                        if (skipped > 0) sb.AppendLine("  " + skipped + " 个文件被占用未删除（重启助手后可再试）");
                        sb.AppendLine();
                        sb.AppendLine(U.GetStoredPwd().Length == 0 ? "固定密码记录：已清空" : "固定密码记录：仍存在");
                        _txtUninstallLog.Text = sb.ToString();

                        // 界面上残留的密码也一并清掉，避免显示已不存在的密码
                        if (U.GetStoredPwd().Length == 0)
                        {
                            _fixedPwd = "";
                            if (_txtFixed != null) { _txtFixed.Text = ""; ShowPwdCue(); }
                        }

                        SetMsg(deleted > 0 ? ("✓ 已清空助手数据（" + deleted + " 个文件）") : "没有需要清理的文件",
                               deleted > 0 ? U.ClrGreen : U.ClrSub, 10);
                    }));
                }
                catch { }
            });
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
        RadioButton MkRadio(string text, int x, int y, int w, bool isChecked)
        {
            RadioButton r = new RadioButton();
            r.Text = text;
            r.Font = U.FSm;
            r.ForeColor = U.ClrText;
            r.BackColor = Color.Transparent;
            r.Location = new Point(x, y);
            r.Size = new Size(w, 22);
            r.Checked = isChecked;
            return r;
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
            if (_panelUninstall != null) _panelUninstall.Visible = (idx == 2);
            if (_panelLog != null) _panelLog.Visible = (idx == 3);
            _seg1.Selected = (idx == 0);
            _seg2.Selected = (idx == 1);
            if (_seg3 != null) _seg3.Selected = (idx == 2);
            if (_seg4 != null) _seg4.Selected = (idx == 3);
            _seg1.Invalidate();
            _seg2.Invalidate();
            if (_seg3 != null) _seg3.Invalidate();
            if (_seg4 != null) _seg4.Invalidate();
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
                // 留空是合法状态（官方公共服务 / 仅局域网），显示时用友好名称，不填默认地址
                s.Srv = srv;
                // 官方/局域网没有"密钥确认"这一说（官方服务器不返回 key 确认，
                // key_confirmed 永远是 false），只能用"客户端进程在跑"来判定可用；
                // 自建服务器仍以 key_confirmed 为准（那才代表真的注册成功）。
                string modeForConfirm = U.DetectMode();
                if (modeForConfirm == U.ModeCustom)
                    s.Confirmed = U.KeyConfirmed(srv);
                else
                    s.Confirmed = cliRun;
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

                // 真正"在线"= 已确认 且 承载网络的进程还在：
                //   服务模式看服务 Running；用户模式看客户端进程。
                // （不能用 SvcRaw=="NotFound" 限定，服务存在但已停用时进程照样能工作）
                bool live = s.Confirmed && (s.CliRun || s.SvcRaw == "Running");

                // 与服务器的连接：以“真正在线”为准
                if (live)
                {
                    _lblSvcState.Text = "● 已连接"; _lblSvcState.ForeColor = U.ClrGreen; _btnSvc.Text = "断开";
                }
                else if (s.SvcRaw == "Running" || s.CliRun)
                {
                    _lblSvcState.Text = "● 未注册"; _lblSvcState.ForeColor = U.ClrAmber; _btnSvc.Text = "连接";
                }
                else
                {
                    _lblSvcState.Text = "● 未连接"; _lblSvcState.ForeColor = U.ClrRed; _btnSvc.Text = "连接";
                }

                string modeNow = U.DetectMode();

                // 运行方式：客户端由“系统服务”还是“用户进程”承载。
                // 服务停止时客户端由用户进程运行，这是正常状态，不能标红误导。
                if (_lblSvcInstall != null)
                {
                    if (s.SvcRaw == "Running")
                    {
                        _lblSvcInstall.Text = "● 系统服务";
                        _lblSvcInstall.ForeColor = U.ClrGreen;
                    }
                    else
                    {
                        _lblSvcInstall.Text = "● 用户进程";
                        _lblSvcInstall.ForeColor = U.ClrGreen;
                    }

                    // 按钮改成“中继开关”（仅自建模式有意义），不再是“安装服务”
                    if (_btnInstallSvc != null)
                    {
                        bool isCustom = (modeNow == U.ModeCustom);
                        _btnInstallSvc.Visible = isCustom;
                        if (isCustom)
                        {
                            bool noRelayNow = U.GetToml(U.ReadCfg(), "relay-server").IndexOf("127.0.0.1") >= 0;
                            _btnInstallSvc.Text = noRelayNow ? "切换到中继" : "设为仅直连";
                            _btnInstallSvc.ForeColor = noRelayNow ? U.ClrAccent : U.ClrText;
                        }
                    }
                }

                // 官方模式提示限速；自建/局域网显示最近一次连接方式
                if (_lblConnMode != null)
                {
                    if (modeNow == U.ModeOfficial)
                    {
                        _lblConnMode.Text = "官方中继有限速";
                        _lblConnMode.ForeColor = U.ClrAmber;
                    }
                    else
                    {
                        string cm = U.LastConnMode();
                        if (cm.Length > 0)
                        {
                            _lblConnMode.Text = "最近：" + cm;
                            _lblConnMode.ForeColor = (cm == "直连") ? U.ClrGreen : U.ClrSub;
                        }
                        else _lblConnMode.Text = "";
                    }
                }

                // 当前模式对应的显示名（官方模式下配置里没有地址，不能直接显示 s.Srv）
                string srvLabel = s.Srv;
                if (modeNow == U.ModeLan) srvLabel = "局域网";
                else if (string.IsNullOrEmpty(srvLabel)) srvLabel = "官方服务器";

                // 顶部状态胶囊
                if (live)
                {
                    if (modeNow == U.ModeOfficial)
                    {
                        // 官方服务器不做密钥确认，RustDesk 界面会一直显示"未就绪"，
                        // 所以这里改用日志健康度判断真实连通性。
                        string h = U.LogHealth(300);
                        if (h == "fail") { _pill.Text = "连接异常 · 检查网络"; _pill.Dot = U.ClrRed; }
                        else if (h == "idle") { _pill.Text = "无响应 · 请重启客户端"; _pill.Dot = U.ClrRed; }
                        else { _pill.Text = "已连接 官方服务器"; _pill.Dot = U.ClrGreen; }
                    }
                    else { _pill.Text = "已连接 " + srvLabel; _pill.Dot = U.ClrGreen; }
                }
                else if (modeNow == U.ModeLan) { _pill.Text = "局域网模式 · 等待发现"; _pill.Dot = U.ClrAmber; }
                else if (s.SvcRaw == "NotFound") { _pill.Text = "未安装服务 · UAC 受限"; _pill.Dot = U.ClrAmber; }
                else if (s.CliRun) { _pill.Text = "未注册 · 点【修复连接】"; _pill.Dot = U.ClrAmber; }
                else { _pill.Text = "客户端未运行"; _pill.Dot = U.ClrRed; }
                _pill.Invalidate();

                // 设备 ID
                if (s.HasId) { _txtId.Text = s.Id; _txtId.ForeColor = U.ClrText; }

                // 修复后的结果校验（安装服务期间不覆盖提示）
                if (_repairWait && !_installBusy)
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
                else _lblMsg.SetStatus(s.CliRun, s.SvcCn, live);
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
            SetPwdMsg("正在写入客户端配置…", U.ClrAccent);
            Refresh();

            // 直接写 RustDesk2.toml 的 [options] password —— 这才是 RustDesk 固定密码的官方位置（明文）。
            // 不用 `rustdesk.exe --password`：那条命令设置的是临时密码通道，且需要“已安装 + 管理员”，
            // 返回 Done 也不会让固定密码生效（实测验证过）。
            try
            {
                string tc = U.ReadCfg();
                if (!Regex.IsMatch(tc, "(?m)^\\s*\\[options\\]"))
                {
                    if (!tc.EndsWith("\n")) tc += "\r\n";
                    tc += "\r\n[options]\r\n";
                }
                tc = U.SetToml(tc, "password", np);
                tc = U.SetToml(tc, "verification-method", "use-both-passwords");
                U.WriteCfg(tc);
                U.SaveAppliedPwd(np);
                U.Log("固定密码已写入 RustDesk2.toml 的 [options] password");

                SetPwdMsg("正在重启客户端使新密码生效…", U.ClrAccent);
                Refresh();
                U.KillAllRustDesk();
                for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(250); }
                if (U.SvcStatus() != "NotFound") { U.EnsureSvcEnabled(); U.StartSvc(); }
                else { U.StartClientViaExplorer(_exe); }

                SetPwdMsg("✓ 设置成功：已写入配置并重启客户端（约 10 秒后就绪）", U.ClrGreen);
            }
            catch (Exception ex)
            {
                SetPwdMsg("✗ 写入失败：" + ex.Message, U.ClrRed);
            }
        }

        // =================================================================
        //  一键安装系统服务（后台线程执行，避免 UI “未响应”）
        // =================================================================
        void DoInstallService()
        {
            if (_installBusy)
            {
                SetMsg("正在安装中，请稍候…", U.ClrAccent, 10);
                return;
            }
            _installBusy = true;
            SetMsg("正在安装系统服务（如弹出“用户账户控制”请选择“是”）…", U.ClrAccent, 300);
            Refresh();

            string exe = _exe;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Res r = U.InstallService(exe);
                try
                {
                    if (!IsHandleCreated) return;
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        _installBusy = false;
                        if (r.Ok)
                        {
                            _exe = U.FindExe();
                            _rdVer = U.RdVersion(_exe);
                            if (_lblVer != null) _lblVer.Text = _rdVer.Length > 0 ? "RustDesk " + _rdVer : "";
                            SetMsg("✓ 已安装为系统服务，客户端正在重新注册…", U.ClrGreen, 25);
                        }
                        else
                        {
                            SetMsg("✗ " + r.Msg, U.ClrRed, 12);
                        }
                        RefreshUi();
                    }));
                }
                catch { }
            });
        }

        // 中继开关：仅直连 ↔ 使用中继（仅自建模式）
        void DoToggleRelay()
        {
            if (U.DetectMode() != U.ModeCustom)
            {
                SetMsg("只有自建服务器模式才支持中继开关", U.ClrAmber, 6);
                return;
            }
            string cfg = U.ReadCfg();
            string srv = U.GetToml(cfg, "custom-rendezvous-server");
            string relay = U.GetToml(cfg, "relay-server");
            bool noRelay = relay.IndexOf("127.0.0.1") >= 0;

            if (!noRelay)
            {
                DialogResult dr = MessageBox.Show(
                    "即将设为【仅直连】：客户端只靠 P2P 打洞直连，打不通就完全连不上（不会回退中继）。\r\n\r\n" +
                    "如果两台机器在同一虚拟网段（EasyTier / ZeroTier 等）内，通常可以直连。\r\n\r\n确定吗？",
                    "设为仅直连", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (dr != DialogResult.OK) return;
            }

            string newRelay = noRelay ? (string.IsNullOrEmpty(srv) ? "" : srv) : "127.0.0.1:1";
            try
            {
                string t = U.SetToml(cfg, "relay-server", newRelay);
                U.WriteCfg(t);
                U.Log("中继切换 -> " + (noRelay ? ("恢复中继 " + newRelay) : "仅直连"));
            }
            catch (Exception ex)
            {
                SetMsg("✗ 写入失败：" + ex.Message, U.ClrRed, 8);
                return;
            }

            // 同步【服务器设置】页的勾选框与输入框，否则切过去还看到旧状态
            if (_chkNoRelay != null)
            {
                _suppressRelayEvent = true;
                _chkNoRelay.Checked = !noRelay;
                _suppressRelayEvent = false;
            }
            if (_txtRelay != null)
            {
                _txtRelay.Text = newRelay;
                _txtRelay.ReadOnly = !noRelay;
                _txtRelay.ForeColor = noRelay ? U.ClrText : U.ClrSub;
            }
            SetSrvMsg(noRelay ? ("已恢复中继：" + newRelay) : "已设为仅直连（127.0.0.1:1）",
                      noRelay ? U.ClrGreen : U.ClrAmber);

            SetMsg(noRelay ? "已恢复中继，正在重启客户端…" : "已设为仅直连，正在重启客户端…", U.ClrAccent, 25);
            ThreadPool.QueueUserWorkItem(delegate
            {
                U.RestartClientForConfig(_exe);
                try
                {
                    if (!IsHandleCreated) return;
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        SetMsg(noRelay ? "✓ 已恢复中继，客户端已重启" : "✓ 已设为仅直连，客户端已重启", U.ClrGreen, 12);
                    }));
                }
                catch { }
            });
        }

        // =================================================================
        //  服务器设置同步
        // =================================================================
        void DoSync(bool skipRelayConfirm = false)
        {
            string mode = U.ModeCustom;
            if (_rbOfficial != null && _rbOfficial.Checked) mode = U.ModeOfficial;
            else if (_rbLan != null && _rbLan.Checked) mode = U.ModeLan;

            string s = _txtSrv.Text.Trim();
            string r = _txtRelay.Text.Trim();
            string k = _txtKey.Text.Trim();

            if (mode == U.ModeCustom)
            {
                if (s.Length == 0) { SetSrvMsg("✗ 自建模式请填写 ID 服务器", U.ClrRed); return; }
                if (k.Length == 0) { SetSrvMsg("✗ 自建模式请填写 Key", U.ClrRed); return; }
            }

            // 仅直连是"硬性禁用中继"，打洞失败就彻底连不上，必须让用户明确知道
            if (!skipRelayConfirm && mode == U.ModeCustom && _chkNoRelay != null && _chkNoRelay.Checked)
            {
                DialogResult dr2 = MessageBox.Show(
                    "你勾选了【仅直连（禁用中继）】。\r\n\r\n" +
                    "这意味着只能靠 P2P 打洞直连：一旦对方在严格 NAT 后、或跨运营商/跨网络，将完全连不上（不会自动回退中继）。\r\n\r\n" +
                    "如果两台机器都在同一虚拟网段（如 EasyTier / ZeroTier）内，通常没问题。\r\n\r\n确定继续吗？",
                    "仅直连风险提示", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (dr2 != DialogResult.OK) return;
            }

            string modeName = (mode == U.ModeOfficial) ? "官方公共服务" : (mode == U.ModeLan ? "仅局域网" : "自建服务器 " + s);
            SetSrvMsg("正在写入配置…", U.ClrAccent);
            SetMsg("正在应用【" + modeName + "】…", U.ClrAccent, 40);
            Refresh();

            try
            {
                if (!U.ApplyMode(mode, s, r, k, ""))
                {
                    SetSrvMsg("✗ 配置写入失败，请点【查看日志】排查", U.ClrRed);
                    SetMsg("✗ 配置写入失败", U.ClrRed, 12);
                    return;
                }
                U.Log("已切换模式：" + modeName);
                SetSrvMsg("配置已写入，正在重启客户端…", U.ClrAccent);

                // 重启要等 Stop/Start 服务状态，动辄十几秒，必须放后台，否则界面未响应
                ThreadPool.QueueUserWorkItem(delegate
                {
                    U.RestartClientForConfig(_exe);
                    try
                    {
                        if (!IsHandleCreated) return;
                        BeginInvoke(new MethodInvoker(delegate
                        {
                            SetSrvMsg("✓ 已应用：" + modeName, U.ClrGreen);
                            SetMsg("✓ 已切换到【" + modeName + "】，客户端正在注册…", U.ClrGreen, 20);
                        }));
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                SetSrvMsg("✗ 应用失败：" + ex.Message, U.ClrRed);
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

            // 按当前配置里的模式重写（不强行填任何默认服务器）
            string mode = U.DetectMode();
            string s = _txtSrv.Text.Trim();
            string k = _txtKey.Text.Trim();
            string r = _txtRelay.Text.Trim();
            if (mode == U.ModeCustom && (s.Length == 0 || k.Length == 0))
            {
                SetMsg("✗ 自建模式下请先在【服务器设置】填写地址和 Key", U.ClrRed, 6);
                _repairWait = false;
                return;
            }
            U.ApplyMode(mode, s, r, k, "");
            U.Log("修复连接：已重置注册状态并按模式重写配置（" + mode + "）");

            // 优先用 Windows 服务承载（服务模式在 UAC / 锁屏时也能工作）；
            // 若客户端没装成服务，回退到纯用户进程模式。
            if (U.SvcStatus() != "NotFound")
            {
                U.EnsureSvcEnabled();
                U.KillAllRustDesk();
                for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(250); }
                Res sr = U.StartSvc();
                U.Log("修复连接：服务重启 -> " + sr.Msg);
                SetMsg("已重启服务，正在重新注册，请稍候…", U.ClrAccent, 12);
            }
            else
            {
                U.KillAllRustDesk();
                for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(250); }
                U.StartClientViaExplorer(_exe);
                U.Log("修复连接：未安装服务，已用用户模式启动客户端");
                SetMsg("已切换为用户模式并重启客户端，正在重新注册，请稍候…", U.ClrAccent, 12);
            }

            _repairWait = true;
            _idFetched = false;
        }

        // =================================================================
        //  需求6：自动下载并静默安装当前最新版 RustDesk
        // =================================================================
        void DoInstallLatest()
        {
            // 已停用：本工具不再自动下载安装客户端，只提示用户手动安装。
            using (MissingClientForm mf = new MissingClientForm()) mf.ShowDialog();
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