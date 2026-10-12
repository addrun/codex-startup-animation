using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DragonLauncher
{
    // Aligned eyelid poses retain the iris and foreground hair at their original size.
    public sealed class EyeRig
    {
        private const int Frames = 121, Columns = 11, PatchWidth = 214, PatchHeight = 132;
        private readonly byte[] pixels;
        private readonly int atlasWidth;
        private readonly BitmapSource[][] cache = { new BitmapSource[Frames], new BitmapSource[Frames] };
        private readonly Rect[] destinations;

        public EyeRig(string root, BitmapSource character, bool preferDetailTextures = true)
        {
            string path = Scene.ResolveTexturePath(root, Path.Combine("assets", "dragon-eye-motion.png"), preferDetailTextures);
            if (!File.Exists(path)) throw new FileNotFoundException("缺少睁眼动画图集，请保留 assets/dragon-eye-motion.png。", path);
            var atlas = new BitmapImage(); atlas.BeginInit(); atlas.CacheOption = BitmapCacheOption.OnLoad;
            atlas.UriSource = new Uri(path); atlas.EndInit(); atlas.Freeze();
            atlasWidth = atlas.PixelWidth;
            if (atlasWidth != Columns * PatchWidth * 2 || atlas.PixelHeight != Columns * PatchHeight)
                throw new InvalidDataException("睁眼动画图集尺寸应为 4708 × 1452。");
            pixels = new byte[atlas.PixelWidth * atlas.PixelHeight * 4];
            new FormatConvertedBitmap(atlas, PixelFormats.Pbgra32, null, 0).CopyPixels(pixels, atlasWidth * 4, 0);
            double sx = character.PixelWidth / 1024.0, sy = character.PixelHeight / 1536.0;
            destinations = new[] {
                new Rect(326 * sx, 606 * sy, PatchWidth * sx, PatchHeight * sy),
                new Rect(549 * sx, 548 * sy, PatchWidth * sx, PatchHeight * sy)
            };
        }
        private static double Smooth(double x) { x = Math.Max(0, Math.Min(1, x)); return x * x * (3 - 2 * x); }
        public void Draw(DrawingContext dc, double openness)
        {
            int frame = (int)Math.Round(Math.Max(0, Math.Min(1, openness)) * (Frames - 1));
            for (int eye = 0; eye < 2; eye++)
            {
                if (cache[eye][frame] == null) cache[eye][frame] = Extract(eye, frame);
                dc.DrawImage(cache[eye][frame], destinations[eye]);
            }
        }
        private BitmapSource Extract(int eye, int frame)
        {
            int originX = frame % Columns * PatchWidth * 2 + eye * PatchWidth;
            int originY = frame / Columns * PatchHeight;
            var data = new byte[PatchWidth * PatchHeight * 4];
            for (int y = 0; y < PatchHeight; y++) for (int x = 0; x < PatchWidth; x++)
            {
                double feather = Smooth(Math.Min(x, PatchWidth - 1 - x) / 10.0) * Smooth(Math.Min(y, PatchHeight - 1 - y) / 10.0);
                int source = ((originY + y) * atlasWidth + originX + x) * 4;
                int target = (y * PatchWidth + x) * 4;
                for (int c = 0; c < 4; c++) data[target + c] = (byte)Math.Round(pixels[source + c] * feather);
            }
            var image = BitmapSource.Create(PatchWidth, PatchHeight, 96, 96, PixelFormats.Pbgra32, null, data, PatchWidth * 4);
            image.Freeze(); return image;
        }
    }
}
