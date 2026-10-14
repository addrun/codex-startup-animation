using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DragonLauncher
{
    // Runs independently of the official Codex entry point. It observes desktop
    // activation; it never invokes AppActivation.Open.
    public static class WatchProgram
    {
        [STAThread]
        public static int Main(string[] args)
        {
            StartupGeometry.EnableDpiAwareness();
            string root = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                if (Has(args, "--start-detached")) return ExplorerStarter.Start(root, args);
                if (Has(args, "--desktop-broker-child") && !WatcherLifetime.Current().DesktopParent)
                    throw new InvalidOperationException("监视器并非由桌面 Explorer 创建，已取消后台监视。");
                bool selfTest = Has(args, "--self-test");
                bool diagnose = Has(args, "--diagnose");
                bool status = Has(args, "--status");
                bool stop = Has(args, "--stop");
                if (selfTest) return WatchHost.SelfTest(root, Value(args, "--report", null));
                if (status) return WatchHost.WriteExternalStatus(root, Value(args, "--report", null));
                if (stop) return WatchHost.SignalStop(root);
                int seconds = 0;
                if (diagnose && (!int.TryParse(Value(args, "--seconds", "10"), out seconds) || seconds < 1 || seconds > 300))
                    throw new ArgumentException("--diagnose 的 --seconds 必须为 1 到 300。");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var host = new WatchHost(root, diagnose, seconds, Value(args, "--report", null)))
                    return host.Run();
            }
            catch (Exception error)
            {
                bool auxiliary = Has(args, "--start-detached") || Has(args, "--diagnose") || Has(args, "--self-test") || Has(args, "--status") || Has(args, "--stop");
                WatchHost.WriteFailure(root, error, !auxiliary);
                if (!Has(args, "--diagnose") && !Has(args, "--self-test") && !Has(args, "--status") && !Has(args, "--stop") && !Has(args, "--start-detached"))
                    try { MessageBox.Show(error.Message, "Codex 龙娘监视器", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    catch { }
                return 1;
            }
        }

        private static bool Has(string[] args, string key) { return Array.IndexOf(args, key) >= 0; }
        private static string Value(string[] args, string key, string fallback)
        {
            int index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
    }

    internal sealed class WatchHost : Form
    {
        private const uint EventObjectCreate = 0x8000, EventObjectDestroy = 0x8001;
        private const uint EventObjectShow = 0x8002, EventObjectHide = 0x8003;
        private const uint EventObjectLocationChange = 0x800B, ObjectIdWindow = 0;
        private const uint WineventOutOfContext = 0;
        private const int ProcessCreate = 1;

        private readonly string root, reportOverride, exePath, appId, mutexName, stopName;
        private readonly bool diagnostic;
        private readonly int diagnosticSeconds;
        private readonly StartupGeometry geometry;
        private readonly WatcherLifetime.Snapshot lifetime = WatcherLifetime.Current();
        private readonly string processStartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o");
        private readonly ConcurrentQueue<int> processStarts = new ConcurrentQueue<int>();
        private readonly ConcurrentQueue<ObservedWindowEvent> windowEvents = new ConcurrentQueue<ObservedWindowEvent>();
        private readonly HashSet<int> knownPids = new HashSet<int>();
        private readonly Dictionary<int, long> knownProcessStarts = new Dictionary<int, long>();
        private readonly HashSet<int> rejectedPids = new HashSet<int>();
        private readonly HashSet<IntPtr> knownWindowHandles = new HashSet<IntPtr>();
        private readonly Dictionary<int, bool> matchCache = new Dictionary<int, bool>();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Stopwatch processScanClock = Stopwatch.StartNew();
        private Mutex mutex;
        private EventWaitHandle stopEvent;
        private ManagementEventWatcher processWatcher;
        private WinEventDelegate winEventCallback;
        private IntPtr winEventHook;
        private Process overlayProcess;
        private IntPtr[] currentWindows = new IntPtr[0];
        private readonly CycleGate cycle = new CycleGate();
        private bool wmiAvailable, exiting, diagnosticOnly;
        private bool winEventAvailable;
        private int dispatchQueued;
        private string monitorMode = "initializing", lastError = "", lastTrigger = "";
        private int cyclesStarted, cyclesSuppressed;
        private int processEvents, windowEventsSeen;
        private long lastWindowScan, lastStatusSave, lastGeometrySave;
        private bool targetWindowsDirty = true;

        private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        private struct ObservedWindowEvent
        {
            public IntPtr Window;
            public uint Kind;
            public ObservedWindowEvent(IntPtr window, uint kind) { Window = window; Kind = kind; }
        }

        public WatchHost(string baseDirectory, bool diagnose, int seconds, string reportPath)
        {
            root = Path.GetFullPath(baseDirectory);
            reportOverride = reportPath == null ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(reportPath));
            diagnostic = diagnose; diagnosticSeconds = seconds;
            Settings settings = Settings.Load(root);
            geometry = new StartupGeometry(root);
            appId = settings.AppId;
            exePath = string.IsNullOrWhiteSpace(settings.Exe) ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.Exe));
            string suffix = RootKey(root);
            mutexName = "Local\\CodexDragonWatcher_" + suffix;
            stopName = "Local\\CodexDragonWatcherStop_" + suffix;
            diagnosticOnly = diagnostic;
            ShowInTaskbar = false; WindowState = FormWindowState.Minimized; Opacity = 0;
            Text = "Codex 龙娘监视器"; FormBorderStyle = FormBorderStyle.FixedToolWindow;
            timer.Interval = 1000; timer.Tick += OnTimer;
            tray.Text = "Codex 龙娘动画监视器";
            tray.Icon = System.Drawing.SystemIcons.Application;
            tray.Visible = !diagnostic;
            var menu = new ContextMenuStrip();
            menu.Items.Add("查看状态", null, delegate { ShowStatus(); });
            menu.Items.Add("打开项目目录", null, delegate { Process.Start(root); });
            menu.Items.Add("退出监视器", null, delegate { Shutdown("tray_exit"); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowStatus(); };
        }

        public int Run()
        {
            bool created;
            mutex = new Mutex(true, mutexName, out created);
            if (!created)
            {
                if (!diagnostic) return 0;
                throw new InvalidOperationException("已有 Codex 龙娘监视器正在运行，无法开始独立诊断。");
            }
            stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, stopName);
            FormClosing += OnFormClosing;
            // Initialization is queued against a native hidden handle rather
            // than relying on Load, which a permanently-hidden form never fires.
            IntPtr messageHandle = Handle;
            BeginInvoke((MethodInvoker)delegate { OnLoad(this, EventArgs.Empty); });
            Application.Run(this);
            return 0;
        }

        protected override void SetVisibleCore(bool value)
        {
            // This Form owns only a message pump for out-of-context WinEvents;
            // it must never become a visible or activated desktop window.
            base.SetVisibleCore(false);
        }

        private void OnLoad(object sender, EventArgs e)
        {
            Hide();
            try
            {
                currentWindows = WindowProbe.List(exePath, appId, true);
                foreach (IntPtr baselineWindow in currentWindows) knownWindowHandles.Add(baselineWindow);
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        try { if (IsPrimaryDesktopProcess(process.Id)) RememberPrimaryProcess(process.Id); }
                        catch { }
                    }
                }
                cycle.Initialize(knownPids.Count != 0 || currentWindows.Length != 0);
                // Hidden/tray instances also belong to the baseline. Only the
                // end of the desktop primary process lifetime rearms a cycle.
                StartProcessWatcher();
                StartWindowHook();
                monitorMode = wmiAvailable ? "wmi_events_with_poll_fallback" : "poll_fallback";
                timer.Start();
                RecordEvent("watcher_started", "pid=" + Process.GetCurrentProcess().Id + "; inJob=" + lifetime.InJob + "; baselineWindows=" + currentWindows.Length + "; baselineDesktopProcesses=" + knownPids.Count);
                SaveStatus();
            }
            catch (Exception error)
            {
                lastError = error.GetType().Name + ": " + error.Message;
                monitorMode = "poll_fallback";
                timer.Start(); SaveStatus();
            }
        }

        private void StartProcessWatcher()
        {
            try
            {
                processWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
                processWatcher.EventArrived += delegate(object sender, EventArrivedEventArgs e)
                {
                    try
                    {
                        int id = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);
                        string name = Convert.ToString(e.NewEvent.Properties["ProcessName"].Value);
                        if (string.Equals(name, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrWhiteSpace(exePath) && string.Equals(name, Path.GetFileName(exePath), StringComparison.OrdinalIgnoreCase)))
                        {
                            processStarts.Enqueue(id);
                            RequestReconcile();
                        }
                    }
                    catch { }
                };
                processWatcher.Start();
                wmiAvailable = true;
            }
            catch (Exception error)
            {
                wmiAvailable = false;
                lastError = "WMI process notifications unavailable: " + error.Message;
                try { if (processWatcher != null) processWatcher.Dispose(); } catch { }
                processWatcher = null;
            }
        }

        private void StartWindowHook()
        {
            try
            {
                winEventCallback = OnWinEvent;
                winEventHook = SetWinEventHook(EventObjectCreate, EventObjectLocationChange, IntPtr.Zero,
                    winEventCallback, 0, 0, WineventOutOfContext);
                winEventAvailable = winEventHook != IntPtr.Zero;
                if (winEventHook == IntPtr.Zero) lastError = "SetWinEventHook failed: " + Marshal.GetLastWin32Error();
            }
            catch (Exception error) { lastError = "WinEvent hook unavailable: " + error.Message; }
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (hwnd == IntPtr.Zero || idObject != ObjectIdWindow || idChild != 0) return;
            if (eventType == EventObjectCreate || eventType == EventObjectDestroy || eventType == EventObjectShow ||
                eventType == EventObjectHide || eventType == EventObjectLocationChange)
            {
                // Queue only: process identity and window inspection happen after
                // returning from the native accessibility callback.
                windowEvents.Enqueue(new ObservedWindowEvent(hwnd, eventType));
                RequestReconcile();
            }
        }

        private void RequestReconcile()
        {
            if (exiting || Interlocked.Exchange(ref dispatchQueued, 1) != 0) return;
            try { BeginInvoke((MethodInvoker)delegate { Interlocked.Exchange(ref dispatchQueued, 0); Reconcile(); }); }
            catch { Interlocked.Exchange(ref dispatchQueued, 0); }
        }

        private void OnTimer(object sender, EventArgs e)
        {
            if (stopEvent != null && stopEvent.WaitOne(0)) { Shutdown("stop_signal"); return; }
            if (diagnostic && clock.Elapsed.TotalSeconds >= diagnosticSeconds)
            {
                SaveStatus(); Shutdown("diagnostic_complete"); return;
            }
            Reconcile();
        }

        private void Reconcile()
        {
            if (exiting) return;
            ProcessQueuedEvents();
            if (targetWindowsDirty || clock.ElapsedMilliseconds - lastWindowScan >= 1000)
            {
                targetWindowsDirty = false; lastWindowScan = clock.ElapsedMilliseconds;
                PollWindows();
            }
            CheckOverlay();
            if (clock.ElapsedMilliseconds - lastStatusSave >= 1000)
            {
                lastStatusSave = clock.ElapsedMilliseconds;
                SaveStatus();
            }
        }

        private void ProcessQueuedEvents()
        {
            // Process exit can be followed by a new window event before the
            // periodic scan, especially when WMI is unavailable.
            PrunePrimaryProcesses();
            int pid;
            while (processStarts.TryDequeue(out pid))
                if (pid > 0) ProcessCandidate(pid, "wmi_process_start");
            ObservedWindowEvent observed;
            while (windowEvents.TryDequeue(out observed))
            {
                IntPtr hwnd = observed.Window;
                if (observed.Kind == EventObjectDestroy && knownWindowHandles.Remove(hwnd)) targetWindowsDirty = true;
                uint eventOwner; GetWindowThreadProcessId(hwnd, out eventOwner);
                bool matches;
                if (!matchCache.TryGetValue((int)eventOwner, out matches))
                {
                    matches = WindowProbe.Matches(eventOwner, 0, exePath, appId);
                    matchCache[(int)eventOwner] = matches;
                }
                if (!matches && Array.IndexOf(currentWindows, hwnd) < 0) continue;
                targetWindowsDirty = true;
                windowEventsSeen++;
                // A newly observed primary process can cover the startup wait
                // before its main window becomes visible. Close/hide/location
                // events must never masquerade as a fresh process activation.
                if (observed.Kind == EventObjectCreate && matches && !cycle.Consumed && !knownPids.Contains((int)eventOwner))
                    ProcessCandidate((int)eventOwner, "primary_process_window_created");
                // A newly-created native window is a fallback for missed process
                // events, never a new cycle for a surviving tray instance.
                if (!cycle.Consumed && (observed.Kind == EventObjectCreate || observed.Kind == EventObjectShow) &&
                    IsTopLevelWindow(hwnd) && Array.IndexOf(WindowProbe.List(exePath, appId, true), hwnd) >= 0)
                {
                    uint owner; GetWindowThreadProcessId(hwnd, out owner);
                    if (owner != 0 && !knownWindowHandles.Contains(hwnd))
                        ProcessCandidate((int)owner, observed.Kind == EventObjectShow ? "main_window_shown" : "main_window_created");
                }
            }
        }

        private void ProcessCandidate(int pid, string trigger)
        {
            // WMI/window events can arrive before the next polling scan. Remove
            // exited identities now so a rapid quit/relaunch is not suppressed
            // by a stale PID, including PID reuse by a new process lifetime.
            PrunePrimaryProcesses();
            if (knownPids.Contains(pid) || rejectedPids.Contains(pid)) return;
            if (!IsPrimaryDesktopProcess(pid)) { rejectedPids.Add(pid); return; }
            bool existingPrimary = knownPids.Count != 0;
            if (!RememberPrimaryProcess(pid)) return;
            matchCache[pid] = true;
            processEvents++;
            RecordPrimaryStart(pid, trigger);
            // An official icon may create a short-lived activation process while
            // its original desktop host survives in the tray. It is the same
            // cycle even when that host has no visible/native main window.
            cycle.PrimaryActivation(existingPrimary, overlayProcess != null && !SafeHasExited(overlayProcess));
            if (!cycle.Consumed && !diagnostic) Trigger(trigger, pid);
            else cyclesSuppressed++;
        }

        private bool RememberPrimaryProcess(int pid)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    if (process.HasExited) return false;
                    long started = process.StartTime.ToUniversalTime().Ticks;
                    knownPids.Add(pid); knownProcessStarts[pid] = started;
                    return true;
                }
            }
            catch { return false; }
        }

        private void RecordPrimaryStart(int pid, string source)
        {
            // Keep local diagnostics about who created the desktop process,
            // without collecting its command line, page text, or credentials.
            string detail = "source=" + source + "; pid=" + pid;
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    detail += "; processStartedUtc=" + process.StartTime.ToUniversalTime().ToString("o");
                    int parentId;
                    if (SnapshotParents().TryGetValue(pid, out parentId) && parentId > 0)
                    {
                        detail += "; parentPid=" + parentId;
                        using (var parent = Process.GetProcessById(parentId))
                            if (parent.StartTime <= process.StartTime)
                                detail += "; parentExecutable=" + Path.GetFileName(parent.MainModule.FileName);
                    }
                }
            }
            catch { detail += "; parentDetails=unavailable"; }
            RecordEvent("primary_process_observed", detail);
        }

        private void PrunePrimaryProcesses()
        {
            foreach (int pid in knownPids.ToArray())
            {
                bool exited = false;
                try
                {
                    using (var process = Process.GetProcessById(pid))
                    {
                        long started;
                        exited = process.HasExited || !knownProcessStarts.TryGetValue(pid, out started) ||
                            process.StartTime.ToUniversalTime().Ticks != started;
                    }
                }
                catch (ArgumentException) { exited = true; }
                catch (InvalidOperationException) { exited = true; }
                // A transient access failure is not evidence of an exit.
                catch { }
                if (!exited) continue;
                knownPids.Remove(pid); knownProcessStarts.Remove(pid);
                rejectedPids.Remove(pid); matchCache.Remove(pid);
            }
            cycle.ObserveDesktopProcesses(knownPids.Count != 0);
        }

        private bool IsPrimaryDesktopProcess(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    if (!WindowProbe.Matches((uint)pid, 0, exePath, appId)) return false;
                    string command = GetCommandLine(pid);
                    if (!string.IsNullOrWhiteSpace(command) && command.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                    if (IsDescendantOfDesktopProcess(pid)) return false;
                    return true;
                }
            }
            catch { return false; }
        }

        private bool IsDescendantOfDesktopProcess(int pid)
        {
            var parents = SnapshotParents();
            var seen = new HashSet<int>();
            for (int depth = 0; depth < 32; depth++)
            {
                int parent;
                if (!parents.TryGetValue(pid, out parent) || parent <= 0 || parent == pid || !seen.Add(parent)) return false;
                if (WindowProbe.Matches((uint)parent, 0, exePath, appId)) return true;
                pid = parent;
            }
            return false;
        }

        private static string GetCommandLine(int pid)
        {
            // A WMI query from a normal Explorer child can stall its window
            // event dispatcher for several seconds. This native, limited-access
            // query does not wait on the WMI provider or read process memory.
            IntPtr process = OpenProcess(0x1000, false, (uint)pid);
            if (process == IntPtr.Zero) return null;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int required;
                NtQueryInformationProcess(process, 60, IntPtr.Zero, 0, out required);
                int header = Marshal.SizeOf(typeof(NativeUnicodeString));
                if (required < header || required > 131072) return null;
                buffer = Marshal.AllocHGlobal(required);
                int returned;
                if (NtQueryInformationProcess(process, 60, buffer, required, out returned) < 0) return null;
                var text = (NativeUnicodeString)Marshal.PtrToStructure(buffer, typeof(NativeUnicodeString));
                long offset = text.Buffer.ToInt64() - buffer.ToInt64();
                if ((text.Length & 1) != 0 || text.Length > text.MaximumLength || offset < header || offset > required - text.Length) return null;
                return Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
            }
            catch { }
            finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); CloseHandle(process); }
            return null;
        }

        private void PollWindows()
        {
            if (processScanClock.ElapsedMilliseconds >= 1000)
            {
                processScanClock.Restart();
                matchCache.Clear();
                // A transient identity/read failure during process creation is
                // retried on the next scan instead of excluding that PID forever.
                rejectedPids.Clear();
                try
                {
                    PrunePrimaryProcesses();
                    var alive = new HashSet<int>();
                    foreach (var process in Process.GetProcesses())
                    using (process)
                    {
                        alive.Add(process.Id);
                        if (knownPids.Contains(process.Id)) continue;
                        try
                        {
                            string name = process.ProcessName + ".exe";
                            if (string.Equals(name, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrWhiteSpace(exePath) && string.Equals(name, Path.GetFileName(exePath), StringComparison.OrdinalIgnoreCase)))
                                ProcessCandidate(process.Id, "process_scan");
                        }
                        catch { }
                    }
                    rejectedPids.RemoveWhere(delegate(int id) { return !alive.Contains(id); });
                }
                catch (Exception error) { lastError = "Process scan failed: " + error.Message; }
            }
            IntPtr[] found;
            try { found = WindowProbe.List(exePath, appId, true); }
            catch (Exception error) { lastError = "Window scan failed: " + error.Message; return; }
            bool wasOpen = currentWindows.Length != 0;
            bool isOpen = found.Length != 0;
            if (wasOpen && !isOpen)
            {
                cycle.ObserveWindows(false);
                lastTrigger = knownPids.Count != 0 ? "main_windows_hidden_primary_alive" : "all_desktop_processes_exited";
            }
            if (!wasOpen && isOpen)
            {
                if (!cycle.Consumed && !diagnostic)
                {
                    int pid = 0;
                    try { uint owner; GetWindowThreadProcessId(found[0], out owner); pid = (int)owner; } catch { }
                    ProcessCandidate(pid, "main_window_created");
                }
                else cyclesSuppressed++;
            }
            cycle.ObserveWindows(isOpen);
            currentWindows = found;
            foreach (IntPtr foundWindow in found) knownWindowHandles.Add(foundWindow);
            if ((overlayProcess == null || SafeHasExited(overlayProcess)) && clock.ElapsedMilliseconds - lastGeometrySave >= 1000)
            {
                lastGeometrySave = clock.ElapsedMilliseconds;
                foreach (IntPtr window in found)
                    try { if (WindowProbe.IsReady(window)) geometry.CaptureNormal(window); }
                    catch (Exception error) { lastError = "Geometry cache failed: " + error.Message; }
            }
        }

        private void Trigger(string reason, int pid)
        {
            if (diagnostic || overlayProcess != null && !overlayProcess.HasExited || !cycle.TryTrigger())
            { cyclesSuppressed++; return; }
            lastTrigger = reason + "; pid=" + pid;
            RecordEvent("cycle_started", lastTrigger);
            string launcher = Path.Combine(root, "Start-Codex-Dragon.exe");
            try
            {
                if (!File.Exists(launcher)) throw new FileNotFoundException("找不到动画程序。", launcher);
                // Production observation respects the configured sound switch.
                // --startup-test is reserved for isolated checks and mutes audio.
                const string arguments = "--observe";
                overlayProcess = Process.Start(new ProcessStartInfo(launcher, arguments)
                { WorkingDirectory = root, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                cyclesStarted++;
                RecordEvent("overlay_started", "pid=" + overlayProcess.Id + "; arguments=" + arguments);
            }
            catch (Exception error) { lastError = "Could not start observe-only animation: " + error.Message; }
        }

        private void CheckOverlay()
        {
            if (overlayProcess == null) return;
            try
            {
                if (!overlayProcess.HasExited) return;
                overlayProcess.Dispose(); overlayProcess = null;
                RecordEvent("overlay_exited", "cycle remains consumed while the desktop primary process survives");
                // A timed-out or skipped overlay consumes the activation cycle.
                // Hidden windows and tray restoration never rearm this cycle.
            }
            catch { overlayProcess = null; }
        }

        private void ShowStatus()
        {
            SaveStatus();
            string detail = "监视模式：" + monitorMode + "\nCodex 主窗口数：" + currentWindows.Length +
                "\n动画会话：" + (overlayProcess != null && !SafeHasExited(overlayProcess) ? "运行中" : "空闲") +
                "\n最近事件：" + lastTrigger + "\n最近错误：" + lastError;
            MessageBox.Show(detail, "Codex 龙娘监视器状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Shutdown(string reason)
        {
            if (exiting) return;
            exiting = true;
            try { timer.Stop(); } catch { }
            try { if (processWatcher != null) processWatcher.Stop(); } catch { }
            try { if (processWatcher != null) processWatcher.Dispose(); } catch { }
            processWatcher = null;
            try { if (winEventHook != IntPtr.Zero) UnhookWinEvent(winEventHook); } catch { }
            winEventHook = IntPtr.Zero;
            try
            {
                if (overlayProcess != null && !overlayProcess.HasExited)
                    PostMessage(overlayProcess.MainWindowHandle, 0x0010, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
            try { tray.Visible = false; } catch { }
            lastTrigger = "stopped: " + reason;
            RecordEvent("watcher_stopped", reason);
            SaveStatus();
            try { Close(); } catch { }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!exiting) { e.Cancel = true; Hide(); return; }
            try { tray.Dispose(); } catch { }
            try { if (stopEvent != null) stopEvent.Dispose(); } catch { }
            try { if (mutex != null) { mutex.ReleaseMutex(); mutex.Dispose(); } } catch { }
        }

        private void SaveStatus()
        {
            var data = new Dictionary<string, object>();
            data["running"] = !exiting; data["monitorMode"] = monitorMode;
            data["pid"] = Process.GetCurrentProcess().Id;
            data["processStartedUtc"] = processStartedUtc;
            data["inJob"] = lifetime.InJob;
            data["jobLimitFlags"] = lifetime.JobLimitFlags.HasValue ? (object)lifetime.JobLimitFlags.Value : null;
            data["parentPid"] = lifetime.ParentPid;
            data["parentExecutable"] = lifetime.ParentExecutable;
            data["desktopParent"] = lifetime.DesktopParent;
            data["lifetimeIndependent"] = lifetime.Independent;
            data["updatedUtc"] = DateTime.UtcNow.ToString("o");
            data["wmiAvailable"] = wmiAvailable;
            data["winEventHook"] = winEventAvailable;
            data["diagnosticOnly"] = diagnosticOnly;
            data["activeOverlayPid"] = overlayProcess != null && !SafeHasExited(overlayProcess) ? (object)overlayProcess.Id : null;
            data["knownDesktopProcessCount"] = knownPids.Count;
            data["mainWindowCount"] = currentWindows.Length;
            data["cyclesStarted"] = cyclesStarted; data["cyclesSuppressed"] = cyclesSuppressed;
            data["processStartEvents"] = processEvents; data["windowEvents"] = windowEventsSeen;
            data["lastTrigger"] = lastTrigger; data["lastError"] = lastError; data["root"] = root;
            data["updatedSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 3);
            string path = reportOverride ?? Path.Combine(root, "output", "watcher-status.json");
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, ToJson(data), new UTF8Encoding(false)); }
            catch (Exception error) { lastError = "Status write failed: " + error.Message; }
        }

        private void RecordEvent(string name, string detail)
        {
            try
            {
                string path = Path.Combine(root, "output", "watcher-events.tsv");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, DateTime.UtcNow.ToString("o") + "\t" + name + "\t" +
                    (detail ?? "").Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine);
            }
            catch { }
        }

        public static int WriteExternalStatus(string root, string report)
        {
            string suffix = RootKey(Path.GetFullPath(root));
            bool mutexExists = false;
            try { using (Mutex.OpenExisting("Local\\CodexDragonWatcher_" + suffix)) mutexExists = true; }
            catch { }
            string liveReport = Path.Combine(root, "output", "watcher-status.json");
            var data = new Dictionary<string, object>();
            try
            {
                if (File.Exists(liveReport))
                {
                    var parsed = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(liveReport, Encoding.UTF8));
                    if (parsed != null) data = parsed;
                    else data["statusReadError"] = "Status report contains no object.";
                }
            }
            catch (Exception error) { data["statusReadError"] = error.Message; }
            bool processAlive = false, heartbeatFresh = false, reportedRunning = false;
            object value;
            if (data.TryGetValue("running", out value)) reportedRunning = value is bool && (bool)value;
            if (data.TryGetValue("pid", out value))
            {
                try
                {
                    using (var process = Process.GetProcessById(Convert.ToInt32(value)))
                    {
                        processAlive = !process.HasExited && string.Equals(process.MainModule.FileName,
                            Path.Combine(root, "Watch-Codex-Dragon.exe"), StringComparison.OrdinalIgnoreCase);
                        object startValue;
                        DateTime recordedStart;
                        if (processAlive && data.TryGetValue("processStartedUtc", out startValue))
                            processAlive = DateTime.TryParse(Convert.ToString(startValue), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out recordedStart) &&
                                Math.Abs((process.StartTime.ToUniversalTime() - recordedStart.ToUniversalTime()).TotalMilliseconds) < 20;
                    }
                }
                catch { processAlive = false; }
            }
            DateTime updated;
            if (data.TryGetValue("updatedUtc", out value) && DateTime.TryParse(Convert.ToString(value), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out updated))
            {
                double age = (DateTime.UtcNow - updated.ToUniversalTime()).TotalSeconds;
                heartbeatFresh = age >= -1 && age <= 6;
            }
            bool running = mutexExists && processAlive && heartbeatFresh && reportedRunning;
            data["running"] = running; data["mutexExists"] = mutexExists;
            data["processAlive"] = processAlive; data["heartbeatFresh"] = heartbeatFresh;
            data["checkedUtc"] = DateTime.UtcNow.ToString("o"); data["root"] = root;
            data["statusReason"] = running ? "live" : !mutexExists ? "watcher_mutex_missing" :
                !processAlive ? "watcher_process_missing" : !heartbeatFresh ? "heartbeat_expired" : "stopped";
            if (!data.ContainsKey("monitorMode")) data["monitorMode"] = "external_status";
            // Keep the last heartbeat as evidence; queries have their own file.
            string path = report == null ? Path.Combine(root, "output", "watcher-status-query.json") : Path.GetFullPath(Environment.ExpandEnvironmentVariables(report));
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, ToJson(data), new UTF8Encoding(false));
            return 0;
        }

        public static int SignalStop(string root)
        {
            string name = "Local\\CodexDragonWatcherStop_" + RootKey(Path.GetFullPath(root));
            try { using (var signal = EventWaitHandle.OpenExisting(name)) signal.Set(); return 0; }
            catch (WaitHandleCannotBeOpenedException) { return 0; }
        }

        public static int SelfTest(string root, string report)
        {
            var gate = new CycleGate();
            var checks = new List<string>();
            if (!gate.TryTrigger()) throw new InvalidOperationException("fresh cycle did not trigger"); checks.Add("cold_start_once");
            if (gate.TryTrigger()) throw new InvalidOperationException("duplicate trigger was accepted"); checks.Add("duplicate_suppressed");
            gate.ObserveWindows(true);
            if (gate.TryTrigger()) throw new InvalidOperationException("active window retriggered"); checks.Add("active_window_suppressed");
            gate.ObserveWindows(false);
            if (gate.TryTrigger()) throw new InvalidOperationException("hidden-window cycle rearmed"); checks.Add("tray_hidden_window_suppressed");
            gate.PrimaryActivation(true, false);
            if (gate.TryTrigger()) throw new InvalidOperationException("surviving primary activation replayed"); checks.Add("surviving_primary_activation_suppressed");
            gate.ObserveDesktopProcesses(false);
            if (!gate.TryTrigger()) throw new InvalidOperationException("exited-primary cycle did not rearm"); checks.Add("exited_primary_rearmed");
            var baseline = new CycleGate(); baseline.Initialize(true);
            if (baseline.TryTrigger()) throw new InvalidOperationException("existing baseline replayed"); checks.Add("existing_baseline_suppressed");
            baseline.ObserveWindows(false); baseline.ObserveDesktopProcesses(true);
            if (baseline.TryTrigger()) throw new InvalidOperationException("hidden baseline replayed"); checks.Add("hidden_process_baseline_suppressed");
            var attempt = new CycleGate(); attempt.TryTrigger(); attempt.PrimaryActivation(false, true);
            if (attempt.TryTrigger()) throw new InvalidOperationException("live overlay duplicate was accepted"); checks.Add("active_overlay_suppressed");
            attempt.PrimaryActivation(false, false);
            if (!attempt.TryTrigger()) throw new InvalidOperationException("fresh primary activation did not rearm"); checks.Add("new_attempt_rearmed");
            var data = new Dictionary<string, object>(); data["passed"] = true; data["checks"] = checks; data["root"] = root;
            string path = report == null ? Path.Combine(root, "output", "watcher-self-test.json") : Path.GetFullPath(Environment.ExpandEnvironmentVariables(report));
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, ToJson(data), new UTF8Encoding(false)); return 0;
        }

        public static void WriteFailure(string root, Exception error, bool replaceStatus = true)
        {
            try
            {
                string path = Path.Combine(root, "output", "watcher-status.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(Path.Combine(root, "output", "watcher-error.txt"), error.ToString(), new UTF8Encoding(false));
                if (!replaceStatus) return;
                var data = new Dictionary<string, object>(); data["running"] = false; data["lastError"] = error.ToString(); data["root"] = root;
                File.WriteAllText(path, ToJson(data), new UTF8Encoding(false));
            }
            catch { }
        }

        private static bool SafeHasExited(Process process) { try { return process.HasExited; } catch { return true; } }
        private static string ToJson(object value)
        {
            if (value == null) return "null";
            if (value is bool) return ((bool)value) ? "true" : "false";
            if (value is int || value is uint || value is long || value is ulong || value is double || value is float || value is decimal)
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            if (value is IDictionary<string, object>)
            {
                var parts = new List<string>();
                foreach (var pair in (IDictionary<string, object>)value) parts.Add(JsonString(pair.Key) + ":" + ToJson(pair.Value));
                return "{" + string.Join(",", parts.ToArray()) + "}";
            }
            if (value is IEnumerable<string>) return "[" + string.Join(",", ((IEnumerable<string>)value).Select(JsonString).ToArray()) + "]";
            return JsonString(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
        }
        private static string JsonString(string value)
        {
            if (value == null) return "null";
            var b = new StringBuilder("\"");
            foreach (char c in value)
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c == '\n') b.Append("\\n"); else if (c == '\r') b.Append("\\r"); else if (c == '\t') b.Append("\\t");
                else if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4")); else b.Append(c);
            }
            return b.Append('"').ToString();
        }
        private static string RootKey(string path)
        {
            using (var md5 = MD5.Create())
            {
                string normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16);
            }
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            public uint Size, Usage, ProcessId;
            public IntPtr DefaultHeap;
            public uint ModuleId, Threads, ParentProcessId;
            public int BasePriority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeUnicodeString
        {
            public ushort Length, MaximumLength;
            public IntPtr Buffer;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint pid);
        [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int informationClass,
            IntPtr information, int length, out int returned);
        private static Dictionary<int, int> SnapshotParents()
        {
            var parents = new Dictionary<int, int>();
            IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot == new IntPtr(-1)) return parents;
            try
            {
                var entry = new ProcessEntry(); entry.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry));
                if (!Process32FirstW(snapshot, ref entry)) return parents;
                do { parents[(int)entry.ProcessId] = (int)entry.ParentProcessId; } while (Process32NextW(snapshot, ref entry));
            }
            finally { CloseHandle(snapshot); }
            return parents;
        }
        private static bool IsTopLevelWindow(IntPtr hwnd)
        {
            return hwnd != IntPtr.Zero && GetAncestor(hwnd, 2) == hwnd && GetWindow(hwnd, 4) == IntPtr.Zero &&
                (GetWindowLong(hwnd, -20) & 0x80) == 0 && (GetWindowLong(hwnd, -16) & 0x40000000) == 0;
        }

        // Small state machine kept separately so edge transitions can be tested
        // without WMI, hooks, a desktop application, or UI.
        private sealed class CycleGate
        {
            private bool consumed;
            public bool Consumed { get { return consumed; } }
            public void Initialize(bool present) { consumed = present; }
            public bool TryTrigger() { if (consumed) return false; consumed = true; return true; }
            public void PrimaryActivation(bool existingPrimary, bool liveOverlay)
            {
                consumed = existingPrimary || liveOverlay;
            }
            public void ObserveDesktopProcesses(bool present) { if (!present) consumed = false; }
            public void ObserveWindows(bool present)
            {
                if (present) consumed = true;
            }
        }
    }
}
