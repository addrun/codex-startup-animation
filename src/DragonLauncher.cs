using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

namespace DragonLauncher
{
    public sealed class Settings
    {
        public double Duration = 8.0;
        public int FrameRate = 60;
        public string AppId = "OpenAI.Codex_2p2nqsd0c76g0!App";
        public string Exe = "", Arguments = "";
        public string Image = "assets/dragon-cinematic-open.png";
        public string ClosedImage = "assets/dragon-cinematic-closed.png";
        public string VideoFile = "output/gpt-dragon-cinematic-2k-60fps.mp4";
        public bool RespectReducedMotion = true;
        public bool PreferDetailTextures = true;
        public bool SoundEnabled = true;
        public double SoundVolume = 0.44;
        public string SoundFile = "assets/dragon-startup.wav";
        public double StartupTimeout = 40.0;
        public double WindowReadyDelay = 0.5;
        public bool RequirePageContent = true;
        public double TailHoldTime = 7.75;
        public double IntroDuration = 8.30;
        public double ReadyFadeDuration = 0.45;
        public double BlinkInterval = 4.0;
        public double BlinkDuration = 0.28;
        public static Settings Load(string root)
        {
            var result = new Settings();
            var file = Path.Combine(root, "launcher.config.xml");
            if (!File.Exists(file)) return result;
            var xml = XDocument.Load(file).Root;
            if (xml == null) throw new InvalidDataException("启动配置为空。");
            Func<string, string, string> value = (name, fallback) =>
                xml.Element(name) == null ? fallback : xml.Element(name).Value.Trim();
            result.Duration = double.Parse(value("durationSeconds", "8.0"), CultureInfo.InvariantCulture);
            if (double.IsNaN(result.Duration) || double.IsInfinity(result.Duration) || result.Duration < 1 || result.Duration > 30)
                throw new InvalidDataException("durationSeconds 必须为 1 到 30 秒。");
            result.AppId = value("appUserModelId", result.AppId);
            result.Exe = value("executablePath", "");
            result.Arguments = value("executableArguments", "");
            result.Image = value("characterImage", result.Image);
            result.ClosedImage = value("closedEyeImage", result.ClosedImage);
            result.VideoFile = value("videoFile", result.VideoFile);
            result.FrameRate = int.Parse(value("frameRate", "60"), CultureInfo.InvariantCulture);
            if (result.FrameRate < 1 || result.FrameRate > 60)
                throw new InvalidDataException("frameRate 必须为 1 到 60 帧/秒。");
            result.RespectReducedMotion = bool.Parse(value("respectReducedMotion", "true"));
            result.PreferDetailTextures = bool.Parse(value("preferDetailTextures", "true"));
            result.SoundEnabled = bool.Parse(value("soundEnabled", "true"));
            result.SoundVolume = double.Parse(value("soundVolume", "0.44"), CultureInfo.InvariantCulture);
            result.SoundFile = value("soundFile", result.SoundFile);
            if (double.IsNaN(result.SoundVolume) || double.IsInfinity(result.SoundVolume) || result.SoundVolume < 0 || result.SoundVolume > 1)
                throw new InvalidDataException("soundVolume 必须为 0 到 1。");
            result.StartupTimeout = double.Parse(value("startupTimeoutSeconds", "40.0"), CultureInfo.InvariantCulture);
            result.WindowReadyDelay = double.Parse(value("windowReadyDelaySeconds", "0.5"), CultureInfo.InvariantCulture);
            result.RequirePageContent = bool.Parse(value("requirePageContent", "true"));
            result.TailHoldTime = double.Parse(value("tailHoldSeconds", "7.75"), CultureInfo.InvariantCulture);
            result.IntroDuration = double.Parse(value("introDurationSeconds", "8.30"), CultureInfo.InvariantCulture);
            result.ReadyFadeDuration = double.Parse(value("readyFadeSeconds", "0.45"), CultureInfo.InvariantCulture);
            if (double.IsNaN(result.IntroDuration) || double.IsInfinity(result.IntroDuration) || result.IntroDuration < result.TailHoldTime || result.IntroDuration > 30 ||
                double.IsNaN(result.ReadyFadeDuration) || double.IsInfinity(result.ReadyFadeDuration) || result.ReadyFadeDuration < 0.1 || result.ReadyFadeDuration > 1)
                throw new InvalidDataException("introDurationSeconds 必须覆盖片头且不超过 30 秒；readyFadeSeconds 必须为 0.1 到 1 秒。");
            result.BlinkInterval = double.Parse(value("blinkIntervalSeconds", "4.0"), CultureInfo.InvariantCulture);
            result.BlinkDuration = double.Parse(value("blinkDurationSeconds", "0.28"), CultureInfo.InvariantCulture);
            if (double.IsNaN(result.TailHoldTime) || double.IsInfinity(result.TailHoldTime) || result.TailHoldTime < 0.1 || result.TailHoldTime >= result.Duration ||
                double.IsNaN(result.BlinkInterval) || double.IsInfinity(result.BlinkInterval) || result.BlinkInterval < 1 || result.BlinkInterval > 30 ||
                double.IsNaN(result.BlinkDuration) || double.IsInfinity(result.BlinkDuration) || result.BlinkDuration < 0.1 || result.BlinkDuration > 1 || result.BlinkDuration >= result.BlinkInterval)
                throw new InvalidDataException("片尾时间必须小于 durationSeconds；眨眼间隔为 1 到 30 秒，持续时间为 0.1 到 1 秒。");
            if (double.IsNaN(result.StartupTimeout) || double.IsInfinity(result.StartupTimeout) || result.StartupTimeout < 1 || result.StartupTimeout > 120)
                throw new InvalidDataException("startupTimeoutSeconds 必须为 1 到 120 秒。");
            if (double.IsNaN(result.WindowReadyDelay) || double.IsInfinity(result.WindowReadyDelay) || result.WindowReadyDelay < 0 || result.WindowReadyDelay > 5 || result.WindowReadyDelay >= result.StartupTimeout)
                throw new InvalidDataException("windowReadyDelaySeconds 必须为 0 到 5 秒，并小于启动超时。");
            if (string.IsNullOrWhiteSpace(result.AppId) && string.IsNullOrWhiteSpace(result.Exe))
                throw new InvalidDataException("请配置 appUserModelId 或 executablePath。");
            return result;
        }
    }

    public static class AppActivation
    {
        [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
        private class ActivationManager { }
        [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivationManager
        {
            [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id,
                [MarshalAs(UnmanagedType.LPWStr)] string args, uint options, out uint pid);
            [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string id,
                IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint pid);
            [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string id,
                IntPtr items, out uint pid);
        }
        public static uint Open(Settings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.Exe))
            {
                var exe = Environment.ExpandEnvironmentVariables(settings.Exe);
                if (!File.Exists(exe)) throw new FileNotFoundException("找不到配置的桌面应用，请检查 executablePath。", exe);
                var process = Process.Start(new ProcessStartInfo(exe, settings.Arguments) { UseShellExecute = true });
                return process == null ? 0 : (uint)process.Id;
            }
            var activation = (IActivationManager)new ActivationManager();
            try
            {
                uint pid;
                var result = activation.ActivateApplication(settings.AppId, "", 0, out pid);
                Marshal.ThrowExceptionForHR(result);
                return pid;
            }
            finally { Marshal.ReleaseComObject(activation); }
        }
    }

    public sealed class Splash : Window, IStartupPresentation
    {
        private readonly Scene scene;
        private readonly Settings settings;
        private readonly bool preview;
        private readonly bool reducedMotion;
        private readonly bool waitingPreview;
        private readonly StartupCoordinator startup;
        private readonly Stopwatch stopwatch = new Stopwatch();
        private bool done;
        private bool introRecorded, readyClose, closeAfterFade;
        private ReadyExitTransition exitTransition;
        public bool IntroComplete
        {
            get
            {
                bool complete = reducedMotion || stopwatch.Elapsed.TotalSeconds >= settings.IntroDuration;
                if (complete && !introRecorded)
                {
                    introRecorded = true;
                    if (startup != null) startup.Record("intro_completed", reducedMotion ? "reduced motion; no mandatory animated intro" : "fallback scene reached " + settings.IntroDuration.ToString("F3", CultureInfo.InvariantCulture) + "s");
                }
                return complete;
            }
        }
        public void RequestReadyFade() { readyClose = !reducedMotion; }
        public int CloseDeadlineMilliseconds { get { return readyClose ? (int)(settings.ReadyFadeDuration * 1000) + 1000 : 250; } }
        public Splash(Scene graphic, Settings config, bool onlyPreview, StartupCoordinator coordinator, bool reduced, bool waitingOnly = false)
        {
            scene = graphic; settings = config; preview = onlyPreview; startup = coordinator; reducedMotion = reduced;
            waitingPreview = waitingOnly;
            scene.Launching = !preview;
            Title = "GPT 龙娘 · Codex 启动";
            Width = Math.Min(Scene.DesignWidth, SystemParameters.WorkArea.Width * 0.9);
            Height = Width * Scene.DesignHeight / Scene.DesignWidth;
            if (Height > SystemParameters.WorkArea.Height * 0.9)
            { Height = SystemParameters.WorkArea.Height * 0.9; Width = Height * Scene.DesignWidth / Scene.DesignHeight; }
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent;
            Topmost = true; ShowInTaskbar = false;
            Content = new System.Windows.Controls.Viewbox { Child = scene, Stretch = Stretch.Uniform };
            ContentRendered += delegate { stopwatch.Start(); CompositionTarget.Rendering += Tick; Activate(); };
            Closed += delegate { done = true; CompositionTarget.Rendering -= Tick; stopwatch.Stop(); if (exitTransition != null) { exitTransition.Dispose(); exitTransition = null; } };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            { if (e.Key == Key.Escape || e.Key == Key.Enter || e.Key == Key.Space) { e.Handled = true; Finish(); } };
            MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                var p = e.GetPosition(scene);
                if (p.X > Scene.DesignWidth - 55 && p.Y < 67) Finish();
                else if (p.Y < 73) DragMove();
            };
        }
        private void Tick(object sender, EventArgs e)
        {
            double elapsed = stopwatch.Elapsed.TotalSeconds;
            scene.SetTime(waitingPreview || reducedMotion ? settings.TailHoldTime : preview ? elapsed : Math.Min(settings.TailHoldTime, elapsed));
            if (waitingPreview || (!preview && elapsed >= settings.TailHoldTime))
            {
                double waitingElapsed = waitingPreview ? elapsed : elapsed - settings.TailHoldTime;
                scene.EyeOpennessOverride = WaitingTail.Openness(waitingElapsed, settings, reducedMotion);
                scene.InvalidateVisual();
                scene.SetWaitingEffectsTime(reducedMotion ? 0 : waitingElapsed);
            }
            if (stopwatch.Elapsed.TotalSeconds >= (waitingPreview ? 16 : settings.Duration))
            {
                if (preview) Finish();
            }
        }
        private void Finish()
        {
            if (done) return; done = true; CompositionTarget.Rendering -= Tick;
            if (startup != null) startup.Dismiss("skip");
            else Close();
        }
        protected override void OnClosing(System.ComponentModel.CancelEventArgs args)
        {
            if (readyClose && !closeAfterFade)
            {
                args.Cancel = true;
                if (exitTransition == null)
                {
                    done = true; CompositionTarget.Rendering -= Tick;
                    exitTransition = new ReadyExitTransition(this, (FrameworkElement)Content, settings.ReadyFadeDuration,
                        startup == null ? (Action<string, string>)null : startup.Record,
                        delegate { closeAfterFade = true; Close(); });
                    exitTransition.Start();
                }
            }
            base.OnClosing(args);
        }
    }

    public static class Program
    {
        private static void SaveFrame(Scene scene, double time, string path)
        {
            scene.SetTime(time); scene.UpdateLayout();
            // Render the vector scene directly at QHD. A VisualBrush scales
            // the scene before rasterization, rather than enlarging a 720p PNG.
            var capture = new DrawingVisual();
            using (var dc = capture.RenderOpen())
                dc.DrawRectangle(new VisualBrush(scene) { Stretch = Stretch.Fill }, null,
                    new Rect(0, 0, Scene.ExportWidth, Scene.ExportHeight));
            var bitmap = new RenderTargetBitmap(Scene.ExportWidth, Scene.ExportHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(capture);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
        }
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                StartupGeometry.EnableDpiAwareness();
                var root = AppDomain.CurrentDomain.BaseDirectory;
                var config = Settings.Load(root);
                // Internal read-only modes bypass animation locks and every activation path.
                if (Array.IndexOf(args, "--page-probe-server") >= 0)
                {
                    if (args.Length != 2) throw new ArgumentException("--page-probe-server <parent-pid> is exclusive.");
                    return PageReadiness.RunServer(root, config, int.Parse(args[1], CultureInfo.InvariantCulture));
                }
                int imageCheck = Array.IndexOf(args, "--image-readiness-test");
                if (imageCheck >= 0)
                {
                    if (args.Length != 3 || imageCheck != 0) throw new ArgumentException("--image-readiness-test <image> <report>");
                    return PageReadiness.VerifyImage(args[1], args[2]);
                }
                bool observe = Array.IndexOf(args, "--observe") >= 0;
                bool tailTest = Array.IndexOf(args, "--tail-test") >= 0;
                bool waitingPreview = Array.IndexOf(args, "--waiting-preview") >= 0;
                bool previewTest = Array.IndexOf(args, "--preview-test") >= 0;
                bool preview = Array.IndexOf(args, "--preview") >= 0 || previewTest || waitingPreview;
                bool startupTest = Array.IndexOf(args, "--startup-test") >= 0;
                if (startupTest || previewTest || Array.IndexOf(args, "--silent") >= 0) config.SoundEnabled = false;
                bool reduced = Array.IndexOf(args, "--reduced-motion") >= 0 || (config.RespectReducedMotion && !SystemParameters.ClientAreaAnimation);
                int render = Array.IndexOf(args, "--render");
                bool selfTest = Array.IndexOf(args, "--self-test") >= 0;
                bool eyeTest = Array.IndexOf(args, "--eye-test") >= 0;
                bool lineTest = Array.IndexOf(args, "--line-test") >= 0;
                if (observe && (preview || render >= 0 || selfTest || eyeTest || lineTest || tailTest))
                    throw new InvalidDataException("--observe 不能与预览或离屏检查一起使用。");
                if (Array.IndexOf(args, "--activation-test") >= 0)
                {
                    if (preview || render >= 0 || selfTest || eyeTest || lineTest || Array.IndexOf(args, "--observe") >= 0 || Array.IndexOf(args, "--tail-test") >= 0)
                        throw new InvalidDataException("预览或离屏检查不能与 --activation-test 一起使用。");
                    uint pid = AppActivation.Open(config);
                    var logFolder = Path.Combine(root, "output");
                    Directory.CreateDirectory(logFolder);
                    File.WriteAllText(Path.Combine(logFolder, "activation-test.txt"), "PASS: desktop app activation accepted. PID: " + pid);
                    return 0;
                }
                if (tailTest)
                {
                    return WaitingTail.Verify(root, config);
                }
                if (render >= 0 || selfTest || eyeTest || lineTest)
                {
                    var scene = new Scene(root, config, false);
                    if (render >= 0 && render + 1 >= args.Length) throw new ArgumentException("--render 后需要指定输出目录。");
                    string folder = selfTest ? Path.Combine(root, "output", "self-test") : eyeTest ? Path.Combine(root, "output", "eye-test") : lineTest ? Path.Combine(root, "output", "line-test") : Path.GetFullPath(args[render + 1]);
                    Directory.CreateDirectory(folder);
                    scene.Measure(new Size(Scene.DesignWidth, Scene.DesignHeight));
                    scene.Arrange(new Rect(0, 0, Scene.DesignWidth, Scene.DesignHeight));
                    scene.UpdateLayout();
                    if (lineTest)
                    {
                        double[] samples = { 0.15, 0.30, 0.45, 0.60, 0.85, 1.10, 1.35, 1.60, 1.90, 2.15, 2.40, 2.90 };
                        string report = "Directional light-path samples rendered for visual inspection. App activation was not executed.\n";
                        for (int i = 0; i < samples.Length; i++)
                        {
                            double seconds = samples[i] * config.Duration / 8;
                            SaveFrame(scene, seconds, Path.Combine(folder, "line-" + i.ToString("D2") + ".png"));
                            report += scene.LineSample(seconds) + "\n";
                        }
                        File.WriteAllText(Path.Combine(folder, "result.txt"), report);
                    }
                    else if (eyeTest)
                    {
                        for (int i = 0; i <= 12; i++)
                            SaveFrame(scene, (3.25 + i * 0.125) * config.Duration / 8, Path.Combine(folder, "eye-" + i.ToString("D2") + ".png"));
                        File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS: 13 samples across the complete eyelid motion rendered for visual inspection. App activation was not executed.");
                    }
                    else if (selfTest)
                    {
                        SaveFrame(scene, config.Duration * 0.2, Path.Combine(folder, "wireframe.png"));
                        SaveFrame(scene, config.Duration * 0.4, Path.Combine(folder, "materialize.png"));
                        SaveFrame(scene, config.Duration * 0.7, Path.Combine(folder, "scene.png"));
                        File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS: config, both eye textures, WPF wireframe/materialize/awake rendering. App activation was not executed.\nImage: " + config.Image + "\nFPS: " + config.FrameRate);
                    }
                    else
                    {
                        int frames = (int)Math.Ceiling(config.Duration * config.FrameRate);
                        for (int i = 0; i < frames; i++) SaveFrame(scene, i / (double)config.FrameRate, Path.Combine(folder, "frame-" + i.ToString("D4") + ".png"));
                        SaveFrame(scene, config.Duration * 0.7, Path.Combine(folder, "poster.png"));
                    }
                    return 0;
                }
                bool acquired;
                using (var mutex = new Mutex(true, preview ? "Local\\GPTDragonCodexPreview" : "Local\\GPTDragonCodexLauncher", out acquired))
                {
                    if (!acquired)
                    {
                        if (previewTest) throw new InvalidOperationException("已有启动器正在运行，无法执行独立播放验证。");
                        return 0;
                    }
                    try
                    {
                        var app = new Application();
                        StartupCoordinator startup = null;
                        if (!preview)
                        {
                            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                            startup = new StartupCoordinator(app, config, root, startupTest || observe, observe);
                            startup.Start();
                        }
                        string film = Path.GetFullPath(Path.Combine(root, config.VideoFile));
                        Window window;
                        if (waitingPreview)
                        {
                            window = new Splash(new Scene(root, config, reduced), config, true, null, reduced, true);
                        }
                        else if ((!reduced || previewTest) && File.Exists(film))
                        {
                            window = new FilmSplash(film, config, preview, previewTest, startup, reduced);
                        }
                        else if (previewTest) throw new FileNotFoundException("60 帧成片不存在。", film);
                        else
                        {
                            try { window = new Splash(new Scene(root, config, reduced), config, preview, startup, reduced); }
                            catch (Exception error)
                            {
                                if (startup == null) throw;
                                startup.Record("fallback_failed", error.GetType().Name + ": " + error.Message);
                                // A broken optional picture must not prevent the activation already underway.
                                window = new Window
                                {
                                    Title = "Codex 正在启动", Width = 520, Height = 180,
                                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                                    Content = "Codex 正在启动… 关闭此窗口不会停止 Codex。"
                                };
                            }
                        }
                        if (startupTest) window.ShowInTaskbar = true;
                        if (startup != null) startup.Attach(window);
                        int exitCode = app.Run(window);
                        if (previewTest && !((FilmSplash)window).PlaybackSucceeded) return 1;
                        return exitCode;
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception error)
            {
                var log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output");
                try { Directory.CreateDirectory(log); File.WriteAllText(Path.Combine(log, "launcher-error.txt"), error.ToString()); } catch { }
                if (Array.IndexOf(args, "--render") < 0 && Array.IndexOf(args, "--self-test") < 0 && Array.IndexOf(args, "--eye-test") < 0 && Array.IndexOf(args, "--line-test") < 0 && Array.IndexOf(args, "--activation-test") < 0 && Array.IndexOf(args, "--preview-test") < 0 && Array.IndexOf(args, "--observe") < 0 && Array.IndexOf(args, "--tail-test") < 0 && Array.IndexOf(args, "--page-probe-server") < 0 && Array.IndexOf(args, "--image-readiness-test") < 0)
                    MessageBox.Show(error.Message, "GPT 龙娘启动器", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }
}
