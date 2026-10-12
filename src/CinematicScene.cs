using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace DragonLauncher
{
    public sealed class Scene : FrameworkElement
    {
        public const double DesignWidth = 1280, DesignHeight = 720;
        public const int ExportWidth = 2560, ExportHeight = 1440;
        private readonly Settings settings;
        private readonly bool reducedMotion;
        private readonly BitmapImage opened, closed;
        private readonly StrokeTrace trace;
        private readonly StrokeTrace mesh;
        private readonly Typeface normal = new Typeface("Microsoft YaHei UI");
        private readonly Typeface mono = new Typeface("Consolas");
        // One family supports both Chinese and Latin, so all three display
        // lines share the same weight and construction rather than a fallback.
        private readonly Typeface display = new Typeface(new FontFamily("DengXian, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        private readonly Geometry titleGlyph, greetingGlyph, englishGlyph;
        private readonly Brush displayFill;
        private readonly DrawingGroup portrait;
        private readonly EyeRig eyes;
        private readonly Point[] eyeCenters = new Point[2];
        private readonly HairBand[] hairBands;
        private byte[] waitingHairSource, waitingHairPixels;
        private WriteableBitmap waitingHairBitmap;
        private double waitingHairPoseTime = double.NaN;
        private const int HairPadding = 16;
        private const int HairUpperTop = 552, HairUpperBottom = 700;
        private const int HairLowerTop = 826, HairLowerBottom = 1060;
        private readonly int leftAnchor, rightAnchor;
        private readonly Geometry[] rainGlyphs = new Geometry[6];
        private readonly LightBloom bloom;
        private readonly Rect bloomArea;
        private BitmapSource bloomBitmap;
        private StrokeTrace.MotionFrame bloomLines, bloomMesh;
        private sealed class LightBloom : FrameworkElement
        {
            public Drawing Drawing;
            public Rect Area;
            protected override void OnRender(DrawingContext dc)
            {
                dc.PushTransform(new ScaleTransform(0.5, 0.5));
                dc.PushTransform(new TranslateTransform(-Area.X, -Area.Y));
                if (Drawing != null) dc.DrawDrawing(Drawing);
                dc.Pop(); dc.Pop();
            }
        }
        private sealed class HairBand
        {
            public int Y, Height;
            public BitmapSource Left, Middle, Right;
        }
        public double Time;
        public bool Launching;
        // When set, the eyes can animate independently while the rest of the
        // composition remains at a fixed time for the startup waiting scene.
        private double? eyeOpennessOverride;
        public double? EyeOpennessOverride
        {
            get { return eyeOpennessOverride; }
            set
            {
                if (eyeOpennessOverride == value) return;
                eyeOpennessOverride = value;
                if (waitingEyeLayer != null) RenderWaitingEyes();
                else InvalidateVisual();
            }
        }
        // This clock belongs to the waiting layer only. Keeping Time fixed
        // freezes the camera, face, text and background from the film.
        private double? waitingEffectsTime;
        private double? waitingLightTime, waitingHairTime;
        private DrawingVisual waitingHairLayer, waitingEyeLayer, waitingForegroundLayer, waitingLightLayer;
        public double? WaitingEffectsTime { get { return waitingEffectsTime; } }
        public double? WaitingHairTime { get { return waitingHairTime; } }
        public bool HasWaitingLayers { get { return waitingHairLayer != null; } }

        // Only frozen WPF resources and plain pixel buffers cross the preparing
        // STA thread. No FrameworkElement or live visual is shared with it.
        internal sealed class WaitingAssets
        {
            internal BitmapImage Opened, Closed;
            internal Geometry Title, Greeting, English;
            internal DrawingGroup Portrait;
            internal EyeRig Eyes;
            internal byte[] HairSource, HairPixels;
            internal BitmapSource HairBitmap, FirstFrame;
            internal double PoseTime;
        }

        internal static WaitingAssets PrepareWaiting(string root, Settings config, bool reduced, double holdTime,
            Func<bool> cancelled)
        {
            if (cancelled()) return null;
            var scene = new Scene(root, config, reduced, true, true, null);
            try
            {
                if (cancelled()) return null;
                scene.Launching = true;
                scene.SetTime(Math.Max(0, Math.Min(config.Duration, holdTime)));
                scene.EyeOpennessOverride = 1;
                scene.SetWaitingEffectsTime(0);
                scene.Measure(new Size(DesignWidth, DesignHeight));
                scene.Arrange(new Rect(0, 0, DesignWidth, DesignHeight));
                scene.UpdateLayout();
                if (cancelled()) return null;
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                    dc.DrawRectangle(new VisualBrush(scene) { Stretch = Stretch.Fill }, null,
                        new Rect(0, 0, ExportWidth, ExportHeight));
                var frame = new RenderTargetBitmap(ExportWidth, ExportHeight, 96, 96, PixelFormats.Pbgra32);
                frame.Render(visual);
                frame.Freeze();
                BitmapSource hair = null;
                if (scene.waitingHairBitmap != null)
                {
                    hair = scene.waitingHairBitmap.CloneCurrentValue();
                    hair.Freeze();
                }
                return cancelled() ? null : new WaitingAssets {
                    Opened = scene.opened, Closed = scene.closed, Title = scene.titleGlyph,
                    Greeting = scene.greetingGlyph, English = scene.englishGlyph, Portrait = scene.portrait,
                    Eyes = scene.eyes, HairSource = scene.waitingHairSource, HairPixels = scene.waitingHairPixels,
                    HairBitmap = hair, FirstFrame = frame, PoseTime = scene.waitingHairPoseTime
                };
            }
            finally { scene.ReleaseWaitingEffects(); }
        }

        protected override int VisualChildrenCount { get { return waitingLightLayer == null ? 0 : 4; } }
        protected override Visual GetVisualChild(int index)
        {
            if (waitingLightLayer == null) throw new ArgumentOutOfRangeException("index");
            if (index == 0) return waitingHairLayer;
            if (index == 1) return waitingEyeLayer;
            if (index == 2) return waitingForegroundLayer;
            if (index == 3) return waitingLightLayer;
            throw new ArgumentOutOfRangeException("index");
        }

        public Scene(string root, Settings config, bool reduced, bool preferDetailTextures = true)
            : this(root, config, reduced, preferDetailTextures, false, null)
        {
        }

        internal Scene(string root, Settings config, bool reduced, bool preferDetailTextures, bool waitingOnly, WaitingAssets prepared)
        {
            settings = config; reducedMotion = reduced;
            preferDetailTextures = preferDetailTextures && config.PreferDetailTextures;
            Width = DesignWidth; Height = DesignHeight;
            titleGlyph = prepared == null ? LetterGeometry("Chatgpt", display, 132, 64, 249, 0.3) : prepared.Title;
            greetingGlyph = prepared == null ? LetterGeometry("欢迎主人回家", display, 25, 87, 430, 3.5) : prepared.Greeting;
            englishGlyph = prepared == null ? LetterGeometry("WELCOME HOME, MASTER", display, 12.5, 88, 468, 1.5) : prepared.English;
            var textGradient = new LinearGradientBrush();
            textGradient.StartPoint = new Point(0, 0); textGradient.EndPoint = new Point(0.85, 1);
            textGradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#C4F6EC"), 0));
            textGradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#EFFAFF"), 0.46));
            textGradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#75BEDB"), 1));
            textGradient.Freeze(); displayFill = textGradient;
            opened = prepared == null ? LoadImage(root, config.Image, preferDetailTextures) : prepared.Opened;
            closed = prepared == null ? LoadImage(root, config.ClosedImage, preferDetailTextures) : prepared.Closed;
            if (opened.PixelWidth != closed.PixelWidth || opened.PixelHeight != closed.PixelHeight)
                throw new InvalidDataException("睁眼与闭眼素材必须具有相同尺寸。");
            if (opened.PixelWidth < 64 || opened.PixelHeight < 64)
                throw new InvalidDataException("龙娘特写素材尺寸过小。");
            // Schedule the camera-visible face and hair, rather than spending
            // drawing time on the full body's offscreen contours.
            if (!waitingOnly)
            {
            trace = new StrokeTrace(closed, new Rect(0, opened.PixelHeight * 0.215,
                opened.PixelWidth * 0.59, opened.PixelHeight * 0.425));
            mesh = BuildCurveMesh(closed);
            bloomArea = new Rect(0, opened.PixelHeight * 0.205, opened.PixelWidth * 0.60, opened.PixelHeight * 0.445);
            bloom = new LightBloom { Area = bloomArea, Width = Math.Ceiling(bloomArea.Width / 2), Height = Math.Ceiling(bloomArea.Height / 2),
                Effect = new BlurEffect { Radius = 3.6, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality } };
            bloom.Measure(new Size(bloom.Width, bloom.Height));
            bloom.Arrange(new Rect(0, 0, bloom.Width, bloom.Height));
            }
            portrait = prepared == null ? new DrawingGroup() : prepared.Portrait;
            if (prepared == null)
            {
                using (var dc = portrait.Open()) dc.DrawImage(closed, new Rect(0, 0, opened.PixelWidth, opened.PixelHeight));
                portrait.Freeze();
            }
            eyeCenters[0] = new Point(opened.PixelWidth * 0.424, opened.PixelHeight * 0.441);
            eyeCenters[1] = new Point(opened.PixelWidth * 0.629, opened.PixelHeight * 0.407);
            eyes = prepared == null ? new EyeRig(root, opened, preferDetailTextures) : prepared.Eyes;
            leftAnchor = (int)(opened.PixelWidth * 0.32);
            rightAnchor = (int)(opened.PixelWidth * 0.79);
            hairBands = new HairBand[waitingOnly ? 0 : (opened.PixelHeight + 23) / 24];
            for (int i = 0; i < hairBands.Length; i++)
            {
                int y = i * 24, height = Math.Min(24, opened.PixelHeight - y);
                // Bleed source pixels across rows to keep resampling from exposing a dark tile edge.
                int top = Math.Max(0, y - 2), bottom = Math.Min(opened.PixelHeight, y + height + 2);
                var band = new HairBand { Y = top, Height = bottom - top };
                band.Left = new CroppedBitmap(closed, new Int32Rect(0, top, leftAnchor + 2, band.Height));
                band.Middle = new CroppedBitmap(closed, new Int32Rect(leftAnchor - 2, top, rightAnchor - leftAnchor + 4, band.Height));
                band.Right = new CroppedBitmap(closed, new Int32Rect(rightAnchor - 2, top, opened.PixelWidth - rightAnchor + 2, band.Height));
                band.Left.Freeze(); band.Middle.Freeze(); band.Right.Freeze(); hairBands[i] = band;
            }
            if (prepared != null && prepared.HairBitmap != null)
            {
                waitingHairSource = prepared.HairSource;
                waitingHairPixels = (byte[])prepared.HairPixels.Clone();
                waitingHairBitmap = new WriteableBitmap(prepared.HairBitmap);
                waitingHairPoseTime = prepared.PoseTime;
            }
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            string[] glyphs = { "0", "1", ":", ".", "+", "/" };
            for (int i = 0; i < glyphs.Length; i++)
            {
                rainGlyphs[i] = new FormattedText(glyphs[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    mono, 9, Brushes.White, 1.0).BuildGeometry(new Point(0, 0));
                rainGlyphs[i].Freeze();
            }
        }
        public static string ResolveTexturePath(string root, string relative, bool preferDetailTextures = true)
        {
            var file = Path.GetFullPath(Path.Combine(root, relative));
            if (preferDetailTextures)
            {
                string detail = Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file) + "-detail" + Path.GetExtension(file));
                if (File.Exists(detail)) return detail;
            }
            return file;
        }
        private static BitmapImage LoadImage(string root, string relative, bool preferDetailTextures)
        {
            var file = ResolveTexturePath(root, relative, preferDetailTextures);
            if (!File.Exists(file)) throw new FileNotFoundException("找不到动画素材，请保留 assets 文件夹。", file);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(file); bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        public void SetTime(double seconds)
        {
            double quantized = Math.Floor(Math.Max(0, seconds) * settings.FrameRate + 0.000001) / settings.FrameRate;
            if (Math.Abs(quantized - Time) < 0.000001) return;
            Time = quantized;
            if (waitingHairLayer != null)
            {
                RenderWaitingForeground();
                RenderWaitingHair();
                RenderWaitingEyes();
                RenderWaitingLights();
            }
            InvalidateVisual();
        }
        public void SetWaitingEffectsTime(double seconds, double? lightSeconds = null, double? hairSeconds = null)
        {
            double quantized = WaitingClock(seconds, settings.FrameRate);
            double lightTime = WaitingClock(lightSeconds ?? seconds, settings.FrameRate);
            // The hair is slow enough for thirty small updates per second.
            // Updating its visual leaves the frozen background and text alone.
            double hairTime = WaitingClock(hairSeconds ?? seconds, Math.Min(30, settings.FrameRate));
            bool lightChanged = !waitingLightTime.HasValue || Math.Abs(lightTime - waitingLightTime.Value) >= 0.000001;
            bool hairChanged = !waitingHairTime.HasValue || Math.Abs(hairTime - waitingHairTime.Value) >= 0.000001;
            waitingEffectsTime = quantized;
            waitingLightTime = lightTime;
            waitingHairTime = hairTime;
            if (waitingLightLayer == null)
            {
                waitingHairLayer = new DrawingVisual();
                waitingEyeLayer = new DrawingVisual();
                waitingForegroundLayer = new DrawingVisual();
                waitingLightLayer = new DrawingVisual();
                AddVisualChild(waitingHairLayer);
                AddVisualChild(waitingEyeLayer);
                AddVisualChild(waitingForegroundLayer);
                AddVisualChild(waitingLightLayer);
                RenderWaitingForeground();
                RenderWaitingEyes();
                InvalidateVisual();
            }
            if (hairChanged) RenderWaitingHair();
            if (lightChanged) RenderWaitingLights();
        }
        private double WaitingClock(double seconds, int frameRate)
        {
            return reducedMotion ? 0 : Math.Floor(Math.Max(0, seconds) * frameRate + 0.000001) / frameRate;
        }
        private void RenderWaitingLights()
        {
            using (var dc = waitingLightLayer.RenderOpen())
            {
                dc.PushClip(new RectangleGeometry(new Rect(35, 235, 576, 280)));
                double u = reducedMotion ? 5.4 : Time * 8 / settings.Duration;
                DrawOrbitLight(dc, u * 0.9 + waitingLightTime.Value * 0.28);
                PaintWaitingLights(dc);
                dc.Pop();
            }
        }
        private void RenderWaitingForeground()
        {
            using (var dc = waitingForegroundLayer.RenderOpen())
            {
                double u = reducedMotion ? 5.4 : Time * 8 / settings.Duration;
                var screen = new Rect(0, 0, DesignWidth, DesignHeight);
                dc.PushClip(new RectangleGeometry(screen, 10, 10));
                dc.PushOpacity(SceneOpacity(u));
                PaintForeground(dc, u, screen);
                dc.Pop(); dc.Pop();
            }
        }
        private void RenderWaitingHair()
        {
            using (var dc = waitingHairLayer.RenderOpen())
            {
                double u = reducedMotion ? 5.4 : Time * 8 / settings.Duration;
                Rect rectangle = CharacterRectangle(u);
                double scale = rectangle.Height / opened.PixelHeight;
                if (!reducedMotion) UpdateWaitingHairBitmap(u, waitingHairTime.Value);
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, DesignWidth, DesignHeight), 10, 10));
                dc.PushOpacity(SceneOpacity(u) * Smooth((u - 2.25) / 1.0));
                dc.PushTransform(new TranslateTransform(rectangle.X, rectangle.Y));
                dc.PushTransform(new ScaleTransform(scale, scale));
                // One complete portrait is sampled and drawn once. There is
                // no internal vertical crop, overlap or second fixed portrait.
                if (reducedMotion) dc.DrawDrawing(portrait);
                else dc.DrawImage(waitingHairBitmap, new Rect(-HairPadding, 0, waitingHairBitmap.PixelWidth, opened.PixelHeight));
                dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();
            }
        }
        private void RenderWaitingEyes()
        {
            using (var dc = waitingEyeLayer.RenderOpen())
            {
                double u = reducedMotion ? 5.4 : Time * 8 / settings.Duration;
                Rect rectangle = CharacterRectangle(u);
                double awake = eyeOpennessOverride.HasValue ? Clamp(eyeOpennessOverride.Value) : reducedMotion ? 1 : EyeOpening(u);
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, DesignWidth, DesignHeight), 10, 10));
                dc.PushOpacity(SceneOpacity(u) * (reducedMotion ? 1 : Smooth((u - 2.25) / 1.0)));
                dc.PushTransform(new TranslateTransform(rectangle.X, rectangle.Y));
                dc.PushTransform(new ScaleTransform(rectangle.Width / opened.PixelWidth, rectangle.Height / opened.PixelHeight));
                PaintEyes(dc, awake);
                dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();
            }
        }
        private void UpdateWaitingHairBitmap(double filmTime, double seconds)
        {
            int sourceWidth = opened.PixelWidth;
            int width = sourceWidth + HairPadding * 2;
            int stride = width * 4;
            bool fullUpdate = waitingHairBitmap == null || Math.Abs(waitingHairPoseTime - filmTime) > 0.000001;
            if (waitingHairBitmap == null)
            {
                waitingHairSource = new byte[sourceWidth * opened.PixelHeight * 4];
                new FormatConvertedBitmap(closed, PixelFormats.Pbgra32, null, 0).CopyPixels(waitingHairSource, sourceWidth * 4, 0);
                // The portrait matte contains a few almost transparent dots
                // outside the silhouette. They belong to the fixed background,
                // and must not drift through the title with the moving hair.
                for (int pixel = 0; pixel < waitingHairSource.Length; pixel += 4)
                    if (waitingHairSource[pixel + 3] <= 4)
                        waitingHairSource[pixel] = waitingHairSource[pixel + 1] =
                            waitingHairSource[pixel + 2] = waitingHairSource[pixel + 3] = 0;
                waitingHairPixels = new byte[width * opened.PixelHeight * 4];
                waitingHairBitmap = new WriteableBitmap(width, opened.PixelHeight, 96, 96, PixelFormats.Pbgra32, null);
            }
            // Keep the film pose at the handoff, but sample it continuously
            // across the entire portrait. Subsequent updates touch only the
            // hair ROI, with a smooth displacement that reaches zero before
            // the face and ear. Fixed regions retain their initial pixels.
            int updateWidth = Math.Min(width, HairPadding + (int)Math.Ceiling(sourceWidth * (350.0 / 1024.0)) + 2);
            for (int y = 0; y < opened.PixelHeight; y++)
            {
                double sourceY = y * 1536.0 / opened.PixelHeight;
                if (!fullUpdate && !(sourceY > HairUpperTop && sourceY < HairUpperBottom) &&
                    !(sourceY > HairLowerTop && sourceY < HairLowerBottom)) continue;
                double baseline = Math.Sin(filmTime * 1.9 + y * 0.006) * 4.5 * Smooth((y - 400) / 480.0);
                double right = Math.Sin(filmTime * 1.9 + y * 0.006 + 1.5) * 4.5 * Smooth((y - 790) / 480.0);
                double motion = WaitingHairOffset(y, seconds);
                double anchor = sourceWidth * (sourceY < HairUpperBottom ? 320.0 : 350.0) / 1024.0;
                double taper = sourceWidth * 150.0 / 1024.0;
                int row = y * sourceWidth * 4, destination = y * stride;
                int end = fullUpdate ? width : updateWidth;
                for (int x = 0; x < end; x++, destination += 4)
                {
                    double dx = x - HairPadding;
                    double sourceX = dx;
                    if (dx < leftAnchor) sourceX = (dx - baseline) / ((leftAnchor - baseline) / leftAnchor);
                    else if (dx > rightAnchor)
                        sourceX = rightAnchor + (dx - rightAnchor) / ((sourceWidth - rightAnchor + right) / (sourceWidth - rightAnchor));
                    sourceX -= motion * Smooth((anchor - dx) / taper);
                    int position = (int)Math.Round(sourceX * 65536);
                    int sample = position >> 16;
                    if (sample < 0 || sample >= sourceWidth)
                    {
                        waitingHairPixels[destination] = waitingHairPixels[destination + 1] =
                            waitingHairPixels[destination + 2] = waitingHairPixels[destination + 3] = 0;
                        continue;
                    }
                    int a = row + sample * 4, b = row + Math.Min(sourceWidth - 1, sample + 1) * 4;
                    int fraction = position & 65535;
                    for (int c = 0; c < 4; c++)
                        waitingHairPixels[destination + c] = (byte)((waitingHairSource[a + c] * (65536 - fraction) +
                            waitingHairSource[b + c] * fraction + 32768) >> 16);
                }
            }
            if (fullUpdate)
                waitingHairBitmap.WritePixels(new Int32Rect(0, 0, width, opened.PixelHeight), waitingHairPixels, stride, 0);
            else
            {
                WriteHairRegion(updateWidth, stride, HairUpperTop, HairUpperBottom);
                WriteHairRegion(updateWidth, stride, HairLowerTop, HairLowerBottom);
            }
            waitingHairPoseTime = filmTime;
        }
        private void WriteHairRegion(int width, int stride, int sourceTop, int sourceBottom)
        {
            int top = Math.Max(0, (int)Math.Floor(opened.PixelHeight * sourceTop / 1536.0));
            int bottom = Math.Min(opened.PixelHeight, (int)Math.Ceiling(opened.PixelHeight * sourceBottom / 1536.0));
            waitingHairBitmap.WritePixels(new Int32Rect(0, top, width, bottom - top), waitingHairPixels, stride, top * stride);
        }
        private double WaitingHairOffset(double sourceY, double seconds)
        {
            // A fixed scalp and ear anchor the two visible outer locks. The
            // added sway starts at zero, and smooth sinusoids avoid a
            // repeated start or a bobbing face. No skin or horn row is moved.
            double y = sourceY * 1536.0 / opened.PixelHeight;
            double upperLock = Smooth((y - HairUpperTop) / 58.0) * Smooth((HairUpperBottom - y) / 42.0);
            double lowerLock = Smooth((y - HairLowerTop) / 40.0) * Smooth((HairLowerBottom - y) / 100.0);
            double primary = Math.Sin(seconds * 0.90) * 6.5;
            double secondary = (Math.Sin(seconds * 1.17 + 0.7) - Math.Sin(0.7)) * 0.7;
            double lower = Math.Sin(seconds * 1.01) * 7.5 + Math.Sin(seconds * 1.37) * 0.6;
            return (upperLock * (primary + secondary) + lowerLock * lower) * opened.PixelWidth / 1024.0;
        }
        public Rect WaitingHairArea
        {
            get
            {
                double u = reducedMotion ? 5.4 : Time * 8 / settings.Duration;
                Rect rectangle = CharacterRectangle(u);
                double scale = rectangle.Height / opened.PixelHeight;
                return Rect.Intersect(new Rect(0, 0, DesignWidth, DesignHeight),
                    new Rect(rectangle.X - HairPadding * scale, rectangle.Y + opened.PixelHeight * ((HairUpperTop - 4.0) / 1536.0) * scale,
                        opened.PixelWidth * (354.0 / 1024.0) * scale + HairPadding * scale,
                        opened.PixelHeight * ((HairLowerBottom + 8.0 - HairUpperTop + 4.0) / 1536.0) * scale));
            }
        }
        public Rect WaitingEarArea
        {
            get
            {
                double u = reducedMotion ? 5.4 : Time * 8 / settings.Duration;
                Rect rectangle = CharacterRectangle(u);
                double scale = rectangle.Height / opened.PixelHeight;
                return Rect.Intersect(new Rect(0, 0, DesignWidth, DesignHeight),
                    new Rect(rectangle.X + opened.PixelWidth * 0.16 * scale,
                        rectangle.Y + opened.PixelHeight * (708.0 / 1536.0) * scale,
                        opened.PixelWidth * 0.18 * scale, opened.PixelHeight * (112.0 / 1536.0) * scale));
            }
        }
        public void ReleaseWaitingEffects()
        {
            if (waitingLightLayer == null) return;
            using (waitingHairLayer.RenderOpen()) { }
            using (waitingEyeLayer.RenderOpen()) { }
            using (waitingForegroundLayer.RenderOpen()) { }
            using (waitingLightLayer.RenderOpen()) { }
            RemoveVisualChild(waitingHairLayer);
            RemoveVisualChild(waitingEyeLayer);
            RemoveVisualChild(waitingForegroundLayer);
            RemoveVisualChild(waitingLightLayer);
            waitingHairLayer = waitingEyeLayer = waitingForegroundLayer = waitingLightLayer = null;
            waitingEffectsTime = waitingLightTime = waitingHairTime = null;
            waitingHairBitmap = null;
            waitingHairSource = waitingHairPixels = null;
            waitingHairPoseTime = double.NaN;
            InvalidateVisual();
        }
        private static double Clamp(double v) { return Math.Max(0, Math.Min(1, v)); }
        private static double Smooth(double v) { v = Clamp(v); return v * v * (3 - 2 * v); }
        private static double Ease(double v) { return 1 - Math.Pow(1 - Clamp(v), 3); }
        private static Brush ColorBrush(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        private static Brush Alpha(double alpha, byte r, byte g, byte b) { return new SolidColorBrush(Color.FromArgb((byte)(Clamp(alpha) * 255), r, g, b)); }
        private static Geometry LetterGeometry(string text, Typeface face, double size, double x, double y, double tracking)
        {
            var geometry = new GeometryGroup();
            foreach (char letter in text)
            {
                var formatted = new FormattedText(letter.ToString(), CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                    face, size, Brushes.White, 2.0);
                geometry.Children.Add(formatted.BuildGeometry(new Point(x, y)));
                x += formatted.WidthIncludingTrailingWhitespace + tracking;
            }
            geometry.Freeze(); return geometry;
        }
        private void Text(DrawingContext dc, string text, double x, double y, double size, string color, bool code)
        {
            dc.DrawText(new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                code ? mono : normal, size, ColorBrush(color), 1.0), new Point(x, y));
        }
        private static StrokeTrace BuildCurveMesh(BitmapSource source)
        {
            int w = source.PixelWidth, h = source.PixelHeight;
            var bytes = new byte[w * h * 4];
            new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0).CopyPixels(bytes, w * 4, 0);
            Func<Point, bool> visible = p => p.X > 0 && p.Y > h * 0.215 && p.X < w * 0.59 && p.Y < h * 0.64 && bytes[((int)p.Y * w + (int)p.X) * 4 + 3] > 110;
            var result = new List<Point[]>();
                // Surface latitude/longitude curves follow the face; no planar triangle lattice.
                for (double row = -0.9; row <= 0.91; row += 0.085)
                {
                    double latitude = row;
                    PlotCurve(result, t =>
                    {
                        double u = (t * 2 - 1);
                        double spread = Math.Sqrt(1 - latitude * latitude);
                        return new Point(w * 0.53 + u * w * 0.225 * spread,
                            h * 0.465 + latitude * h * 0.16 + (45 + 22 * spread) * u * u * spread);
                    }, visible);
                }
                for (double column = -0.9; column <= 0.91; column += 0.095)
                {
                    double longitude = column;
                    PlotCurve(result, t =>
                    {
                        double v = t * 1.9 - 0.95;
                        return new Point(w * 0.53 + longitude * w * 0.225 * Math.Sqrt(1 - v * v),
                            h * 0.465 + v * h * 0.16 + 15 * longitude * longitude);
                    }, visible);
                }
                // Arched cross-sections add flow to the hair, while the source contours supply its real strands.
                for (double band = 0.20; band < 0.64; band += 0.027)
                {
                    double y = band;
                    PlotCurve(result, t => new Point(w * (0.09 + t * 0.85),
                        h * y + h * 0.045 * Math.Sin(t * Math.PI) + w * 0.015 * Math.Sin(t * 7 + y * 4)), visible);
                }
            return new StrokeTrace(result);
        }
        private static void PlotCurve(List<Point[]> curves, Func<double, Point> curve, Func<Point, bool> visible)
        {
            var points = new List<Point>();
            for (int i = 0; i <= 120; i++)
            {
                Point point = curve(i / 120.0);
                if (!visible(point))
                {
                    if (points.Count > 1) curves.Add(points.ToArray());
                    points.Clear(); continue;
                }
                points.Add(point);
            }
            if (points.Count > 1) curves.Add(points.ToArray());
        }
        private static double EyeOpening(double time)
        {
            double x = Clamp((time - 3.25) / 1.5);
            return Smooth(x);
        }
        private static void Cross(DrawingContext dc, double x, double y, double length, double opacity)
        {
            var pen = new Pen(Alpha(opacity, 118, 212, 218), 0.8);
            dc.DrawLine(pen, new Point(x - length, y), new Point(x + length, y));
            dc.DrawLine(pen, new Point(x, y - length), new Point(x, y + length));
        }
        private static void Bokeh(DrawingContext dc, double x, double y, double radius, double opacity)
        {
            var glow = new RadialGradientBrush();
            glow.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(Clamp(opacity) * 255), 39, 178, 207), 0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, 25, 119, 177), 1));
            dc.DrawEllipse(glow, null, new Point(x, y), radius, radius);
        }
        private void PaintWaitingLights(DrawingContext dc)
        {
            if (!waitingEffectsTime.HasValue) return;
            double t = waitingLightTime.Value;
            // Keep the foreground lights within the title area. They drift
            // slowly with smooth periodic brightness, avoiding visible resets.
            for (int i = 0; i < 9; i++)
            {
                double phase = i * 1.618;
                double x = 96 + (i * 73 % 409) + Math.Sin(t * 0.39 + phase) * 12;
                double y = 259 + (i * 37 % 218) + Math.Cos(t * 0.32 + phase * 1.31) * 8;
                double breathe = 0.5 + 0.5 * Math.Sin(t * 0.93 + phase);
                double opacity = 0.12 + 0.22 * breathe;
                double radius = 5 + i % 3 * 2 + breathe * 1.7;
                Bokeh(dc, x, y, radius, opacity);
                dc.DrawEllipse(Alpha(0.24 + 0.40 * breathe, 177, 244, 245), null,
                    new Point(x, y), 0.8 + i % 2 * 0.25, 0.8 + i % 2 * 0.25);
            }
            // A faint glint stays clipped to the original glyphs; no letter
            // changes size or position while Codex finishes loading.
            double glintX = 300 + Math.Sin(t * 0.42 - 0.45) * 236;
            double glintOpacity = 0.5 + 0.5 * Math.Sin(t * 0.57);
            var glint = new LinearGradientBrush();
            glint.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            glint.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(18 + 20 * glintOpacity), 217, 255, 251), 0.5));
            glint.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            dc.PushClip(titleGlyph);
            dc.DrawRectangle(glint, null, new Rect(glintX - 36, 240, 72, 174));
            dc.Pop();
        }
        private static void DrawOrbitLight(DrawingContext dc, double angle)
        {
            dc.PushTransform(new RotateTransform(-14, 315, 365));
            var comet = new Point(315 + Math.Cos(angle) * 274, 365 + Math.Sin(angle) * 53);
            Bokeh(dc, comet.X, comet.Y, 12, 0.48);
            dc.DrawEllipse(ColorBrush("#9EEBEE"), null, comet, 1.9, 1.9);
            dc.Pop();
        }
        private void PaintPortrait(DrawingContext dc, Rect rectangle, double awake)
        {
            dc.PushTransform(new TranslateTransform(rectangle.X, rectangle.Y));
            dc.PushTransform(new ScaleTransform(rectangle.Width / opened.PixelWidth, rectangle.Height / opened.PixelHeight));
            if (reducedMotion || hairBands.Length == 0) dc.DrawDrawing(portrait);
            else
            {
                DrawHair(dc);
            }
            PaintEyes(dc, awake);
            dc.Pop(); dc.Pop();
        }
        private void PaintEyes(DrawingContext dc, double awake)
        {
            eyes.Draw(dc, awake);
            if (awake > 0.85)
            {
                for (int i = 0; i < 2; i++)
                {
                        Point pupil = new Point(eyeCenters[i].X + 5, eyeCenters[i].Y + 2);
                        dc.PushClip(new EllipseGeometry(pupil, 25, 19));
                        dc.PushOpacity((awake - 0.85) / 0.15);
                        for (int k = 0; k < 3; k++)
                        {
                            double x = pupil.X - 13 + k * 11;
                            double y = pupil.Y - 12 + Math.Sin(Time * 1.3 + k) * 2;
                            dc.DrawLine(new Pen(Alpha(0.18, 63, 233, 222), 1.3), new Point(x, y), new Point(x, y + 24));
                        }
                        Bokeh(dc, pupil.X + 5, pupil.Y - 9, 5, 0.25);
                        dc.Pop(); dc.Pop();
                }
            }
        }
        private void DrawHair(DrawingContext dc)
        {
            double u = Time * 8 / settings.Duration;
            foreach (var band in hairBands)
            {
                // Source bleed overlaps the fixed face anchors, avoiding vertical filter seams.
                double left = Math.Sin(u * 1.9 + band.Y * 0.006) * 4.5 * Smooth((band.Y - 400) / 480.0);
                double right = Math.Sin(u * 1.9 + band.Y * 0.006 + 1.5) * 4.5 * Smooth((band.Y - 790) / 480.0);
                double leftScale = (leftAnchor - left) / leftAnchor;
                double rightScale = (opened.PixelWidth - rightAnchor + right) / (opened.PixelWidth - rightAnchor);
                dc.DrawImage(band.Left, new Rect(left, band.Y, (leftAnchor + 2) * leftScale, band.Height));
                dc.DrawImage(band.Middle, new Rect(leftAnchor - 2, band.Y, rightAnchor - leftAnchor + 4, band.Height));
                dc.DrawImage(band.Right, new Rect(rightAnchor - 2 * rightScale, band.Y, (opened.PixelWidth - rightAnchor + 2) * rightScale, band.Height));
            }
        }
        private static Pen StrokePen(double opacity, byte r, byte g, byte b, double width)
        {
            return new Pen(Alpha(opacity, r, g, b), width) {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        }
        private static void PaintLightPaths(DrawingContext dc, StrokeTrace.MotionFrame frame, double strength, bool surface)
        {
            // The broad bloom is blurred separately. These narrow strokes keep
            // the moving head and its tapered wake crisp inside that light.
            double width = surface ? 0.45 : 0.68;
            dc.DrawGeometry(null, StrokePen(strength * 0.09, 42, 206, 250, 2.0), frame.Lines);
            dc.DrawGeometry(null, StrokePen(strength * 0.76, 113, 231, 248, width), frame.Lines);
            dc.DrawGeometry(null, StrokePen(strength * 0.16, 41, 210, 255, 2.4), frame.Tail);
            dc.DrawGeometry(null, StrokePen(strength * 0.42, 166, 244, 255, width + 0.25), frame.Tail);
            dc.DrawGeometry(null, StrokePen(strength * 0.65, 203, 252, 255, width + 0.32), frame.Light);
            dc.DrawGeometry(null, StrokePen(strength, 239, 255, 255, width + 0.40), frame.Hot);
            foreach (Point head in frame.Heads)
            {
                Bokeh(dc, head.X, head.Y, surface ? 3.2 : 6, strength * 0.65);
                dc.DrawEllipse(Alpha(strength * 0.95, 233, 255, 255), null, head, surface ? 0.43 : 0.72, surface ? 0.43 : 0.72);
            }
        }
        private void PaintBloom(DrawingContext dc, StrokeTrace.MotionFrame lines, StrokeTrace.MotionFrame surface)
        {
            if (bloomBitmap == null || !Object.ReferenceEquals(lines, bloomLines) || !Object.ReferenceEquals(surface, bloomMesh))
            {
                var drawing = new DrawingGroup();
                using (var glow = drawing.Open())
                {
                    glow.DrawGeometry(null, StrokePen(0.18, 23, 161, 251, 2.8), surface.Lines);
                    glow.DrawGeometry(null, StrokePen(0.42, 23, 178, 255, 3.8), lines.Lines);
                    glow.DrawGeometry(null, StrokePen(0.75, 27, 197, 255, 5.8), lines.Tail);
                    glow.DrawGeometry(null, StrokePen(0.50, 75, 233, 255, 4.2), lines.Hot);
                }
                drawing.Freeze(); bloom.Drawing = drawing; bloom.InvalidateVisual(); bloom.UpdateLayout();
                var image = new RenderTargetBitmap((int)bloom.Width, (int)bloom.Height, 96, 96, PixelFormats.Pbgra32);
                image.Render(bloom); image.Freeze(); bloomBitmap = image;
                bloomLines = lines; bloomMesh = surface;
            }
            dc.DrawImage(bloomBitmap, new Rect(bloomArea.X, bloomArea.Y, bloom.Width * 2, bloom.Height * 2));
        }
        private void PaintWireframe(DrawingContext dc, Rect rectangle, double opacity, double progress, double meshProgress)
        {
            if (opacity <= 0 || trace == null || mesh == null) return;
            dc.PushOpacity(opacity);
            dc.PushTransform(new TranslateTransform(rectangle.X, rectangle.Y));
            dc.PushTransform(new ScaleTransform(rectangle.Width / opened.PixelWidth, rectangle.Height / opened.PixelHeight));
            var lines = trace.GetFrame(progress); var surface = mesh.GetFrame(meshProgress);
            PaintBloom(dc, lines, surface);
            PaintLightPaths(dc, surface, 0.38, true);
            PaintLightPaths(dc, lines, 1.0, false);
            dc.Pop(); dc.Pop(); dc.Pop();
        }
        public string LineSample(double seconds)
        {
            var frame = trace.GetFrame(seconds * 8 / settings.Duration / 2.25);
            string heads = "";
            foreach (Point p in frame.Heads)
                heads += String.Format(CultureInfo.InvariantCulture, " ({0:F1},{1:F1})", p.X, p.Y);
            return String.Format(CultureInfo.InvariantCulture, "time={0:F3}s; visibleCurves={1}; revealedLength={2:F1}/{3:F1}; travellingHeads={4}; straightSegments={6}; curvedSegments={7}; sourcePositions:{5}",
                seconds, trace.StrokeCount, frame.RevealedLength, trace.TotalLength, frame.Heads.Length, heads, trace.StraightSegments, trace.CurvedSegments);
        }
        private double SceneOpacity(double u)
        {
            return reducedMotion ? 1 : Ease(u / 0.20) * Clamp((8 - u) / 0.18);
        }
        private Rect CharacterRectangle(double u)
        {
            double camera = Smooth((u - 0.1) / 2.7);
            double height = 2670 - camera * 120;
            double width = height * opened.PixelWidth / opened.PixelHeight;
            double floatY = reducedMotion ? 0 : Math.Sin(u * 1.13) * 2.4;
            return new Rect(430 - camera * 37 + Math.Sin(u * 0.6) * 1.4,
                -650 - camera * 107 + floatY, width, height);
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double u = reducedMotion ? 5.4 : Time * 8 / settings.Duration;
            var screen = new Rect(0, 0, DesignWidth, DesignHeight);
            dc.PushClip(new RectangleGeometry(screen, 10, 10));
            dc.DrawRectangle(ColorBrush("#030A0D"), null, screen);
            var wash = new RadialGradientBrush();
            wash.GradientStops.Add(new GradientStop(Color.FromArgb(60, 5, 57, 68), 0));
            wash.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            dc.DrawEllipse(wash, null, new Point(927, 320), 620, 560);
            double opacity = SceneOpacity(u);
            dc.PushOpacity(opacity);
            for (int i = 0; i < 48; i++)
            {
                double x = 26 + (i * 173 % 1220);
                double y = 70 + (i * 89 % 443);
                double travel = reducedMotion ? 0 : (u * (7 + i % 4)) % 58;
                dc.DrawLine(new Pen(Alpha(0.013 + i % 3 * 0.005, 79, 162, 164), 1), new Point(x, 40), new Point(x, 690));
                dc.DrawLine(new Pen(Alpha(0.10 + i % 4 * 0.017, 50, 158, 156), 0.9), new Point(x, y + travel), new Point(x, y + travel + 20 + i % 16));
            }
            for (int column = 0; column < 26; column++)
            {
                double x = 57 + column * 43;
                double scroll = (u * (29 + column % 5 * 5) + column * 47) % 36;
                for (int row = 0; row < 20; row++)
                {
                    double y = 35 + row * 35 + scroll;
                    dc.PushTransform(new TranslateTransform(x, y));
                    dc.DrawGeometry(Alpha(0.035 + ((column * 7 + row * 3) % 9) * 0.004, 56, 142, 128), null,
                        rainGlyphs[(column * 3 + row * 7) % rainGlyphs.Length]);
                    dc.Pop();
                }
            }
            for (int i = 0; i < 21; i++)
            {
                double x = 35 + (i * 151 % 1190);
                double y = 34 + ((i * 127 + u * (3 + i % 6)) % 650);
                double brightness = 0.17 + 0.13 * Math.Sin(u * 1.1 + i * 0.7);
                Bokeh(dc, x, y, 6 + i % 14, brightness);
            }
            Rect characterRect = CharacterRectangle(u);
            double awaken = EyeOpening(u);
            if (reducedMotion) awaken = 1;
            if (EyeOpennessOverride.HasValue) awaken = Clamp(EyeOpennessOverride.Value);
            double material = reducedMotion ? 1 : Smooth((u - 2.25) / 1.0);
            if (material > 0 && !waitingEffectsTime.HasValue)
            {
                dc.PushOpacity(material);
                PaintPortrait(dc, characterRect, awaken);
                dc.Pop();
            }
            if (material < 1)
            {
                PaintWireframe(dc, characterRect, (1 - material) * Ease(u / 0.3),
                    Clamp(u / 2.25), Clamp((u - 0.95) / 1.30));
            }
            if (!waitingEffectsTime.HasValue) PaintForeground(dc, u, screen);
            dc.Pop(); dc.Pop();
        }
        private void PaintForeground(DrawingContext dc, double u, Rect screen)
        {
            // Slowly moving light across the foreground adds depth while the face remains anchored.
            if (u > 3.7)
            {
                double breathing = 0.06 + 0.025 * Math.Sin(u * 1.6);
                Bokeh(dc, 1125, 367, 165, breathing);
            }
            var darken = new LinearGradientBrush();
            darken.StartPoint = new Point(0, 0); darken.EndPoint = new Point(1, 0);
            darken.GradientStops.Add(new GradientStop(Color.FromArgb(95, 0, 6, 9), 0));
            darken.GradientStops.Add(new GradientStop(Color.FromArgb(25, 0, 6, 9), 0.56));
            darken.GradientStops.Add(new GradientStop(Colors.Transparent, 0.8));
            dc.DrawRectangle(darken, null, screen);
            Cross(dc, 35, 33, 5, 0.85);
            Text(dc, "Codex", 53, 24, 18, "#D6E7E7", true);
            dc.DrawLine(new Pen(Alpha(0.36, 169, 209, 211), 0.7), new Point(130, 34), new Point(469, 34));
            Cross(dc, 478, 34, 4, 0.65);
            Text(dc, "SILVER DRAGON / PERSONAL EDITION", 34, 55, 9, "#768E94", true);
            Text(dc, "01  WIREFRAME", 35, 115, 9.5, u < 2.25 ? "#A0E7EA" : "#4A646B", true);
            Text(dc, "02  MATERIALIZE", 35, 132, 9.5, u >= 2.25 && u < 3.25 ? "#A0E7EA" : "#4A646B", true);
            Text(dc, "03  AWAKEN", 35, 149, 9.5, u >= 3.25 ? "#A0E7EA" : "#4A646B", true);
            dc.DrawLine(new Pen(ColorBrush("#47C9C5"), 2), new Point(35, 164), new Point(35 + 43 * Clamp(u / 4.5), 164));
            double titleEnter = Smooth((u - 3.0) / 1.0);
            dc.PushOpacity(titleEnter);
            dc.PushClip(new RectangleGeometry(new Rect(60, 240, 520 * titleEnter, 174)));
            dc.DrawGeometry(null, StrokePen(0.045, 38, 192, 209, 8), titleGlyph);
            dc.DrawGeometry(null, StrokePen(0.12, 76, 216, 230, 3.0), titleGlyph);
            dc.DrawGeometry(null, StrokePen(0.78, 147, 234, 241, 0.65), titleGlyph);
            dc.PushOpacity(Smooth((u - 4.0) / 0.75));
            dc.DrawGeometry(displayFill, null, titleGlyph);
            dc.PushClip(titleGlyph);
            double lightX = reducedMotion ? 280 : 70 + (u - 4) * 180;
            var glint = new LinearGradientBrush();
            glint.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            glint.GradientStops.Add(new GradientStop(Color.FromArgb(80, 239, 255, 255), 0.5));
            glint.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            dc.DrawRectangle(glint, null, new Rect(lightX, 240, 52, 174));
            dc.Pop(); dc.Pop();
            dc.Pop(); dc.Pop();
            double message = Smooth((u - 4.0) / 0.75);
            dc.PushOpacity(message);
            dc.DrawGeometry(null, StrokePen(0.045, 38, 192, 209, 3), greetingGlyph);
            dc.DrawGeometry(null, StrokePen(0.12, 76, 216, 230, 1.1), greetingGlyph);
            dc.DrawGeometry(null, StrokePen(0.55, 147, 234, 241, 0.35), greetingGlyph);
            dc.DrawGeometry(displayFill, null, greetingGlyph);
            dc.PushOpacity(0.68 * Smooth((u - 4.15) / 0.75));
            dc.DrawGeometry(displayFill, null, englishGlyph);
            dc.Pop();
            var rule = new LinearGradientBrush();
            rule.GradientStops.Add(new GradientStop(Color.FromArgb(165, 106, 229, 216), 0));
            rule.GradientStops.Add(new GradientStop(Color.FromArgb(50, 100, 196, 218), 0.6));
            rule.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            dc.DrawRectangle(rule, null, new Rect(82, 415, 463, 0.8));
            dc.DrawLine(StrokePen(0.75, 102, 223, 214, 1.6), new Point(82, 413), new Point(112, 413));
            Cross(dc, 82, 447, 2.6, 0.72);
            dc.Pop();
            var ellipse = new Pen(Alpha(0.13, 158, 217, 219), 0.8);
            dc.PushTransform(new RotateTransform(-14, 315, 365));
            dc.DrawEllipse(null, ellipse, new Point(315, 365), 274, 53);
            dc.Pop();
            if (!waitingEffectsTime.HasValue) DrawOrbitLight(dc, u * 0.9);
            Text(dc, Launching ? "正在打开 Codex…" : u < 2.25 ? "正在描绘龙娘轮廓…" : u < 3.25 ? "正在让角色显影…" : u < 4.75 ? "龙娘正在苏醒…" : "准备出发。", 82, 559, 12, "#77B5B5", false);
            Text(dc, "QHD / " + settings.FrameRate + " FPS", 35, 669, 9, "#708C92", true);
            Text(dc, Launching ? "ESC 跳过动画 · Codex 继续启动" : "ESC 关闭预览", 237, 666, 11, "#6C878D", false);
            double progress = Clamp(Time / settings.Duration);
            dc.DrawLine(new Pen(Alpha(0.17, 104, 186, 190), 1), new Point(35, 697), new Point(562, 697));
            dc.DrawLine(new Pen(Alpha(0.70, 92, 206, 205), 1.5), new Point(35, 697), new Point(35 + 527 * progress, 697));
            Text(dc, ((int)(progress * 100)).ToString("D2") + "%", 577, 689, 9, "#708C92", true);
            Cross(dc, 1250, 677, 4, 0.35);
            for (int y = 0; y < DesignHeight; y += 3)
                dc.DrawLine(new Pen(Alpha(0.014, 3, 6, 9), 0.6), new Point(0, y), new Point(DesignWidth, y));
        }
    }
}
