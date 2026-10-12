using System;
using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DragonLauncher
{
    /// <summary>Plays the packaged film while the independent coordinator starts Codex.</summary>
    public sealed class FilmSplash : Window, IStartupPresentation
    {
        private readonly string filmPath;
        private readonly string rootPath;
        private readonly Settings settings;
        private readonly bool preview;
        private readonly bool reducedMotion;
        private readonly bool quietErrors;
        private readonly StartupCoordinator startup;
        private readonly MediaElement player;
        private readonly Grid surface;
        private readonly Button cancel;
        private readonly DispatcherTimer watchdog;
        private readonly Stopwatch clock = new Stopwatch();
        private readonly Stopwatch playbackClock = new Stopwatch();
        private WaitingTail tail;
        private Grid tailSurface;
        private Image firstTailFrame;
        private StartupSound sound;
        private readonly Stopwatch transitionClock = new Stopwatch();
        private readonly Stopwatch tailAudioClock = new Stopwatch();
        private ReadyExitTransition exitTransition;
        private bool introRecorded, tailRunning, readyClose, closeAfterVisualFade;
        private volatile bool preparationCancelled;
        private bool preparing, preparationFailed, handoffStarted, videoReleased, finishingTail;
        private bool closeFading, closeAfterFade;
        private bool previewWaitingSound;
        private int opaqueFrames, liveFrames;
        private double handoffPosition;
        private bool done, cleaned, opened, waitingTail, mediaStopped, hasRendered;
        private double duration, lastPosition, lastProgress, previewSoundDeadline;
        public bool PlaybackSucceeded { get; private set; }
        public bool IntroComplete
        {
            get
            {
                bool soundComplete = sound == null || sound.IsFinished || !settings.SoundEnabled || settings.SoundVolume <= 0;
                bool complete = reducedMotion || (waitingTail && (tailRunning || preparationFailed) &&
                    handoffPosition + tailAudioClock.Elapsed.TotalSeconds >= settings.IntroDuration && soundComplete);
                if (complete && !introRecorded)
                {
                    introRecorded = true;
                    Record("intro_completed", "presentation position=" + (handoffPosition + tailAudioClock.Elapsed.TotalSeconds).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                        "s; complete cue ended or silent; ready handoff allowed");
                }
                return complete;
            }
        }
        public void RequestReadyFade()
        {
            readyClose = !reducedMotion && tailRunning && videoReleased;
            if (!reducedMotion && !readyClose) Record("ready_fade_unavailable", "no composed waiting scene; close safely without capturing native video");
        }
        public int CloseDeadlineMilliseconds { get { return readyClose ? (int)(settings.ReadyFadeDuration * 1000) + 1000 : 250; } }

        public FilmSplash(string path, Settings config, bool onlyPreview, bool quietErrors, StartupCoordinator coordinator)
            : this(path, config, onlyPreview, quietErrors, coordinator,
                config != null && config.RespectReducedMotion && !SystemParameters.ClientAreaAnimation)
        {
        }

        public FilmSplash(string path, Settings config, bool onlyPreview, bool quietErrors, StartupCoordinator coordinator, bool reduceMotion)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("未配置启动动画视频路径。", "path");
            filmPath = Path.GetFullPath(path);
            rootPath = AppDomain.CurrentDomain.BaseDirectory;
            if (!File.Exists(filmPath)) throw new FileNotFoundException("找不到启动动画视频，请保留 output 文件夹。", filmPath);
            settings = config;
            preview = onlyPreview;
            reducedMotion = reduceMotion;
            this.quietErrors = quietErrors;
            startup = coordinator;
            duration = settings.Duration;

            Title = "GPT 龙娘 · Codex 启动";
            Width = Math.Min(Scene.DesignWidth, SystemParameters.WorkArea.Width * 0.9);
            Height = Width * Scene.DesignHeight / Scene.DesignWidth;
            if (Height > SystemParameters.WorkArea.Height * 0.9)
            {
                Height = SystemParameters.WorkArea.Height * 0.9;
                Width = Height * Scene.DesignWidth / Scene.DesignHeight;
            }
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // An opaque native window allows WPF's video surface to use hardware composition.
            AllowsTransparency = false;
            Background = Brushes.Black;
            Topmost = true;
            ShowInTaskbar = false;

            player = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                Stretch = Stretch.Uniform,
                IsMuted = true,
                Volume = 0,
                IsHitTestVisible = false
            };
            surface = new Grid { Background = Brushes.Black };
            surface.Children.Add(player);
            double scale = Width / Scene.DesignWidth;
            cancel = new Button
            {
                Content = "×",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 24 * scale,
                Foreground = new SolidColorBrush(Color.FromRgb(153, 201, 209)),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Width = 48 * scale,
                Height = 48 * scale,
                Margin = new Thickness(0, 10 * scale, 14 * scale, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = onlyPreview ? "关闭预览" : "关闭动画（Codex 继续启动）",
                Focusable = false
            };
            surface.Children.Add(cancel);
            // Cover the old baked-in skip hint/progress with the current startup controls.
            surface.Children.Add(new Border
            {
                Height = 50 * scale,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromRgb(9, 18, 24)),
                Child = new TextBlock
                {
                    Text = onlyPreview ? "动画预览 · Esc 关闭" : "Codex 正在启动 · 窗口出现后自动进入 · Esc 跳过动画",
                    Foreground = new SolidColorBrush(Color.FromRgb(153, 201, 209)),
                    FontSize = 13 * scale,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            });
            Content = surface;

            watchdog = new DispatcherTimer(DispatcherPriority.Background);
            watchdog.Interval = TimeSpan.FromMilliseconds(33);
            watchdog.Tick += OnWatchdog;
            player.MediaOpened += OnMediaOpened;
            player.MediaEnded += OnMediaEnded;
            player.MediaFailed += OnMediaFailed;
            cancel.Click += OnCancel;
            surface.MouseLeftButtonDown += OnSurfaceMouseDown;
            PreviewKeyDown += OnPreviewKeyDown;
            ContentRendered += OnContentRendered;
            Closed += OnClosed;
            IsVisibleChanged += OnVisibilityChanged;
        }

        private void OnContentRendered(object sender, EventArgs e)
        {
            ContentRendered -= OnContentRendered;
            if (done) return;
            Activate();
            Focus();
            hasRendered = true;
            clock.Start();
            watchdog.Start();
            if (!preview) CompositionTarget.Rendering += OnFrame;
            try
            {
                player.Source = new Uri(filmPath, UriKind.Absolute);
                player.Play();
                if (startup != null) startup.Record("animation_started", "native video playback requested");
            }
            catch (Exception error) { Fail("启动动画无法播放。", error); }
        }

        private void OnMediaOpened(object sender, RoutedEventArgs e)
        {
            if (done) return;
            opened = true;
            if (player.NaturalDuration.HasTimeSpan)
                duration = player.NaturalDuration.TimeSpan.TotalSeconds;
            if (double.IsNaN(duration) || double.IsInfinity(duration) || duration <= 0 || duration > 60)
            {
                Fail("启动动画时长无效，视频应在 60 秒以内。", null);
                return;
            }
            lastPosition = player.Position.TotalSeconds;
            lastProgress = clock.Elapsed.TotalSeconds;
            playbackClock.Restart();
            Record("media_opened", "utc=" + DateTime.UtcNow.ToString("o") + "; video position=" + lastPosition.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            if (!reducedMotion)
            {
                sound = new StartupSound(rootPath, settings, Record);
                sound.TrackVideo(lastPosition, duration);
            }
            if (!preview) PrepareTail();
        }

        private void OnWatchdog(object sender, EventArgs e)
        {
            if (done) return;
            double elapsed = clock.Elapsed.TotalSeconds;
            if (previewWaitingSound)
            {
                if (sound == null || sound.IsFinished || elapsed >= previewSoundDeadline) LaunchClose();
                return;
            }
            if (!opened)
            {
                if (elapsed >= 10) Fail("视频解码器在 10 秒内未能打开启动动画。", null);
                return;
            }
            double position = player.Position.TotalSeconds;
            // MediaElement may reset Position before delivering MediaEnded.
            // Recognize only a reset at the actual end, so a preview does not
            // mistake it for a request to replay and silence the longer cue.
            if (preview && position < lastPosition - 0.10 && lastPosition >= duration - 0.25)
            {
                if (sound != null) sound.ContinueAfterVideo(duration, duration);
                return;
            }
            if (sound != null) sound.TrackVideo(position, duration);
            if (!preview && position >= TailStartSeconds())
            {
                BeginWaitingTail(position);
                return;
            }
            if (position > lastPosition + 0.005)
            {
                lastPosition = position;
                lastProgress = elapsed;
            }
            if (elapsed - lastProgress > 5)
                Fail("启动动画播放停止超过 5 秒。", null);
            else if (elapsed > duration + 10)
                Fail("启动动画超出预期播放时长。", null);
        }

        private void OnFrame(object sender, EventArgs e)
        {
            if (done || preview || !opened) return;
            if (waitingTail) { AdvanceTailHandoff(); return; }
            double position = player.Position.TotalSeconds;
            if (position >= TailStartSeconds()) BeginWaitingTail(position);
        }

        private void OnMediaEnded(object sender, RoutedEventArgs e)
        {
            if (done) return;
            if (!preview)
            {
                PlaybackSucceeded = true;
                BeginWaitingTail(TailStartSeconds());
                return;
            }
            PlaybackSucceeded = true;
            playbackClock.Stop();
            if (preview)
            {
                try
                {
                    string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output");
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, "native-preview-validation.txt"),
                        "PASS: native MediaElement playback reached MediaEnded. App activation was not executed.\n" +
                        "NaturalVideoWidth: " + player.NaturalVideoWidth + "\n" +
                        "NaturalVideoHeight: " + player.NaturalVideoHeight + "\n" +
                        "NaturalDurationSeconds: " + duration.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + "\n" +
                        "PlaybackElapsedSeconds: " + playbackClock.Elapsed.TotalSeconds.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + "\n" +
                        "Video: " + filmPath);
                }
                catch (Exception error)
                {
                    PlaybackSucceeded = false;
                    Fail("启动动画播放完成，但无法保存验证日志。", error);
                    return;
                }
            }
            if (sound != null && settings.SoundEnabled && settings.SoundVolume > 0 && !sound.IsFinished)
            {
                // Preview keeps the last video frame for the remaining cue.
                // Startup handoff remains solely controlled by page readiness.
                previewWaitingSound = true;
                previewSoundDeadline = clock.Elapsed.TotalSeconds + Math.Max(0, 30 - duration) + 1;
                sound.ContinueAfterVideo(duration, duration);
                return;
            }
            LaunchClose();
        }

        private double TailStartSeconds()
        {
            return Math.Max(0.1, Math.Min(settings.TailHoldTime, duration - 0.25));
        }

        private void BeginWaitingTail(double videoPosition)
        {
            if (done || waitingTail || preview) return;
            waitingTail = true;
            watchdog.Stop();
            playbackClock.Stop();
            handoffPosition = videoPosition;
            tailAudioClock.Restart();
            try { player.Pause(); }
            catch { }
            if (sound != null) sound.ContinueAfterVideo(videoPosition, duration);
            if (tail != null) StartTailHandoff();
            else if (preparationFailed) HoldVideoFallback("preparation failed");
            // Keep the decoded frame when preparation is still in flight.
            // The worker's dispatcher callback starts the handoff when ready.
        }

        private void PrepareTail()
        {
            if (preparing || done || preview) return;
            preparing = true;
            double hold = TailStartSeconds();
            Record("tail_prepare_started", "video position=" + player.Position.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            var worker = new Thread(delegate() {
                var preparation = Stopwatch.StartNew();
                Scene.WaitingAssets assets = null;
                Exception failure = null;
                try { assets = Scene.PrepareWaiting(rootPath, settings, reducedMotion, hold, delegate { return preparationCancelled; }); }
                catch (Exception error) { failure = error; }
                if (preparationCancelled || Dispatcher.HasShutdownStarted) return;
                try
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate {
                        if (done || preparationCancelled) return;
                        InstallPreparedTail(assets, failure, hold, preparation.Elapsed.TotalMilliseconds);
                    }));
                }
                catch (InvalidOperationException) { }
            });
            worker.IsBackground = true;
            worker.Name = "Dragon waiting frame preparation";
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        private void InstallPreparedTail(Scene.WaitingAssets assets, Exception failure, double hold, double preparationMs)
        {
            if (assets == null || failure != null)
            {
                preparationFailed = true;
                Record("tail_prepare_failed", failure == null ? "cancelled" : failure.GetType().Name + ": " + failure.Message);
                if (waitingTail) HoldVideoFallback("preparation failed");
                return;
            }
            var mount = Stopwatch.StartNew();
            try
            {
                tail = new WaitingTail(rootPath, settings, reducedMotion, hold, true, assets);
                firstTailFrame = new Image { Source = assets.FirstFrame, Stretch = Stretch.Uniform, IsHitTestVisible = false };
                tailSurface = new Grid { Opacity = 0, IsHitTestVisible = false };
                tailSurface.Children.Add(tail.Element);
                tailSurface.Children.Add(firstTailFrame);
                // The prepared, opaque frame sits above the video. Controls
                // retain their existing order and remain usable while loading.
                surface.Children.Insert(1, tailSurface);
                tailSurface.Measure(new Size(surface.ActualWidth, surface.ActualHeight));
                tailSurface.Arrange(new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
                tailSurface.UpdateLayout();
                Record("tail_prepared", "prepare_ms=" + preparationMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    "; mount_ms=" + mount.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    "; video position=" + player.Position.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                Record("tail_layout_ready", "opaque prepared image mounted; video still playing");
                if (waitingTail) StartTailHandoff();
            }
            catch (Exception error)
            {
                RemoveTail();
                preparationFailed = true;
                Record("tail_prepare_failed", error.GetType().Name + ": " + error.Message);
                if (waitingTail) HoldVideoFallback(error.Message);
            }
        }

        private void StartTailHandoff()
        {
            if (handoffStarted || tail == null || done) return;
            handoffStarted = true;
            transitionClock.Restart();
            tailSurface.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
            Record("tail_handoff_started", "video position=" + handoffPosition.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }

        private void AdvanceTailHandoff()
        {
            if (!handoffStarted || tailSurface == null) return;
            if (!videoReleased)
            {
                if (transitionClock.Elapsed.TotalMilliseconds < 140 || tailSurface.Opacity < 0.999) return;
                if (++opaqueFrames < 2) return;
                Record("tail_revealed", "video position=" + handoffPosition.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    "; handoff_ms=" + transitionClock.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    "; opaque prepared frame across two composition ticks");
                videoReleased = true;
                player.Visibility = Visibility.Collapsed;
                ReleaseMedia();
                return;
            }
            if (finishingTail || ++liveFrames < 2) return;
            finishingTail = true;
            var reveal = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(60));
            reveal.Completed += delegate {
                if (done || tail == null) return;
                tailSurface.Children.Remove(firstTailFrame);
                firstTailFrame.Source = null;
                firstTailFrame = null;
                tail.Start();
                tailRunning = true;
                CompositionTarget.Rendering -= OnFrame;
                transitionClock.Stop();
                Record("animation_tail", "fixed scene at " + TailStartSeconds().ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    "s; video position=" + handoffPosition.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    "s; handoff_ms=" + transitionClock.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    "; blink, hair and title lights=" + (reducedMotion ? "static" : "enabled"));
            };
            firstTailFrame.BeginAnimation(UIElement.OpacityProperty, reveal);
        }

        private void HoldVideoFallback(string reason)
        {
            CompositionTarget.Rendering -= OnFrame;
            Record("animation_tail_fallback", "holding last decoded frame; " + reason);
        }

        private void Record(string name, string detail)
        {
            if (startup != null) { startup.Record(name, detail); return; }
            try
            {
                string output = Path.Combine(rootPath, "output");
                Directory.CreateDirectory(output);
                File.AppendAllText(Path.Combine(output, "film-transition.log"),
                    DateTime.UtcNow.ToString("o") + "\t" + name + "\t" + detail + "\n");
            }
            catch { }
        }

        private void RemoveTail()
        {
            if (tailSurface != null)
            {
                tailSurface.BeginAnimation(UIElement.OpacityProperty, null);
                surface.Children.Remove(tailSurface);
                tailSurface.Children.Clear();
                tailSurface = null;
            }
            if (firstTailFrame != null)
            {
                firstTailFrame.BeginAnimation(UIElement.OpacityProperty, null);
                firstTailFrame.Source = null;
                firstTailFrame = null;
            }
            if (tail != null) { tail.Dispose(); tail = null; }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (readyClose && !closeAfterVisualFade)
            {
                e.Cancel = true;
                if (exitTransition == null)
                {
                    done = true;
                    QuiescePlayback();
                    if (tail != null) tail.Stop();
                    if (sound != null && sound.IsPlaying) sound.FadeOut();
                    exitTransition = new ReadyExitTransition(this, surface, settings.ReadyFadeDuration, Record,
                        delegate { closeAfterVisualFade = true; closeAfterFade = true; Close(); });
                    exitTransition.Start();
                }
                base.OnClosing(e);
                return;
            }
            if (!closeAfterFade && (closeFading || (sound != null && sound.IsPlaying)))
            {
                e.Cancel = true;
                if (!closeFading)
                {
                    closeFading = true;
                    done = true;
                    QuiescePlayback();
                    sound.FadeOut(delegate {
                        if (Dispatcher.HasShutdownStarted) return;
                        Dispatcher.BeginInvoke(DispatcherPriority.Send,
                            new Action(delegate { closeAfterFade = true; Close(); }));
                    });
                }
            }
            base.OnClosing(e);
        }

        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!hasRendered || IsVisible || mediaStopped || (exitTransition != null && exitTransition.IsRunning)) return;
            done = true;
            StopPlayback();
        }
        private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            if (waitingTail && tail != null) return;
            Fail("启动动画无法解码。请确认 MP4 文件完整，Windows 已启用媒体播放组件。", e.ErrorException);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape && e.Key != Key.Enter && e.Key != Key.Space) return;
            e.Handled = true;
            LaunchClose();
        }

        private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (done || e.ChangedButton != MouseButton.Left) return;
            Point point = e.GetPosition(surface);
            double scale = ActualWidth / Scene.DesignWidth;
            if (point.Y >= 70 * scale) return;
            e.Handled = true;
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            LaunchClose();
        }

        private void LaunchClose()
        {
            if (done) return;
            done = true;
            if (startup != null) startup.Dismiss("skip");
            else Close();
        }

        private void Fail(string message, Exception error)
        {
            if (done) return;
            done = true;
            QuiescePlayback();
            if (sound != null) sound.FadeOut();
            string details = message + (error == null ? "" : "\n\n" + error.Message);
            try
            {
                string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "launcher-media-error.txt"),
                    details + "\n\nVideo: " + filmPath + (error == null ? "" : "\n\n" + error));
            }
            catch { }
            if (startup != null)
            {
                startup.Record("media_failed", message);
                startup.Dismiss("media_failed");
                return;
            }
            if (quietErrors) { Close(); return; }
            Topmost = false;
            try
            {
                MessageBox.Show(this, details, "GPT 龙娘启动器", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { Close(); }
        }

        private void StopPlayback()
        {
            if (mediaStopped) return;
            mediaStopped = true;
            QuiescePlayback();
            RemoveTail();
            if (sound != null) { sound.Dispose(); sound = null; }
            try { player.Stop(); }
            catch { }
            ReleaseMedia();
        }

        private void QuiescePlayback()
        {
            preparationCancelled = true;
            watchdog.Stop();
            CompositionTarget.Rendering -= OnFrame;
            clock.Stop();
            playbackClock.Stop();
            transitionClock.Stop();
            tailAudioClock.Stop();
        }

        private void ReleaseMedia()
        {
            if (player == null) return;
            try { player.Stop(); player.Close(); player.Source = null; }
            catch { }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (cleaned) return;
            cleaned = true;
            done = true;
            StopPlayback();
            if (exitTransition != null) { exitTransition.Dispose(); exitTransition = null; }
            watchdog.Tick -= OnWatchdog;
            player.MediaOpened -= OnMediaOpened;
            player.MediaEnded -= OnMediaEnded;
            player.MediaFailed -= OnMediaFailed;
            cancel.Click -= OnCancel;
            surface.MouseLeftButtonDown -= OnSurfaceMouseDown;
            PreviewKeyDown -= OnPreviewKeyDown;
            ContentRendered -= OnContentRendered;
            IsVisibleChanged -= OnVisibilityChanged;
            Closed -= OnClosed;
            try { player.Close(); player.Source = null; }
            catch { }
        }
    }
}
