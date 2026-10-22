using Microsoft.Win32;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Tubase.Audio;

namespace Tubase;

public partial class MainWindow : Window
{
    private const double HeaderWidth = 230;
    private const int OutputRate = 44100;
    private ProjectDocument _project = new();
    private string? _projectDirectory;
    private readonly Dictionary<string, AudioAsset> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly WaveOutPlayer _player = new();
    private readonly System.Windows.Threading.DispatcherTimer _uiTimer = new();
    private readonly List<(Canvas Canvas, Line Line)> _playheadLines = new();
    private double _playhead;
    private double _pixelsPerSecond = 80;
    private int _selectedTrack;

    private sealed class ClipDrag
    {
        public required AudioClip Clip { get; init; }
        public required Canvas Canvas { get; init; }
        public required Border Border { get; init; }
        public required Point StartPoint { get; init; }
        public required double OriginalStart { get; init; }
        public required double OriginalDuration { get; init; }
        public bool Resizing { get; init; }
    }
    private ClipDrag? _clipDrag;

    public MainWindow()
    {
        InitializeComponent();
        _player.PositionChanged += _ => Dispatcher.BeginInvoke(() => UpdatePlayhead());
        _uiTimer.Interval = TimeSpan.FromMilliseconds(33);
        _uiTimer.Tick += (_, _) =>
        {
            if (_player.IsPlaying)
            {
                _playhead = _player.PositionSeconds;
                UpdatePlayhead();
            }
            else if (PlayButton.Content?.ToString()?.Contains("Pause") == true)
            {
                _player.Stop();
                PlayButton.Content = "▶ Play";
                StatusText.Text = "Playback finished.";
            }
        };
        _uiTimer.Start();
        AddTrackInternal();
        RefreshTimeline();
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder for the Tubase project" };
        if (dialog.ShowDialog() != true) return;
        var directory = dialog.FolderName;
        var manifest = ProjectStore.ManifestPath(directory);
        if (File.Exists(manifest))
        {
            var answer = MessageBox.Show("A Tubase project already exists here. Open it?", "Tubase",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) LoadProject(manifest);
            return;
        }
        if (Directory.EnumerateFileSystemEntries(directory).Any())
        {
            var answer = MessageBox.Show("This folder is not empty. Create the project here without deleting existing files?",
                "Tubase", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }
        _player.Stop();
        _projectDirectory = directory;
        _project = new ProjectDocument { Name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)) };
        _assets.Clear();
        _playhead = 0;
        _selectedTrack = 0;
        AddTrackInternal();
        SaveCurrentProject();
        RefreshTimeline();
        StatusText.Text = "New local project created.";
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open Tubase project",
            Filter = "Tubase project (project.json)|project.json|JSON files (*.json)|*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true) LoadProject(dialog.FileName);
    }

    private void LoadProject(string manifest)
    {
        try
        {
            _player.Stop();
            var loaded = ProjectStore.Load(manifest);
            var directory = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
            var assets = new Dictionary<string, AudioAsset>(StringComparer.OrdinalIgnoreCase);
            foreach (var clip in loaded.Tracks.SelectMany(t => t.Clips))
            {
                var fullPath = ResolveAssetPath(directory, clip.File);
                if (!File.Exists(fullPath)) continue;
                if (!assets.ContainsKey(clip.File)) assets[clip.File] = WavFile.Read(fullPath);
                clip.DurationSeconds = Math.Clamp(clip.DurationSeconds, 0.05,
                    Math.Max(0.05, assets[clip.File].Duration - clip.SourceOffsetSeconds));
            }
            _project = loaded;
            _projectDirectory = directory;
            _assets.Clear();
            foreach (var pair in assets) _assets[pair.Key] = pair.Value;
            _selectedTrack = Math.Clamp(_selectedTrack, 0, Math.Max(0, _project.Tracks.Count - 1));
            _playhead = 0;
            RefreshTimeline();
            StatusText.Text = "Project loaded. Missing media is marked in the track.";
        }
        catch (Exception ex) { ShowError("Could not open project", ex); }
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_projectDirectory is null) { NewProject_Click(sender, e); return; }
        SaveCurrentProject();
    }

    private void SaveCurrentProject()
    {
        if (_projectDirectory is null) return;
        try
        {
            ProjectStore.Save(_project, _projectDirectory);
            ProjectText.Text = _project.Name;
            StatusText.Text = "Project saved locally.";
        }
        catch (Exception ex) { ShowError("Could not save project", ex); }
    }

    private void AddTrack_Click(object sender, RoutedEventArgs e)
    {
        StopForStructuralEdit();
        AddTrackInternal();
        _selectedTrack = _project.Tracks.Count - 1;
        RefreshTimeline();
        StatusText.Text = "Audio track added.";
    }

    private void StopForStructuralEdit()
    {
        if (!_player.IsPlaying) return;
        _playhead = _player.PositionSeconds;
        _player.Stop();
        PlayButton.Content = "▶ Play";
    }

    private void AddTrackInternal()
    {
        var number = _project.Tracks.Count + 1;
        _project.Tracks.Add(new AudioTrack { Name = "Audio " + number });
    }

    private void ImportWav_Click(object sender, RoutedEventArgs e)
    {
        if (_projectDirectory is null)
        {
            MessageBox.Show("Create or open a project before importing audio.", "Tubase",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "Import WAV audio",
            Filter = "WAV audio (*.wav)|*.wav",
            Multiselect = true,
            CheckFileExists = true,
            CheckPathExists = true,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            StopForStructuralEdit();
            var result = WavImportService.Import(
                _project, _assets, _projectDirectory, dialog.FileNames, _selectedTrack, _playhead,
                putEachFileOnItsOwnTrack: dialog.FileNames.Length > 1);
            if (result.ImportedCount > 0)
            {
                if (_project.Tracks.Count == 0) AddTrackInternal();
                _selectedTrack = Math.Clamp(_selectedTrack, 0, _project.Tracks.Count - 1);
                RefreshTimeline();
            }
            StatusText.Text = $"Imported {result.ImportedCount} WAV file(s); {result.Failures.Count} failed.";
            if (result.Failures.Count > 0)
            {
                var details = string.Join(Environment.NewLine, result.Failures.Select(f =>
                    Path.GetFileName(f.SourcePath) + ": " + f.Reason));
                MessageBox.Show(
                    $"Imported {result.ImportedCount} file(s). The following file(s) could not be imported:\n\n{details}\n\nDetails were also written to %LOCALAPPDATA%\\Tubase\\Logs\\import.log.",
                    "WAV import completed with errors", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex) { ShowError("Could not import WAV", ex); }
    }

    private void ExportWav_Click(object sender, RoutedEventArgs e)
    {
        if (_projectDirectory is null) { MessageBox.Show("Save the project first."); return; }
        var dialog = new SaveFileDialog
        {
            Title = "Export stereo WAV",
            Filter = "WAV audio (*.wav)|*.wav",
            FileName = _project.Name + ".wav",
            DefaultExt = ".wav",
            AddExtension = true
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var framesLong = (long)Math.Ceiling(Mixer.ProjectDuration(_project) * OutputRate);
            if (framesLong <= 0) throw new InvalidOperationException("There is no audio to export.");
            if (framesLong * 4L + 44 > uint.MaxValue)
                throw new NotSupportedException("WAV export is limited to files smaller than 4 GiB.");
            using var stream = File.Create(dialog.FileName);
            using var writer = new BinaryWriter(stream);
            var dataBytes = checked((uint)(framesLong * 4));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36u + dataBytes);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1); writer.Write((short)2);
            writer.Write(OutputRate); writer.Write(OutputRate * 4);
            writer.Write((short)4); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(dataBytes);
            var left = new float[8192];
            var right = new float[8192];
            long position = 0;
            while (position < framesLong)
            {
                var count = (int)Math.Min(left.Length, framesLong - position);
                if (count != left.Length) { left = new float[count]; right = new float[count]; }
                Mixer.Render(_project, _assets, left, right, (int)position, OutputRate);
                for (var i = 0; i < count; i++)
                {
                    writer.Write(ToInt16(left[i]));
                    writer.Write(ToInt16(right[i]));
                }
                position += count;
            }
            StatusText.Text = "Exported stereo 16-bit PCM WAV at 44.1 kHz.";
        }
        catch (Exception ex) { ShowError("Could not export WAV", ex); }
    }

    private static short ToInt16(float value) =>
        (short)Math.Clamp((int)Math.Round(Math.Clamp(value, -1f, 1f) * 32767f), short.MinValue, short.MaxValue);

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_projectDirectory is null || !_project.Tracks.SelectMany(t => t.Clips).Any())
        {
            StatusText.Text = "Import at least one WAV clip to play.";
            return;
        }
        try
        {
            if (_player.IsPlaying)
            {
                _player.Stop();
                PlayButton.Content = "▶ Play";
                StatusText.Text = "Playback stopped.";
            }
            else
            {
                if (_playhead >= Mixer.ProjectDuration(_project)) _playhead = 0;
                _player.Play(_project, _assets, _playhead);
                PlayButton.Content = "❚❚ Pause";
                StatusText.Text = "Playing through the Windows audio output.";
            }
        }
        catch (Exception ex)
        {
            _player.Stop();
            PlayButton.Content = "▶ Play";
            ShowError("Could not start audio playback", ex);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        PlayButton.Content = "▶ Play";
        _playhead = 0;
        UpdatePlayhead();
        StatusText.Text = "Stopped and returned to the start.";
    }

    private void ZoomSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _pixelsPerSecond = e.NewValue;
        if (IsLoaded) RefreshTimeline();
    }

    private void RefreshTimeline()
    {
        if (TracksPanel is null) return;
        TracksPanel.Children.Clear();
        _playheadLines.Clear();
        var end = Math.Max(12, Mixer.ProjectDuration(_project) + 4);
        var width = Math.Max(1500, end * _pixelsPerSecond);
        var ruler = new Grid { Width = HeaderWidth + width, Height = 32, Background = new SolidColorBrush(Color.FromRgb(35, 39, 48)) };
        ruler.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(HeaderWidth) });
        ruler.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        var label = new TextBlock { Text = "TRACK / TIME", Margin = new Thickness(10, 7, 0, 0), Foreground = Brushes.LightGray, FontSize = 11 };
        ruler.Children.Add(label);
        var rulerCanvas = new Canvas { Width = width, Height = 32 };
        Grid.SetColumn(rulerCanvas, 1);
        ruler.Children.Add(rulerCanvas);
        var tickSeconds = _pixelsPerSecond < 55 ? 5 : 1;
        for (var s = 0; s <= end; s += tickSeconds)
        {
            var x = s * _pixelsPerSecond;
            rulerCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 15, Y2 = 32, Stroke = new SolidColorBrush(Color.FromRgb(94, 101, 116)), StrokeThickness = 1 });
            var tickLabel = new TextBlock { Text = TimeSpan.FromSeconds(s).ToString(@"mm\:ss"), Foreground = Brushes.LightGray, FontSize = 10 };
            Canvas.SetLeft(tickLabel, x + 3);
            Canvas.SetTop(tickLabel, 0);
            rulerCanvas.Children.Add(tickLabel);
        }
        TracksPanel.Children.Add(ruler);

        for (var index = 0; index < _project.Tracks.Count; index++)
        {
            var trackIndex = index;
            var track = _project.Tracks[index];
            var row = new Grid { Width = HeaderWidth + width, Height = 116, Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(HeaderWidth) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            var header = BuildTrackHeader(track, trackIndex);
            row.Children.Add(header);
            var canvas = new Canvas { Width = width, Height = 116, Background = new SolidColorBrush(Color.FromRgb(39, 43, 52)), ClipToBounds = true };
            Grid.SetColumn(canvas, 1);
            row.Children.Add(canvas);
            for (var s = 0; s <= end; s += tickSeconds)
            {
                var x = s * _pixelsPerSecond;
                canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = 116, Stroke = new SolidColorBrush(Color.FromRgb(53, 58, 69)), StrokeThickness = 1 });
            }
            foreach (var clip in track.Clips) canvas.Children.Add(BuildClip(clip, canvas));
            var playhead = new Line { Y1 = 0, Y2 = 116, Stroke = Brushes.OrangeRed, StrokeThickness = 2, IsHitTestVisible = false };
            canvas.Children.Add(playhead);
            _playheadLines.Add((canvas, playhead));
            canvas.MouseLeftButtonDown += (_, args) =>
            {
                if (args.OriginalSource == canvas)
                {
                    _selectedTrack = trackIndex;
                    _playhead = Math.Max(0, args.GetPosition(canvas).X / _pixelsPerSecond);
                    UpdatePlayhead();
                }
            };
            row.MouseLeftButtonDown += (_, _) => _selectedTrack = trackIndex;
            TracksPanel.Children.Add(row);
        }
        UpdatePlayhead();
        ProjectText.Text = _projectDirectory is null ? "No project" : _project.Name;
    }

    private Border BuildTrackHeader(AudioTrack track, int trackIndex)
    {
        var panel = new StackPanel { Margin = new Thickness(8, 5, 8, 4) };
        var title = new DockPanel();
        var name = new TextBox
        {
            Text = track.Name, Width = 135, Background = new SolidColorBrush(Color.FromRgb(45, 50, 61)),
            Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(4)
        };
        name.LostFocus += (_, _) => track.Name = name.Text;
        title.Children.Add(name);
        var mute = new CheckBox { Content = "M", IsChecked = track.Muted, Margin = new Thickness(8, 3, 0, 0), Foreground = Brushes.White };
        mute.Checked += (_, _) => track.Muted = true;
        mute.Unchecked += (_, _) => track.Muted = false;
        title.Children.Add(mute);
        panel.Children.Add(title);
        panel.Children.Add(new TextBlock { Text = "Volume", FontSize = 10, Foreground = Brushes.LightGray });
        var gain = new Slider { Minimum = 0, Maximum = 2, Value = track.Gain, Width = 205, Height = 20, Tag = trackIndex };
        gain.ValueChanged += (_, _) => track.Gain = (float)gain.Value;
        panel.Children.Add(gain);
        panel.Children.Add(new TextBlock { Text = "Pan", FontSize = 10, Foreground = Brushes.LightGray });
        var pan = new Slider { Minimum = -1, Maximum = 1, Value = track.Pan, Width = 205, Height = 20 };
        pan.ValueChanged += (_, _) => track.Pan = (float)pan.Value;
        panel.Children.Add(pan);
        return new Border
        {
            Background = new SolidColorBrush(trackIndex == _selectedTrack ? Color.FromRgb(48, 58, 69) : Color.FromRgb(43, 47, 57)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 64, 76)), BorderThickness = new Thickness(0, 0, 1, 1),
            Child = panel
        };
    }

    private Border BuildClip(AudioClip clip, Canvas canvas)
    {
        var assetExists = _assets.ContainsKey(clip.File);
        var width = Math.Max(35, clip.DurationSeconds * _pixelsPerSecond);
        var border = new Border
        {
            Width = width, Height = 76, CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(assetExists ? Color.FromRgb(26, 112, 105) : Color.FromRgb(130, 55, 58)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(102, 222, 192)), BorderThickness = new Thickness(1),
            Cursor = Cursors.SizeAll, Tag = clip, ToolTip = assetExists ? clip.Name : "Missing media: " + clip.File
        };
        Canvas.SetLeft(border, clip.StartSeconds * _pixelsPerSecond);
        Canvas.SetTop(border, 18);
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        content.Children.Add(new TextBlock
        {
            Text = clip.Name, Margin = new Thickness(7, 5, 0, 0), Foreground = Brushes.White,
            FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Top
        });
        var grip = new Border { Background = new SolidColorBrush(Color.FromRgb(185, 255, 230)), Cursor = Cursors.SizeWE };
        Grid.SetColumn(grip, 1);
        content.Children.Add(grip);
        border.Child = content;
        border.MouseLeftButtonDown += (_, e) =>
        {
            _selectedTrack = _project.Tracks.FindIndex(t => t.Clips.Contains(clip));
            var point = e.GetPosition(canvas);
            if (_player.IsPlaying) StopForStructuralEdit();
            _clipDrag = new ClipDrag
            {
                Clip = clip, Canvas = canvas, Border = border, StartPoint = point,
                OriginalStart = clip.StartSeconds, OriginalDuration = clip.DurationSeconds,
                Resizing = point.X - Canvas.GetLeft(border) >= border.ActualWidth - 15
            };
            border.CaptureMouse();
            e.Handled = true;
        };
        border.MouseMove += (_, e) =>
        {
            if (_clipDrag is null || _clipDrag.Border != border || e.LeftButton != MouseButtonState.Pressed) return;
            var delta = (e.GetPosition(canvas).X - _clipDrag.StartPoint.X) / _pixelsPerSecond;
            if (_clipDrag.Resizing)
            {
                var maxLength = _assets.TryGetValue(clip.File, out var asset)
                    ? Math.Max(0.05, asset.Duration - clip.SourceOffsetSeconds) : clip.DurationSeconds + delta;
                clip.DurationSeconds = Math.Clamp(_clipDrag.OriginalDuration + delta, 0.05, maxLength);
                border.Width = Math.Max(35, clip.DurationSeconds * _pixelsPerSecond);
            }
            else
            {
                clip.StartSeconds = Math.Max(0, _clipDrag.OriginalStart + delta);
                Canvas.SetLeft(border, clip.StartSeconds * _pixelsPerSecond);
            }
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            if (_clipDrag?.Border != border) return;
            border.ReleaseMouseCapture();
            _clipDrag = null;
            StatusText.Text = "Clip position or trim updated. Save the project to keep the change.";
        };
        return border;
    }

    private void UpdatePlayhead()
    {
        foreach (var (canvas, line) in _playheadLines)
            Canvas.SetLeft(line, _playhead * _pixelsPerSecond);
        var time = TimeSpan.FromSeconds(Math.Max(0, _playhead));
        PositionText.Text = time.ToString(@"mm\:ss\.fff");
    }

    private static string ResolveAssetPath(string directory, string relative) =>
        Path.GetFullPath(Path.Combine(directory, relative));

    private void ShowError(string title, Exception ex)
    {
        StatusText.Text = title + ": " + ex.Message;
        MessageBox.Show(title + Environment.NewLine + ex.Message, "Tubase", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnClosed(EventArgs e)
    {
        _uiTimer.Stop();
        _player.Dispose();
        base.OnClosed(e);
    }
}
