using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;

namespace DragonLauncher
{
    // Owns one activation request independently of the animation window's lifetime.
    public sealed class StartupCoordinator
    {
        private readonly Application application;
        private readonly Settings settings;
        private readonly bool quiet;
        private readonly bool observe;
        private readonly StartupGeometry geometry;
        private readonly PageReadinessClient pageProbe;
        private readonly string logPath;
        private readonly object gate = new object();
        private readonly Stopwatch clock = new Stopwatch();
        private readonly DispatcherTimer timer;
        private DispatcherTimer shutdownTimer;
        private bool shutdownRequested, shutdownCommitted;
        private int shutdownExitCode;
        private Window overlay;
        private bool started, dismissed, finished, activationFinished, overlayClosed;
        private volatile bool stopProbe;
        private string targetPath;
        private Exception activationError;
        private IntPtr readyWindow;
        private double windowSince = -1;
        private double probeUpdated = -1;
        private bool pageReadyDeferred;

        public StartupCoordinator(Application app, Settings config, string root, bool quietErrors, bool observeOnly)
        {
            application = app; settings = config; quiet = quietErrors; observe = observeOnly;
            geometry = new StartupGeometry(root);
            pageProbe = new PageReadinessClient(root);
            logPath = Path.Combine(root, "output", "startup-timing.log");
            if (!string.IsNullOrWhiteSpace(settings.Exe))
                targetPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.Exe));
            timer = new DispatcherTimer(DispatcherPriority.Background, app.Dispatcher);
            timer.Interval = TimeSpan.FromMilliseconds(100);
            timer.Tick += OnTick;
        }

        public void Attach(Window window)
        {
            overlay = window;
            geometry.ApplyOverlay(window);
            window.SourceInitialized += delegate { Start(); };
            window.Closed += delegate {
                overlayClosed = true;
                Dismiss("window_closed");
                if (shutdownRequested) ShutdownNow();
            };
        }

        public void Start()
        {
            if (started) return;
            started = true; clock.Start();
            try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)); File.WriteAllText(logPath, "seconds\tevent\tdetail\n"); } catch { }
            Record("startup_started", "utc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            timer.Start();
            if (observe)
            {
                activationFinished = true;
                Record("observation_started", "passive mode; activation requests=0");
            }
            else
            {
                Record("activation_requested", "one request; animation runs concurrently");
                var activation = new Thread(Activate);
                activation.IsBackground = true;
                activation.SetApartmentState(ApartmentState.STA);
                activation.Start();
            }
            var probe = new Thread(Probe);
            probe.IsBackground = true;
            application.Exit += delegate { stopProbe = true; pageProbe.Dispose(); };
            probe.Start();
        }

        private void Activate()
        {
            try
            {
                uint pid = AppActivation.Open(settings);
                string path = targetPath;
                if (path == null && pid != 0)
                {
                    try { using (var process = Process.GetProcessById((int)pid)) path = process.MainModule.FileName; } catch { }
                }
                lock (gate) { targetPath = path; }
                Record("activation_accepted", "pid=" + pid);
            }
            catch (Exception error)
            {
                lock (gate) { activationError = error; }
                Record("activation_failed", error.GetType().Name + ": " + error.Message);
            }
            finally { lock (gate) { activationFinished = true; } }
        }

        private void Probe()
        {
            IntPtr previous = IntPtr.Zero;
            IntPtr observed = IntPtr.Zero;
            bool previousContentReady = false;
            string previousState = null;
            double lastRecord = -10;
            double since = -1;
            while (!stopProbe)
            {
                string path;
                lock (gate) { path = targetPath; }
                var windows = WindowProbe.List(path, string.IsNullOrWhiteSpace(settings.Exe) ? settings.AppId : null, false);
                IntPtr found = IntPtr.Zero;
                foreach (var candidate in windows)
                {
                    // Responding HWNDs can participate in startup geometry handoff.
                    // Readiness starts only after the normal frame has reached the overlay bounds.
                    if (!WindowProbe.IsReady(candidate)) continue;
                    if (geometry.AlignTarget(candidate)) { found = candidate; break; }
                }
                if (found != IntPtr.Zero && found != observed)
                {
                    observed = found;
                    Record("window_observed", "hwnd=" + found.ToInt64() + "; native frame aligned; checking page content");
                }
                // Publish pending before any provider work. A delayed/blocked
                // provider may never leave a stale success eligible for handoff.
                lock (gate) { probeUpdated = -1; }
                PageProbeResult content = found == IntPtr.Zero ? new PageProbeResult(false, "no_window") :
                    settings.RequirePageContent ? pageProbe.Check(found) : new PageProbeResult(true, "content_check_disabled");
                if (stopProbe) break;
                double now = clock.Elapsed.TotalSeconds;
                bool contentReady = content.Ready && found != IntPtr.Zero && WindowProbe.IsReady(found);
                if (found != previous) { since = -1; previousContentReady = false; }
                if (!contentReady) since = -1;
                else if (since < 0)
                {
                    since = now;
                    Record("page_content_ready", content.Detail);
                }
                if (!contentReady && previousContentReady) Record("page_content_lost", content.Detail);
                string state = contentReady ? "ready" : content.Detail.Split(';')[0];
                if (state != previousState || now - lastRecord >= 2)
                { Record("page_probe", content.Detail); previousState = state; lastRecord = now; }
                previousContentReady = contentReady;
                previous = found;
                lock (gate) { readyWindow = found; windowSince = since; probeUpdated = now; }
                Thread.Sleep(settings.RequirePageContent ? 150 : 100);
            }
        }

        private void OnTick(object sender, EventArgs args)
        {
            if (finished) return;
            bool accepted; Exception error; IntPtr window; double since; double updated;
            lock (gate)
            {
                accepted = activationFinished; error = activationError;
                window = readyWindow; since = windowSince;
                updated = probeUpdated;
            }
            if (accepted && error != null)
            {
                Complete("activation_error", 1);
                if (!quiet)
                    MessageBox.Show("无法打开 Codex。请检查 launcher.config.xml 中的应用标识或路径。\n\n" + error.Message,
                        "GPT 龙娘启动器", MessageBoxButton.OK, MessageBoxImage.Error);
                RequestShutdown(1);
                return;
            }
            if (dismissed && accepted)
            {
                Complete("dismissed", 0); RequestShutdown(0); return;
            }
            var currentPresentation = overlay as IStartupPresentation;
            bool introComplete = currentPresentation == null || currentPresentation.IntroComplete;
            if (window != IntPtr.Zero && since >= 0 && updated >= 0 && clock.Elapsed.TotalSeconds - updated < 0.45 &&
                clock.Elapsed.TotalSeconds - since >= settings.WindowReadyDelay && WindowProbe.IsReady(window))
            {
                if (!introComplete)
                {
                    if (!pageReadyDeferred)
                    { pageReadyDeferred = true; Record("page_ready_deferred", "page is stable; keep playing complete intro and cue"); }
                }
                else
                {
                    if (!dismissed)
                    {
                        Dismiss("window_ready");
                        if (!observe) WindowProbe.BringForward(window);
                    }
                    if (accepted) { Complete("window_ready", 0); RequestShutdown(0); return; }
                }
            }
            if (clock.Elapsed.TotalSeconds >= settings.StartupTimeout)
            {
                Complete("window_timeout", 2);
                RequestShutdown(2);
            }
        }

        public void Dismiss(string reason)
        {
            if (dismissed || finished) return;
            dismissed = true;
            Record("overlay_dismissed", reason);
            var presentation = overlay as IStartupPresentation;
            if (reason == "window_ready" && presentation != null) presentation.RequestReadyFade();
            // Close releases MediaElement and rendering callbacks. Explicit shutdown keeps
            // the dispatcher alive for an already issued activation request if necessary.
            if (overlay != null && !overlayClosed) overlay.Close();
            if (readyWindow != IntPtr.Zero) geometry.CaptureNormal(readyWindow);
        }

        private void Complete(string reason, int exitCode)
        {
            if (finished) return;
            finished = true; stopProbe = true;
            timer.Stop(); timer.Tick -= OnTick;
            pageProbe.Dispose();
            if (overlay != null && !overlayClosed) overlay.Close();
            if (readyWindow != IntPtr.Zero) geometry.CaptureNormal(readyWindow);
            Record("completed", reason + "; exit=" + exitCode);
        }

        private void RequestShutdown(int exitCode)
        {
            if (shutdownRequested || shutdownCommitted) return;
            shutdownRequested = true;
            shutdownExitCode = exitCode;
            if (overlay == null || overlayClosed) { ShutdownNow(); return; }
            // A playing cue can defer Window.Close for its 100 ms fade. Keep
            // that dispatcher alive briefly, with an independent hard bound.
            shutdownTimer = new DispatcherTimer(DispatcherPriority.Normal, application.Dispatcher);
            var presentation = overlay as IStartupPresentation;
            int deadline = presentation == null ? 250 : presentation.CloseDeadlineMilliseconds;
            shutdownTimer.Interval = TimeSpan.FromMilliseconds(deadline);
            shutdownTimer.Tick += OnShutdownDeadline;
            shutdownTimer.Start();
            Record("shutdown_waiting", "waiting for overlay close; maximum " + deadline + "ms");
        }

        private void OnShutdownDeadline(object sender, EventArgs args)
        {
            Record("shutdown_deadline", "bounded close deadline reached");
            ShutdownNow();
        }

        private void ShutdownNow()
        {
            if (shutdownCommitted) return;
            shutdownCommitted = true;
            if (shutdownTimer != null)
            {
                shutdownTimer.Stop();
                shutdownTimer.Tick -= OnShutdownDeadline;
                shutdownTimer = null;
            }
            Record("shutdown", "exit=" + shutdownExitCode + "; overlay_closed=" + overlayClosed);
            application.Shutdown(shutdownExitCode);
        }

        public void Record(string eventName, string detail)
        {
            lock (gate)
            {
                try
                {
                    File.AppendAllText(logPath, clock.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) + "\t" +
                        eventName + "\t" + (detail ?? "").Replace('\r', ' ').Replace('\n', ' ') + "\n");
                }
                catch { }
            }
        }
    }

    public static class WindowProbe
    {
        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rectangle);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder id);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint length);

        public static string ProcessPath(uint pid)
        {
            try
            {
                using (var process = Process.GetProcessById((int)pid))
                {
                    var path = new StringBuilder(32768); uint length = (uint)path.Capacity;
                    return QueryFullProcessImageName(process.Handle, 0, path, ref length) ? path.ToString() : null;
                }
            }
            catch { return null; }
        }

        public static IntPtr[] List(string executablePath, string appId, bool includeMinimized)
        {
            var matches = new List<IntPtr>();
            EnumWindows(delegate(IntPtr window, IntPtr unused)
            {
                bool minimized = IsIconic(window);
                if (!IsWindowVisible(window) || (!includeMinimized && minimized) || GetWindow(window, 4) != IntPtr.Zero ||
                    (GetWindowLong(window, -20) & 0x80) != 0) return true;
                Rect client;
                // Minimized windows can have a zero-sized client rect and be DWM cloaked;
                // they still belong to the existing startup cycle.
                if (!minimized && (!GetClientRect(window, out client) || client.Right - client.Left < 300 || client.Bottom - client.Top < 200)) return true;
                int cloaked;
                if (!minimized && DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                uint pid; GetWindowThreadProcessId(window, out pid);
                if (Matches(pid, 0, executablePath, appId)) matches.Add(window);
                return true;
            }, IntPtr.Zero);
            return matches.ToArray();
        }

        public static bool IsReady(IntPtr window)
        {
            if (!IsWindowVisible(window) || IsIconic(window)) return false;
            IntPtr result;
            return SendMessageTimeout(window, 0, IntPtr.Zero, IntPtr.Zero, 0x22, 100, out result) != IntPtr.Zero;
        }

        public static IntPtr Find(uint activationPid, string executablePath, string appId)
        {
            foreach (var window in List(executablePath, appId, false))
                if (IsReady(window)) return window;
            return IntPtr.Zero;
        }

        public static bool Matches(uint pid, uint activationPid, string executablePath, string appId)
        {
            if (pid == 0 || pid == (uint)Process.GetCurrentProcess().Id) return false;
            // The activated Electron process may forward to an existing instance or child.
            try
            {
                using (var process = Process.GetProcessById((int)pid))
                {
                    if (!string.IsNullOrWhiteSpace(executablePath))
                    {
                        return string.Equals(ProcessPath(pid), executablePath, StringComparison.OrdinalIgnoreCase);
                    }
                    if (string.IsNullOrWhiteSpace(appId)) return false;
                    // Packaged CLI/helper processes can inherit identity. Only the desktop
                    // app executable under this package family's WindowsApps directory qualifies.
                    string image = ProcessPath(pid);
                    int separator = appId.IndexOf('!');
                    string family = separator >= 0 ? appId.Substring(0, separator) : appId;
                    int familySeparator = family.LastIndexOf('_');
                    if (image == null || familySeparator <= 0 ||
                        image.IndexOf("\\WindowsApps\\" + family.Substring(0, familySeparator) + "_", StringComparison.OrdinalIgnoreCase) < 0 ||
                        image.IndexOf("__" + family.Substring(familySeparator + 1) + "\\", StringComparison.OrdinalIgnoreCase) < 0 ||
                        !image.EndsWith("\\app\\ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) return false;
                    uint length = 0;
                    if (GetApplicationUserModelId(process.Handle, ref length, null) != 122 || length == 0 || length > 1024) return false;
                    var id = new StringBuilder((int)length);
                    return GetApplicationUserModelId(process.Handle, ref length, id) == 0 &&
                        string.Equals(id.ToString(), appId, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        public static void BringForward(IntPtr window)
        {
            ShowWindowAsync(window, 9);
            SetForegroundWindow(window);
        }
    }
}
