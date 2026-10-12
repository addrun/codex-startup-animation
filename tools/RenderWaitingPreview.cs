using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DragonLauncher;

// Offscreen art export only. No window, coordinator or activation API is used.
public static class RenderWaitingPreview
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(args[0]), folder = Path.GetFullPath(args[1]);
            double seconds = double.Parse(args[2], CultureInfo.InvariantCulture);
            int fps = int.Parse(args[3], CultureInfo.InvariantCulture);
            Directory.CreateDirectory(folder);
            Settings settings = Settings.Load(root);
            settings.PreferDetailTextures = true;
            WaitingTail.RenderFrames(root, settings, Path.Combine(folder, "frames"), seconds, fps);
            RenderStill(root, settings, folder, false);
            RenderStill(root, settings, folder, true);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void RenderStill(string root, Settings settings, string folder, bool detail)
    {
        var scene = new Scene(root, settings, false, detail);
        scene.Launching = true;
        scene.SetTime(settings.TailHoldTime);
        scene.EyeOpennessOverride = 1;
        scene.SetWaitingEffectsTime(0);
        scene.Measure(new Size(Scene.DesignWidth, Scene.DesignHeight));
        scene.Arrange(new Rect(0, 0, Scene.DesignWidth, Scene.DesignHeight));
        scene.UpdateLayout();
        var capture = new DrawingVisual();
        using (var dc = capture.RenderOpen())
            dc.DrawRectangle(new VisualBrush(scene) { Stretch = Stretch.Fill }, null,
                new Rect(0, 0, Scene.ExportWidth, Scene.ExportHeight));
        var image = new RenderTargetBitmap(Scene.ExportWidth, Scene.ExportHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(capture);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(Path.Combine(folder, detail ? "waiting-detail-2k.png" : "waiting-original-2k.png")))
            encoder.Save(file);

        if (detail)
        {
            var timings = new double[120];
            for (int i = 0; i < timings.Length; i++)
            {
                var clock = Stopwatch.StartNew();
                scene.SetWaitingEffectsTime(i / 30.0);
                clock.Stop(); timings[i] = clock.Elapsed.TotalMilliseconds;
            }
            Array.Sort(timings);
            double total = 0; foreach (double time in timings) total += time;
            File.WriteAllText(Path.Combine(folder, "cpu-update-timing.txt"), String.Format(CultureInfo.InvariantCulture,
                "Offscreen waiting-effect CPU update, 120 samples at 30 Hz: mean={0:F3} ms; p95={1:F3} ms; max={2:F3} ms. " +
                "Excludes GPU presentation and does not prove interactive frame rate. No app activation.\n",
                total / timings.Length, timings[113], timings[timings.Length - 1]));
        }
        scene.ReleaseWaitingEffects();
    }
}
