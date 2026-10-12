using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Xml.Linq;

namespace DragonLauncher
{
    // These rectangles are physical screen pixels, never WPF device-independent units.
    public struct PixelRect
    {
        public int X, Y, Width, Height;
        public PixelRect(int x, int y, int width, int height)
        { X = x; Y = y; Width = width; Height = height; }
        public override string ToString()
        { return X + "," + Y + "," + Width + "," + Height; }
    }

    // A frozen geometry snapshot belongs to one startup. Only CaptureNormal persists
    // later user changes; startup alignment is never a permanent window constraint.
    public sealed class StartupGeometry
    {
        private readonly string statePath, logPath;
        private readonly object stateGate = new object();
        private readonly HashSet<IntPtr> alignedWindows = new HashSet<IntPtr>();
        private readonly HashSet<IntPtr> placementAttempted = new HashSet<IntPtr>();
        private PixelRect target, saved;
        private string savedMonitor = "";
        private int leftInset, topInset, rightInset, bottomInset;
        private bool haveSaved;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
            public NativeRect(PixelRect value)
            { Left = value.X; Top = value.Y; Right = value.X + value.Width; Bottom = value.Y + value.Height; }
            public PixelRect Pixels()
            { return new PixelRect(Left, Top, Right - Left, Bottom - Top); }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPlacement
        {
            public int Length, Flags, ShowCommand;
            public Point MinimumPosition, MaximumPosition;
            public NativeRect NormalPosition;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor, Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }
        private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect rectangle, IntPtr data);
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("shcore.dll")] private static extern int SetProcessDpiAwareness(int awareness);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rectangle, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int kind, out uint x, out uint y);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out NativeRect rectangle, int size);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);

        public StartupGeometry(string root)
        {
            statePath = Path.Combine(root, "output", "window-geometry.xml");
            logPath = Path.Combine(root, "output", "geometry.log");
            // Last known user-approved baseline. No Codex state or credentials are read.
            saved = new PixelRect(187, 164, 1291, 830);
            ReadState();
            try
            {
                MonitorInfo info = MonitorFor(saved, savedMonitor);
                target = Normalize(saved, info.Work.Pixels());
            }
            catch (Exception error)
            {
                target = Normalize(saved, new PixelRect(0, 0, 1920, 1080));
                Record("monitor_fallback", error.Message);
            }
        }

        public PixelRect TargetBounds { get { return target; } }

        // Call before constructing WPF Application or reading SystemParameters.
        public static void EnableDpiAwareness()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            try { if (SetProcessDpiAwareness(2) == 0) return; }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            try { SetProcessDPIAware(); }
            catch (EntryPointNotFoundException) { }
        }

        // Round independently to physical pixels, keeping the aspect error below one
        // pixel while fitting even unusually small or negatively positioned monitors.
        public static PixelRect Normalize(PixelRect source, PixelRect workArea)
        {
            if (workArea.Width <= 0 || workArea.Height <= 0)
                throw new ArgumentOutOfRangeException("workArea", "The monitor work area must have positive dimensions.");
            int height = source.Height > 0 ? source.Height : 830;
            height = Math.Min(height, workArea.Height);
            height = Math.Min(height, Math.Max(1, (int)Math.Floor(workArea.Width * 9.0 / 16.0)));
            int width = Math.Min(workArea.Width, Math.Max(1, (int)Math.Round(height * 16.0 / 9.0)));
            double centerX = source.X + source.Width / 2.0;
            double centerY = source.Y + source.Height / 2.0;
            long x = (long)Math.Round(centerX - width / 2.0);
            long y = (long)Math.Round(centerY - height / 2.0);
            x = Math.Max(workArea.X, Math.Min(x, (long)workArea.X + workArea.Width - width));
            y = Math.Max(workArea.Y, Math.Min(y, (long)workArea.Y + workArea.Height - height));
            return new PixelRect((int)x, (int)y, width, height);
        }

        public void ApplyOverlay(Window window)
        {
            if (window == null) throw new ArgumentNullException("window");
            try
            {
                uint dpiX = 96, dpiY = 96;
                NativeRect native = new NativeRect(target);
                IntPtr monitor = MonitorFromRect(ref native, 2);
                try
                {
                    uint x, y;
                    if (GetDpiForMonitor(monitor, 0, out x, out y) == 0 && x > 0 && y > 0)
                    { dpiX = x; dpiY = y; }
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = target.X * 96.0 / dpiX; window.Top = target.Y * 96.0 / dpiY;
                window.Width = target.Width * 96.0 / dpiX; window.Height = target.Height * 96.0 / dpiY;
                Action place = delegate
                {
                    try
                    {
                        IntPtr handle = new WindowInteropHelper(window).Handle;
                        if (handle != IntPtr.Zero) PlaceVisible(handle, target, false);
                    }
                    catch (Exception error) { Record("overlay_alignment_failed", error.Message); }
                };
                window.SourceInitialized += delegate { place(); };
                // WPF can perform one final placement while realizing its first frame.
                window.ContentRendered += delegate { place(); };
                if (new WindowInteropHelper(window).Handle != IntPtr.Zero) place();
            }
            catch (Exception error) { Record("overlay_geometry_failed", error.Message); }
        }

        // Return false until an asynchronous restore/move has actually taken effect.
        // The coordinator should retry before starting its 0.5 s readiness interval.
        public bool AlignTarget(IntPtr window)
        {
            try
            {
                if (window == IntPtr.Zero || !IsWindow(window)) return false;
                // Once the startup handoff has succeeded, subsequent readiness probes
                // must not undo the user's immediate resize, move, or maximize action.
                if (alignedWindows.Contains(window)) return true;
                if (IsIconic(window) || IsZoomed(window))
                { ShowWindowAsync(window, 9); return false; }
                PixelRect visible;
                if (!VisibleBounds(window, out visible)) return false;
                if (Matches(visible, target)) { alignedWindows.Add(window); return true; }
                // SetWindowPos is asynchronous so the target's UI thread never blocks
                // startup. Issue it once per HWND and observe completion on later probes.
                if (placementAttempted.Contains(window)) return false;
                placementAttempted.Add(window);
                if (!PlaceVisible(window, target, true)) return false;
                bool aligned = !IsZoomed(window) && !IsIconic(window) && VisibleBounds(window, out visible) && Matches(visible, target);
                if (aligned) alignedWindows.Add(window);
                return aligned;
            }
            catch (Exception error) { Record("target_geometry_failed", error.Message); return false; }
        }

        public void CaptureNormal(IntPtr window)
        {
            lock (stateGate)
            {
                try
                {
                    if (window == IntPtr.Zero || !IsWindow(window) || IsIconic(window)) return;
                    PixelRect normal;
                    if (IsZoomed(window))
                    {
                        WindowPlacement placement = new WindowPlacement();
                        placement.Length = Marshal.SizeOf(typeof(WindowPlacement));
                        if (!GetWindowPlacement(window, ref placement)) return;
                        normal = placement.NormalPosition.Pixels();
                        // Standard top-level WINDOWPLACEMENT uses work-area coordinates.
                        // Tool windows use screen coordinates instead.
                        if ((GetWindowLong(window, -20) & 0x80) == 0)
                        {
                            MonitorInfo monitor = ReadMonitor(MonitorFromWindow(window, 2));
                            normal.X += monitor.Work.Left - monitor.Monitor.Left;
                            normal.Y += monitor.Work.Top - monitor.Monitor.Top;
                        }
                        normal.X += leftInset; normal.Y += topInset;
                        normal.Width -= leftInset + rightInset; normal.Height -= topInset + bottomInset;
                    }
                    else
                    {
                        NativeRect outer;
                        if (!VisibleBounds(window, out normal) || !GetWindowRect(window, out outer)) return;
                        leftInset = normal.X - outer.Left; topInset = normal.Y - outer.Top;
                        rightInset = outer.Right - normal.X - normal.Width;
                        bottomInset = outer.Bottom - normal.Y - normal.Height;
                    }
                    if (normal.Width <= 0 || normal.Height <= 0) return;
                    MonitorInfo info = MonitorFor(normal, "");
                    if (haveSaved && Exact(normal, saved) && info.Device == savedMonitor) return;
                    saved = normal; savedMonitor = info.Device; haveSaved = true;
                    SaveState(info);
                }
                catch (Exception error) { Record("capture_geometry_failed", error.Message); }
            }
        }

        private bool PlaceVisible(IntPtr window, PixelRect desired, bool asynchronous)
        {
            NativeRect outer;
            PixelRect visible;
            if (!GetWindowRect(window, out outer) || !VisibleBounds(window, out visible)) return false;
            int left = visible.X - outer.Left, top = visible.Y - outer.Top;
            int right = outer.Right - visible.X - visible.Width, bottom = outer.Bottom - visible.Y - visible.Height;
            uint flags = 0x0004 | 0x0010; // NOZORDER | NOACTIVATE
            if (asynchronous) flags |= 0x4000; // ASYNCWINDOWPOS: never block on an unresponsive application.
            return SetWindowPos(window, IntPtr.Zero, desired.X - left, desired.Y - top,
                desired.Width + left + right, desired.Height + top + bottom, flags);
        }

        private static bool VisibleBounds(IntPtr window, out PixelRect value)
        {
            NativeRect native;
            bool obtained = false;
            try { obtained = DwmGetWindowAttribute(window, 9, out native, Marshal.SizeOf(typeof(NativeRect))) == 0; }
            catch (DllNotFoundException) { native = new NativeRect(); }
            catch (EntryPointNotFoundException) { native = new NativeRect(); }
            if (!obtained && !GetWindowRect(window, out native)) { value = new PixelRect(); return false; }
            value = native.Pixels();
            return value.Width > 0 && value.Height > 0;
        }

        private static bool Matches(PixelRect a, PixelRect b)
        {
            return Math.Abs((long)a.X - b.X) <= 1 && Math.Abs((long)a.Y - b.Y) <= 1 &&
                Math.Abs((long)a.Width - b.Width) <= 1 && Math.Abs((long)a.Height - b.Height) <= 1;
        }
        private static bool Exact(PixelRect a, PixelRect b)
        { return a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height; }

        private static MonitorInfo MonitorFor(PixelRect rect, string preferred)
        {
            IntPtr selected = IntPtr.Zero;
            if (!string.IsNullOrEmpty(preferred))
            {
                MonitorCallback callback = delegate(IntPtr handle, IntPtr dc, ref NativeRect bounds, IntPtr data)
                {
                    MonitorInfo candidate = ReadMonitor(handle);
                    if (string.Equals(candidate.Device, preferred, StringComparison.OrdinalIgnoreCase))
                    { selected = handle; return false; }
                    return true;
                };
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            }
            if (selected == IntPtr.Zero)
            {
                NativeRect native = new NativeRect(rect);
                selected = MonitorFromRect(ref native, 2);
            }
            return ReadMonitor(selected);
        }
        private static MonitorInfo ReadMonitor(IntPtr handle)
        {
            MonitorInfo info = new MonitorInfo(); info.Size = Marshal.SizeOf(typeof(MonitorInfo));
            if (handle == IntPtr.Zero || !GetMonitorInfo(handle, ref info))
                throw new InvalidOperationException("No monitor work area is available.");
            return info;
        }

        private void ReadState()
        {
            try
            {
                if (!File.Exists(statePath)) return;
                XElement xml = XDocument.Load(statePath).Root;
                if (xml == null || xml.Name != "windowGeometry") throw new InvalidDataException("Invalid geometry state.");
                PixelRect rect = new PixelRect(Number(xml, "x"), Number(xml, "y"), Number(xml, "width"), Number(xml, "height"));
                if (rect.Width < 1 || rect.Height < 1 || rect.Width > 100000 || rect.Height > 100000 ||
                    Math.Abs((long)rect.X) > 1000000 || Math.Abs((long)rect.Y) > 1000000)
                    throw new InvalidDataException("Stored geometry is outside supported bounds.");
                saved = rect; savedMonitor = (string)xml.Attribute("monitor") ?? ""; haveSaved = true;
                leftInset = OptionalNumber(xml, "leftInset"); topInset = OptionalNumber(xml, "topInset");
                rightInset = OptionalNumber(xml, "rightInset"); bottomInset = OptionalNumber(xml, "bottomInset");
            }
            catch (Exception error) { Record("geometry_state_ignored", error.Message); }
        }
        private static int Number(XElement xml, string key)
        { return int.Parse((string)xml.Attribute(key), CultureInfo.InvariantCulture); }
        private static int OptionalNumber(XElement xml, string key)
        {
            int value; string text = (string)xml.Attribute(key);
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && Math.Abs((long)value) <= 256 ? value : 0;
        }
        private void SaveState(MonitorInfo monitor)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath));
            XElement xml = new XElement("windowGeometry", new XAttribute("version", 1),
                new XAttribute("x", saved.X), new XAttribute("y", saved.Y), new XAttribute("width", saved.Width),
                new XAttribute("height", saved.Height), new XAttribute("monitor", savedMonitor ?? ""),
                new XAttribute("leftInset", leftInset), new XAttribute("topInset", topInset),
                new XAttribute("rightInset", rightInset), new XAttribute("bottomInset", bottomInset),
                new XAttribute("workX", monitor.Work.Left), new XAttribute("workY", monitor.Work.Top),
                new XAttribute("workWidth", monitor.Work.Right - monitor.Work.Left),
                new XAttribute("workHeight", monitor.Work.Bottom - monitor.Work.Top));
            string temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                new XDocument(xml).Save(temporary);
                if (File.Exists(statePath)) File.Replace(temporary, statePath, null);
                else File.Move(temporary, statePath);
                Record("normal_bounds_saved", saved.ToString() + "; monitor=" + savedMonitor);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private void Record(string eventName, string detail)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                File.AppendAllText(logPath, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\t" + eventName + "\t" +
                    (detail ?? "").Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine);
            }
            catch { }
        }
    }
}
