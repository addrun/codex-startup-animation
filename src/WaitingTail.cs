using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DragonLauncher
{
    /// <summary>A fixed face and camera with eyelids, outer hair locks and foreground lights.</summary>
    public sealed class WaitingTail : IDisposable
    {
        private Scene scene;
        private readonly bool reducedMotion;
        private readonly double interval, duration;
        private readonly Stopwatch clock = new Stopwatch();
        private readonly DispatcherTimer frameTimer;
        private int lastEyeFrame = -1;
        private bool running, disposed;

        public FrameworkElement Element { get; private set; }

        public WaitingTail(string root, Settings settings, bool reduceMotion, double holdTime, bool preferDetailTextures = true)
            : this(root, settings, reduceMotion, holdTime, preferDetailTextures, null)
        {
        }

        internal WaitingTail(string root, Settings settings, bool reduceMotion, double holdTime, bool preferDetailTextures,
            Scene.WaitingAssets prepared)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            reducedMotion = reduceMotion;
            interval = settings.BlinkInterval;
            duration = settings.BlinkDuration;
            frameTimer = new DispatcherTimer(DispatcherPriority.Render);
            frameTimer.Interval = TimeSpan.FromSeconds(1.0 / settings.FrameRate);
            frameTimer.Tick += OnRendering;
            scene = new Scene(root, settings, reduceMotion, preferDetailTextures, true, prepared);
            scene.Launching = true;
            scene.SetTime(Math.Max(0, Math.Min(settings.Duration, holdTime)));
            scene.EyeOpennessOverride = 1.0;
            scene.SetWaitingEffectsTime(0);
            Element = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Child = scene
            };
        }

        public void Start()
        {
            if (disposed || running || reducedMotion) return;
            running = true;
            clock.Restart();
            frameTimer.Start();
        }

        private void OnRendering(object sender, EventArgs e)
        {
            if (!running || disposed) return;
            double elapsed = clock.Elapsed.TotalSeconds;
            double openness = Openness(elapsed, interval, duration);
            int frame = (int)Math.Round(openness * 120.0);
            if (frame != lastEyeFrame)
            {
                lastEyeFrame = frame;
                scene.EyeOpennessOverride = frame / 120.0;
                scene.InvalidateVisual();
            }
            scene.SetWaitingEffectsTime(elapsed);
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            frameTimer.Stop();
            clock.Stop();
        }

        public void Dispose()
        {
            if (disposed) return;
            Stop();
            disposed = true;
            frameTimer.Stop();
            frameTimer.Tick -= OnRendering;
            var viewbox = Element as Viewbox;
            if (viewbox != null) viewbox.Child = null;
            if (scene != null) scene.ReleaseWaitingEffects();
            Element = null;
            scene = null;
        }

        private static double Smooth(double value)
        {
            value = Math.Max(0, Math.Min(1, value));
            return value * value * (3 - 2 * value);
        }

        public static double Openness(double elapsed, double interval, double blinkDuration)
        {
            if (interval <= 0 || blinkDuration <= 0 || blinkDuration >= interval) return 1;
            if (elapsed < interval) return 1;
            double phase = (elapsed - interval) % interval;
            if (phase >= blinkDuration) return 1;
            // Close gently, then reopen a little more slowly.
            double closing = blinkDuration * 0.38;
            if (phase < closing) return 1 - Smooth(phase / closing);
            return Smooth((phase - closing) / (blinkDuration - closing));
        }

        public static double Openness(double elapsed, Settings settings, bool reduced)
        {
            return reduced ? 1 : Openness(elapsed, settings.BlinkInterval, settings.BlinkDuration);
        }

        /// <summary>Checks eye/light/hair isolation, reduced motion and cleanup without launching an app.</summary>
        public static int Verify(string root, Settings settings)
        {
            string folder = Path.Combine(root, "output", "tail-test");
            Directory.CreateDirectory(folder);
            var waiting = new WaitingTail(root, settings, false, settings.TailHoldTime);
            var scene = waiting.scene;
            scene.Measure(new Size(Scene.DesignWidth, Scene.DesignHeight));
            scene.Arrange(new Rect(0, 0, Scene.DesignWidth, Scene.DesignHeight));
            scene.UpdateLayout();
            BitmapSource open = Capture(scene);
            Save(open, Path.Combine(folder, "waiting-open.png"));
            BitmapSource repeatedOpen = Capture(scene);

            scene.EyeOpennessOverride = 0;
            scene.InvalidateVisual();
            scene.UpdateLayout();
            BitmapSource closed = Capture(scene);
            Save(closed, Path.Combine(folder, "waiting-blink.png"));
            Rect changedArea;
            int changed = CountDifferentPixels(open, closed, out changedArea);
            Rect repeatedArea;
            int staticChanges = CountDifferentPixels(open, repeatedOpen, out repeatedArea);
            scene.EyeOpennessOverride = 1;
            scene.InvalidateVisual();
            scene.SetWaitingEffectsTime(1.5, 1.5, 0);
            scene.UpdateLayout();
            BitmapSource lights = Capture(scene);
            Save(lights, Path.Combine(folder, "waiting-lights-1.png"));
            scene.SetWaitingEffectsTime(3.0, 3.0, 0);
            scene.UpdateLayout();
            BitmapSource laterLights = Capture(scene);
            Save(laterLights, Path.Combine(folder, "waiting-lights-2.png"));
            Rect lightArea;
            int lightChanges = CountDifferentPixels(open, lights, out lightArea);
            Rect laterLightArea;
            int laterLightChanges = CountDifferentPixels(lights, laterLights, out laterLightArea);
            var titleArea = new Rect(35, 235, 576, 280);
            bool foregroundOnly = !lightArea.IsEmpty && !laterLightArea.IsEmpty &&
                titleArea.Contains(lightArea) && titleArea.Contains(laterLightArea) &&
                lightChanges < Scene.DesignWidth * Scene.DesignHeight * 0.10;

            // Hold the eyes and lights still so these samples can only differ
            // in the hair. Broad protected regions also catch a moved ear,
            // face, horn, title or status text rather than merely a small diff.
            scene.SetWaitingEffectsTime(1.5, 0, 1.5);
            scene.UpdateLayout();
            BitmapSource hair = Capture(scene);
            Save(hair, Path.Combine(folder, "waiting-hair-1.png"));
            scene.SetWaitingEffectsTime(4.5, 0, 4.5);
            scene.UpdateLayout();
            BitmapSource laterHair = Capture(scene);
            Save(laterHair, Path.Combine(folder, "waiting-hair-2.png"));
            scene.SetWaitingEffectsTime(7.5, 0, 7.5);
            scene.UpdateLayout();
            BitmapSource lastHair = Capture(scene);
            Save(lastHair, Path.Combine(folder, "waiting-hair-3.png"));
            Rect hairArea, laterHairArea, lastHairArea;
            int hairChanges = CountDifferentPixels(open, hair, out hairArea);
            int laterHairChanges = CountDifferentPixels(hair, laterHair, out laterHairArea);
            int lastHairChanges = CountDifferentPixels(laterHair, lastHair, out lastHairArea);
            Rect allowedHair = scene.WaitingHairArea;
            Rect earArea = scene.WaitingEarArea;
            var fixedRegions = new[]
            {
                earArea,
                new Rect(allowedHair.Right + 3, 0, Scene.DesignWidth - allowedHair.Right - 3, Scene.DesignHeight),
                new Rect(25, 18, 520, 154),
                new Rect(55, 260, 505, 105),
                new Rect(76, 426, 445, 62),
                new Rect(75, 556, 245, 25),
                new Rect(615, 0, 175, 148)
            };
            int fixedHairChanges = CountDifferentPixelsWithin(open, hair, fixedRegions) +
                CountDifferentPixelsWithin(open, laterHair, fixedRegions) +
                CountDifferentPixelsWithin(open, lastHair, fixedRegions);
            bool hairOnly = !hairArea.IsEmpty && !laterHairArea.IsEmpty && !lastHairArea.IsEmpty &&
                allowedHair.Contains(hairArea) && allowedHair.Contains(laterHairArea) && allowedHair.Contains(lastHairArea) &&
                fixedHairChanges == 0 && hairChanges < Scene.DesignWidth * Scene.DesignHeight * 0.15;
            scene.SetWaitingEffectsTime(0, 0, 0);
            scene.UpdateLayout();
            Rect resetArea;
            int resetChanges = CountDifferentPixels(open, Capture(scene), out resetArea);
            var reduced = new WaitingTail(root, settings, true, settings.TailHoldTime);
            reduced.scene.Measure(new Size(Scene.DesignWidth, Scene.DesignHeight));
            reduced.scene.Arrange(new Rect(0, 0, Scene.DesignWidth, Scene.DesignHeight));
            reduced.scene.UpdateLayout();
            BitmapSource reducedStart = Capture(reduced.scene);
            reduced.Start();
            reduced.scene.SetWaitingEffectsTime(3);
            reduced.scene.EyeOpennessOverride = Openness(settings.BlinkInterval + settings.BlinkDuration * 0.38, settings, true);
            reduced.scene.InvalidateVisual();
            reduced.scene.UpdateLayout();
            BitmapSource reducedEnd = Capture(reduced.scene);
            Save(reducedEnd, Path.Combine(folder, "waiting-reduced-motion.png"));
            Rect reducedArea;
            int reducedChanges = CountDifferentPixels(reducedStart, reducedEnd, out reducedArea);
            bool reducedStatic = reducedChanges == 0 && !reduced.running && !reduced.frameTimer.IsEnabled;
            reduced.Dispose();
            double frozenTime = scene.Time;
            waiting.Start();
            PumpDispatcher(TimeSpan.FromSeconds(Math.Max(0.14, 2.5 / settings.FrameRate)));
            bool timerAdvances = scene.WaitingEffectsTime.HasValue && scene.WaitingEffectsTime.Value > 0 &&
                scene.WaitingHairTime.HasValue && scene.WaitingHairTime.Value > 0 && scene.Time == frozenTime;
            waiting.Stop();
            double stoppedEffectsTime = scene.WaitingEffectsTime.Value;
            double stoppedHairTime = scene.WaitingHairTime.Value;
            PumpDispatcher(TimeSpan.FromMilliseconds(100));
            bool stoppedStable = !waiting.running && !waiting.frameTimer.IsEnabled && !waiting.clock.IsRunning &&
                scene.WaitingEffectsTime.Value == stoppedEffectsTime && scene.WaitingHairTime.Value == stoppedHairTime;
            waiting.Start();
            var priorElement = waiting.Element as Viewbox;
            waiting.Dispose();
            PumpDispatcher(TimeSpan.FromMilliseconds(60));
            bool cleanedUp = !waiting.running && !waiting.frameTimer.IsEnabled && !waiting.clock.IsRunning &&
                waiting.Element == null && waiting.scene == null && priorElement != null && priorElement.Child == null &&
                !scene.HasWaitingLayers && !scene.WaitingEffectsTime.HasValue && !scene.WaitingHairTime.HasValue;
            bool timings = Openness(0, settings, false) == 1 && Openness(settings.BlinkInterval - 0.01, settings, false) == 1 &&
                Openness(settings.BlinkInterval + settings.BlinkDuration * 0.38, settings, false) < 0.001 &&
                Openness(settings.BlinkInterval + settings.BlinkDuration + 0.01, settings, false) == 1 &&
                Openness(settings.BlinkInterval + settings.BlinkDuration * 0.38, settings, true) == 1;
            bool eyeOnly = changedArea.Width < Scene.DesignWidth * 0.4 && changedArea.Height < Scene.DesignHeight * 0.5 && changed < Scene.DesignWidth * Scene.DesignHeight * 0.15;
            bool passed = open.PixelWidth == Scene.DesignWidth && open.PixelHeight == Scene.DesignHeight &&
                changed > 100 && staticChanges == 0 && timings && eyeOnly && lightChanges > 100 &&
                laterLightChanges > 100 && foregroundOnly && hairChanges > 100 && laterHairChanges > 100 &&
                lastHairChanges > 100 && hairOnly && resetChanges == 0 && reducedStatic && timerAdvances && stoppedStable && cleanedUp;
            File.WriteAllText(Path.Combine(folder, "result.txt"),
                (passed ? "PASS" : "FAIL") + ": fixed waiting scene rendered at " + open.PixelWidth + "x" + open.PixelHeight +
                "; open/closed samples differ at " + changed + " pixels; changed-region=" + changedArea.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "; repeated frozen-frame differences=" + staticChanges + "; timing checks=" + timings + "; eye-only check=" + eyeOnly + "; blink interval=" + settings.BlinkInterval.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                "s; duration=" + settings.BlinkDuration.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                "s; hold=" + settings.TailHoldTime.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "s.\n" +
                (foregroundOnly ? "PASS" : "FAIL") + ": moving lights change " + lightChanges + "/" + laterLightChanges +
                " pixels between samples; first light-region=" + lightArea.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "; later light-region=" + laterLightArea.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ". Both regions stay within the foreground title area; character, camera, text layout and background remain fixed in the isolated light check.\n" +
                (hairOnly && resetChanges == 0 ? "PASS" : "FAIL") + ": isolated outer-hair samples change " +
                hairChanges + "/" + laterHairChanges + "/" + lastHairChanges + " pixels; regions=" +
                hairArea.ToString(System.Globalization.CultureInfo.InvariantCulture) + " / " +
                laterHairArea.ToString(System.Globalization.CultureInfo.InvariantCulture) + " / " +
                lastHairArea.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "; allowed=" + allowedHair.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "; protected face, ear, horn and text differences=" + fixedHairChanges +
                "; exact t=0 reset differences=" + resetChanges + "; film camera time remains fixed.\n" +
                (reducedStatic ? "PASS" : "FAIL") + ": reduced-motion pixel differences=" + reducedChanges + "; no timer runs.\n" +
                (timerAdvances && stoppedStable && cleanedUp ? "PASS" : "FAIL") + ": waiting timer advances=" + timerAdvances +
                "; film time fixed=" + (scene.Time == frozenTime) + "; Stop preserves the last frame=" + stoppedStable +
                "; Dispose stops and detaches timer, viewbox, scene and all waiting visuals=" + cleanedUp + ". App activation was not executed.");
            return passed ? 0 : 1;
        }

        /// <summary>Renders waiting frames for visual QA, without opening or activating an app.</summary>
        public static void RenderFrames(string root, Settings settings, string folder, double seconds, int framesPerSecond,
            bool reduced = false, bool preferDetailTextures = true)
        {
            if (seconds <= 0 || framesPerSecond < 1 || framesPerSecond > 60) throw new ArgumentOutOfRangeException("seconds");
            Directory.CreateDirectory(folder);
            using (var waiting = new WaitingTail(root, settings, reduced, settings.TailHoldTime, preferDetailTextures))
            {
                Scene sample = waiting.scene;
                sample.Measure(new Size(Scene.DesignWidth, Scene.DesignHeight));
                sample.Arrange(new Rect(0, 0, Scene.DesignWidth, Scene.DesignHeight));
                int frames = (int)Math.Ceiling(seconds * framesPerSecond);
                for (int i = 0; i < frames; i++)
                {
                    double time = i / (double)framesPerSecond;
                    sample.EyeOpennessOverride = Openness(time, settings, reduced);
                    sample.SetWaitingEffectsTime(time);
                    sample.InvalidateVisual();
                    sample.UpdateLayout();
                    Save(Capture(sample), Path.Combine(folder, "waiting-" + i.ToString("D4") + ".png"));
                }
            }
        }

        private static void PumpDispatcher(TimeSpan duration)
        {
            var frame = new DispatcherFrame();
            var end = new DispatcherTimer(DispatcherPriority.Send) { Interval = duration };
            EventHandler stop = delegate { end.Stop(); frame.Continue = false; };
            end.Tick += stop;
            try { end.Start(); Dispatcher.PushFrame(frame); }
            finally { end.Stop(); end.Tick -= stop; }
        }

        private static BitmapSource Capture(Scene scene)
        {
            var bitmap = new RenderTargetBitmap((int)Scene.DesignWidth, (int)Scene.DesignHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(scene);
            bitmap.Freeze();
            return bitmap;
        }

        private static void Save(BitmapSource bitmap, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
        }

        private static int CountDifferentPixels(BitmapSource first, BitmapSource second, out Rect changedArea)
        {
            int stride = first.PixelWidth * 4;
            var a = new byte[stride * first.PixelHeight];
            var b = new byte[stride * second.PixelHeight];
            first.CopyPixels(a, stride, 0);
            second.CopyPixels(b, stride, 0);
            int changed = 0;
            int minX = first.PixelWidth, minY = first.PixelHeight, maxX = -1, maxY = -1;
            for (int pixel = 0; pixel < a.Length; pixel += 4)
                if (a[pixel] != b[pixel] || a[pixel + 1] != b[pixel + 1] || a[pixel + 2] != b[pixel + 2] || a[pixel + 3] != b[pixel + 3])
                {
                    changed++;
                    int x = (pixel / 4) % first.PixelWidth;
                    int y = (pixel / 4) / first.PixelWidth;
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                }
            changedArea = changed == 0 ? Rect.Empty : new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return changed;
        }
        private static int CountDifferentPixelsWithin(BitmapSource first, BitmapSource second, Rect[] regions)
        {
            int stride = first.PixelWidth * 4;
            var a = new byte[stride * first.PixelHeight];
            var b = new byte[stride * second.PixelHeight];
            first.CopyPixels(a, stride, 0);
            second.CopyPixels(b, stride, 0);
            int changed = 0;
            for (int y = 0; y < first.PixelHeight; y++)
                for (int x = 0; x < first.PixelWidth; x++)
                {
                    int pixel = y * stride + x * 4;
                    if (a[pixel] == b[pixel] && a[pixel + 1] == b[pixel + 1] &&
                        a[pixel + 2] == b[pixel + 2] && a[pixel + 3] == b[pixel + 3]) continue;
                    foreach (Rect region in regions)
                        if (region.Contains(new Point(x + 0.5, y + 0.5))) { changed++; break; }
                }
            return changed;
        }
    }
}
