using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DragonLauncher
{
    public interface IStartupPresentation
    {
        bool IntroComplete { get; }
        void RequestReadyFade();
        int CloseDeadlineMilliseconds { get; }
    }

    // Fade a composed still of our own waiting scene. The opaque video HWND
    // remains unchanged; only the temporary, video-free HWND is transparent.
    public sealed class ReadyExitTransition : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        private readonly Window host;
        private readonly FrameworkElement content;
        private readonly double duration;
        private readonly Action<string, string> record;
        private readonly Action completed;
        private readonly DispatcherTimer timer;
        private readonly Stopwatch clock = new Stopwatch();
        private readonly Stopwatch fadeClock = new Stopwatch();
        private Window layer;
        private Image image;
        private bool disposed, running, fading, finishing, contentRendered;
        private int composedFrames;
        private NativeRect bounds;

        public bool IsRunning { get { return running && !disposed; } }

        public ReadyExitTransition(Window owner, FrameworkElement scene, double seconds,
            Action<string, string> recorder, Action done)
        {
            host = owner; content = scene; duration = seconds; record = recorder; completed = done;
            timer = new DispatcherTimer(DispatcherPriority.Send, host.Dispatcher);
            timer.Interval = TimeSpan.FromMilliseconds(15);
            timer.Tick += Tick;
            host.Dispatcher.ShutdownStarted += DispatcherShutdown;
        }

        public void Start()
        {
            if (running || disposed) return;
            running = true;
            clock.Start();
            try
            {
                IntPtr ownerHandle = new WindowInteropHelper(host).Handle;
                if (ownerHandle == IntPtr.Zero || !GetWindowRect(ownerHandle, out bounds))
                    throw new InvalidOperationException("Animation window bounds are unavailable.");
                int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
                if (width < 1 || height < 1 || width > 8192 || height > 8192)
                    throw new InvalidOperationException("Animation window bounds are invalid.");
                content.UpdateLayout();
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                    drawing.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual); bitmap.Freeze();
                byte[] pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                int opaque = 0;
                for (int offset = 3; offset < pixels.Length; offset += 4) if (pixels[offset] >= 250) opaque++;
                if (opaque < width * height * 0.95)
                    throw new InvalidOperationException("The composed waiting scene is not opaque.");
                image = new Image { Source = bitmap, Stretch = Stretch.Fill, IsHitTestVisible = false };
                layer = new Window {
                    Title = "GPT 龙娘 · Codex 交接", WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
                    Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false,
                    ShowActivated = true, Width = host.ActualWidth, Height = host.ActualHeight,
                    Left = host.Left, Top = host.Top, WindowStartupLocation = WindowStartupLocation.Manual,
                    Content = image
                };
                layer.SourceInitialized += PlaceLayer;
                layer.ContentRendered += LayerRendered;
                layer.Closing += LayerClosing;
                layer.PreviewKeyDown += LayerKeyDown;
                layer.MouseLeftButtonDown += LayerClick;
                CompositionTarget.Rendering += Composed;
                timer.Start();
                Record("ready_fade_prepared", "bounds=" + bounds.Left + "," + bounds.Top + "," + width + "," + height +
                    "; own composed waiting scene; original window retained");
                layer.Show();
            }
            catch (Exception error) { Fail(error); }
        }

        private void PlaceLayer(object sender, EventArgs args)
        {
            IntPtr handle = new WindowInteropHelper(layer).Handle;
            if (!SetWindowPos(handle, new IntPtr(-1), bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x10))
                throw new InvalidOperationException("Unable to align the transition layer.");
        }

        private void LayerRendered(object sender, EventArgs args)
        { contentRendered = true; composedFrames = 0; }

        private void Composed(object sender, EventArgs args)
        {
            if (!running || disposed || finishing || fading || layer == null || !contentRendered) return;
            if (++composedFrames < 2) return;
            try
            {
                // Keep both windows fully opaque for two composition ticks.
                // Hide the video host only after its replacement has rendered.
                fading = true;
                host.Hide();
                // Hiding the active host may raise another topmost window.
                // Restore this layer's position after that activation change.
                PlaceLayer(layer, EventArgs.Empty);
                layer.Activate(); layer.Focus();
                fadeClock.Start();
                Record("ready_fade_started", "duration=" + duration.ToString("F3", CultureInfo.InvariantCulture) +
                    "s; original hidden after two composition ticks; fade to loaded page");
            }
            catch (Exception error) { Fail(error); }
        }

        private void Tick(object sender, EventArgs args)
        {
            if (!running || disposed || finishing) return;
            if (!fading)
            {
                if (clock.Elapsed.TotalSeconds > 0.65) Fail(new TimeoutException("Transition layer did not render in time."));
                return;
            }
            try
            {
                double progress = Math.Min(1, fadeClock.Elapsed.TotalSeconds / duration);
                double eased = progress * progress * (3 - 2 * progress);
                layer.Opacity = 1 - eased;
                if (progress >= 1)
                {
                    Record("ready_fade_completed", "elapsed_ms=" + fadeClock.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture) +
                        "; transparent layer; releasing windows");
                    Finish();
                }
            }
            catch (Exception error) { Fail(error); }
        }

        private void LayerClosing(object sender, System.ComponentModel.CancelEventArgs args)
        {
            if (disposed) return;
            args.Cancel = true;
            Record("ready_fade_skipped", "transition window closed manually");
            host.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(Finish));
        }
        private void LayerKeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key != Key.Escape && args.Key != Key.Enter && args.Key != Key.Space) return;
            args.Handled = true;
            Record("ready_fade_skipped", "manual key skip"); Finish();
        }
        private void LayerClick(object sender, MouseButtonEventArgs args)
        {
            if (layer == null) return;
            Point position = args.GetPosition(layer);
            if (position.X >= layer.ActualWidth - 70 && position.Y < 70)
            { args.Handled = true; Record("ready_fade_skipped", "close button"); Finish(); }
        }
        private void Fail(Exception error)
        { Record("ready_fade_failed", error.GetType().Name + ": " + error.Message + "; closing normally"); Finish(); }
        private void Finish()
        {
            if (finishing || disposed) return;
            finishing = true;
            Dispose();
            if (!host.Dispatcher.HasShutdownStarted)
                host.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(delegate { if (completed != null) completed(); }));
        }
        private void Record(string name, string detail) { try { if (record != null) record(name, detail); } catch { } }
        private void DispatcherShutdown(object sender, EventArgs args) { Dispose(); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; running = false;
            timer.Stop(); timer.Tick -= Tick;
            CompositionTarget.Rendering -= Composed;
            host.Dispatcher.ShutdownStarted -= DispatcherShutdown;
            clock.Stop(); fadeClock.Stop();
            if (image != null) { image.Source = null; image = null; }
            var closing = layer; layer = null;
            if (closing != null)
            {
                closing.SourceInitialized -= PlaceLayer;
                closing.ContentRendered -= LayerRendered;
                closing.Closing -= LayerClosing; closing.PreviewKeyDown -= LayerKeyDown; closing.MouseLeftButtonDown -= LayerClick;
                closing.Content = null;
                try { closing.Close(); } catch { }
            }
        }
    }
}
