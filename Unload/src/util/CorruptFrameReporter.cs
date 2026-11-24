using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;

namespace unload;

// Tells the user about frames that can't be read
public sealed class CorruptFrameReporter
{
    private readonly HashSet<int> reportedFrames = new();

    public static void Show(CorruptFrameException ex)
    {
        MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // Reports each frame once. Scrubbing, stepping through frames or moving the crop sliders shows the same frame many
    // times in a row, and a message box each time would keep popping up while the user tries to move away from it.
    public void ShowOnce(CorruptFrameException ex)
    {
        if (reportedFrames.Add(ex.FrameNumber)) Show(ex);
    }

    // Returns a frame that can be displayed, or null after reporting it when it can't be read
    public BitmapSource? TryLoadFrameImage(FrameStore frames, int frameNumber)
    {
        try
        {
            return ImageProcessor.LoadFrameImage(frames, frameNumber);
        }
        catch (CorruptFrameException ex)
        {
            ShowOnce(ex);
            return null;
        }
    }
}
