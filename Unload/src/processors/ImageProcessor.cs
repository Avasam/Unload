using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Shipwreck.Phash;
using Shipwreck.Phash.Imaging;

namespace unload;

public static class ImageProcessor
{
    private static readonly Rectangle FullFrame = new(0, 0, 100, 100);

    // Crops a frame and returns its Phash
    public static Digest HashFrame(FrameStore frames, int frameNumber, Rectangle cropPercentages)
    {
        byte[] pixels = ReadFramePixels(frames, frameNumber, cropPercentages, out Rectangle crop);
        byte[] luminance = new byte[crop.Width * crop.Height];
        try
        {
            // Same luminance formula as Shipwreck.Phash.Bitmaps' ToLuminanceImage, so hashes match the
            // System.Drawing pipeline this replaces. The pixels are in B, G, R order.
            for (int i = 0, p = 0; i < luminance.Length; i++, p += 3)
            {
                int b = pixels[p];
                int g = pixels[p + 1];
                int r = pixels[p + 2];
                luminance[i] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
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
            frameHashes[i] = HashFrame(frames, i, cropPercentage);

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

    // Returns a frame that can be displayed on the interface
    public static BitmapSource LoadFrameImage(FrameStore frames, int frameNumber)
    {
        return LoadCroppedFrameImage(frames, frameNumber, FullFrame);
    }

    // Returns a cropped frame that can be displayed on the interface
    public static BitmapSource LoadCroppedFrameImage(FrameStore frames, int frameNumber, Rectangle cropPercentages)
    {
        byte[] pixels = ReadFramePixels(frames, frameNumber, cropPercentages, out Rectangle crop);
        try
        {
            // Copies the pixels, so the buffer can go back to the pool right after
            BitmapSource bitmap = BitmapSource.Create(crop.Width, crop.Height, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null,
                pixels, crop.Width * 3);
            bitmap.Freeze();

            return bitmap;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    // Reads a frame and copies the cropped area as tightly packed BGR24 pixels, into a buffer rented from
    // ArrayPool<byte>.Shared that the caller returns. A 1080p frame is about 6 MB, which would otherwise be a new large
    // object heap allocation for every frame shown while scrubbing the timeline.
    // Every frame read goes through here, so a damaged frame always surfaces as a CorruptFrameException.
    // Decodes through WicFrame rather than System.Drawing or WPF imaging, WicFrame explains why.
    private static byte[] ReadFramePixels(FrameStore frames, int frameNumber, Rectangle cropPercentages, out Rectangle crop)
    {
        // Outside the try, since failing to create the WIC factory has nothing to do with this frame
        WicFrame.InitializeThreadFactory();

        byte[]? pixels = null;
        try
        {
            using WicFrame frame = new(frames.ReadFrame(frameNumber));
            crop = GetCropPixels(frame.Width, frame.Height, cropPercentages);
            pixels = ArrayPool<byte>.Shared.Rent(WicFrame.Bgr24Size(crop));
            frame.CopyPixelsBgr24(crop, pixels);

            return pixels;
        }
        // WicFrame reports every WIC failure as COMException, and a truncated frames file is an IOException
        catch (Exception ex) when (ex is COMException or IOException)
        {
            if (pixels != null) ArrayPool<byte>.Shared.Return(pixels);
            throw new CorruptFrameException(frameNumber, ex);
        }
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
}
