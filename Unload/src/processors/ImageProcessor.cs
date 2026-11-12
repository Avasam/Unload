using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Shipwreck.Phash;
using Shipwreck.Phash.Imaging;

namespace unload;

public static class ImageProcessor
{
    // Crops a frame image and returns its Phash.
    // Decodes through WicFrame rather than System.Drawing or WPF imaging, which both break under Wine when
    // frames are hashed in parallel. WicFrame explains why.
    public static Digest HashFrame(byte[] image, Rectangle cropPercentages)
    {
        Rectangle crop;
        byte[] pixels;
        using (WicFrame frame = new(image))
        {
            crop = GetCropPixels(frame.Width, frame.Height, cropPercentages);
            pixels = frame.CopyPixelsBgr24(crop);
        }

        // Same luminance formula as Shipwreck.Phash.Bitmaps' ToLuminanceImage, so hashes match the
        // System.Drawing pipeline this replaces. The pixels are in B, G, R order.
        byte[] luminance = new byte[crop.Width * crop.Height];
        for (int i = 0, p = 0; i < luminance.Length; i++, p += 3)
        {
            int b = pixels[p];
            int g = pixels[p + 1];
            int r = pixels[p + 2];
            luminance[i] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
        }

        return ImagePhash.ComputeDigest(new ByteImage(crop.Width, crop.Height, luminance));
    }

    // Applies cropping and hashes all frames in range and returns the hashes in a dictionary
    public static Dictionary<int, Digest> CropAndPhashFrames(FrameStore frames, Rectangle cropPercentage, int startFrame, int endFrame,
        CancellationTokenSource cts, Action<double> onProgress, Action onFinished)
    {
        ConcurrentDictionary<int, Digest> frameHashes = new();

        int doneFrames = 0;
        int totalFrames = endFrame - startFrame + 1;

        // Hashes frames in parallel to the max amount of concurrent tasks the user defined. Holds a cancellation token so it can be stopped
        // endFrame is inclusive, but Parallel.For's upper bound is exclusive
        Parallel.For(startFrame, endFrame + 1, new ParallelOptions { CancellationToken = cts.Token }, i =>
        {
            // Crops and hashes the current frame, then stores the hash at the frame number
            frameHashes[i] = HashFrame(frames.ReadFrame(i), cropPercentage);

            // Notifies the caller of its progress
            int done = Interlocked.Increment(ref doneFrames);
            onProgress(done / (double)totalFrames * 100d);
        });

        onFinished();

        return new Dictionary<int, Digest>(frameHashes);
    }

    // Compares an entire dictiory against reference hashes and returns a dictionary of similarity
    public static Dictionary<int, float[]> GetHashDictSimilarity(Dictionary<int, Digest> hashDict, Digest[] referenceHashes)
    {
        ConcurrentDictionary<int, float[]> frameSimilarities = new();

        // Gets similarity of all frames in parallel
        Parallel.ForEach(hashDict, hash =>
        {
            frameSimilarities[hash.Key] = new float[referenceHashes.Length];

            // Calculated hashes of reference images
            for (int i = 0; i < referenceHashes.Length; i++)
            {
                float score = ImagePhash.GetCrossCorrelation(hash.Value, referenceHashes[i]);
                frameSimilarities[hash.Key][i] = score;
            }
        });

        return new Dictionary<int, float[]>(frameSimilarities);
    }

    // Decodes an encoded image and returns a cropped copy of it.
    // The full size image is disposed right away: GDI+ keeps its pixels in unmanaged memory,
    // which the garbage collector does not account for, so leaving it to the finalizer leaks.
    public static Bitmap LoadCroppedImage(byte[] image, Rectangle cropPercentages)
    {
        // GDI+ reads from the stream for as long as the bitmap lives, the cropped copy doesn't need it
        using MemoryStream stream = new(image);
        using Bitmap source = new(stream);
        return CropImage(source, cropPercentages);
    }

    // Crops an image to by given percentages
    public static Bitmap CropImage(Bitmap source, Rectangle cropPercentages)
    {
        return source.Clone(GetCropPixels(source.Width, source.Height, cropPercentages), PixelFormat.Format24bppRgb);
    }

    // Converts crop percentages to pixels, kept at least 1 pixel and inside the image
    private static Rectangle GetCropPixels(int width, int height, Rectangle cropPercentages)
    {
        int x = Math.Min((int)Math.Round(cropPercentages.X / 100d * width), width - 1);
        int y = Math.Min((int)Math.Round(cropPercentages.Y / 100d * height), height - 1);

        return new Rectangle
        {
            X = x,
            Y = y,
            Width = Math.Clamp((int)Math.Round(cropPercentages.Width / 100d * width), 1, width - x),
            Height = Math.Clamp((int)Math.Round(cropPercentages.Height / 100d * height), 1, height - y)
        };
    }

    // Converts a bitmap to a bitmap image that can be displayed on the interface
    public static BitmapImage BitmapToBitmapImage(this Bitmap bitmap)
    {
        using MemoryStream memory = new();

        bitmap.Save(memory, ImageFormat.Png);
        memory.Position = 0;

        return LoadBitmapImage(memory);
    }

    // Decodes an encoded image into a bitmap image that can be displayed on the interface
    public static BitmapImage LoadBitmapImage(byte[] image)
    {
        using MemoryStream memory = new(image);
        return LoadBitmapImage(memory);
    }

    private static BitmapImage LoadBitmapImage(Stream stream)
    {
        BitmapImage bitmapImage = new();
        bitmapImage.BeginInit();

        // OnLoad decodes during EndInit, so the stream can be disposed right after
        bitmapImage.StreamSource = stream;
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;

        bitmapImage.EndInit();
        bitmapImage.Freeze();

        return bitmapImage;
    }
}
