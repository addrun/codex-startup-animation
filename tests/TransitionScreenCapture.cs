using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

// Samples only the owned test overlay. The burst stays in memory; PNG encoding
// and disk writes happen after capture, so they cannot stall the video handoff.
public static class TransitionScreenCapture
{
    public sealed class Frame
    {
        public int Index;
        public double Seconds;
        public double CaptureMilliseconds;
        public string Utc;
        public string Path;
        public long WindowHandle;
        public bool OwnershipVerified;
        internal Bitmap Image;
    }

    public sealed class Burst
    {
        public string StartedUtc;
        public string Failure;
        public int ProcessId;
        public int RequestedFps;
        public double RequestedSeconds;
        public int ClientScreenX, ClientScreenY, ClientWidth, ClientHeight;
        public int RoiScreenX, RoiScreenY, RoiWidth, RoiHeight;
        public int ImageWidth, ImageHeight;
        public int OwnershipPointCount;
        public Frame[] Frames;
    }

    private delegate bool EnumProc(IntPtr window, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Bounds bounds);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Bounds bounds);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    public static string LastFailure;

    public static void EnableDpi()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch (EntryPointNotFoundException) { SetProcessDPIAware(); }
    }

    private static bool Find(int processId, out IntPtr target, out Rectangle client)
    {
        IntPtr found = IntPtr.Zero;
        Rectangle foundClient = Rectangle.Empty;
        EnumWindows(delegate(IntPtr window, IntPtr data) {
            uint pid; GetWindowThreadProcessId(window, out pid);
            Bounds bounds;
            if (pid != processId || !IsWindowVisible(window) || IsIconic(window) ||
                !GetClientRect(window, out bounds) || bounds.Right < 500 || bounds.Bottom < 300) return true;
            NativePoint point = new NativePoint();
            if (!ClientToScreen(window, ref point)) return true;
            found = window;
            foundClient = new Rectangle(point.X, point.Y, bounds.Right, bounds.Bottom);
            return false;
        }, IntPtr.Zero);
        target = found; client = foundClient;
        return found != IntPtr.Zero;
    }

    public static bool OwnedWindowVisible(int processId)
    {
        IntPtr target; Rectangle client;
        return Find(processId, out target, out client);
    }

    // Test both the Z order and a grid of points. A window overlapping the ROI
    // makes capture fail conservatively, even when its visible content is small.
    private static bool Verify(int processId, IntPtr target, Rectangle originalClient, Rectangle roi)
    {
        if (!IsWindowVisible(target) || IsIconic(target))
        { LastFailure = "Owned animation window is not visible"; return false; }
        Bounds bounds; NativePoint origin = new NativePoint();
        if (!GetClientRect(target, out bounds) || !ClientToScreen(target, ref origin) ||
            new Rectangle(origin.X, origin.Y, bounds.Right, bounds.Bottom) != originalClient)
        { LastFailure = "Owned animation window moved or resized during burst"; return false; }
        if (!System.Windows.Forms.SystemInformation.VirtualScreen.Contains(roi))
        { LastFailure = "Character ROI is outside virtual screen"; return false; }
        for (IntPtr above = GetWindow(target, 3); above != IntPtr.Zero; above = GetWindow(above, 3))
        {
            int cloaked;
            if (!IsWindowVisible(above) || IsIconic(above) ||
                (DwmGetWindowAttribute(above, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0)) continue;
            uint pid; GetWindowThreadProcessId(above, out pid);
            if (pid == processId) continue;
            Bounds outer;
            if (GetWindowRect(above, out outer) &&
                roi.IntersectsWith(Rectangle.FromLTRB(outer.Left, outer.Top, outer.Right, outer.Bottom)))
            { LastFailure = "Another visible window overlaps the test ROI; owner=" + pid; return false; }
        }
        for (int row = 0; row < 5; row++) for (int column = 0; column < 5; column++)
        {
            NativePoint point = new NativePoint {
                X = roi.Left + 1 + column * (roi.Width - 3) / 4,
                Y = roi.Top + 1 + row * (roi.Height - 3) / 4
            };
            uint pid; GetWindowThreadProcessId(WindowFromPoint(point), out pid);
            if (pid != processId)
            { LastFailure = "Character ROI is occluded at grid point " + row + "," + column + "; owner=" + pid; return false; }
        }
        return true;
    }

    public static Burst Capture(int processId, int fps, double seconds, int imageWidth)
    {
        if (fps < 30 || fps > 60 || seconds < 1 || seconds > 8 || imageWidth < 200 || imageWidth > 800)
            throw new ArgumentOutOfRangeException("Invalid burst size or sampling rate");
        LastFailure = "";
        IntPtr target; Rectangle client;
        if (!Find(processId, out target, out client)) throw new InvalidOperationException("No owned test animation window");
        // Left hair and part of the face remain bright in the last film frame,
        // move in the waiting scene, and avoid the controls and rounded corners.
        Rectangle roi = new Rectangle(client.Left + (int)Math.Round(client.Width * 0.52),
            client.Top + (int)Math.Round(client.Height * 0.22),
            (int)Math.Round(client.Width * 0.36), (int)Math.Round(client.Height * 0.58));
        int imageHeight = (int)Math.Round((double)imageWidth * roi.Height / roi.Width);
        DateTime utc = DateTime.UtcNow;
        var burst = new Burst {
            StartedUtc = utc.ToString("o"), Failure = "", ProcessId = processId,
            RequestedFps = fps, RequestedSeconds = seconds,
            ClientScreenX = client.X, ClientScreenY = client.Y, ClientWidth = client.Width, ClientHeight = client.Height,
            RoiScreenX = roi.X, RoiScreenY = roi.Y, RoiWidth = roi.Width, RoiHeight = roi.Height,
            ImageWidth = imageWidth, ImageHeight = imageHeight, OwnershipPointCount = 25
        };
        var frames = new List<Frame>();
        var clock = Stopwatch.StartNew();
        double interval = 1.0 / fps, next = 0;
        using (var source = new Bitmap(roi.Width, roi.Height, PixelFormat.Format24bppRgb))
        using (var copy = Graphics.FromImage(source))
        {
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                double remaining = next - clock.Elapsed.TotalSeconds;
                if (remaining > 0.002) { Thread.Sleep(Math.Max(1, (int)(remaining * 1000) - 1)); continue; }
                if (remaining > 0) { Thread.SpinWait(100); continue; }
                DwmFlush();
                if (!Verify(processId, target, client, roi)) { burst.Failure = LastFailure; break; }
                double start = clock.Elapsed.TotalSeconds;
                try { copy.CopyFromScreen(roi.Location, System.Drawing.Point.Empty, roi.Size, CopyPixelOperation.SourceCopy); }
                catch (Exception error) { burst.Failure = error.GetType().Name + ": " + error.Message; break; }
                double captured = clock.Elapsed.TotalSeconds;
                if (!Verify(processId, target, client, roi)) { burst.Failure = LastFailure; break; }
                var image = new Bitmap(imageWidth, imageHeight, PixelFormat.Format24bppRgb);
                using (var resize = Graphics.FromImage(image))
                {
                    resize.InterpolationMode = InterpolationMode.Bilinear;
                    resize.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    resize.DrawImage(source, new Rectangle(0, 0, imageWidth, imageHeight));
                }
                frames.Add(new Frame { Index = frames.Count, Seconds = captured,
                    CaptureMilliseconds = (captured - start) * 1000,
                    Utc = utc.AddSeconds(captured).ToString("o"), WindowHandle = target.ToInt64(),
                    OwnershipVerified = true, Image = image });
                // Skip deadlines missed by composition instead of issuing a
                // burst of duplicate reads to catch up with the nominal FPS.
                next += interval;
                if (clock.Elapsed.TotalSeconds - next > interval)
                    next = (Math.Floor(clock.Elapsed.TotalSeconds / interval) + 1) * interval;
            }
        }
        burst.Frames = frames.ToArray();
        return burst;
    }

    public static void SaveAndRelease(Burst burst, string folder)
    {
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var frame in burst.Frames)
            {
                frame.Path = System.IO.Path.Combine(folder, "frame-" + frame.Index.ToString("D4") + ".png");
                frame.Image.Save(frame.Path, ImageFormat.Png);
                frame.Image.Dispose(); frame.Image = null;
            }
        }
        finally
        {
            foreach (var frame in burst.Frames)
                if (frame.Image != null) { frame.Image.Dispose(); frame.Image = null; }
        }
    }
}
