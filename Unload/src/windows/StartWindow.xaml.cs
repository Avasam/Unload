using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Win32;
using unload.Properties;

namespace unload;

public partial class StartWindow : Window
{
    private class PreviousVideo
    {
        public string FileName { get; set; }
        public string FramesDirectory { get; set; }
        public DateTime LastOpened { get; set; }

        public PreviousVideo(string fileName, string framesDirectory, DateTime lastOpened)
        {
            FileName = fileName;
            FramesDirectory = framesDirectory;
            LastOpened = lastOpened;
        }
    }

    public string? workingDirectory;

    private const string FRAMES_SUFFIX = "_frames";

    private ObservableCollection<PreviousVideo> previousVideos;

    public StartWindow()
    {
        InitializeComponent();

        Title += $" {FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion}";

        try
        {
            VideoProcessor.SetFFMpegPath();
        }
        catch
        {
            string message = "Failed to initialize FFMpeg. Make sure ffmpeg.exe and ffprobe.exe are located in the ffmpeg folder of this application.";
            MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
        }

        PreviousVideo[] previousVideosArray = JsonSerializer.Deserialize<PreviousVideo[]>(Settings.Default.PreviousVideos) ?? [];
        previousVideos = new(previousVideosArray);

        if (previousVideos.Count > 0)
        {
            lbxPreviousVideos.ItemsSource = previousVideos;
        }
        else
        {
            lbxPreviousVideos.Visibility = Visibility.Hidden;
            lblNoPreviousVideos.Visibility = Visibility.Visible;
        }

        workingDirectory = Settings.Default.WorkingDirectory;
        if (workingDirectory.Length == 0) workingDirectory = null;
    }

    private const string ISSUES_URL = "https://github.com/milankarman/Unload/issues";

    public async Task LoadProject(string framesDirectory)
    {
        string infoPath = Path.Join(framesDirectory, "conversion-info.json");

        // Check if conversion info file can be found
        if (!File.Exists(infoPath))
        {
            string message = "Couldn't find \"conversion-info.json\" in frames folder. Please convert the video again.";
            MessageBox.Show(message, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);

            return;
        }

        string jsonString = File.ReadAllText(infoPath);
        ConversionInfo? info;
        try
        {
            info = JsonSerializer.Deserialize<ConversionInfo>(jsonString);
        }
        catch (JsonException)
        {
            info = null;
        }

        // Check if conversion info contains data
        if (info == null)
        {
            string message = "Couldn't read \"conversion-info.json\" in frames folder. The file might be corrupted. Please convert the video again.";
            MessageBox.Show(message, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);

            return;
        }

        // Frames folders from before FrameStore hold a JPEG file per frame, which isn't read anymore
        if (!File.Exists(Path.Join(framesDirectory, FrameStore.FRAMES_FILE_NAME)))
        {
            string message = $"Couldn't find \"{FrameStore.FRAMES_FILE_NAME}\" in frames folder. " +
                "Frames converted by older versions of Unload are stored differently. Please convert the video again.";
            MessageBox.Show(message, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);

            return;
        }

        string fileName = info.FileName;
        double fps = info.FPS;

        // Indexing reads every frame, which takes a while for a long video on a hard drive. Opening indexed frames is
        // instant, so the progress window only shows for indexing.
        ProgressWindow? progress = FrameStore.HasIndex(framesDirectory) ? null : new("Indexing frames", this);
        Action<double>? onIndexing = progress == null ? null : percentage => progress.percentage = percentage;
        CancellationToken cancellationToken = progress?.cts.Token ?? CancellationToken.None;

        Project? project = null;
        try
        {
            progress?.Show();
            gridContainer.IsEnabled = false;

            try
            {
                project = await Task.Run(() => new Project(framesDirectory, fileName ?? "Unkown", fps, onIndexing, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                // The progress window closes itself when cancelled
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show($"Couldn't read the converted frames: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);

                return;
            }
            finally
            {
                gridContainer.IsEnabled = true;
                progress?.Close();
                progress?.cts.Dispose();
            }

            // A conversion that failed or was stopped before its first frame leaves no frames to show
            if (project.totalFrames == 0)
            {
                string message = "No converted frames were found in the frames folder. Please convert the video again.";
                MessageBox.Show(message, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);

                return;
            }

            // Check if the same amount of converted images are found as the video has frames (1 missing or 1 extra seems normal)
            if (project.totalFrames < info.ExpectedFrames - 1)
            {
                string message = "Warning, fewer converted frames are found than expected. This could mean that the video has dropped frames. " +
                    "The converted frames could also be damaged, which converting the video again would fix.";
                MessageBox.Show(message, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // The frames still open without the index, but indexing them on every open is slow and shouldn't happen
            if (project.frames.IndexError != null)
            {
                string message = $"Couldn't read or save the frames index \"{FrameStore.INDEX_FILE_NAME}\": {project.frames.IndexError.Message}" +
                    Environment.NewLine + Environment.NewLine +
                    "The frames were indexed again, which makes opening this video slower. " +
                    $"If this keeps happening, please report it at {ISSUES_URL}";
                MessageBox.Show(message, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (fileName != null)
            {
                // Add to previously opened videos then sort and save them
                PreviousVideo? existing = previousVideos.FirstOrDefault(i => i.FileName == fileName);

                if (existing != null)
                {
                    existing.LastOpened = DateTime.Now;
                }
                else
                {
                    PreviousVideo previousVideo = new(fileName, framesDirectory, DateTime.Now);
                    previousVideos.Add(previousVideo);
                }

                previousVideos = new(previousVideos.OrderByDescending(i => i.LastOpened).Take(5).ToList());

                SavePreviousVideos();
            }

            // Start the main window, which disposes the project when closed
            MainWindow mainWindow = new(project);
            project = null;

            mainWindow.Show();
            Close();
        }
        finally
        {
            // Only still set when something failed before the main window took ownership of the project
            project?.Dispose();
        }
    }

    private async void btnLoad_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new();
        dialog.Filter = "Unload Supported Formats|*.3g2;*.3gp;*.3gp2;*.asf;*.avi;*.dvrms;*.flv;*.h261;*.h263;*.h264;*.m2t;" +
            "*.m2ts;*.m4v;*.mkv;*.mod;*.mov;*.mp4;*.mpg;*.mxf;*.webm;*.wmv;*.xmv;conversion-info.json;";

        if (dialog.ShowDialog() == true)
        {
            string? framesDirectory;

            if (dialog.SafeFileName == "conversion-info.json")
            {
                framesDirectory = Path.GetDirectoryName(dialog.FileName);
            }
            else
            {
                string? fileDirectory = Path.GetDirectoryName(dialog.FileName);
                framesDirectory = Path.Join(workingDirectory ?? fileDirectory,
                    RemoveSymbols(dialog.SafeFileName) + FRAMES_SUFFIX);
            }

            if (framesDirectory == null || !Directory.Exists(framesDirectory))
            {
                MessageBox.Show(
                    $"No {FRAMES_SUFFIX} folder accompanying this video found. Convert the video first.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            await LoadProject(framesDirectory);
        }
    }

    private void btnConvert_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new();
        dialog.Filter = "Unload Supported Formats|*.3g2;*.3gp;*.3gp2;*.asf;*.avi;*.dvrms;*.flv;*.h261;*.h263;*.h264;*.m2t;" +
            "*.m2ts;*.m4v;*.mkv;*.mod;*.mov;*.mp4;*.mpg;*.mxf;*.webm;*.wmv;*.xmv;";

        if (dialog.ShowDialog() == true)
        {
            // Create _frames folder to store the image sequence, ommiting illegal symbols
            string? fileDirectory = Path.GetDirectoryName(dialog.FileName);

            string framesDirectory = Path.Join(workingDirectory ?? fileDirectory,
                RemoveSymbols(dialog.SafeFileName) + FRAMES_SUFFIX);

            if (!Directory.Exists(framesDirectory))
            {
                Directory.CreateDirectory(framesDirectory);
            }

            async void onFinished()
            {
                gridContainer.IsEnabled = true;
                await LoadProject(framesDirectory);
            }

            gridContainer.IsEnabled = false;
            ConvertWindow convertWindow = new(this, dialog.FileName, framesDirectory, onFinished);
            convertWindow.GetVideoInfoAndShow();
        }
    }

    private void btnStartSettings_Click(object sender, RoutedEventArgs e)
    {
        StartSettingsWindow startSettingsWindow = new(this, workingDirectory);
        startSettingsWindow.Show();
        gridContainer.IsEnabled = false;
    }

    private async void btnPreviousVideoLoad_Click(object sender, RoutedEventArgs e)
    {
        Button cmd = (Button)sender;
        if (cmd.DataContext is PreviousVideo previousVideo)
        {
            if (!Directory.Exists(previousVideo.FramesDirectory))
            {
                MessageBox.Show(
                    $"{previousVideo.FramesDirectory} not found. The working directory might have changed or the frames folder " +
                    $"could have been moved or deleted.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            await LoadProject(previousVideo.FramesDirectory);
        }
    }

    private async void btnPreviousVideoDelete_Click(object sender, RoutedEventArgs e)
    {
        Button cmd = (Button)sender;
        if (cmd.DataContext is PreviousVideo previousVideo)
        {
            string framesDirectory = previousVideo.FramesDirectory;

            if (Directory.Exists(framesDirectory))
            {
                MessageBoxResult result = MessageBox.Show(
                    $"Delete the converted frames of \"{previousVideo.FileName}\" from disk?\n\n{framesDirectory}",
                    "Delete frames",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

                if (result != MessageBoxResult.Yes) return;

                // Disable the whole row so the entry can't be loaded while its frames are being deleted
                UIElement row = (UIElement)cmd.Parent;
                row.IsEnabled = false;

                try
                {
                    await Task.Run(() => DeleteFramesDirectory(framesDirectory));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    row.IsEnabled = true;
                    MessageBox.Show(
                        $"Failed to delete {framesDirectory}: {ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }
            }

            previousVideos.Remove(previousVideo);
            SavePreviousVideos();

            if (previousVideos.Count == 0)
            {
                lbxPreviousVideos.Visibility = Visibility.Hidden;
                lblNoPreviousVideos.Visibility = Visibility.Visible;
            }
        }
    }

    // Only deletes what a conversion writes, so a folder the user picked by its conversion-info.json keeps their other files
    private static void DeleteFramesDirectory(string framesDirectory)
    {
        File.Delete(Path.Join(framesDirectory, FrameStore.FRAMES_FILE_NAME));
        File.Delete(Path.Join(framesDirectory, FrameStore.INDEX_FILE_NAME));

        // Frames folders from before FrameStore hold a JPEG file per frame, deleting them still frees that space
        IEnumerable<string> frames = Directory.EnumerateFiles(framesDirectory, "*.jpg")
            .Where(frame => FrameFileName().IsMatch(Path.GetFileNameWithoutExtension(frame)));

        try
        {
            Parallel.ForEach(frames, File.Delete);
        }
        catch (AggregateException ex)
        {
            // Surface the first failure as-is so the caller's IOException / UnauthorizedAccessException filter still applies
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }

        File.Delete(Path.Join(framesDirectory, "conversion-info.json"));

        if (!Directory.EnumerateFileSystemEntries(framesDirectory).Any())
        {
            Directory.Delete(framesDirectory);
        }
    }

    private void SavePreviousVideos()
    {
        Settings.Default.PreviousVideos = JsonSerializer.Serialize(previousVideos);
        Settings.Default.Save();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo("cmd", $"/c start {e.Uri.AbsoluteUri}") { CreateNoWindow = true });
        e.Handled = true;
    }

    // Removes symbols that conflict with FFmpeg arguments
    private static string RemoveSymbols(string path)
    {
        return FFmpegConflictingSymbols().Replace(path, "");
    }

    [GeneratedRegex(@"[^0-9a-zA-Z\/\\:]+")]
    private static partial Regex FFmpegConflictingSymbols();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex FrameFileName();
}
