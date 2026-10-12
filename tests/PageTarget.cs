using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

// Simulates the screenshot's blank Electron loading surface without involving
// the real application. The loaded state contains functional WPF controls.
public static class PageTarget
{
    private static string marker;
    private static Window window;
    private static bool dark, spinner;
    private static int flashDuration;
    private static long renderVersion;

    private static string Value(string[] args, string name, string fallback)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }
    private static int Number(string[] args, string name, int fallback)
    { return int.Parse(Value(args, name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture); }

    private static void Record(string name, string detail)
    {
        if (marker.Length == 0) return;
        File.AppendAllText(marker, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\t" +
            Process.GetCurrentProcess().Id + "\t" + name + "\t" + detail + Environment.NewLine);
    }

    private static void After(int milliseconds, Action action)
    {
        if (milliseconds < 0) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)) };
        timer.Tick += delegate { timer.Stop(); action(); };
        timer.Start();
    }

    private static FrameworkElement LoadingSurface()
    {
        var background = new LinearGradientBrush(
            dark ? Color.FromRgb(26, 27, 32) : Color.FromRgb(246, 243, 253),
            dark ? Color.FromRgb(29, 28, 31) : Color.FromRgb(252, 242, 247), 0);
        var grid = new Grid { Background = background };
        if (spinner)
        {
            // A compact spinner carries local detail but no real page controls.
            var ring = new Ellipse
            {
                Width = 28, Height = 28, StrokeThickness = 3,
                Stroke = dark ? Brushes.LightGray : Brushes.SlateGray,
                StrokeDashArray = new DoubleCollection { 3, 2, 1, 2 },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var rotation = new RotateTransform(); ring.RenderTransform = rotation;
            rotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
            grid.Children.Add(ring);
        }
        return grid;
    }

    private static void SetPage(string state)
    {
        bool ready = state != "blank";
        Record(state + "_requested", "");
        window.Content = ready ? FakeTarget.CreateInteractivePage(dark) : LoadingSurface();
        long version = ++renderVersion;
        // The marker is emitted after WPF has performed layout/render work. A
        // separate dispatcher turn prevents a requested state from being used
        // as evidence that the page was actually rendered.
        window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate
        {
            if (version == renderVersion)
                Record(state + "_rendered", "hwnd=" + new WindowInteropHelper(window).Handle.ToInt64().ToString(CultureInfo.InvariantCulture));
        }));
    }

    private static void Flash()
    {
        SetPage("flash_ready");
        After(flashDuration, delegate { SetPage("blank"); });
    }

    [STAThread]
    public static int Main(string[] args)
    {
        marker = Value(args, "--marker", "");
        dark = Value(args, "--theme", "light") == "dark";
        spinner = Array.IndexOf(args, "--spinner") >= 0;
        int showDelay = Number(args, "--window-delay-ms", 50);
        int readyDelay = Number(args, "--ready-delay-ms", 2700);
        int flashDelay = Number(args, "--flash-ready-ms", -1);
        flashDuration = Number(args, "--flash-duration-ms", 300);
        int lifetime = Number(args, "--lifetime-ms", 15000);
        string control = Value(args, "--control", "");
        string previousCommand = "";
        Record("process_started", "theme=" + (dark ? "dark" : "light") + ";spinner=" + spinner);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        After(lifetime, delegate { Record("automatic_exit", ""); app.Shutdown(); });
        After(showDelay, delegate
        {
            window = new Window
            {
                Title = "Page readiness integration fixture", Width = 1000, Height = 630,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = LoadingSurface(), Background = dark ? Brushes.Black : Brushes.White,
                Topmost = Array.IndexOf(args, "--topmost") >= 0
            };
            window.ContentRendered += delegate { Record("window_rendered", "hwnd=" + new WindowInteropHelper(window).Handle.ToInt64().ToString(CultureInfo.InvariantCulture)); };
            window.Closed += delegate { Record("window_closed", ""); app.Shutdown(); };
            window.Show();
            After(readyDelay, delegate { SetPage("page_ready"); });
            After(flashDelay, Flash);
        });
        if (control.Length != 0)
        {
            var commands = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
            commands.Tick += delegate
            {
                string command;
                try
                {
                    if (!File.Exists(control)) return;
                    using (var reader = new StreamReader(new FileStream(control, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))) command = reader.ReadToEnd().Trim();
                }
                catch (IOException) { return; }
                if (window == null || command == previousCommand) return;
                previousCommand = command;
                string operation = command.Split(':')[0];
                if (operation == "flash") Flash();
                else if (operation == "ready") SetPage("page_ready");
                else if (operation == "blank") SetPage("blank");
                else if (operation == "exit") { Record("requested_exit", ""); app.Shutdown(); }
            };
            commands.Start();
        }
        app.Run();
        Record("process_exited", "");
        return 0;
    }
}
