using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

// Lifecycle-only stand-in for the splash. No media, UI, or application activation.
public static class FakeObserver
{
    public static int Main(string[] args)
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        string output = Path.Combine(root, "output");
        Directory.CreateDirectory(output);
        bool passive = args.Length == 1 && string.Equals(args[0], "--observe", StringComparison.Ordinal);
        File.AppendAllText(Path.Combine(output, "observer.tsv"),
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\t" +
            Process.GetCurrentProcess().Id + "\t" + (passive ? "passive_only" : "unexpected_arguments") +
            "\t" + string.Join(" ", args) + Environment.NewLine);
        Thread.Sleep(350);
        return passive ? 0 : 1;
    }
}
