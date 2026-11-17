using System;

namespace unload;

// A frame that couldn't be read from the frames file, or that doesn't decode.
// WIC errors come back as a generic "Unexpected HRESULT" message, so the message shows the error code instead.
public sealed class CorruptFrameException(int frameNumber, Exception innerException)
    : Exception($"Couldn't read frame {frameNumber} (error 0x{innerException.HResult:X8}). " +
        "The converted frames may be corrupted, converting the video again should fix it.", innerException)
{
    public int FrameNumber { get; } = frameNumber;
}
