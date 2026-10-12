using System;
using System.Runtime.InteropServices;

// Sends close messages only to the known PID of an isolated animation copy.
public static class SoundOverlayNative
{
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);

    public static long Find(int processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr parameter)
        {
            uint owner; GetWindowThreadProcessId(window, out owner);
            Rect client;
            if (owner == (uint)processId && IsWindowVisible(window) && GetClientRect(window, out client) &&
                client.Right - client.Left >= 300 && client.Bottom - client.Top >= 200)
            {
                found = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found.ToInt64();
    }

    public static bool Close(long handle, int expectedProcessId)
    {
        var window = new IntPtr(handle);
        uint owner; GetWindowThreadProcessId(window, out owner);
        return owner == (uint)expectedProcessId && PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero);
    }
}
