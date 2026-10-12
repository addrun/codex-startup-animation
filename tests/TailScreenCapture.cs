using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// Captures only the inside of the owned animation window after checking its
// screen bounds and visible ownership. No other application is sampled.
public static class TailScreenCapture
{
    public static string LastFailure;
    public delegate bool EnumProc(IntPtr window, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Bounds bounds);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    public static void EnableDpi()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch (EntryPointNotFoundException) { SetProcessDPIAware(); }
    }
    public static bool Capture(int processId, string path)
    {
        LastFailure = "";
        IntPtr target = IntPtr.Zero;
        Bounds bounds = new Bounds(); Point origin = new Point();
        EnumWindows(delegate(IntPtr window, IntPtr data) {
            uint pid; GetWindowThreadProcessId(window, out pid);
            Bounds client;
            if (pid != processId || !IsWindowVisible(window) || !GetClientRect(window, out client) ||
                client.Right < 500 || client.Bottom < 300) return true;
            var location = new Point();
            if (!ClientToScreen(window, ref location)) return true;
            target = window; bounds = client; origin = location; return false;
        }, IntPtr.Zero);
        if (target == IntPtr.Zero) { LastFailure = "No visible owned animation window"; return false; }
        // Exclude rounded transparent corners and the native perimeter.
        int inset = 16, width = bounds.Right - inset * 2, height = bounds.Bottom - inset * 2;
        var area = new Rectangle(origin.X + inset, origin.Y + inset, width, height);
        if (!System.Windows.Forms.SystemInformation.VirtualScreen.Contains(area))
        { LastFailure = "Animation outside virtual screen: " + area; return false; }
        for (int row = 0; row < 3; row++) for (int column = 0; column < 3; column++)
        {
            var point = new Point { X = area.Left + (column + 1) * width / 4,
                Y = area.Top + (row + 1) * height / 4 };
            uint pid; GetWindowThreadProcessId(WindowFromPoint(point), out pid);
            if (pid != processId) { LastFailure = "Animation occluded at sample " + row + "," + column + "; owner=" + pid; return false; }
        }
        DwmFlush();
        using (var image = new Bitmap(width, height, PixelFormat.Format24bppRgb))
        {
            using (var graphics = Graphics.FromImage(image))
                graphics.CopyFromScreen(area.Location, System.Drawing.Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
            image.Save(path, ImageFormat.Png);
        }
        return true;
    }
}
