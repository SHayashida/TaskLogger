// TaskLogger - 自分用の軽量な作業記録ツール (Windows)
// マウスクリックをきっかけに「どのツールで」「何をしていたか」をCSVに記録します。
// Windows標準の .NET Framework 4.x の csc.exe でビルドできるよう、C# 5 の文法で書いています。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskLogger
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // 高DPI・複数モニターでもクリック位置を正しく扱えるようにする
            bool dpiSet = false;
            try { dpiSet = NativeMethods.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            if (!dpiSet) { try { NativeMethods.SetProcessDPIAware(); } catch { } }

            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            Config config = Config.Load(Path.Combine(exeDir, "TaskLogger.ini"), exeDir);

            // TaskLogger.exe /summary 2026-11-03 で過去日のサマリーを開く
            if (args.Length >= 1 && args[0].Equals("/summary", StringComparison.OrdinalIgnoreCase))
            {
                DateTime day = DateTime.Today;
                if (args.Length >= 2)
                {
                    DateTime parsed;
                    if (DateTime.TryParseExact(args[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                        day = parsed;
                }
                Summary.OpenFor(config, day);
                return;
            }

            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\TaskLogger_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("TaskLogger はすでに起動しています。\nタスクバー右下の通知領域にアイコンがあります。",
                        "TaskLogger", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext(config));
            }
        }
    }

    // ------------------------------------------------------------------
    // 設定 (TaskLogger.ini)
    // ------------------------------------------------------------------
    class Config
    {
        public string LogDir;
        public bool CaptureUrl = true;
        public string UrlDetail = "noquery";      // full / noquery / domain
        public bool CaptureElement = true;
        public bool CaptureElementInBrowser = true;
        public bool RecordRightClick = true;
        public int DedupSeconds = 2;
        public int IdleCapMinutes = 10;
        public int MeetingGapMinutes = 20;
        public int MeetingMergeMinutes = 60;
        public List<string> ExcludeProcesses = new List<string>();
        public List<string> PrivateKeywords = new List<string>(new string[] { "InPrivate", "シークレット", "Incognito", "プライベート ブラウジング", "Private Browsing" });

        public static Config Load(string path, string exeDir)
        {
            Config c = new Config();
            c.LogDir = Path.Combine(exeDir, "logs");
            if (!File.Exists(path)) return c;

            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("[")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();

                switch (key)
                {
                    case "logdir":
                        if (val.Length > 0)
                            c.LogDir = Path.IsPathRooted(val) ? Environment.ExpandEnvironmentVariables(val)
                                                              : Path.Combine(exeDir, Environment.ExpandEnvironmentVariables(val));
                        break;
                    case "captureurl": c.CaptureUrl = ParseBool(val, c.CaptureUrl); break;
                    case "urldetail": c.UrlDetail = val.ToLowerInvariant(); break;
                    case "captureelement": c.CaptureElement = ParseBool(val, c.CaptureElement); break;
                    case "captureelementinbrowser": c.CaptureElementInBrowser = ParseBool(val, c.CaptureElementInBrowser); break;
                    case "recordrightclick": c.RecordRightClick = ParseBool(val, c.RecordRightClick); break;
                    case "dedupseconds": c.DedupSeconds = ParseInt(val, c.DedupSeconds); break;
                    case "idlecapminutes": c.IdleCapMinutes = Math.Max(1, ParseInt(val, c.IdleCapMinutes)); break;
                    case "meetinggapminutes": c.MeetingGapMinutes = Math.Max(1, ParseInt(val, c.MeetingGapMinutes)); break;
                    case "meetingmergeminutes": c.MeetingMergeMinutes = Math.Max(0, ParseInt(val, c.MeetingMergeMinutes)); break;
                    case "excludeprocesses": c.ExcludeProcesses = SplitList(val); break;
                    case "privatekeywords": c.PrivateKeywords = SplitList(val); break;
                }
            }
            return c;
        }

        static bool ParseBool(string v, bool def)
        {
            v = v.ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off") return false;
            return def;
        }

        static int ParseInt(string v, int def)
        {
            int n;
            return int.TryParse(v, out n) ? n : def;
        }

        static List<string> SplitList(string v)
        {
            List<string> list = new List<string>();
            foreach (string s in v.Split(','))
            {
                string t = s.Trim();
                if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 4);
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }
    }

    // ------------------------------------------------------------------
    // トレイアイコンとアプリ全体
    // ------------------------------------------------------------------
    class TrayContext : ApplicationContext
    {
        readonly Config config;
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem pauseItem;
        readonly ToolStripMenuItem autoStartItem;
        readonly System.Windows.Forms.Timer refreshTimer;
        readonly System.Windows.Forms.Timer watchdogTimer;
        readonly MouseHook hook;
        NativeMethods.POINT lastCursor;
        bool haveCursor;
        int lastCheckTick;
        public const string Version = "1.2";
        readonly ClickRecorder recorder;
        Icon activeIcon, pausedIcon;

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "TaskLogger";

        public TrayContext(Config config)
        {
            this.config = config;
            Directory.CreateDirectory(config.LogDir);
            SystemLog.Dir = config.LogDir;
            SystemLog.Write("起動 (v" + Version + ")");

            recorder = new ClickRecorder(config);
            hook = new MouseHook(config.RecordRightClick, recorder.Enqueue);

            activeIcon = MakeIcon(Color.FromArgb(34, 160, 90));
            pausedIcon = MakeIcon(Color.FromArgb(150, 150, 150));

            ContextMenuStrip menu = new ContextMenuStrip();
            pauseItem = new ToolStripMenuItem("一時停止", null, delegate { TogglePause(); });
            menu.Items.Add(pauseItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("今日のサマリーを表示", null, delegate { ShowSummary(); }));
            menu.Items.Add(new ToolStripMenuItem("今日のログ(CSV)を開く", null, delegate { OpenTodayCsv(); }));
            menu.Items.Add(new ToolStripMenuItem("ログフォルダを開く", null, delegate { SafeStart(config.LogDir); }));
            menu.Items.Add(new ToolStripSeparator());
            autoStartItem = new ToolStripMenuItem("Windows起動時に自動で開始", null, delegate { ToggleAutoStart(); });
            autoStartItem.Checked = IsAutoStart();
            menu.Items.Add(autoStartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("終了", null, delegate { ExitApp(); }));

            tray = new NotifyIcon();
            tray.Icon = activeIcon;
            tray.ContextMenuStrip = menu;
            tray.Text = "TaskLogger - 記録中";
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowSummary(); };

            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = 5000;
            refreshTimer.Tick += delegate { RefreshTooltip(); recorder.FlushPending(); };
            refreshTimer.Start();

            if (!hook.Start())
            {
                SystemLog.Write("マウスの監視を開始できませんでした");
                MessageBox.Show("マウスの監視を開始できませんでした。", "TaskLogger", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            // 見張り役: フックやクリック処理が止まっていないかを10秒ごとに確認する
            watchdogTimer = new System.Windows.Forms.Timer();
            watchdogTimer.Interval = 10000;
            watchdogTimer.Tick += delegate { Watchdog(); };
            watchdogTimer.Start();

            // スリープ・ロック・サインアウトへの対応
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.SessionEnding += OnSessionEnding;

            tray.ShowBalloonTip(2000, "TaskLogger", "作業の記録を開始しました。", ToolTipIcon.Info);
        }

        void Watchdog()
        {
            try
            {
                // マウスが動いたのにフックに何も届いていなければ、フックが外れている
                NativeMethods.POINT p;
                int now = Environment.TickCount;
                if (NativeMethods.GetCursorPos(out p))
                {
                    if (haveCursor && (p.X != lastCursor.X || p.Y != lastCursor.Y)
                        && unchecked(hook.LastCallbackTick - lastCheckTick) < 0)
                    {
                        bool ok = hook.Restart();
                        SystemLog.Write("マウスの動きが届いていなかったため、監視を付け直しました" + (ok ? "" : "（失敗）"));
                    }
                    lastCursor = p;
                    haveCursor = true;
                }
                else haveCursor = false;   // ロック画面など
                lastCheckTick = now;

                recorder.CheckWorker(30);
            }
            catch (Exception ex) { SystemLog.Write("見張り処理でエラー: " + ex.Message); }
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                SystemLog.Write("スリープに入ります");
                recorder.FlushPending(true);
            }
            else if (e.Mode == PowerModes.Resume)
            {
                recorder.ResetCaches();
                bool ok = hook.Restart();
                haveCursor = false;
                SystemLog.Write("スリープから復帰しました。監視を付け直しました" + (ok ? "" : "（失敗）"));
            }
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock) SystemLog.Write("画面ロック");
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                recorder.ResetCaches();
                bool ok = hook.Restart();
                haveCursor = false;
                SystemLog.Write("ロック解除。監視を付け直しました" + (ok ? "" : "（失敗）"));
            }
        }

        void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            SystemLog.Write("サインアウト/シャットダウン");
            recorder.FlushPending(true);
        }

        void TogglePause()
        {
            recorder.Paused = !recorder.Paused;
            pauseItem.Text = recorder.Paused ? "記録を再開" : "一時停止";
            tray.Icon = recorder.Paused ? pausedIcon : activeIcon;
            RefreshTooltip();
        }

        void RefreshTooltip()
        {
            string state = recorder.Paused ? "一時停止中" : "記録中";
            string text = "TaskLogger - " + state + " (今日 " + recorder.TodayCount + " 件";
            DateTime last = recorder.LastRecordTime;
            if (last.Date == DateTime.Today) text += " / 最終 " + last.ToString("HH:mm");
            text += ")";
            if (text.Length > 63) text = text.Substring(0, 63);
            tray.Text = text;
        }

        void ShowSummary()
        {
            recorder.FlushPending();
            // サマリー作成は時間がかかることがあるので、別スレッドで行う
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Summary.OpenFor(config, DateTime.Today); }
                catch (Exception ex) { SystemLog.Write("サマリー作成でエラー: " + ex.Message); }
            });
        }

        void OpenTodayCsv()
        {
            recorder.FlushPending();
            string path = ClickRecorder.CsvPathFor(config, DateTime.Today);
            if (File.Exists(path)) SafeStart(path);
            else MessageBox.Show("今日の記録はまだありません。", "TaskLogger");
        }

        static void SafeStart(string target)
        {
            try { Process.Start(target); } catch (Exception ex) { MessageBox.Show(ex.Message, "TaskLogger"); }
        }

        bool IsAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    return k != null && k.GetValue(RunName) != null;
                }
            }
            catch { return false; }
        }

        void ToggleAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (IsAutoStart()) k.DeleteValue(RunName, false);
                    else k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "TaskLogger"); }
            autoStartItem.Checked = IsAutoStart();
        }

        void ExitApp()
        {
            refreshTimer.Stop();
            watchdogTimer.Stop();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.SessionEnding -= OnSessionEnding;
            hook.Stop();
            recorder.Stop();
            SystemLog.Write("終了");
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }

        static Icon MakeIcon(Color color)
        {
            using (Bitmap bmp = new Bitmap(16, 16))
            using (Graphics g = Graphics.FromImage(bmp))
            using (SolidBrush b = new SolidBrush(color))
            using (Pen p = new Pen(Color.White, 1.5f))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                g.FillEllipse(b, 1, 1, 14, 14);
                g.DrawLine(p, 5, 5, 11, 5);   // メモ帳風の横線
                g.DrawLine(p, 5, 8, 11, 8);
                g.DrawLine(p, 5, 11, 9, 11);
                IntPtr h = bmp.GetHicon();
                Icon tmp = Icon.FromHandle(h);
                Icon copy = (Icon)tmp.Clone();
                NativeMethods.DestroyIcon(h);
                return copy;
            }
        }
    }

    // ------------------------------------------------------------------
    // 低レベルマウスフック (クリックを検知するだけ。処理は別スレッドで行う)
    // ------------------------------------------------------------------
    struct ClickEvent
    {
        public DateTime Time;
        public int X, Y;
        public IntPtr RootHwnd;
        public string Button;
    }

    // フックは専用スレッドで動かす。UIの処理(サマリー作成など)で応答が遅れると、
    // Windows はフックを黙って外してしまうため。外れても Restart() で付け直せる。
    class MouseHook
    {
        const int WH_MOUSE_LL = 14;
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_RBUTTONDOWN = 0x0204;
        const uint WM_QUIT = 0x0012;
        const uint GA_ROOT = 2;

        readonly bool includeRight;
        readonly Action<ClickEvent> onClick;
        readonly object sync = new object();
        Thread thread;
        uint threadId;
        volatile int lastCallbackTick = Environment.TickCount;
        public int RestartCount;

        public MouseHook(bool includeRight, Action<ClickEvent> onClick)
        {
            this.includeRight = includeRight;
            this.onClick = onClick;
        }

        // 最後にマウスの動き・クリックを受け取った時刻 (Environment.TickCount)
        public int LastCallbackTick { get { return lastCallbackTick; } }

        public bool Start()
        {
            lock (sync)
            {
                ManualResetEvent ready = new ManualResetEvent(false);
                bool ok = false;
                Thread t = new Thread(delegate()
                {
                    NativeMethods.LowLevelMouseProc proc = null;
                    IntPtr hookId = IntPtr.Zero;
                    proc = delegate(int nCode, IntPtr wParam, IntPtr lParam)
                    {
                        if (nCode >= 0) Callback(wParam, lParam);
                        return NativeMethods.CallNextHookEx(hookId, nCode, wParam, lParam);
                    };
                    threadId = NativeMethods.GetCurrentThreadId();
                    hookId = NativeMethods.SetWindowsHookEx(WH_MOUSE_LL, proc, NativeMethods.GetModuleHandle(null), 0);
                    ok = hookId != IntPtr.Zero;
                    lastCallbackTick = Environment.TickCount;
                    ready.Set();
                    if (!ok) return;
                    NativeMethods.MSG msg;
                    while (NativeMethods.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { }
                    NativeMethods.UnhookWindowsHookEx(hookId);
                    GC.KeepAlive(proc);
                });
                t.IsBackground = true;
                t.Priority = ThreadPriority.Highest;
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                ready.WaitOne(5000);
                thread = t;
                return ok;
            }
        }

        public void Stop()
        {
            lock (sync)
            {
                if (thread == null) return;
                NativeMethods.PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                thread.Join(1000);
                thread = null;
            }
        }

        // フックを作り直す(スリープ復帰時や、反応がなくなった時)
        public bool Restart()
        {
            Stop();
            RestartCount++;
            return Start();
        }

        void Callback(IntPtr wParam, IntPtr lParam)
        {
            lastCallbackTick = Environment.TickCount;   // マウス移動も含めて「生きている」印
            int msg = wParam.ToInt32();
            if (msg != WM_LBUTTONDOWN && !(includeRight && msg == WM_RBUTTONDOWN)) return;
            try
            {
                NativeMethods.MSLLHOOKSTRUCT info = (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));
                IntPtr w = NativeMethods.WindowFromPoint(info.pt);
                ClickEvent ev = new ClickEvent();
                ev.Time = DateTime.Now;
                ev.X = info.pt.X;
                ev.Y = info.pt.Y;
                ev.RootHwnd = w == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(w, GA_ROOT);
                ev.Button = msg == WM_LBUTTONDOWN ? "左" : "右";
                onClick(ev);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    // 動作ログ (logs\system.log) ― 記録が止まった時の原因調査用
    // ------------------------------------------------------------------
    static class SystemLog
    {
        static readonly object sync = new object();
        public static string Dir;

        public static void Write(string message)
        {
            if (Dir == null) return;
            lock (sync)
            {
                try
                {
                    string path = Path.Combine(Dir, "system.log");
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        string old = Path.Combine(Dir, "system.old.log");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + "\r\n", Encoding.UTF8);
                }
                catch { }
            }
        }
    }

    // ------------------------------------------------------------------
    // クリック情報の解析と記録
    // ------------------------------------------------------------------
    class ClickRecorder
    {
        readonly Config config;
        readonly BlockingCollection<ClickEvent> queue = new BlockingCollection<ClickEvent>(200);
        readonly object stateLock = new object();   // キャッシュ・重複判定・書き込みの保護
        int generation;                              // ワーカーを作り直した時に古いものを止めるための番号
        volatile bool busy;
        volatile int busySinceTick;
        int dropped;
        DateTime lastRecordTime = DateTime.MinValue;
        int firstWriteFailTick;
        bool sidecarNotified;
        readonly int ownPid = System.Diagnostics.Process.GetCurrentProcess().Id;
        readonly Dictionary<uint, string> procNameCache = new Dictionary<uint, string>();
        readonly Dictionary<IntPtr, KeyValuePair<string, string>> urlCache = new Dictionary<IntPtr, KeyValuePair<string, string>>();
        readonly List<KeyValuePair<string, string>> pending = new List<KeyValuePair<string, string>>(); // (path, line)
        readonly object pendingLock = new object();

        string lastKey = null;
        DateTime lastTime = DateTime.MinValue;
        DateTime countDay = DateTime.Today;
        int todayCount;
        volatile bool paused;

        public static readonly string[] Header = { "日時", "ツール", "作業内容", "URL", "クリック対象", "対象の種類", "ボタン", "プロセス" };

        static readonly HashSet<string> Browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "iexplore" };

        static readonly Dictionary<string, string> FriendlyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "chrome", "Google Chrome" }, { "msedge", "Microsoft Edge" }, { "firefox", "Firefox" },
            { "brave", "Brave" }, { "opera", "Opera" }, { "vivaldi", "Vivaldi" }, { "iexplore", "Internet Explorer" },
            { "EXCEL", "Excel" }, { "WINWORD", "Word" }, { "POWERPNT", "PowerPoint" }, { "OUTLOOK", "Outlook" },
            { "olk", "Outlook (new)" }, { "ONENOTE", "OneNote" }, { "MSACCESS", "Access" }, { "VISIO", "Visio" },
            { "ms-teams", "Teams" }, { "Teams", "Teams" }, { "slack", "Slack" }, { "Zoom", "Zoom" },
            { "explorer", "エクスプローラー" }, { "notepad", "メモ帳" }, { "Notion", "Notion" },
            { "Code", "VS Code" }, { "AcroRd32", "Adobe Acrobat" }, { "Acrobat", "Adobe Acrobat" },
            { "mspaint", "ペイント" }, { "SnippingTool", "Snipping Tool" }, { "ApplicationFrameHost", "Windowsアプリ" },
        };

        // タイトル末尾の「 - アプリ名」を取り除くための目印
        static readonly string[] AppSuffixMarks =
            { "Google Chrome", "Microsoft Edge", "Mozilla Firefox", "Firefox", "Brave", "Opera", "Vivaldi",
              "Excel", "Word", "PowerPoint", "Outlook", "OneNote", "Access", "Visio", "メモ帳", "Notepad",
              "Visual Studio Code", "Adobe Acrobat", "ペイント" };

        public ClickRecorder(Config config)
        {
            this.config = config;
            todayCount = CountExistingLines(CsvPathFor(config, DateTime.Today));
            StartWorker();
        }

        void StartWorker()
        {
            int gen = Interlocked.Increment(ref generation);
            busy = false;
            Thread t = new Thread(delegate() { WorkerLoop(gen); });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA); // UI Automation は MTA スレッドから呼ぶ
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        // 応答しないアプリへの問い合わせで処理が止まっていたら、新しいワーカーに切り替える
        public void CheckWorker(int stuckSeconds)
        {
            if (busy && unchecked(Environment.TickCount - busySinceTick) > stuckSeconds * 1000)
            {
                SystemLog.Write("クリック処理が " + stuckSeconds + " 秒以上止まっていたため、処理スレッドを作り直しました");
                StartWorker();
            }
            int d = Interlocked.Exchange(ref dropped, 0);
            if (d > 0) SystemLog.Write("処理が追いつかず " + d + " 件のクリックを記録できませんでした");
        }

        // スリープ復帰後などに、古いウィンドウ情報を捨てる
        public void ResetCaches()
        {
            lock (stateLock)
            {
                procNameCache.Clear(); descCache.Clear(); urlCache.Clear();
                lastKey = null;
            }
        }

        public DateTime LastRecordTime { get { return lastRecordTime; } }

        public bool Paused { get { return paused; } set { paused = value; } }

        public int TodayCount
        {
            get { if (countDay != DateTime.Today) { countDay = DateTime.Today; todayCount = 0; } return todayCount; }
        }

        public void Enqueue(ClickEvent ev)
        {
            if (paused) return;
            if (!queue.TryAdd(ev)) Interlocked.Increment(ref dropped); // 処理が詰まっている時は捨てる(フックを遅らせない)
        }

        public void Stop()
        {
            queue.CompleteAdding();
            for (int i = 0; i < 20 && queue.Count > 0; i++) Thread.Sleep(100);
            FlushPending(true);
        }

        void WorkerLoop(int gen)
        {
            while (gen == generation)
            {
                ClickEvent ev;
                try
                {
                    if (!queue.TryTake(out ev, 1000)) { if (queue.IsCompleted) return; continue; }
                }
                catch (InvalidOperationException) { return; }   // 終了処理中 (ObjectDisposedException も含む)
                busySinceTick = Environment.TickCount;
                busy = true;
                try { HandleClick(ev); }
                catch (Exception ex) { ErrorLog(ex); }
                finally { if (gen == generation) busy = false; }
            }
        }

        void HandleClick(ClickEvent ev)
        {
            if (ev.RootHwnd == IntPtr.Zero) return;

            uint pid;
            NativeMethods.GetWindowThreadProcessId(ev.RootHwnd, out pid);
            if (pid == 0 || pid == (uint)ownPid) return;

            string procName;
            lock (stateLock) procName = GetProcessName(pid);
            if (procName.Length == 0) return;
            foreach (string ex in config.ExcludeProcesses)
                if (ex.Equals(procName, StringComparison.OrdinalIgnoreCase)) return;

            string title = Clean(GetWindowText(ev.RootHwnd));
            string cls = GetClassName(ev.RootHwnd);

            foreach (string kw in config.PrivateKeywords)
                if (kw.Length > 0 && title.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return;

            string tool;
            lock (stateLock) tool = FriendlyName(procName, pid);
            if (procName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
            {
                if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") tool = "タスクバー";
                else if (cls == "Progman" || cls == "WorkerW") tool = "デスクトップ";
            }

            bool isBrowser = Browsers.Contains(procName);
            string work = StripAppSuffix(title);

            string url = "";
            if (isBrowser && config.CaptureUrl) url = GetBrowserUrl(ev.RootHwnd, procName, title);

            string elemName = "", elemType = "";
            if (config.CaptureElement && (!isBrowser || config.CaptureElementInBrowser))
                GetElementAt(ev.X, ev.Y, out elemName, out elemType);

            string[] fields = {
                ev.Time.ToString("yyyy-MM-dd HH:mm:ss"), tool, work, url, elemName, elemType, ev.Button, procName
            };
            lock (stateLock)
            {
                // ダブルクリック等の連続した同一クリックはまとめる
                string key = ev.RootHwnd.ToInt64() + "|" + title + "|" + elemName;
                if (key == lastKey && (ev.Time - lastTime).TotalSeconds < config.DedupSeconds)
                {
                    lastTime = ev.Time;
                    return;
                }
                lastKey = key;
                lastTime = ev.Time;
                lastRecordTime = ev.Time;
                Write(ev.Time.Date, Csv.Line(fields));
            }
        }

        // ---------- 書き込み ----------
        public static string CsvPathFor(Config config, DateTime day)
        {
            return Path.Combine(config.LogDir, day.ToString("yyyy-MM-dd") + ".csv");
        }

        void Write(DateTime day, string line)
        {
            string path = CsvPathFor(config, day);
            lock (pendingLock) { pending.Add(new KeyValuePair<string, string>(path, line)); }
            FlushPending();
            if (day == DateTime.Today)
            {
                if (countDay != day) { countDay = day; todayCount = 0; }
                todayCount++;
            }
        }

        // 本体のCSVがExcelで開かれていて書けない間の退避先 (例: 2026-11-18.pending.csv)
        public static string SidecarPathFor(string csvPath)
        {
            return Path.Combine(Path.GetDirectoryName(csvPath), Path.GetFileNameWithoutExtension(csvPath) + ".pending.csv");
        }

        // CSVがロックされていても記録は失わない:
        //   まずメモリに保持 → 60秒以上書けなければ退避ファイルへ → 書けるようになったら本体へ戻す
        public void FlushPending() { FlushPending(false); }

        public void FlushPending(bool force)
        {
            lock (pendingLock)
            {
                while (pending.Count > 0)
                {
                    string path = pending[0].Key;
                    StringBuilder sb = new StringBuilder();
                    int n = 0;
                    while (n < pending.Count && pending[n].Key == path) { sb.Append(pending[n].Value).Append("\r\n"); n++; }

                    if (TryAppend(path, sb.ToString(), true))
                    {
                        pending.RemoveRange(0, n);
                        firstWriteFailTick = 0;
                        continue;
                    }
                    if (firstWriteFailTick == 0) firstWriteFailTick = Environment.TickCount;
                    if (force || unchecked(Environment.TickCount - firstWriteFailTick) > 60000)
                    {
                        if (TryAppend(SidecarPathFor(path), sb.ToString(), false))
                        {
                            pending.RemoveRange(0, n);
                            if (!sidecarNotified)
                            {
                                SystemLog.Write(Path.GetFileName(path) + " に書き込めないため(Excelで開いている等) " + Path.GetFileName(SidecarPathFor(path)) + " に退避しました");
                                sidecarNotified = true;
                            }
                            continue;
                        }
                    }
                    break;
                }
                if (pending.Count > 20000) pending.RemoveRange(0, pending.Count - 20000);
            }
        }

        static bool TryAppend(string path, string text, bool mergeSidecar)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                bool isNew = !File.Exists(path);
                // BOM付きUTF-8: Excelでそのまま開いても文字化けしない
                using (StreamWriter w = new StreamWriter(path, true, new UTF8Encoding(isNew)))
                {
                    if (isNew) w.Write(Csv.Line(Header) + "\r\n");
                    string side = mergeSidecar ? SidecarPathFor(path) : null;
                    if (side != null && File.Exists(side))
                    {
                        // 退避していた分を先に本体へ戻す
                        string[] lines = File.ReadAllLines(side, Encoding.UTF8);
                        StringBuilder sb = new StringBuilder();
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (i == 0 && lines[i].StartsWith(Header[0])) continue;
                            if (lines[i].Length > 0) sb.Append(lines[i]).Append("\r\n");
                        }
                        w.Write(sb.ToString());
                        w.Flush();
                        File.Delete(side);
                        SystemLog.Write(Path.GetFileName(side) + " の内容を " + Path.GetFileName(path) + " に戻しました");
                    }
                    w.Write(text);
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        static int CountExistingLines(string path)
        {
            try
            {
                if (!File.Exists(path)) return 0;
                return Math.Max(0, Csv.ReadRows(path).Count - 1);
            }
            catch { return 0; }
        }

        void ErrorLog(Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(config.LogDir, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex.GetType().Name + ": " + ex.Message + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        // ---------- ウィンドウ・プロセス情報 ----------
        string GetProcessName(uint pid)
        {
            string name;
            if (procNameCache.TryGetValue(pid, out name)) return name;
            try { using (Process p = System.Diagnostics.Process.GetProcessById((int)pid)) name = p.ProcessName; }
            catch { name = ""; }
            if (procNameCache.Count > 300) procNameCache.Clear();
            procNameCache[pid] = name;
            return name;
        }

        readonly Dictionary<uint, string> descCache = new Dictionary<uint, string>();

        string FriendlyName(string procName, uint pid)
        {
            string f;
            if (FriendlyNames.TryGetValue(procName, out f)) return f;
            if (descCache.TryGetValue(pid, out f)) return f;
            f = procName;
            try
            {
                using (Process p = System.Diagnostics.Process.GetProcessById((int)pid))
                {
                    string desc = p.MainModule.FileVersionInfo.FileDescription;
                    if (!string.IsNullOrEmpty(desc) && desc.Trim().Length > 0) f = desc.Trim();
                }
            }
            catch { } // 管理者権限のプロセスなどは取得できないのでプロセス名のまま
            if (descCache.Count > 300) descCache.Clear();
            descCache[pid] = f;
            return f;
        }

        static string GetWindowText(IntPtr hwnd)
        {
            int len = NativeMethods.GetWindowTextLength(hwnd);
            if (len <= 0) return "";
            StringBuilder sb = new StringBuilder(len + 1);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        static string GetClassName(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string StripAppSuffix(string title)
        {
            string t = title;
            for (int guard = 0; guard < 3; guard++)
            {
                int idx = Math.Max(t.LastIndexOf(" - "), t.LastIndexOf(" — "));
                if (idx <= 0) break;
                string tail = t.Substring(idx + 3);
                bool isApp = false;
                foreach (string m in AppSuffixMarks)
                    if (tail.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) { isApp = true; break; }
                if (!isApp) break;
                t = t.Substring(0, idx).TrimEnd();
            }
            return t;
        }

        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (ch == '​' || ch == '‎' || ch == '‏' || ch == '﻿') continue; // Edgeのタイトルに入るゼロ幅文字など
                sb.Append(char.IsControl(ch) ? ' ' : ch);
            }
            string r = sb.ToString().Trim();
            while (r.Contains("  ")) r = r.Replace("  ", " ");
            return r;
        }

        static string Truncate(string s, int max)
        {
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }

        // ---------- UI Automation ----------
        static void GetElementAt(int x, int y, out string name, out string type)
        {
            name = ""; type = "";
            try
            {
                AutomationElement el = AutomationElement.FromPoint(new System.Windows.Point(x, y));
                if (el == null) return;
                AutomationElement cur = el;
                // 名前が空なら親を2段まで遡る(アイコンだけのボタン等)
                for (int i = 0; i < 3 && cur != null; i++)
                {
                    AutomationElement.AutomationElementInformation info = cur.Current;
                    if (i == 0) type = info.LocalizedControlType ?? "";
                    if (info.IsPassword) { name = "(パスワード欄)"; return; }
                    string n = Clean(info.Name);
                    if (n.Length > 0) { name = Truncate(n, 80); if (i > 0) type = info.LocalizedControlType ?? type; return; }
                    cur = TreeWalker.ControlViewWalker.GetParent(cur);
                }
            }
            catch { }
        }

        string GetBrowserUrl(IntPtr hwnd, string procName, string title)
        {
            // タイトルが同じ間はキャッシュを使う(UI Automation の呼び出しを最小限にする)
            KeyValuePair<string, string> cached;
            lock (stateLock)
                if (urlCache.TryGetValue(hwnd, out cached) && cached.Key == title) return cached.Value;

            string url = "";
            try
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                AutomationElement edit = FindAddressBar(root, procName);
                if (edit != null)
                {
                    object pat;
                    if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out pat))
                        url = ((ValuePattern)pat).Current.Value ?? "";
                }
            }
            catch { }

            // アドレスバー以外(ページ内の入力欄など)の値を拾わないよう、URLの形をしたものだけ残す
            url = Clean(url);
            if (!LooksLikeUrl(url)) url = "";
            url = FormatUrl(url, config.UrlDetail);
            lock (stateLock)
            {
                if (urlCache.Count > 100) urlCache.Clear();
                urlCache[hwnd] = new KeyValuePair<string, string>(title, url);
            }
            return url;
        }

        // アドレスバーの名前 (Chrome / Edge / Brave など。日本語・英語)
        static readonly string[] AddressBarNames = { "アドレス検索バー", "アドレスと検索バー", "Address and search bar", "Search or enter address", "Search or enter web address" };

        static AutomationElement FindAddressBar(AutomationElement root, string procName)
        {
            if (procName.Equals("firefox", StringComparison.OrdinalIgnoreCase))
            {
                AutomationElement ff = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input"));
                if (ff != null) return ff;
            }
            // 1) 名前で探す (ページ内の入力欄を拾わない)。
            //    Chrome は版によってアドレスバーが「編集」だったり「コンボ ボックス」だったりするので種類は問わない
            Condition[] names = new Condition[AddressBarNames.Length];
            for (int i = 0; i < names.Length; i++) names[i] = new PropertyCondition(AutomationElement.NameProperty, AddressBarNames[i]);
            AutomationElement byName = root.FindFirst(TreeScope.Descendants, new OrCondition(names));
            if (byName != null)
            {
                object pat;
                if (byName.TryGetCurrentPattern(ValuePattern.Pattern, out pat)) return byName;
                AutomationElement inner = byName.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                if (inner != null) return inner;
            }

            // 2) 見つからなければ、ページ本体(Document)の中には入らずにツールバーの入力欄を探す
            TreeWalker walker = TreeWalker.ControlViewWalker;
            Queue<AutomationElement> q = new Queue<AutomationElement>();
            q.Enqueue(root);
            int visited = 0;
            while (q.Count > 0 && visited < 300)
            {
                AutomationElement child = walker.GetFirstChild(q.Dequeue());
                while (child != null && visited < 300)
                {
                    visited++;
                    ControlType ct = child.Current.ControlType;
                    if (ct == ControlType.Edit || ct == ControlType.ComboBox) return child;
                    if (ct != ControlType.Document) q.Enqueue(child);
                    child = walker.GetNextSibling(child);
                }
            }
            return null;
        }

        static readonly Regex UrlShape = new Regex(
            @"^(?:[a-z][a-z0-9+.\-]*://\S+|(?:[a-z0-9-]+\.)+[a-z]{2,}(?::\d+)?(?:[/?#]\S*)?|localhost(?::\d+)?(?:[/?#]\S*)?|\d{1,3}(?:\.\d{1,3}){3}(?::\d+)?(?:[/?#]\S*)?|[a-z]:[/\\]\S*|(?:chrome|edge|about|file):\S*)$",
            RegexOptions.IgnoreCase);

        public static bool LooksLikeUrl(string s)
        {
            return s.Length > 0 && s.IndexOf(' ') < 0 && UrlShape.IsMatch(s);
        }

        public static string FormatUrl(string url, string detail)
        {
            if (url.Length == 0 || url.Contains(" ")) return ""; // 入力途中の検索語などは記録しない
            if (detail == "full") return Truncate(url, 300);
            string u = url;
            int cut = u.IndexOfAny(new char[] { '?', '#' });
            if (cut >= 0) u = u.Substring(0, cut);
            if (detail == "domain")
            {
                int start = u.IndexOf("://");
                start = start >= 0 ? start + 3 : 0;
                int slash = u.IndexOf('/', start);
                if (slash >= 0) u = u.Substring(0, slash);
            }
            return Truncate(u, 300);
        }
    }

    // ------------------------------------------------------------------
    // CSV ユーティリティ
    // ------------------------------------------------------------------
    static class Csv
    {
        // ExcelでShift_JISとして保存し直されたファイルや、その後に追記されて混ざったファイルも読めるよう、
        // 行ごとにUTF-8かShift_JISかを判定して読む
        public static string DecodeMixed(byte[] bytes)
        {
            int start = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
            UTF8Encoding strict = new UTF8Encoding(false, true);
            Encoding sjis = null;
            try { sjis = Encoding.GetEncoding(932); } catch { }
            StringBuilder sb = new StringBuilder(bytes.Length);
            int i = start;
            while (i < bytes.Length)
            {
                int nl = Array.IndexOf(bytes, (byte)0x0A, i);
                int end = nl < 0 ? bytes.Length : nl + 1;
                try { sb.Append(strict.GetString(bytes, i, end - i)); }
                catch (DecoderFallbackException)
                {
                    sb.Append(sjis != null ? sjis.GetString(bytes, i, end - i) : Encoding.UTF8.GetString(bytes, i, end - i));
                }
                i = end;
            }
            return sb.ToString();
        }

        public static string Line(string[] fields)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0) sb.Append(',');
                string f = fields[i] ?? "";
                if (f.IndexOfAny(new char[] { ',', '"', '\r', '\n' }) >= 0) sb.Append('"').Append(f.Replace("\"", "\"\"")).Append('"');
                else sb.Append(f);
            }
            return sb.ToString();
        }

        public static List<string[]> ReadRows(string path)
        {
            byte[] bytes;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (MemoryStream ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                bytes = ms.ToArray();
            }
            string text = DecodeMixed(bytes);

            List<string[]> rows = new List<string[]>();
            List<string> cur = new List<string>();
            StringBuilder field = new StringBuilder();
            bool inQ = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inQ)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else inQ = false;
                    }
                    else field.Append(c);
                }
                else if (c == '"') inQ = true;
                else if (c == ',') { cur.Add(field.ToString()); field.Length = 0; }
                else if (c == '\r') { }
                else if (c == '\n')
                {
                    cur.Add(field.ToString()); field.Length = 0;
                    rows.Add(cur.ToArray()); cur.Clear();
                }
                else field.Append(c);
            }
            if (field.Length > 0 || cur.Count > 0) { cur.Add(field.ToString()); rows.Add(cur.ToArray()); }
            return rows;
        }
    }

    // ------------------------------------------------------------------
    // 1日のサマリー (HTML) ― 後処理のみ。記録処理(ClickRecorder)とは独立
    // ------------------------------------------------------------------
    class Rec
    {
        public DateTime Time;
        public string Tool, Work, Url, Target, TargetType, Proc;
        public double Minutes;
        public Meeting Parent;      // 並行作業として紐付いた会議 (parent_meeting)
        public Meeting MeetingOf;   // 会議画面そのものの操作ならその会議
        public DateTime EndTime { get { return Time.AddMinutes(Minutes); } }
    }

    class Segment
    {
        public DateTime Start, End;
        public string Tool, Work, Url;
        public Meeting Parent, MeetingOf;
        public int Clicks;
        public double Minutes;
        public Dictionary<string, int> Targets = new Dictionary<string, int>();
    }

    class Meeting
    {
        public string Key, Kind, Name;
        public DateTime Start, End;
        public bool ExitSeen;
        public List<Rec> Rows = new List<Rec>();
        public double SpanMin { get { return Math.Max(0, (End - Start).TotalMinutes); } }
        // 集計結果
        public double ScreenMin;
        public Dictionary<string, double> ToolMin = new Dictionary<string, double>();
        public Dictionary<string, Dictionary<string, double>> WorkMin = new Dictionary<string, Dictionary<string, double>>();
        public double ParallelMin { get { double s = 0; foreach (double v in ToolMin.Values) s += v; return s; } }
        public double NoClickMin { get { return Math.Max(0, SpanMin - ScreenMin - ParallelMin); } }
    }

    // 会議セッションの自動認識
    static class MeetingDetector
    {
        static readonly HashSet<string> BrowserProcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" };
        static readonly string[] ZoomMeetingTitles = { "Zoom ミーティング", "Zoom Meeting", "Zoom ウェビナー", "Zoom Webinar" };
        static readonly string[] ExitWords = { "通話から退出", "Leave call", "ミーティングを退出", "Leave meeting", "ミーティングを終了", "End meeting", "退出", "Leave", "切断", "Hang up" };
        static readonly string[] JoinWords = { "今すぐ参加", "Join now", "参加", "Join", "再参加", "Rejoin" };
        static readonly Regex MeetCode = new Regex(@"meet\.google\.com/(?:meet/)?([a-z]{3}-[a-z]{4}-[a-z]{3})", RegexOptions.IgnoreCase);
        static readonly Regex CalendarTitle = new Regex("「([^」]{1,80})」");

        const int PostExitIgnoreMinutes = 3;
        public const string NoTitle = "(タイトルなし)";   // 退出後の「再参加」画面などを新しい会議と見なさない時間

        static bool ContainsAny(string s, string[] words)
        {
            foreach (string w in words) if (s.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static bool IsTeams(Rec r) { return r.Proc.Equals("ms-teams", StringComparison.OrdinalIgnoreCase) || r.Proc.Equals("Teams", StringComparison.OrdinalIgnoreCase); }
        static bool IsZoom(Rec r) { return r.Proc.Equals("Zoom", StringComparison.OrdinalIgnoreCase); }

        // その行が会議画面の操作か判定し、会議のキーを返す (会議でなければ null)
        static string Classify(Rec r, Meeting active, out string kind, out string name)
        {
            kind = null; name = null;

            // Google Meet (ブラウザのタブタイトルが「Meet - 会議名」)
            if (BrowserProcs.Contains(r.Proc) || r.Proc.Length == 0)
            {
                if (r.Work.StartsWith("Meet - "))
                {
                    kind = "Google Meet"; name = r.Work.Substring(7).Trim();
                    return "meet:" + name;
                }
                Match m = MeetCode.Match(r.Url);
                if (m.Success && r.Work.StartsWith("Meet"))
                {
                    kind = "Google Meet"; name = m.Groups[1].Value;
                    return "meet:" + name;
                }
                // Zoom のブラウザ版 (Web クライアント)
                if (r.Url.IndexOf("zoom.us/wc/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    kind = "Zoom"; return "zoom";
                }
            }

            // Zoom デスクトップアプリ: 「Zoom ミーティング」ウィンドウで開始、会議中は Zoom の操作をすべて会議画面とみなす
            if (IsZoom(r))
            {
                if (active != null && active.Key == "zoom") { kind = "Zoom"; return "zoom"; }
                foreach (string t in ZoomMeetingTitles)
                    if (r.Work.Equals(t, StringComparison.OrdinalIgnoreCase) || r.Target.Equals(t, StringComparison.OrdinalIgnoreCase)) { kind = "Zoom"; return "zoom"; }
                return null;
            }

            // Teams: 「今すぐ参加」で開始、会議中は Teams の操作をすべて会議画面とみなす (実ログ未検証)
            if (IsTeams(r))
            {
                if (active != null && active.Key == "teams") { kind = "Teams"; return "teams"; }
                // 長く操作がなかった後の「退出」は、直前の Teams 会議の続きとして扱う(後で統合される)
                if (ContainsAny(r.Target, ExitWords)) { kind = "Teams"; return "teams"; }
                if (ContainsAny(r.Target, new string[] { "今すぐ参加", "Join now" }))
                {
                    kind = "Teams";
                    string t = r.Work.Replace("| Microsoft Teams", "").Trim().TrimEnd('|').Trim();
                    if (t.Length > 0 && t != NoTitle && t != "Microsoft Teams" && t != "会議" && t != "Meeting") name = t;
                    return "teams";
                }
            }
            return null;
        }

        // Zoom/Teams は会議名が画面に出ないので、直前のZoomウィンドウ名やカレンダーのクリックから推定する
        static string GuessName(List<Rec> recs, int startIdx, string kind)
        {
            DateTime start = recs[startIdx].Time;
            for (int i = startIdx - 1; i >= 0 && (start - recs[i].Time).TotalMinutes <= 3; i--)
            {
                Rec r = recs[i];
                if (kind == "Zoom" && IsZoom(r) && r.Work.Length > 0 && r.Work.IndexOf("Zoom", StringComparison.OrdinalIgnoreCase) < 0
                    && r.Work != NoTitle && r.Work != "ミーティングのセキュリティ" && r.Work != "設定")
                    return r.Work;
            }
            for (int i = startIdx - 1; i >= 0 && (start - recs[i].Time).TotalMinutes <= 20; i--)
            {
                Rec r = recs[i];
                if (r.Url.IndexOf("calendar.google.com", StringComparison.OrdinalIgnoreCase) >= 0 || r.Work.Contains("カレンダー") || r.Work.Contains("Calendar"))
                {
                    Match m = CalendarTitle.Match(r.Target);
                    if (m.Success) return m.Groups[1].Value.Trim();
                }
            }
            return kind == "Zoom" ? "Zoom ミーティング" : kind == "Teams" ? "Teams 会議" : "会議";
        }

        public static List<Meeting> Detect(List<Rec> recs, int gapMinutes, int mergeMinutes)
        {
            List<Meeting> list = new List<Meeting>();
            Meeting cur = null;
            DateTime curLast = DateTime.MinValue;
            string lastExitKey = null;
            DateTime lastExitTime = DateTime.MinValue;

            // 1周目: 会議画面の操作だけを見て、開始〜終了の区間を決める
            for (int i = 0; i < recs.Count; i++)
            {
                Rec r = recs[i];
                if (cur != null && (r.Time - curLast).TotalMinutes > gapMinutes)
                {
                    cur.End = curLast; list.Add(cur); cur = null;     // ログ途絶 → 最後の会議操作で終了
                }

                string kind, name;
                string key = Classify(r, cur, out kind, out name);
                if (key == null) continue;

                if (cur != null && cur.Key != key)
                {
                    cur.End = curLast; list.Add(cur); cur = null;     // 別の会議に移った
                }
                if (cur == null)
                {
                    bool rejoin = ContainsAny(r.Target, JoinWords);
                    if (key == lastExitKey && (r.Time - lastExitTime).TotalMinutes < PostExitIgnoreMinutes && !rejoin)
                        continue;                                     // 退出直後の画面は無視
                    cur = new Meeting();
                    cur.Key = key; cur.Kind = kind; cur.Start = r.Time;
                    cur.Name = name ?? GuessName(recs, i, kind);
                }
                cur.Rows.Add(r);
                curLast = r.Time;

                if (ContainsAny(r.Target, ExitWords) && (r.TargetType.Length == 0 || r.TargetType.Contains("ボタン") || r.TargetType.Contains("Button") || r.TargetType.Contains("メニュー")))
                {
                    cur.End = r.Time; cur.ExitSeen = true; list.Add(cur);
                    lastExitKey = key; lastExitTime = r.Time; cur = null;
                }
            }
            if (cur != null) { cur.End = curLast; list.Add(cur); }

            // 退出操作なしで途切れた同じ会議が、間をおかず再開していたら1つにまとめる
            List<Meeting> merged = new List<Meeting>();
            foreach (Meeting m in list)
            {
                Meeting prev = merged.Count > 0 ? merged[merged.Count - 1] : null;
                if (prev != null && prev.Key == m.Key && !prev.ExitSeen && (m.Start - prev.End).TotalMinutes <= mergeMinutes)
                {
                    prev.End = m.End; prev.ExitSeen = m.ExitSeen; prev.Rows.AddRange(m.Rows);
                }
                else merged.Add(m);
            }
            // 数秒だけ会議ページを開いた等は除外
            merged.RemoveAll(m => m.SpanMin < 1);

            // 2周目: 区間内の操作に親子関係を付ける
            foreach (Meeting m in merged)
            {
                foreach (Rec r in m.Rows) r.MeetingOf = m;
                foreach (Rec r in recs)
                {
                    if (r.Time >= m.Start && r.Time <= m.End && r.MeetingOf == null) r.Parent = m;

                    // 区間と重なる分だけ時間を配分 (区間の前から続く操作も重なり分を数える)
                    DateTime s = r.Time > m.Start ? r.Time : m.Start;
                    DateTime e = r.EndTime < m.End ? r.EndTime : m.End;
                    double ov = (e - s).TotalMinutes;
                    if (ov <= 0) continue;
                    if (r.MeetingOf == m) m.ScreenMin += ov;
                    else
                    {
                        double v; m.ToolMin.TryGetValue(r.Tool, out v); m.ToolMin[r.Tool] = v + ov;
                        Dictionary<string, double> wd;
                        if (!m.WorkMin.TryGetValue(r.Tool, out wd)) { wd = new Dictionary<string, double>(); m.WorkMin[r.Tool] = wd; }
                        double w; wd.TryGetValue(r.Work, out w); wd[r.Work] = w + ov;
                    }
                }
            }
            return merged;
        }
    }

    static class Summary
    {
        public static void OpenFor(Config config, DateTime day)
        {
            string csv = ClickRecorder.CsvPathFor(config, day);
            if (!File.Exists(csv) && !File.Exists(ClickRecorder.SidecarPathFor(csv)))
            {
                MessageBox.Show(day.ToString("yyyy-MM-dd") + " の記録はありません。", "TaskLogger");
                return;
            }
            string html = Build(Load(csv), day, config.IdleCapMinutes, DateTime.Now, config.MeetingGapMinutes, config.MeetingMergeMinutes);
            string outPath = Path.Combine(config.LogDir, "summary_" + day.ToString("yyyy-MM-dd") + ".html");
            File.WriteAllText(outPath, html, new UTF8Encoding(true));
            try { Process.Start(outPath); } catch (Exception ex) { MessageBox.Show(ex.Message, "TaskLogger"); }
        }

        // Excelで保存し直すと「2026/11/18 14:58」のような形式になるため、どちらも読めるようにする
        static readonly string[] TimeFormats = {
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd H:mm:ss", "yyyy-MM-dd H:mm",
            "yyyy/MM/dd HH:mm:ss", "yyyy/M/d H:mm:ss", "yyyy/MM/dd H:mm", "yyyy/M/d H:mm" };

        public static bool TryParseTime(string s, out DateTime t)
        {
            return DateTime.TryParseExact((s ?? "").Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out t);
        }

        public static List<Rec> Load(string csv)
        {
            List<Rec> list = new List<Rec>();
            List<string[]> rows = new List<string[]>();
            if (File.Exists(csv)) rows.AddRange(Csv.ReadRows(csv));
            string side = ClickRecorder.SidecarPathFor(csv);   // Excelで開いている間に退避した分
            if (File.Exists(side)) rows.AddRange(Csv.ReadRows(side));
            for (int i = 0; i < rows.Count; i++)
            {
                string[] r = rows[i];
                if (r.Length < 5) continue;
                DateTime t;
                if (!TryParseTime(r[0], out t)) continue;   // 見出し行やメモ行は飛ばす
                Rec rec = new Rec();
                rec.Time = t; rec.Tool = r[1]; rec.Work = r[2].Length > 0 ? r[2] : MeetingDetector.NoTitle; rec.Url = r[3]; rec.Target = r[4];
                rec.TargetType = r.Length > 5 ? r[5] : "";
                rec.Proc = r.Length > 7 ? r[7] : "";
                list.Add(rec);
            }
            // 同時刻の行の順序を保つため安定ソート
            return list.OrderBy(x => x.Time).ToList();
        }

        // クリックから次のクリックまでを、そのクリックの作業時間とみなす(無操作が長い分は上限でカット)
        public static void AssignDurations(List<Rec> recs, int idleCapMinutes, DateTime now)
        {
            for (int i = 0; i < recs.Count; i++)
            {
                DateTime next;
                if (i + 1 < recs.Count) next = recs[i + 1].Time;
                else next = recs[i].Time.Date == now.Date ? now : recs[i].Time.AddMinutes(1);
                double m = (next - recs[i].Time).TotalMinutes;
                recs[i].Minutes = Math.Max(0, Math.Min(m, idleCapMinutes));
            }
        }

        public static List<Segment> Segments(List<Rec> recs)
        {
            List<Segment> segs = new List<Segment>();
            Segment cur = null;
            foreach (Rec r in recs)
            {
                if (cur == null || cur.Tool != r.Tool || cur.Work != r.Work || cur.Parent != r.Parent || cur.MeetingOf != r.MeetingOf)
                {
                    cur = new Segment();
                    cur.Start = r.Time; cur.Tool = r.Tool; cur.Work = r.Work; cur.Url = r.Url;
                    cur.Parent = r.Parent; cur.MeetingOf = r.MeetingOf;
                    segs.Add(cur);
                }
                cur.End = r.Time.AddMinutes(r.Minutes);
                cur.Clicks++;
                cur.Minutes += r.Minutes;
                if (cur.Url.Length == 0 && r.Url.Length > 0) cur.Url = r.Url;
                if (r.Target.Length > 0)
                {
                    int c; cur.Targets.TryGetValue(r.Target, out c); cur.Targets[r.Target] = c + 1;
                }
            }
            return segs;
        }

        public static string Build(List<Rec> recs, DateTime day, int idleCap, DateTime now)
        {
            return Build(recs, day, idleCap, now, 20, 60);
        }

        public static string Build(List<Rec> recs, DateTime day, int idleCap, DateTime now, int meetingGap, int meetingMerge)
        {
            AssignDurations(recs, idleCap, now);
            List<Meeting> meetings = MeetingDetector.Detect(recs, meetingGap, meetingMerge);
            List<Segment> segs = Segments(recs);
            double total = recs.Sum(r => r.Minutes);

            var byTool = recs.GroupBy(r => r.Tool)
                .Select(g => new { Tool = g.Key, Min = g.Sum(r => r.Minutes), Clicks = g.Count() })
                .OrderByDescending(x => x.Min).ToList();

            var byWork = recs.GroupBy(r => r.Tool + "\u0001" + r.Work)
                .Select(g => new {
                    Tool = g.First().Tool, Work = g.First().Work,
                    Url = g.Select(r => r.Url).FirstOrDefault(u => u.Length > 0) ?? "",
                    Min = g.Sum(r => r.Minutes), Clicks = g.Count(),
                    First = g.Min(r => r.Time), Last = g.Max(r => r.Time.AddMinutes(r.Minutes)) })
                .OrderByDescending(x => x.Min).Take(60).ToList();

            StringBuilder h = new StringBuilder();
            h.Append("<!DOCTYPE html><html lang=\"ja\"><head><meta charset=\"utf-8\"><title>作業記録 ").Append(day.ToString("yyyy-MM-dd")).Append("</title>");
            h.Append("<style>");
            h.Append("body{font-family:'Yu Gothic UI','Meiryo',sans-serif;margin:24px auto;max-width:1100px;color:#222;padding:0 16px}");
            h.Append("h1{font-size:22px;margin-bottom:4px}h2{font-size:17px;margin-top:32px;border-bottom:2px solid #2a9d5a;padding-bottom:4px}");
            h.Append("h2 small{font-weight:normal;color:#666;font-size:12px;margin-left:8px}");
            h.Append(".meta{color:#666;font-size:13px}table{border-collapse:collapse;width:100%;font-size:13px}");
            h.Append("th,td{border-bottom:1px solid #e4e4e4;padding:6px 8px;text-align:left;vertical-align:top}th{background:#f5f7f6}");
            h.Append("td.num{text-align:right;white-space:nowrap}.bar{background:#2a9d5a;height:10px;border-radius:3px}");
            h.Append(".url{color:#777;font-size:11px;word-break:break-all}.tgt{color:#555;font-size:12px}.time{white-space:nowrap;color:#444}");
            // 会議ブロック
            h.Append(".ctx{display:flex;gap:24px;flex-wrap:wrap;margin:8px 0 12px;font-size:13px}.ctx b{font-size:18px;display:block}");
            h.Append("details.mtg{border:1px solid #dfe5e2;border-radius:8px;margin:10px 0;background:#fff}");
            h.Append("details.mtg>summary{cursor:pointer;padding:10px 12px;list-style-position:inside}");
            h.Append(".mname{font-weight:bold;font-size:15px}.mkind{font-size:11px;color:#fff;background:#3b6fb6;border-radius:3px;padding:1px 6px;margin-left:6px}");
            h.Append(".mnote{font-size:11px;color:#888;margin-left:6px}.mline{font-size:13px;color:#444;margin:6px 0 4px}");
            h.Append(".stack{display:flex;height:12px;border-radius:3px;overflow:hidden;background:#eee;max-width:640px}");
            h.Append(".s-scr{background:#3b6fb6}.s-par{background:#2a9d5a}.s-non{background:#cfd4d2}");
            h.Append(".key{display:inline-block;width:10px;height:10px;border-radius:2px;margin:0 4px 0 10px;vertical-align:middle}");
            h.Append(".mbody{padding:0 12px 12px 28px}.mbody table td:first-child{width:55%}");
            h.Append("details.tool>summary{cursor:pointer}details.tool ul{margin:4px 0 4px 18px;padding:0;color:#555;font-size:12px}");
            h.Append(".tag{font-size:11px;border-radius:3px;padding:1px 6px;white-space:nowrap}.tag-m{background:#e3ecf8;color:#2c5591}.tag-p{background:#e6f4ec;color:#22744a}");
            h.Append("</style></head><body>");
            h.Append("<h1>作業記録 ").Append(day.ToString("yyyy年M月d日 (ddd)", new CultureInfo("ja-JP"))).Append("</h1>");
            h.Append("<div class=\"meta\">クリック ").Append(recs.Count).Append(" 回 / 推定作業時間 ").Append(FormatMin(total));
            if (recs.Count > 0) h.Append(" / ").Append(recs[0].Time.ToString("HH:mm")).Append("〜").Append(recs[recs.Count - 1].Time.ToString("HH:mm"));
            if (meetings.Count > 0) h.Append(" / 会議 ").Append(meetings.Count).Append(" 件（拘束 ").Append(FormatMin(meetings.Sum(m => m.SpanMin))).Append("）");
            h.Append("<br>※ 時間はクリック間隔から推定しています(").Append(idleCap).Append("分以上操作がない間はカウントしません)。</div>");

            // ---------- コンテキスト軸: 会議・並行作業 ----------
            h.Append("<h2>会議・並行作業<small>コンテキスト軸：会議に拘束された時間と、その間の手元の作業</small></h2>");
            if (meetings.Count == 0)
            {
                h.Append("<p class=\"meta\">この日は会議（Google Meet / Zoom / Teams）を検出しませんでした。</p>");
            }
            else
            {
                double span = meetings.Sum(m => m.SpanMin), scr = meetings.Sum(m => m.ScreenMin), par = meetings.Sum(m => m.ParallelMin), non = meetings.Sum(m => m.NoClickMin);
                h.Append("<div class=\"ctx\">");
                h.Append("<div>会議の拘束時間<b>").Append(FormatMin(span)).Append("</b></div>");
                h.Append("<div>会議画面の操作<b>").Append(FormatMin(scr)).Append("</b></div>");
                h.Append("<div>並行作業<b>").Append(FormatMin(par)).Append("</b></div>");
                h.Append("<div>クリックなし（聞く・話す等）<b>").Append(FormatMin(non)).Append("</b></div>");
                h.Append("</div>");
                h.Append("<div class=\"meta\"><span class=\"key s-scr\"></span>会議画面<span class=\"key s-par\"></span>並行作業<span class=\"key s-non\"></span>クリックなし</div>");

                foreach (Meeting m in meetings)
                {
                    h.Append("<details class=\"mtg\"><summary>");
                    h.Append("<span class=\"time\">").Append(m.Start.ToString("HH:mm")).Append("〜").Append(m.End.ToString("HH:mm")).Append("</span> ");
                    h.Append("<span class=\"mname\">").Append(Esc(m.Name)).Append("</span><span class=\"mkind\">").Append(Esc(m.Kind)).Append("</span>");
                    if (!m.ExitSeen) h.Append("<span class=\"mnote\">終了時刻は最後の会議画面操作から推定</span>");
                    h.Append("<div class=\"mline\">拘束 <b>").Append(FormatMin(m.SpanMin)).Append("</b>　＝ 会議画面 ").Append(FormatMin(m.ScreenMin))
                     .Append(" ＋ 並行作業 ").Append(FormatMin(m.ParallelMin)).Append(" ＋ クリックなし ").Append(FormatMin(m.NoClickMin)).Append("</div>");
                    double sp = Math.Max(m.SpanMin, 0.01);
                    h.Append("<div class=\"stack\"><div class=\"s-scr\" style=\"width:").Append(Pct(m.ScreenMin / sp)).Append("\"></div><div class=\"s-par\" style=\"width:")
                     .Append(Pct(m.ParallelMin / sp)).Append("\"></div><div class=\"s-non\" style=\"width:").Append(Pct(m.NoClickMin / sp)).Append("\"></div></div>");
                    h.Append("</summary><div class=\"mbody\"><table><tr><th>内訳</th><th>時間</th><th>割合</th></tr>");
                    h.Append("<tr><td>会議画面（").Append(Esc(m.Kind)).Append("）</td><td class=\"num\">").Append(FormatMin(m.ScreenMin)).Append("</td><td class=\"num\">").Append(Pct(m.ScreenMin / sp)).Append("</td></tr>");
                    foreach (KeyValuePair<string, double> t in m.ToolMin.Where(kv => kv.Value >= 0.1).OrderByDescending(kv => kv.Value))
                    {
                        h.Append("<tr><td><details class=\"tool\"><summary>並行：").Append(Esc(t.Key)).Append("</summary><ul>");
                        foreach (KeyValuePair<string, double> w in m.WorkMin[t.Key].Where(kv => kv.Value >= 0.1).OrderByDescending(kv => kv.Value).Take(8))
                            h.Append("<li>").Append(Esc(w.Key)).Append("（").Append(FormatMin(w.Value)).Append("）</li>");
                        h.Append("</ul></details></td><td class=\"num\">").Append(FormatMin(t.Value)).Append("</td><td class=\"num\">").Append(Pct(t.Value / sp)).Append("</td></tr>");
                    }
                    if (m.NoClickMin >= 0.5) h.Append("<tr><td>クリックなし（聞く・話す・画面を見ている等）</td><td class=\"num\">").Append(FormatMin(m.NoClickMin)).Append("</td><td class=\"num\">").Append(Pct(m.NoClickMin / sp)).Append("</td></tr>");
                    h.Append("</table></div></details>");
                }
            }

            // ---------- アテンション軸: 実際に手を動かした時間 (従来どおり) ----------
            h.Append("<h2>ツール別<small>アテンション軸：実際に画面を操作していた時間</small></h2><table><tr><th>ツール</th><th style=\"width:40%\"></th><th>時間</th><th>クリック</th></tr>");
            double maxTool = byTool.Count > 0 ? Math.Max(byTool[0].Min, 0.01) : 1;
            foreach (var t in byTool)
            {
                h.Append("<tr><td>").Append(Esc(t.Tool)).Append("</td><td><div class=\"bar\" style=\"width:")
                 .Append(((int)Math.Round(t.Min / maxTool * 100)).ToString(CultureInfo.InvariantCulture)).Append("%\"></div></td><td class=\"num\">")
                 .Append(FormatMin(t.Min)).Append("</td><td class=\"num\">").Append(t.Clicks).Append("</td></tr>");
            }
            h.Append("</table>");

            h.Append("<h2>作業別 (時間の長い順)</h2><table><tr><th>ツール</th><th>作業内容</th><th>時間</th><th>クリック</th><th>時間帯</th></tr>");
            foreach (var w in byWork)
            {
                h.Append("<tr><td>").Append(Esc(w.Tool)).Append("</td><td>").Append(Esc(w.Work));
                if (w.Url.Length > 0) h.Append("<div class=\"url\">").Append(Esc(w.Url)).Append("</div>");
                h.Append("</td><td class=\"num\">").Append(FormatMin(w.Min)).Append("</td><td class=\"num\">").Append(w.Clicks)
                 .Append("</td><td class=\"time\">").Append(w.First.ToString("HH:mm")).Append("〜").Append(w.Last.ToString("HH:mm")).Append("</td></tr>");
            }
            h.Append("</table>");

            h.Append("<h2>タイムライン</h2><table><tr><th>時間帯</th><th>ツール</th><th>作業内容 / 主なクリック対象</th><th>時間</th></tr>");
            foreach (Segment s in segs)
            {
                h.Append("<tr><td class=\"time\">").Append(s.Start.ToString("HH:mm")).Append("〜").Append(s.End.ToString("HH:mm"))
                 .Append("</td><td>").Append(Esc(s.Tool)).Append("</td><td>");
                if (s.MeetingOf != null) h.Append("<span class=\"tag tag-m\">会議画面</span> ");
                else if (s.Parent != null) h.Append("<span class=\"tag tag-p\">会議中：").Append(Esc(s.Parent.Name)).Append("</span> ");
                h.Append(Esc(s.Work));
                if (s.Url.Length > 0) h.Append("<div class=\"url\">").Append(Esc(s.Url)).Append("</div>");
                if (s.Targets.Count > 0)
                {
                    string tg = string.Join(" ・ ", s.Targets.OrderByDescending(kv => kv.Value).Take(4)
                        .Select(kv => kv.Key + (kv.Value > 1 ? " ×" + kv.Value : "")).ToArray());
                    h.Append("<div class=\"tgt\">").Append(Esc(tg)).Append("</div>");
                }
                h.Append("</td><td class=\"num\">").Append(FormatMin(s.Minutes)).Append("</td></tr>");
            }
            h.Append("</table></body></html>");
            return h.ToString();
        }

        static string Pct(double ratio)
        {
            return ((int)Math.Round(Math.Max(0, Math.Min(1, ratio)) * 100)).ToString(CultureInfo.InvariantCulture) + "%";
        }

        static string FormatMin(double m)
        {
            int total = (int)Math.Round(m);
            if (total < 1) return m > 0 ? "1分未満" : "0分";
            if (total < 60) return total + "分";
            return (total / 60) + "時間" + (total % 60 > 0 ? (total % 60) + "分" : "");
        }

        static string Esc(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }

    // ------------------------------------------------------------------
    // Win32 API
    // ------------------------------------------------------------------
    static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);
    }
}
