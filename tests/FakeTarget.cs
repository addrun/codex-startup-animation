using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

// An isolated WPF target for launcher integration checks. It never touches Codex.
public static class FakeTarget
{
    private static string marker;

    // A small usable page, rather than a label masquerading as a loaded UI.
    // PageTarget also uses it to switch from its simulated loading surface.
    public static FrameworkElement CreateInteractivePage(bool dark)
    {
        Brush body = new SolidColorBrush(dark ? Color.FromRgb(31, 34, 41) : Color.FromRgb(248, 249, 252));
        Brush panel = new SolidColorBrush(dark ? Color.FromRgb(48, 53, 64) : Color.FromRgb(228, 233, 242));
        Brush ink = dark ? Brushes.WhiteSmoke : Brushes.MidnightBlue;
        var root = new Grid { Background = body };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var navigation = new StackPanel { Margin = new Thickness(18, 32, 18, 18) };
        var sidebar = new Border { Background = panel, Child = navigation };
        root.Children.Add(sidebar);
        navigation.Children.Add(new TextBlock { Text = "Fixture workspace", Foreground = ink, FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 20) });

        var main = new Grid { Margin = new Thickness(28, 32, 28, 24) };
        Grid.SetColumn(main, 1); root.Children.Add(main);
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock { Text = "Page is ready", FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = ink };
        main.Children.Add(heading);
        foreach (string label in new[] { "New conversation", "Recent work", "Settings" })
        {
            var button = new Button { Content = label, MinHeight = 36, Margin = new Thickness(0, 0, 0, 14), Padding = new Thickness(8), HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += delegate(object sender, RoutedEventArgs e) { heading.Text = ((Button)sender).Content.ToString(); };
            navigation.Children.Add(button);
        }
        var text = new TextBlock
        {
            Text = "This isolated test application has finished loading.\n\nThe navigation buttons and message box are interactive.\n\nIt never opens or changes the real Codex application.",
            Foreground = ink, FontSize = 17, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 24, 0, 24)
        };
        Grid.SetRow(text, 1); main.Children.Add(text);
        var compose = new Grid();
        compose.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        compose.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var input = new TextBox { MinHeight = 42, Text = "Type a fixture message", Padding = new Thickness(10), FontSize = 16, VerticalContentAlignment = VerticalAlignment.Center };
        var send = new Button { Content = "Send", MinWidth = 80, Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        send.Click += delegate { text.Text = "Fixture received: " + input.Text; input.Clear(); };
        compose.Children.Add(input); Grid.SetColumn(send, 1); compose.Children.Add(send);
        Grid.SetRow(compose, 2); main.Children.Add(compose);
        return root;
    }

    private static void Record(string name)
    {
        if (string.IsNullOrWhiteSpace(marker)) return;
        File.AppendAllText(marker, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\t" +
            Process.GetCurrentProcess().Id + "\t" + name + Environment.NewLine);
    }

    private static string Value(string[] args, string name, string fallback)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        marker = Value(args, "--marker", "");
        int delay = int.Parse(Value(args, "--delay-ms", "1500"), CultureInfo.InvariantCulture);
        int lifetime = int.Parse(Value(args, "--lifetime-ms", "12000"), CultureInfo.InvariantCulture);
        bool noWindow = Array.IndexOf(args, "--no-window") >= 0;
        bool maximize = Array.IndexOf(args, "--maximized") >= 0;
        string control = Value(args, "--control", "");
        Window targetWindow = null;
        string previousCommand = "";
        Record("process_started");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exit = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(lifetime) };
        exit.Tick += delegate { exit.Stop(); Record("automatic_exit"); app.Shutdown(); };
        exit.Start();
        if (!noWindow)
        {
            var show = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, delay)) };
            show.Tick += delegate
            {
                show.Stop();
                var window = new Window
                {
                    Title = "Launcher integration fake target",
                    Width = 640,
                    Height = 420,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Background = Brushes.DarkSlateGray,
                    Content = CreateInteractivePage(true)
                };
                window.ContentRendered += delegate { Record("window_rendered"); };
                window.Closed += delegate { Record("window_closed"); };
                targetWindow = window;
                if (maximize) window.WindowState = WindowState.Maximized;
                window.Show();
            };
            show.Start();
        }
        if (control.Length != 0)
        {
            var commands = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            commands.Tick += delegate
            {
                string command;
                try
                {
                    if (!File.Exists(control)) command = "";
                    else using (var reader = new StreamReader(new FileStream(control, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))) command = reader.ReadToEnd().Trim();
                }
                catch { return; }
                string operation = command.Split('-')[0];
                if (operation == previousCommand || targetWindow == null) return;
                previousCommand = operation;
                if (operation == "minimize") targetWindow.WindowState = WindowState.Minimized;
                else if (operation == "restore") { targetWindow.WindowState = WindowState.Normal; targetWindow.Show(); }
                else if (operation == "hide") targetWindow.Hide();
                else if (operation == "show") targetWindow.Show();
                else if (operation == "child")
                {
                    string childExecutable = Process.GetCurrentProcess().MainModule.FileName;
                    if (childExecutable.IndexOf("UnrelatedCodexCli.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                        childExecutable = Path.Combine(Path.GetDirectoryName(childExecutable), "FakeTarget.exe");
                    Process.Start(new ProcessStartInfo(childExecutable,
                        "--type=renderer --no-window --lifetime-ms 1800 --marker \"" + marker + ".child\"") { UseShellExecute = false });
                }
                else if (operation == "exit") { Record("requested_exit"); app.Shutdown(); }
                else return;
                Record("command_" + operation);
            };
            commands.Start();
        }
        app.Run();
        Record("process_exited");
        return 0;
    }
}
