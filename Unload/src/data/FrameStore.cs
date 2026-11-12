using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace unload;

// The frames of a converted video, stored as a single MJPEG file: complete JPEG images back to back, frame 1 first.
//
// One JPEG file per frame made converting quadratically slower under Wine. On a case sensitive filesystem, Wine
// looks for a case insensitive match before creating any file, which reads the whole directory, so every frame
// cost more than the one before. Measured under Wine:
// - 5 minutes of 60 FPS video (18k frames): about 100 seconds as a file per frame, 20 seconds in one file. With a
//   file per frame, the rate fell from 434 to 107 frames per second as the directory filled.
// - Only creating empty files in one directory: the first 5000 took 8 seconds, files 25k to 30k took 76 seconds.
// - 25 minutes of 60 FPS video (90k frames) in one file: 95 seconds. As a file per frame, long videos took over
//   an hour.
//
// One file is also lighter on any system:
// - Antivirus (Windows Defender) scans one new file instead of one per frame.
// - No per file overhead: no MFT record or inode per frame, and no partly used cluster per frame (about 3% of the
//   frames' size with 4 KB clusters).
// - Frames are written and hashed as one sequential stream, instead of a file system metadata update and a
//   separate open per frame, which matters most on hard drives where each of those is a seek.
// - Deleting a conversion frees one directory entry and a few runs of clusters. Deleting a file per frame updated
//   metadata scattered across the disk for every frame: 132k files took 12 seconds under Wine on an SSD, one 5 GB
//   file a quarter second, and hard drives widen that gap.
//
// Where each frame starts is saved in an index next to the frames, so opening a project doesn't read every frame.
public sealed class FrameStore : IDisposable
{
    public const string FRAMES_FILE_NAME = "frames.mjpeg";
    public const string INDEX_FILE_NAME = "frames.idx";

    private const byte MARKER_PREFIX = 0xFF;
    private const byte MARKER_SOI = 0xD8;
    private const byte MARKER_EOI = 0xD9;
    private const byte MARKER_SOS = 0xDA;
    private const byte MARKER_TEM = 0x01;
    private const byte MARKER_RST0 = 0xD0;
    private const byte MARKER_RST7 = 0xD7;

    private readonly SafeFileHandle file;
    // Byte offset where each frame starts, followed by where the last frame ends
    private readonly long[] offsets;

    public int FrameCount => offsets.Length - 1;

    // Why the index couldn't be read or saved when opening the frames, null when it could. The frames are still
    // readable, but every open indexes them again, so a failure that keeps happening should be reported.
    public Exception? IndexError { get; }

    private FrameStore(SafeFileHandle _file, long[] _offsets, Exception? _indexError)
    {
        file = _file;
        offsets = _offsets;
        IndexError = _indexError;
    }

    // Opens the frames in a frames directory, indexing them first when there is no up to date index.
    // `onIndexing` receives the indexing progress as a percentage, it isn't called when the index is up to date.
    public static FrameStore Open(string framesDirectory, Action<double>? onIndexing = null,
        CancellationToken cancellationToken = default)
    {
        SafeFileHandle file = OpenFramesFile(framesDirectory);
        try
        {
            long length = RandomAccess.GetLength(file);
            Exception? indexError = null;

            long[]? offsets = null;
            try
            {
                offsets = ReadIndex(framesDirectory, length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                indexError = ex;
            }

            if (offsets == null)
            {
                offsets = IndexFrames(file, length, onIndexing, cancellationToken);
                try
                {
                    SaveIndex(framesDirectory, length, offsets);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    indexError ??= ex;
                }
            }

            return new FrameStore(file, offsets, indexError);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    // Whether the frames in a frames directory have an up to date index, so opening them won't index them first
    public static bool HasIndex(string framesDirectory)
    {
        try
        {
            using SafeFileHandle file = OpenFramesFile(framesDirectory);
            return ReadIndex(framesDirectory, RandomAccess.GetLength(file)) != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Indexes the frames in a frames directory and saves the index next to them
    public static void WriteIndex(string framesDirectory)
    {
        using SafeFileHandle file = OpenFramesFile(framesDirectory);
        long length = RandomAccess.GetLength(file);
        long[] offsets = IndexFrames(file, length, null, CancellationToken.None);

        try
        {
            SaveIndex(framesDirectory, length, offsets);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Opening the frames indexes them again, and reports it through IndexError if saving fails again
        }
    }

    // Reads the JPEG image of a frame, frame numbers start at 1
    public byte[] ReadFrame(int frameNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameNumber, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frameNumber, FrameCount);

        long start = offsets[frameNumber - 1];
        byte[] image = new byte[offsets[frameNumber] - start];

        // RandomAccess reads at an offset without a shared file position, so frames can be read from many threads
        int read = 0;
        while (read < image.Length)
        {
            int count = RandomAccess.Read(file, image.AsSpan(read), start + read);
            if (count == 0) throw new EndOfStreamException($"{FRAMES_FILE_NAME} ended inside frame {frameNumber}.");
            read += count;
        }

        return image;
    }

    public void Dispose()
    {
        file.Dispose();
    }

    private static SafeFileHandle OpenFramesFile(string framesDirectory)
    {
        // FileShare.Delete so the frames can still be deleted or converted again while a project has them open
        return File.OpenHandle(Path.Join(framesDirectory, FRAMES_FILE_NAME), FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);
    }

    // The index holds 64 bit little endian values: the frames file length it was made for, the frame count,
    // then the frame offsets. Returns null when there is no index or it doesn't match the frames file, throws when
    // it can't be read.
    private static long[]? ReadIndex(string framesDirectory, long framesFileLength)
    {
        string indexPath = Path.Join(framesDirectory, INDEX_FILE_NAME);
        if (!File.Exists(indexPath)) return null;

        ReadOnlySpan<long> values = MemoryMarshal.Cast<byte, long>(File.ReadAllBytes(indexPath));
        if (values.Length < 3 || values[0] != framesFileLength || values[1] != values.Length - 3) return null;

        return values[2..].ToArray();
    }

    private static void SaveIndex(string framesDirectory, long framesFileLength, long[] offsets)
    {
        long[] values = new long[offsets.Length + 2];
        values[0] = framesFileLength;
        values[1] = offsets.Length - 1;
        offsets.CopyTo(values, 2);

        File.WriteAllBytes(Path.Join(framesDirectory, INDEX_FILE_NAME), MemoryMarshal.AsBytes(values.AsSpan()));
    }

    // Finds where every complete JPEG image in the frames file starts. A conversion that was stopped can leave an
    // incomplete last image, which is left out. Indexing also stops at a damaged image, as skipping it would shift
    // every later frame.
    private static long[] IndexFrames(SafeFileHandle file, long length, Action<double>? onProgress,
        CancellationToken cancellationToken)
    {
        FileWindow data = new(file, length);
        List<long> offsets = [];
        long start = 0;

        while (start < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onProgress?.Invoke(100d * start / length);

            long end = FindImageEnd(data, start);
            if (end < 0) break;

            offsets.Add(start);
            start = end;
        }

        offsets.Add(start);
        return offsets.ToArray();
    }

    // Returns where the JPEG image starting at `start` ends, or -1 when no complete image starts there.
    // Walks the marker segments by their lengths, so bytes inside them are never mistaken for markers.
    // https://www.w3.org/Graphics/JPEG/itu-t81.pdf (B.1.1.2 Markers, B.1.1.5 Entropy-coded data segments)
    private static long FindImageEnd(FileWindow data, long start)
    {
        long length = data.Length;
        if (length - start < 2 || data[start] != MARKER_PREFIX || data[start + 1] != MARKER_SOI) return -1;

        long position = start + 2;
        while (true)
        {
            // Any marker may be preceded by fill bytes
            while (length - position >= 2 && data[position] == MARKER_PREFIX && data[position + 1] == MARKER_PREFIX)
            {
                position++;
            }

            if (length - position < 2 || data[position] != MARKER_PREFIX) return -1;

            byte marker = data[position + 1];
            position += 2;

            if (marker == MARKER_EOI) return position;
            // Markers without a length or segment
            if (marker is MARKER_TEM or (>= MARKER_RST0 and <= MARKER_RST7)) continue;

            if (length - position < 2) return -1;
            int segmentLength = (data[position] << 8) | data[position + 1];
            if (segmentLength < 2) return -1;
            position += segmentLength;

            if (marker == MARKER_SOS)
            {
                position = FindMarkerAfterScan(data, position);
                if (position < 0) return -1;
            }
        }
    }

    // Skips the entropy coded data of a scan and returns where the next marker starts, or -1 when the data ends first.
    // In that data, 0xFF is followed by 0x00 when it is a data byte, and restart markers belong to the scan.
    private static long FindMarkerAfterScan(FileWindow data, long position)
    {
        while (true)
        {
            position = data.IndexOf(MARKER_PREFIX, position);
            if (position < 0 || data.Length - position < 2) return -1;

            byte next = data[position + 1];
            if (next is not (0x00 or (>= MARKER_RST0 and <= MARKER_RST7))) return position;

            position += 2;
        }
    }

    // Reads a file through a window that moves forward as it is read. Reading through a memory mapping instead counted
    // every page of the frames file in the working set (several GB for a long video), until the OS reclaimed them.
    private sealed class FileWindow(SafeFileHandle file, long length)
    {
        private readonly byte[] buffer = new byte[4 * 1024 * 1024];
        private long bufferStart;
        private int bufferLength;

        public long Length => length;

        public byte this[long position]
        {
            get
            {
                if (position < bufferStart || position >= bufferStart + bufferLength) Fill(position);
                return buffer[position - bufferStart];
            }
        }

        // Returns the position of the first `value` at or after `position`, or -1 when the file ends first
        public long IndexOf(byte value, long position)
        {
            while (position < length)
            {
                if (position < bufferStart || position >= bufferStart + bufferLength) Fill(position);

                int offset = (int)(position - bufferStart);
                int found = buffer.AsSpan(offset, bufferLength - offset).IndexOf(value);
                if (found >= 0) return position + found;

                position = bufferStart + bufferLength;
            }

            return -1;
        }

        private void Fill(long position)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(position, length);

            bufferStart = position;
            bufferLength = 0;
            int toRead = (int)Math.Min(buffer.Length, length - position);
            while (bufferLength < toRead)
            {
                int count = RandomAccess.Read(file, buffer.AsSpan(bufferLength, toRead - bufferLength), position + bufferLength);
                if (count == 0) throw new EndOfStreamException($"{FRAMES_FILE_NAME} got shorter while it was indexed.");
                bufferLength += count;
            }
        }
    }
}
