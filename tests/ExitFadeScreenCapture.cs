using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

// Capture only the verified rectangle of our synthetic page and overlay.
// Frames stay in memory until the burst has completed.
public static class ExitFadeScreenCapture
{
    public sealed class Frame
    {
        public string utc, file;
        public double seconds;
        public long topHwnd;
        public int topPid;
        internal Bitmap bitmap;
    }
    public sealed class Result
    {
        public int x, y, width, height, imageWidth, imageHeight;
        public double actualFps, maximumGapMilliseconds;
        public long targetHwnd;
        public int targetStyleBefore, targetStyleAfter;
        public bool targetVisibleAfter, targetIconicAfter;
        public string error;
        public Frame[] frames;
    }
    private delegate bool EnumCallback(IntPtr window, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    public static void EnableDpi() { try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } }
    public static Result Capture(int targetPid, int overlayPid, string folder)
    {
        IntPtr target = IntPtr.Zero, overlay = IntPtr.Zero;
        EnumWindows(delegate(IntPtr handle, IntPtr unused) {
            uint pid; GetWindowThreadProcessId(handle, out pid); Rect rect;
            if ((pid == targetPid || pid == overlayPid) && IsWindowVisible(handle) && !IsIconic(handle) && GetClientRect(handle, out rect) && rect.Right > 300 && rect.Bottom > 200)
            { if (pid == targetPid) target = handle; else overlay = handle; }
            return true;
        }, IntPtr.Zero);
        if (target == IntPtr.Zero) throw new InvalidOperationException("Synthetic target window is unavailable.");
        if (overlay == IntPtr.Zero) throw new InvalidOperationException("Owned overlay window is unavailable.");
        // Keep only the test page immediately behind its own topmost overlay.
        // Otherwise an unrelated foreground app may cover the fixture as soon
        // as the overlay closes. Both test windows are disposed by the suite.
        // Explicitly enter the topmost band, then put the overlay above it.
        // Inserting a normal window after a topmost window alone does not
        // reliably change its band on every Windows build.
        if (!SetWindowPos(target, new IntPtr(-1), 0, 0, 0, 0, 0x13) ||
            !SetWindowPos(overlay, new IntPtr(-1), 0, 0, 0, 0, 0x13))
            throw new InvalidOperationException("Unable to place the owned target behind its overlay.");
        Rect client; Point origin = new Point();
        if (!GetClientRect(target, out client) || !ClientToScreen(target, ref origin)) throw new InvalidOperationException("Synthetic bounds unavailable.");
        var roi = new Rectangle(origin.X + 8, origin.Y + 8, client.Right - 16, client.Bottom - 16);
        if (!System.Windows.Forms.SystemInformation.VirtualScreen.Contains(roi)) throw new InvalidOperationException("Synthetic capture outside screen.");
        int width = 640, height = (int)Math.Round(640.0 * roi.Height / roi.Width);
        var result = new Result { x = roi.X, y = roi.Y, width = roi.Width, height = roi.Height, imageWidth = width, imageHeight = height, error = "",
            targetHwnd = target.ToInt64(), targetStyleBefore = GetWindowLong(target, -20) };
        var frames = new List<Frame>();
        var clock = Stopwatch.StartNew(); double next = 0;
        using (var source = new Bitmap(roi.Width, roi.Height, PixelFormat.Format24bppRgb))
        using (var graphics = Graphics.FromImage(source))
        {
            while (clock.Elapsed.TotalSeconds < 3.5)
            {
                if (clock.Elapsed.TotalSeconds < next) { Thread.Sleep(1); continue; }
                DwmFlush();
                long top = 0; int topPid = 0; bool owned = true;
                for (int row = 0; row < 7 && owned; row++) for (int column = 0; column < 9; column++)
                {
                    var point = new Point { X = roi.Left + 1 + column * (roi.Width - 3) / 8, Y = roi.Top + 1 + row * (roi.Height - 3) / 6 };
                    IntPtr handle = WindowFromPoint(point); uint pid; GetWindowThreadProcessId(handle, out pid);
                    if (pid != targetPid && pid != overlayPid) { result.error = "Capture refused: unrelated window at sample point; owner=" + pid + "; point=" + point.X + "," + point.Y; owned = false; break; }
                    if (row == 3 && column == 4) { top = handle.ToInt64(); topPid = (int)pid; }
                }
                if (!owned) break;
                graphics.CopyFromScreen(roi.Location, System.Drawing.Point.Empty, roi.Size, CopyPixelOperation.SourceCopy);
                var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                using (var scaling = Graphics.FromImage(bitmap)) scaling.DrawImage(source, new Rectangle(0, 0, width, height));
                frames.Add(new Frame { utc = DateTime.UtcNow.ToString("o"), seconds = clock.Elapsed.TotalSeconds, topHwnd = top, topPid = topPid, bitmap = bitmap });
                // DwmFlush already synchronizes the burst to composition.
                // Do not add a second frame interval after the capture work.
                next = Math.Max(next + 1.0 / 60, clock.Elapsed.TotalSeconds);
            }
        }
        Directory.CreateDirectory(folder);
        for (int index = 0; index < frames.Count; index++)
        {
            Frame frame = frames[index]; frame.file = Path.Combine(folder, "frame-" + index.ToString("D4") + ".png");
            frame.bitmap.Save(frame.file, ImageFormat.Png); frame.bitmap.Dispose(); frame.bitmap = null;
            if (index > 0) result.maximumGapMilliseconds = Math.Max(result.maximumGapMilliseconds, (frame.seconds - frames[index - 1].seconds) * 1000);
        }
        result.frames = frames.ToArray();
        result.targetStyleAfter = GetWindowLong(target, -20);
        result.targetVisibleAfter = IsWindowVisible(target); result.targetIconicAfter = IsIconic(target);
        if (frames.Count > 1) result.actualFps = (frames.Count - 1) / (frames[frames.Count - 1].seconds - frames[0].seconds);
        return result;
    }
}
