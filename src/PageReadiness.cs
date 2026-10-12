using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace DragonLauncher
{
    public sealed class PageProbeResult
    {
        public bool Ready;
        public string Detail;
        public PageProbeResult(bool ready, string detail) { Ready = ready; Detail = detail; }
    }

    // Native rendering and accessibility providers may block. Run them in one
    // disposable child process, with a deadline and no activation/media code.
    public sealed class PageReadinessClient : IDisposable
    {
        private readonly object gate = new object();
        private readonly string executable;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Process worker;
        private bool disposed;
        public PageReadinessClient(string root) { executable = Path.Combine(root, "Start-Codex-Dragon.exe"); }

        public PageProbeResult Check(IntPtr window)
        {
            lock (gate)
            {
                if (disposed) return new PageProbeResult(false, "probe_stopped");
                try
                {
                    if (worker == null || worker.HasExited)
                    {
                        StopWorker();
                        worker = Process.Start(new ProcessStartInfo(executable, "--page-probe-server " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture))
                        {
                            UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardInput = true, RedirectStandardOutput = true,
                            WorkingDirectory = Path.GetDirectoryName(executable)
                        });
                    }
                    worker.StandardInput.WriteLine(window.ToInt64().ToString(CultureInfo.InvariantCulture));
                    worker.StandardInput.Flush();
                    Task<string> answer = worker.StandardOutput.ReadLineAsync();
                    // An isolated provider cannot consume the overall 40 s budget
                    // or freeze Esc/Close on the overlay dispatcher.
                    if (!answer.Wait(1800, cancellation.Token))
                    {
                        StopWorker();
                        return new PageProbeResult(false, "probe_timeout");
                    }
                    string line = answer.Result;
                    if (string.IsNullOrEmpty(line) || (!line.StartsWith("0\t") && !line.StartsWith("1\t")))
                    {
                        StopWorker(); return new PageProbeResult(false, "probe_invalid_result");
                    }
                    return new PageProbeResult(line[0] == '1', line.Substring(2));
                }
                catch (Exception error)
                {
                    StopWorker(); return new PageProbeResult(false, "probe_error=" + error.GetType().Name);
                }
            }
        }

        private void StopWorker()
        {
            if (worker == null) return;
            try { if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(500); } } catch { }
            worker.Dispose(); worker = null;
        }
        public void Dispose()
        {
            // Wake an IPC wait before taking its lock, so the overlay's Close/Esc
            // does not wait for an unresponsive accessibility/render provider.
            cancellation.Cancel();
            lock (gate) { disposed = true; StopWorker(); }
        }
    }

    public static class PageReadiness
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

        public static int RunServer(string root, Settings settings, int parentPid)
        {
            // EOF normally releases this process. Also watch the parent handle
            // so even a blocked native provider exits after a parent crash/kill.
            using (var parent = Process.GetProcessById(parentPid))
            using (var lifetime = new System.Threading.Timer(delegate
            {
                try { if (parent.HasExited) Environment.Exit(0); }
                catch { Environment.Exit(0); }
            }, null, 500, 500))
            {
                return Serve(settings);
            }
        }

        private static int Serve(Settings settings)
        {
            string path = string.IsNullOrWhiteSpace(settings.Exe) ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.Exe));
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                long value;
                if (!long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return 1;
                var hwnd = new IntPtr(value);
                PageProbeResult result;
                try
                {
                    bool matched = Array.IndexOf(WindowProbe.List(path, path == null ? settings.AppId : null, false), hwnd) >= 0;
                    result = matched && WindowProbe.IsReady(hwnd) ? Check(hwnd) : new PageProbeResult(false, "target_unavailable");
                }
                catch (Exception error) { result = new PageProbeResult(false, "provider_error=" + error.GetType().Name); }
                Console.WriteLine((result.Ready ? "1\t" : "0\t") + result.Detail);
                Console.Out.Flush();
            }
            return 0;
        }

        public static PageProbeResult Check(IntPtr hwnd)
        {
            NativeRect client;
            if (!GetClientRect(hwnd, out client)) return new PageProbeResult(false, "client_unavailable");
            int width = client.Right - client.Left, height = client.Bottom - client.Top;
            if (width < 300 || height < 200 || width > 8192 || height > 8192 || (long)width * height > 24000000)
                return new PageProbeResult(false, "client_size_unavailable");
            uint dpi = 96;
            try { dpi = GetDpiForWindow(hwnd); } catch (EntryPointNotFoundException) { }
            if (dpi == 0) dpi = 96;
            // Ignore custom/native title bars and their three window buttons.
            int top = Math.Min(height / 5, Math.Max((int)(48 * dpi / 96), height / 20));
            Rectangle crop = new Rectangle(Math.Max(1, width / 100), top,
                width - Math.Max(1, width / 100) * 2, height - top - Math.Max(1, height / 100));
            PageProbeResult visual = CaptureAndAnalyze(hwnd, width, height, crop);
            if (visual.Ready) return visual;
            // Some Chromium/GPU configurations do not implement PrintWindow.
            // A strong accessible control layout is a second independent signal.
            // Counts/rectangles only: never request Name, Value, or text patterns.
            PageProbeResult structure = CheckStructure(hwnd, crop);
            if (structure.Ready) return structure;
            return new PageProbeResult(false, visual.Detail + "; " + structure.Detail);
        }

        private static PageProbeResult CaptureAndAnalyze(IntPtr hwnd, int width, int height, Rectangle crop)
        {
            try
            {
                using (var image = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (var graphics = Graphics.FromImage(image))
                    {
                        graphics.Clear(Color.Black);
                        IntPtr dc = graphics.GetHdc();
                        bool copied;
                        try { copied = PrintWindow(hwnd, dc, 3); } // CLIENTONLY | RENDERFULLCONTENT; works under the overlay.
                        finally { graphics.ReleaseHdc(dc); }
                        if (!copied) return new PageProbeResult(false, "capture_unavailable");
                    }
                    return Analyze(image, crop);
                }
            }
            catch (Exception error) { return new PageProbeResult(false, "capture_error=" + error.GetType().Name); }
        }

        public static PageProbeResult Analyze(Bitmap image, Rectangle crop)
        {
            using (var sample = new Bitmap(256, 144, PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(sample))
                {
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    graphics.DrawImage(image, new Rectangle(0, 0, 256, 144), crop, GraphicsUnit.Pixel);
                }
                int[,] tiles = new int[16, 9];
                int edges = 0;
                BitmapData data = sample.LockBits(new Rectangle(0, 0, 256, 144), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    var bytes = new byte[data.Stride * data.Height];
                    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                    for (int y = 1; y < 143; y++)
                        for (int x = 1; x < 255; x++)
                        {
                            int i = y * data.Stride + x * 3;
                            int horizontal = 0, vertical = 0;
                            for (int c = 0; c < 3; c++)
                            {
                                horizontal += Math.Abs(bytes[i + c] - bytes[i + 3 + c]);
                                vertical += Math.Abs(bytes[i + c] - bytes[i + data.Stride + c]);
                            }
                            if (Math.Max(horizontal, vertical) >= 72) { tiles[x / 16, y / 16]++; edges++; }
                        }
                }
                finally { sample.UnlockBits(data); }
                int active = 0, rows = 0, columns = 0, left = 0, middle = 0, right = 0;
                for (int y = 0; y < 9; y++)
                {
                    bool occupied = false;
                    for (int x = 0; x < 16; x++)
                        if (tiles[x, y] >= 4)
                        {
                            occupied = true; active++;
                            if (x < 5) left++; else if (x < 11) middle++; else right++;
                        }
                    if (occupied) rows++;
                }
                for (int x = 0; x < 16; x++)
                {
                    for (int y = 0; y < 9; y++) if (tiles[x, y] >= 4) { columns++; break; }
                }
                // Flat light/dark gradients and a compact spinner have too few
                // high-contrast cells. Require distributed actual UI detail.
                bool ready = edges >= 160 && active >= 12 && rows >= 3 && columns >= 5;
                return new PageProbeResult(ready, string.Format(CultureInfo.InvariantCulture,
                    "visual={0}; edges={1}; tiles={2}; rows={3}; columns={4}; regions={5},{6},{7}",
                    ready ? "content" : "blank_or_loading", edges, active, rows, columns, left, middle, right));
            }
        }

        private static PageProbeResult CheckStructure(IntPtr hwnd, Rectangle crop)
        {
            try
            {
                var origin = new NativePoint();
                if (!ClientToScreen(hwnd, ref origin)) return new PageProbeResult(false, "structure_unavailable");
                var body = new System.Windows.Rect(origin.X + crop.X, origin.Y + crop.Y, crop.Width, crop.Height);
                AutomationElement element = AutomationElement.FromHandle(hwnd);
                var request = new CacheRequest();
                request.Add(AutomationElement.ControlTypeProperty);
                request.Add(AutomationElement.IsOffscreenProperty);
                request.Add(AutomationElement.BoundingRectangleProperty);
                request.TreeScope = TreeScope.Element;
                int buttons = 0, editors = 0, links = 0, navigation = 0, text = 0;
                int left = 0, right = 0;
                using (request.Activate())
                {
                    Condition controls = new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tree),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                    AutomationElementCollection found = element.FindAll(TreeScope.Descendants, controls);
                    for (int i = 0; i < Math.Min(found.Count, 256); i++)
                    {
                        var info = found[i].Cached;
                        var area = info.BoundingRectangle;
                        if (info.IsOffscreen || area.IsEmpty || area.Width < 2 || area.Height < 2 || !area.IntersectsWith(body)) continue;
                        if (info.ControlType == ControlType.Button) buttons++;
                        else if (info.ControlType == ControlType.Edit) editors++;
                        else if (info.ControlType == ControlType.Hyperlink) links++;
                        else if (info.ControlType == ControlType.Text) text++;
                        else navigation++;
                        if (area.X + area.Width / 2 < body.X + body.Width * 0.35) left++; else right++;
                    }
                }
                bool ready = (editors >= 1 && buttons >= 3 && left >= 2 && right >= 2) ||
                    (buttons + links >= 6 && text >= 3 && left >= 2 && right >= 2 && navigation >= 1);
                return new PageProbeResult(ready, string.Format(CultureInfo.InvariantCulture,
                    "structure={0}; buttons={1}; editors={2}; links={3}; navigation={4}; textNodes={5}; regions={6},{7}",
                    ready ? "interactive" : "insufficient", buttons, editors, links, navigation, text, left, right));
            }
            catch (Exception error) { return new PageProbeResult(false, "structure_error=" + error.GetType().Name); }
        }

        public static int VerifyImage(string path, string report)
        {
            using (var image = new Bitmap(path))
            {
                int top = Math.Max(image.Height / 20, Math.Min(80, image.Height / 5));
                var result = Analyze(image, new Rectangle(image.Width / 100, top, image.Width - image.Width / 50, image.Height - top - image.Height / 100));
                File.WriteAllText(report, (result.Ready ? "1\t" : "0\t") + result.Detail);
                return 0;
            }
        }
    }
}
