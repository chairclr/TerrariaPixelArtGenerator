using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace TerrariaPixelArtGenerator;

internal class PixelImage
{
    public Rgba32[,]? RawPixels;

    public int Width = 0;

    public int Height = 0;

    public PixelImage()
    {

    }

    public PixelImage(Image<Rgba32> image)
    {
        Load(image);
    }

    public void Load(Image<Rgba32> image)
    {
        Size size = image.Size();
        Width = size.Width;
        Height = size.Height;

        CopyImageToPixels(image);
    }

    private unsafe void CopyImageToPixels(Image<Rgba32> image)
    {
        RawPixels = new Rgba32[Height, Width];

        if (!image.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> memory))
        {
            image.CopyPixelDataTo(new Span<Rgba32>(Unsafe.AsPointer(ref RawPixels[0, 0]), Height * Width));

            return;
        }

        Unsafe.CopyBlock(ref Unsafe.As<Rgba32, byte>(ref RawPixels[0, 0]), ref MemoryMarshal.AsBytes(memory.Span)[0], (uint)Width * (uint)Height * 4);
    }

    public Rgba32 PointSample(int x, int y, int width, int height)
    {
        if (RawPixels is null) return Color.Transparent;

        float xRatio = (float)Width / (float)width;
        float yRatio = (float)Height / (float)height;

        int fullX = (int)Math.Floor((float)x * xRatio);
        int fullY = (int)Math.Floor((float)y * yRatio);

        return RawPixels[fullY, fullX];
    }

    // Gaussian distribution average for downsampling
    // Lanczos interpolation for upsampling
    public Rgba32 InterpolatedSample(int x, int y, int width, int height)
    {
        if (RawPixels is null) return Color.Transparent;

        float xRatio = (float)Width / (float)width;
        float yRatio = (float)Height / (float)height;

        // Image has not been resized
        if (xRatio == 1f && yRatio == 1f)
        {
            return RawPixels[y, x];
        }
        // Image has been upscaled
        else if (xRatio < 1f && yRatio < 1f)
        {
            static float Lanczos(float u, float v, float c00, float c10, float c01, float c11)
            {
                float s = 2; // Lanczos filter size

                float x = (float)Math.PI * u;
                float y = (float)Math.PI * v;

                float sincX = Sinc(x / s) * Sinc(x);
                float sincY = Sinc(y / s) * Sinc(y);

                return c00 * sincX * sincY + c10 * (1 - sincX) * sincY + c01 * sincX * (1 - sincY) + c11 * (1 - sincX) * (1 - sincY);
            }

            static float Sinc(float x)
            {
                if (x == 0)
                    return 1;

                return (float)Math.Sin(x) / x;
            }

            float scaledX = x * xRatio;
            float scaledY = y * yRatio;

            int y0 = (int)Math.Floor(scaledY);
            int x0 = (int)Math.Floor(scaledX);
            int y1 = y0 + 1;
            int x1 = x0 + 1;

            float v = scaledY - y0;
            float u = scaledX - x0;

            y0 = Math.Max(0, Math.Min(y0, Height - 1));
            y1 = Math.Max(0, Math.Min(y1, Height - 1));
            x0 = Math.Max(0, Math.Min(x0, Width - 1));
            x1 = Math.Max(0, Math.Min(x1, Width - 1));

            float resultR = Lanczos(u, v, RawPixels[y0, x0].R, RawPixels[y0, x1].R, RawPixels[y1, x0].R, RawPixels[y1, x1].R);
            float resultG = Lanczos(u, v, RawPixels[y0, x0].G, RawPixels[y0, x1].G, RawPixels[y1, x0].G, RawPixels[y1, x1].G);
            float resultB = Lanczos(u, v, RawPixels[y0, x0].B, RawPixels[y0, x1].B, RawPixels[y1, x0].B, RawPixels[y1, x1].B);
            float resultA = Lanczos(u, v, RawPixels[y0, x0].A, RawPixels[y0, x1].A, RawPixels[y1, x0].A, RawPixels[y1, x1].A);

            return new Rgba32(
                (int)Math.Round(resultR),
                (int)Math.Round(resultG),
                (int)Math.Round(resultB),
                (int)Math.Round(resultA));
        }
        // Image has been downscaled
        else
        {

            int fullX = (int)Math.Floor((float)x * xRatio);
            int fullY = (int)Math.Floor((float)y * yRatio);

            int sampleWidth = (int)Math.Ceiling(xRatio);
            int sampleHeight = (int)Math.Ceiling(yRatio);

            float totalR = 0, totalG = 0, totalB = 0, totalA = 0;

            float sampleWeightSum = 0;

            float averageRatio = (xRatio + yRatio) / 2f;

            float standardDeviation = 1.8f + (averageRatio / 5f - 1f);

            for (int xs = 0; xs < sampleWidth; xs++)
            {
                for (int ys = 0; ys < sampleHeight; ys++)
                {
                    Rgba32 col = RawPixels[Math.Clamp(fullY + ys, 0, Height), Math.Clamp(fullX + xs, 0, Width)];

                    static float Gauss(float x, float y, float stddev)
                    {
                        float stddevSquared = stddev * stddev;
                        float eToThePowerOf = ((x * x) + (y * y)) / (2 * stddevSquared);

                        return (1f / (MathF.Tau * stddevSquared)) * MathF.Pow(MathF.E, -eToThePowerOf);
                    }

                    float weight = Gauss(xs - sampleWidth / 2f, ys - sampleHeight / 2f, standardDeviation);

                    totalR += (float)col.R * weight;
                    totalG += (float)col.G * weight;
                    totalB += (float)col.B * weight;
                    totalA += (float)col.A * weight;

                    sampleWeightSum += weight;
                }
            }

            return new Rgba32(
                ((float)totalR / (float)sampleWeightSum) / 255f,
                ((float)totalG / (float)sampleWeightSum) / 255f,
                ((float)totalB / (float)sampleWeightSum) / 255f,
                ((float)totalA / (float)sampleWeightSum) / 255f);
        }
    }
}
