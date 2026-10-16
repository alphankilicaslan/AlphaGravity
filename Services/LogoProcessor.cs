using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EnterpriseAIApp.Services
{
    public static class LogoProcessor
    {
        public static void ConvertBlueLogoToClaudeOrange(string inputPath, string outputPngPath, string? outputIcoPath = null)
        {
            if (!File.Exists(inputPath))
            {
                throw new FileNotFoundException("Logo file not found: " + inputPath);
            }

            var outputDir = Path.GetDirectoryName(outputPngPath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            // 1. Load source image as Bgra32
            var uri = new Uri(Path.GetFullPath(inputPath), UriKind.Absolute);
            var decoder = BitmapDecoder.Create(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];

            var formattedBitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int width = formattedBitmap.PixelWidth;
            int height = formattedBitmap.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[height * stride];
            formattedBitmap.CopyPixels(pixels, stride, 0);

            // Claude terracotta orange target:
            // Anthropic Claude brand color: warm terracotta/coral (#D97756 / #DA7756 / #CC5A36)
            // Target Hue: ~18 to 22 degrees
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte a = pixels[i + 3];

                if (a == 0) continue; // transparent pixel

                RgbToHsl(r, g, b, out double h, out double s, out double l);

                // In original logo, blue/cyan hue is ~200 - 230 deg.
                // Map smoothly to Claude terracotta orange (~16 - 24 deg).
                double newH = 19.0 + ((h - 215.0) * 0.35);
                if (newH < 14.0) newH = 14.0;
                if (newH > 26.0) newH = 26.0;

                // Slightly enhance saturation for vivid Claude coral/terracotta
                double newS = Math.Min(1.0, s * 1.06);
                double newL = l;

                HslToRgb(newH, newS, newL, out byte outR, out byte outG, out byte outB);

                pixels[i] = outB;
                pixels[i + 1] = outG;
                pixels[i + 2] = outR;
            }

            // Create new BitmapSource
            var resultSource = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);

            // Save PNG
            var pngEncoder = new PngBitmapEncoder();
            pngEncoder.Frames.Add(BitmapFrame.Create(resultSource));
            using (var fs = new FileStream(outputPngPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                pngEncoder.Save(fs);
            }

            // Save ICO if requested
            if (!string.IsNullOrEmpty(outputIcoPath))
            {
                CreateIcoFromBitmapSource(resultSource, outputIcoPath);
            }
        }

        private static void CreateIcoFromBitmapSource(BitmapSource source, string icoPath)
        {
            // Create a multi-resolution ICO file (256, 64, 48, 32, 16)
            int[] sizes = new[] { 256, 64, 48, 32, 16 };
            var pngBytesList = new System.Collections.Generic.List<byte[]>();

            foreach (var size in sizes)
            {
                var target = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform((double)size / source.PixelWidth, (double)size / source.PixelHeight));
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(target));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                pngBytesList.Add(ms.ToArray());
            }

            using var fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var bw = new BinaryWriter(fs);

            // ICONDIR Header
            bw.Write((short)0);           // Reserved, must be 0
            bw.Write((short)1);           // Image type: 1 = ICO
            bw.Write((short)sizes.Length);// Number of images

            int offset = 6 + (16 * sizes.Length);

            // ICONDIRENTRY entries
            for (int i = 0; i < sizes.Length; i++)
            {
                int size = sizes[i];
                byte[] data = pngBytesList[i];

                bw.Write((byte)(size == 256 ? 0 : size)); // Width (0 means 256)
                bw.Write((byte)(size == 256 ? 0 : size)); // Height
                bw.Write((byte)0);                        // Color count
                bw.Write((byte)0);                        // Reserved
                bw.Write((short)1);                       // Color planes
                bw.Write((short)32);                      // Bits per pixel
                bw.Write((int)data.Length);               // Size of image data
                bw.Write((int)offset);                    // Offset of image data

                offset += data.Length;
            }

            // Write PNG image data chunks
            for (int i = 0; i < sizes.Length; i++)
            {
                bw.Write(pngBytesList[i]);
            }
        }

        private static void RgbToHsl(byte r, byte g, byte b, out double h, out double s, out double l)
        {
            double rd = r / 255.0;
            double gd = g / 255.0;
            double bd = b / 255.0;

            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double delta = max - min;

            l = (max + min) / 2.0;

            if (Math.Abs(delta) < 0.00001)
            {
                h = 0;
                s = 0;
                return;
            }

            s = l > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);

            if (Math.Abs(max - rd) < 0.00001)
                h = (gd - bd) / delta + (gd < bd ? 6.0 : 0.0);
            else if (Math.Abs(max - gd) < 0.00001)
                h = (bd - rd) / delta + 2.0;
            else
                h = (rd - gd) / delta + 4.0;

            h *= 60.0;
        }

        private static void HslToRgb(double h, double s, double l, out byte r, out byte g, out byte b)
        {
            double rd, gd, bd;
            if (s == 0)
            {
                rd = gd = bd = l;
            }
            else
            {
                double q = l < 0.5 ? l * (1.0 + s) : l + s - (l * s);
                double p = 2.0 * l - q;
                double hk = h / 360.0;
                rd = HueToRgb(p, q, hk + 1.0 / 3.0);
                gd = HueToRgb(p, q, hk);
                bd = HueToRgb(p, q, hk - 1.0 / 3.0);
            }
            r = (byte)Math.Clamp((int)Math.Round(rd * 255.0), 0, 255);
            g = (byte)Math.Clamp((int)Math.Round(gd * 255.0), 0, 255);
            b = (byte)Math.Clamp((int)Math.Round(bd * 255.0), 0, 255);
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1.0;
            if (t > 1) t -= 1.0;
            if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
            return p;
        }
    }
}
