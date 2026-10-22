using Microsoft.Win32;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Text.Json;
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
    private readonly System.Windows.Threading.DispatcherTimer _autosaveTimer = new();
    private readonly List<(Canvas Canvas, Line Line)> _playheadLines = new();
    private readonly List<Border> _trackHeaders = new();
    private readonly List<TextBlock> _trackSummaries = new();
    private double _playhead;
    private double _pixelsPerSecond = 80;
    private int _selectedTrack;
    private Guid? _selectedClipId;
    private readonly ProjectEditHistory _history = new();
    private bool _restoring;
    private bool _updatingInspectorControls;
    private Guid? _inspectorEditTrackId;
    private double _rightZoneWidth = 270;
    private bool _rightZoneVisible;

    private string CaptureState() => JsonSerializer.Serialize(_project);
    private void RecordUndo()
    {
        if (_restoring) return;
        _history.Record(CaptureState()); UpdateHistoryButtons();
    }
    private void UpdateHistoryButtons()
    {
        if (UndoButton is not null) UndoButton.IsEnabled = _history.CanUndo;
        if (RedoButton is not null) RedoButton.IsEnabled = _history.CanRedo;
    }
    private void RestoreState(string state)
    {
        _restoring = true;
        try { _project = JsonSerializer.Deserialize<ProjectDocument>(state) ?? new ProjectDocument(); _selectedTrack = Math.Clamp(_selectedTrack, 0, Math.Max(0, _project.Tracks.Count - 1)); RefreshTimeline(); SaveCurrentProject(); }
        finally { _restoring = false; UpdateHistoryButtons(); }
    }
    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        StopForStructuralEdit();
        if (!_history.Undo(CaptureState(), out var previous)) return;
        RestoreState(previous); StatusText.Text = "元に戻しました。";
    }
    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        StopForStructuralEdit();
        if (!_history.Redo(CaptureState(), out var next)) return;
        RestoreState(next); StatusText.Text = "やり直しました。";
    }

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
        ProjectLayoutGrid.SizeChanged += ProjectLayoutGrid_SizeChanged;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        _player.PositionChanged += _ => Dispatcher.BeginInvoke(() => UpdatePlayhead());
        _uiTimer.Interval = TimeSpan.FromMilliseconds(33);
        _uiTimer.Tick += (_, _) =>
        {
            if (_player.IsPlaying)
            {
                _playhead = _player.PositionSeconds;
                UpdatePlayhead();
            }
            else if (PlayButton.Content?.ToString()?.Contains("⏸") == true)
            {
                _player.Stop();
                PlayButton.Content = "▶";
                StatusText.Text = "再生が終了しました。";
            }
        };
        _uiTimer.Start();
        _autosaveTimer.Interval = TimeSpan.FromMinutes(2);
        _autosaveTimer.Tick += (_, _) => SaveRecoverySnapshot();
        _autosaveTimer.Start();
        AddTrackInternal();
        RefreshTimeline();
    }

    private void RightZoneToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (RightZoneColumn is null || RightZoneToggle is null) return;
        var show = RightZoneToggle.IsChecked == true;
        if (show == _rightZoneVisible) return;

        if (show)
        {
            RightZoneColumn.Width = new GridLength(_rightZoneWidth);
            RightZoneSplitter.Visibility = Visibility.Visible;
            RightZonePane.Visibility = Visibility.Visible;
            _rightZoneVisible = true;
            FitRightZoneWidth();
        }
        else
        {
            if (RightZoneColumn.ActualWidth >= RightZonePane.MinWidth)
                _rightZoneWidth = RightZoneColumn.ActualWidth;
            _rightZoneVisible = false;
            RightZoneSplitter.Visibility = Visibility.Collapsed;
            RightZonePane.Visibility = Visibility.Collapsed;
            RightZoneColumn.Width = new GridLength(0);
        }
    }

    private void ProjectLayoutGrid_SizeChanged(object sender, SizeChangedEventArgs e) => FitRightZoneWidth();

    private void FitRightZoneWidth()
    {
        if (!_rightZoneVisible || ProjectLayoutGrid.ActualWidth <= 0) return;
        const double minimumRightWidth = 150;
        const double splitterWidth = 10;
        var maximumWidth = Math.Max(minimumRightWidth,
            ProjectLayoutGrid.ActualWidth - InspectorColumn.ActualWidth - ProjectZoneColumn.MinWidth - splitterWidth);
        var fittedWidth = Math.Min(_rightZoneWidth, maximumWidth);
        if (Math.Abs(RightZoneColumn.Width.Value - fittedWidth) > 0.5)
            RightZoneColumn.Width = new GridLength(fittedWidth);
    }

    private void RightZoneSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_rightZoneVisible && RightZoneColumn.ActualWidth >= RightZonePane.MinWidth)
            _rightZoneWidth = RightZoneColumn.ActualWidth;
    }

    private void SaveRecoverySnapshot()
    {
        if (_projectDirectory is null) return;
        try
        {
            ProjectStore.SaveRecovery(_project, _projectDirectory);
        }
        catch (Exception ex) { StatusText.Text = "復旧データの保存に失敗しました: " + ex.Message; }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var command = EditorInput.Resolve(e.Key, Keyboard.Modifiers, Keyboard.FocusedElement is TextBox);
        switch (command)
        {
            case EditorCommand.Save: SaveProject_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case EditorCommand.Undo: Undo_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case EditorCommand.Redo: Redo_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case EditorCommand.DeleteSelection:
                if (_selectedClipId is Guid clipId && _project.Tracks.SelectMany(t => t.Clips).Any(c => c.Id == clipId))
                {
                    RecordUndo(); StopForStructuralEdit(); TimelineEditing.RemoveClip(_project, clipId); _selectedClipId = null;
                    RefreshTimeline(); StatusText.Text = "クリップを削除しました。Ctrl+Zで元に戻せます。";
                }
                e.Handled = true; break;
            case EditorCommand.TogglePlayback:
                if (_player.IsPlaying) Stop_Click(this, new RoutedEventArgs()); else Play_Click(this, new RoutedEventArgs());
                e.Handled = true; break;
        }
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Tubaseプロジェクトの保存先フォルダーを選択" };
        if (dialog.ShowDialog() != true) return;
        var directory = dialog.FolderName;
        var manifest = ProjectStore.ManifestPath(directory);
        if (File.Exists(manifest))
        {
            var answer = MessageBox.Show("このフォルダーにはTubaseプロジェクトがあります。開きますか？", "Tubase",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) LoadProject(manifest);
            return;
        }
        if (Directory.EnumerateFileSystemEntries(directory).Any())
        {
            var answer = MessageBox.Show("このフォルダーは空ではありません。既存ファイルを削除せずにプロジェクトを作成しますか？",
                "Tubase", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }
        _player.Stop();
        _history.Clear(); UpdateHistoryButtons();
        _projectDirectory = directory;
        _project = new ProjectDocument { Name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)) };
        _assets.Clear();
        _playhead = 0;
        _selectedTrack = 0;
        AddTrackInternal();
        SaveCurrentProject();
        RefreshTimeline();
        StatusText.Text = "新しいローカルプロジェクトを作成しました。";
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Tubaseプロジェクトを開く",
            Filter = "Tubaseプロジェクト (project.json)|project.json|JSONファイル (*.json)|*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true) LoadProject(dialog.FileName);
    }

    private void LoadProject(string manifest)
    {
        try
        {
            _player.Stop();
            var directory = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
            var loaded = ProjectStore.Load(manifest);
            var recovery = ProjectStore.RecoveryPath(directory);
            if (File.Exists(recovery) && File.GetLastWriteTimeUtc(recovery) > File.GetLastWriteTimeUtc(manifest))
            {
                var answer = MessageBox.Show("新しい復旧スナップショットがあります。復元しますか？", "Tubase の復旧", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                    loaded = ProjectStore.LoadRecovery(directory) ?? loaded;
            }
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
            _history.Clear(); UpdateHistoryButtons();
            StatusText.Text = "プロジェクトを読み込みました。見つからない素材はトラックに表示されます。";
        }
        catch (Exception ex) { ShowError("プロジェクトを開けませんでした", ex); }
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
            var recovery = Path.Combine(_projectDirectory, "Autosaves", "recovery.json");
            if (File.Exists(recovery)) File.Delete(recovery);
            ProjectText.Text = _project.Name;
            StatusText.Text = "プロジェクトを保存しました。";
        }
        catch (Exception ex) { ShowError("プロジェクトを保存できませんでした", ex); }
    }

    private void AddTrack_Click(object sender, RoutedEventArgs e)
    {
        RecordUndo();
        StopForStructuralEdit();
        AddTrackInternal();
        _selectedTrack = _project.Tracks.Count - 1;
        RefreshTimeline();
        StatusText.Text = "オーディオトラックを追加しました。";
    }

    private void DeleteTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index } || index < 0 || index >= _project.Tracks.Count) return;
        RecordUndo(); StopForStructuralEdit();
        TimelineEditing.RemoveTrack(_project, index);
        _selectedTrack = Math.Clamp(index, 0, _project.Tracks.Count - 1);
        RefreshTimeline(); StatusText.Text = "トラックを削除しました。Ctrl+Zで元に戻せます。";
    }

    private void StopForStructuralEdit()
    {
        if (!_player.IsPlaying) return;
        _playhead = _player.PositionSeconds;
        _player.Stop();
        PlayButton.Content = "▶";
    }

    private void AddTrackInternal()
    {
        var number = _project.Tracks.Count + 1;
        _project.Tracks.Add(new AudioTrack { Name = "オーディオ " + number });
    }

    private void ImportWav_Click(object sender, RoutedEventArgs e)
    {
        if (_projectDirectory is null)
        {
            MessageBox.Show("オーディオを読み込む前にプロジェクトを作成するか開いてください。", "Tubase",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "WAVオーディオを読み込む",
            Filter = "WAVオーディオ (*.wav)|*.wav",
            Multiselect = true,
            CheckFileExists = true,
            CheckPathExists = true,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            RecordUndo();
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
            StatusText.Text = $"WAV {result.ImportedCount}件を読み込みました（失敗 {result.Failures.Count}件）。";
            if (result.Failures.Count > 0)
            {
                var details = string.Join(Environment.NewLine, result.Failures.Select(f =>
                    Path.GetFileName(f.SourcePath) + ": " + f.Reason));
                MessageBox.Show(
                    $"{result.ImportedCount}件を読み込みました。次のファイルは読み込めませんでした:\n\n{details}\n\n詳細は %LOCALAPPDATA%\\Tubase\\Logs\\import.log にも記録しました。",
                    "WAV読み込みでエラーが発生しました", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex) { ShowError("WAVを読み込めませんでした", ex); }
    }

    private void ExportWav_Click(object sender, RoutedEventArgs e)
    {
        if (_projectDirectory is null) { MessageBox.Show("先にプロジェクトを保存してください。", "Tubase"); return; }
        var dialog = new SaveFileDialog
        {
            Title = "ステレオWAVを書き出す",
            Filter = "WAVオーディオ (*.wav)|*.wav",
            FileName = _project.Name + ".wav",
            DefaultExt = ".wav",
            AddExtension = true
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var framesLong = (long)Math.Ceiling(Mixer.ProjectDuration(_project) * OutputRate);
            if (framesLong <= 0) throw new InvalidOperationException("書き出すオーディオがありません。");
            if (framesLong * 4L + 44 > uint.MaxValue)
                throw new NotSupportedException("WAV書き出しは4 GiB未満のファイルに限られます。");
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
            StatusText.Text = "44.1 kHz・16-bit PCMステレオWAVを書き出しました。";
        }
        catch (Exception ex) { ShowError("WAVを書き出せませんでした", ex); }
    }

    private static short ToInt16(float value) =>
        (short)Math.Clamp((int)Math.Round(Math.Clamp(value, -1f, 1f) * 32767f), short.MinValue, short.MaxValue);

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_projectDirectory is null || !_project.Tracks.SelectMany(t => t.Clips).Any())
        {
            StatusText.Text = "再生するWAVクリップを読み込んでください。";
            return;
        }
        try
        {
            if (_player.IsPlaying)
            {
                _player.Stop();
                PlayButton.Content = "▶";
                StatusText.Text = "再生を一時停止しました。";
            }
            else
            {
                if (_playhead >= Mixer.ProjectDuration(_project)) _playhead = 0;
                _player.Play(_project, _assets, _playhead);
                PlayButton.Content = "⏸";
                StatusText.Text = "Windowsオーディオで再生中です。";
            }
        }
        catch (Exception ex)
        {
            _player.Stop();
            PlayButton.Content = "▶";
            ShowError("再生を開始できませんでした", ex);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        PlayButton.Content = "▶";
        _playhead = 0;
        UpdatePlayhead();
        StatusText.Text = "停止して先頭に戻りました。";
    }

    private void ZoomSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _pixelsPerSecond = e.NewValue;
        if (IsLoaded) RefreshTimeline();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value = Math.Min(ZoomSlider.Maximum, ZoomSlider.Value + 15);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value = Math.Max(ZoomSlider.Minimum, ZoomSlider.Value - 15);

    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(
        "Tubase オーディオ制作環境\n\nCtrl+S 保存　 Ctrl+Z 元に戻す　 Ctrl+Y やり直す　 Space 再生／停止\n音量・パンは左のインスペクターで調整します。",
        "Tubase ヘルプ", MessageBoxButton.OK, MessageBoxImage.Information);

    private void RefreshTimeline()
    {
        if (TracksPanel is null) return;
        TracksPanel.Children.Clear();
        _playheadLines.Clear();
        _trackHeaders.Clear();
        _trackSummaries.Clear();
        var end = Math.Max(12, Mixer.ProjectDuration(_project) + 4);
        var width = Math.Max(1500, end * _pixelsPerSecond);
        var ruler = new Grid { Width = HeaderWidth + width, Height = 32, Background = new SolidColorBrush(Color.FromRgb(37, 39, 44)) };
        ruler.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(HeaderWidth) });
        ruler.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        var label = new TextBlock { Text = "トラックリスト", Margin = new Thickness(12, 7, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(166, 171, 180)), FontSize = 10, FontWeight = FontWeights.SemiBold };
        ruler.Children.Add(label);
        var rulerCanvas = new Canvas { Width = width, Height = 32 };
        Grid.SetColumn(rulerCanvas, 1);
        ruler.Children.Add(rulerCanvas);
        var tickSeconds = _pixelsPerSecond < 55 ? 1d : 0.25d;
        for (var s = 0d; s <= end; s += tickSeconds)
        {
            var x = s * _pixelsPerSecond;
            var major = Math.Abs(s - Math.Round(s)) < 0.001;
            rulerCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = major ? 12 : 23, Y2 = 32, Stroke = new SolidColorBrush(major ? Color.FromRgb(91, 96, 105) : Color.FromRgb(57, 60, 68)), StrokeThickness = 1 });
            if (major)
            {
                var tickLabel = new TextBlock { Text = TimeSpan.FromSeconds(s).ToString(@"mm\:ss"), Foreground = Brushes.LightGray, FontSize = 10 };
                Canvas.SetLeft(tickLabel, x + 3);
                Canvas.SetTop(tickLabel, 0);
                rulerCanvas.Children.Add(tickLabel);
            }
        }
        TracksPanel.Children.Add(ruler);

        for (var index = 0; index < _project.Tracks.Count; index++)
        {
            var trackIndex = index;
            var track = _project.Tracks[index];
            var row = new Grid { Width = HeaderWidth + width, Height = 132, Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(HeaderWidth) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            var header = BuildTrackHeader(track, trackIndex);
            _trackHeaders.Add(header);
            row.Children.Add(header);
            var canvas = new Canvas { Width = width, Height = 132, Background = new SolidColorBrush(Color.FromRgb(30, 32, 37)), ClipToBounds = true };
            Grid.SetColumn(canvas, 1);
            row.Children.Add(canvas);
            for (var s = 0d; s <= end; s += tickSeconds)
            {
                var x = s * _pixelsPerSecond;
                var major = Math.Abs(s - Math.Round(s)) < 0.001;
                canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = 132, Stroke = new SolidColorBrush(major ? Color.FromRgb(47, 50, 57) : Color.FromRgb(36, 38, 44)), StrokeThickness = 1 });
            }
            foreach (var clip in track.Clips) canvas.Children.Add(BuildClip(clip, canvas));
            var playhead = new Line { Y1 = 0, Y2 = 132, Stroke = new SolidColorBrush(Color.FromRgb(232, 91, 83)), StrokeThickness = 2, IsHitTestVisible = false };
            canvas.Children.Add(playhead);
            _playheadLines.Add((canvas, playhead));
            canvas.MouseLeftButtonDown += (_, args) =>
            {
                if (args.OriginalSource == canvas)
                {
                    SelectTrack(trackIndex);
                    var seconds = Math.Max(0, args.GetPosition(canvas).X / _pixelsPerSecond);
                    _playhead = TimelineEditing.Snap(seconds, SnapCheck.IsChecked == true);
                    UpdatePlayhead();
                }
            };
            TracksPanel.Children.Add(row);
        }
        UpdateInspector();
        UpdatePlayhead();
        ProjectText.Text = _projectDirectory is null ? "プロジェクトなし" : _project.Name;
    }

    private void SelectTrack(int trackIndex, Guid? clipId = null)
    {
        if (trackIndex < 0 || trackIndex >= _project.Tracks.Count) return;
        _selectedTrack = trackIndex;
        _selectedClipId = clipId;
        for (var i = 0; i < _trackHeaders.Count; i++)
        {
            var selected = i == trackIndex;
            _trackHeaders[i].Background = new SolidColorBrush(selected ? Color.FromRgb(47, 51, 59) : Color.FromRgb(39, 41, 47));
            _trackHeaders[i].BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(98, 200, 178) : Color.FromRgb(54, 57, 64));
            _trackHeaders[i].BorderThickness = new Thickness(selected ? 3 : 1, 0, 1, 1);
        }
        UpdateInspector();
    }

    private void UpdateInspector()
    {
        if (InspectorTrackName is null) return;
        if (_selectedTrack < 0 || _selectedTrack >= _project.Tracks.Count)
        {
            InspectorTrackName.Text = "トラックなし";
            InspectorTrackDetails.Text = "トラックを選択してください";
            InspectorEventName.Text = "クリップを選択してください";
            InspectorEventDetails.Text = string.Empty;
            InspectorMediaStatus.Text = string.Empty;
            _updatingInspectorControls = true;
            InspectorGain.IsEnabled = false;
            InspectorPan.IsEnabled = false;
            InspectorGain.Value = 1;
            InspectorPan.Value = 0;
            InspectorGainValue.Text = "100%";
            InspectorPanValue.Text = "中央";
            _inspectorEditTrackId = null;
            _updatingInspectorControls = false;
            return;
        }

        var track = _project.Tracks[_selectedTrack];
        InspectorTrackName.Text = track.Name;
        InspectorTrackDetails.Text = $"オーディオトラック · {track.Clips.Count} クリップ";
        _updatingInspectorControls = true;
        InspectorGain.IsEnabled = true;
        InspectorPan.IsEnabled = true;
        InspectorGain.Value = Math.Clamp(track.Gain, 0f, 2f);
        InspectorPan.Value = Math.Clamp(track.Pan, -1f, 1f);
        InspectorGainValue.Text = $"{track.Gain * 100:0}%";
        InspectorPanValue.Text = FormatPan(track.Pan);
        _inspectorEditTrackId = track.Id;
        _updatingInspectorControls = false;
        var clip = _selectedClipId is Guid id ? track.Clips.FirstOrDefault(item => item.Id == id) : null;
        if (clip is null)
        {
            InspectorEventName.Text = "クリップを選択してください";
            InspectorEventDetails.Text = string.Empty;
            InspectorMediaStatus.Text = string.Empty;
            return;
        }

        InspectorEventName.Text = clip.Name;
        InspectorEventDetails.Text = $"開始 {clip.StartSeconds:0.000} 秒 · 長さ {clip.DurationSeconds:0.000} 秒";
        InspectorMediaStatus.Text = _assets.ContainsKey(clip.File)
            ? $"素材読込済み · {Path.GetFileName(clip.File)}"
            : $"素材未検出 · {Path.GetFileName(clip.File)}";
    }

    private static string FormatPan(float pan) => Math.Abs(pan) < 0.005f
        ? "中央"
        : pan < 0 ? $"左 {Math.Abs(pan) * 100:0}%" : $"右 {pan * 100:0}%";

    private void InspectorControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        BeginInspectorEdit();
    }

    private void InspectorControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            BeginInspectorEdit();
    }

    private void BeginInspectorEdit()
    {
        if (_selectedTrack < 0 || _selectedTrack >= _project.Tracks.Count) return;
        var selected = _project.Tracks[_selectedTrack];
        if (_inspectorEditTrackId != selected.Id) return;
        RecordUndo();
    }

    private AudioTrack? GetInspectorEditTrack() =>
        _inspectorEditTrackId is Guid id && _selectedTrack >= 0 && _selectedTrack < _project.Tracks.Count && _project.Tracks[_selectedTrack].Id == id
            ? _project.Tracks[_selectedTrack]
            : null;

    private void InspectorGain_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingInspectorControls) return;
        var track = GetInspectorEditTrack();
        if (track is null) return;
        track.Gain = (float)e.NewValue;
        InspectorGainValue.Text = $"{track.Gain * 100:0}%";
        UpdateTrackSummary(track);
    }

    private void InspectorPan_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingInspectorControls) return;
        var track = GetInspectorEditTrack();
        if (track is null) return;
        track.Pan = (float)e.NewValue;
        InspectorPanValue.Text = FormatPan(track.Pan);
        UpdateTrackSummary(track);
    }

    private void UpdateTrackSummary(AudioTrack track)
    {
        var index = _project.Tracks.IndexOf(track);
        if (index >= 0 && index < _trackSummaries.Count)
            _trackSummaries[index].Text = $"音量 {track.Gain * 100:0}%   ·   パン {FormatPan(track.Pan)}";
    }

    private Border BuildTrackHeader(AudioTrack track, int trackIndex)
    {
        var panel = new StackPanel { Margin = new Thickness(10, 7, 10, 6) };
        var title = new DockPanel();
        var name = new TextBox
        {
            Text = track.Name, Width = 140, Background = new SolidColorBrush(Color.FromRgb(38, 40, 46)),
            Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(4)
        };
        name.LostFocus += (_, _) => { if (track.Name != name.Text) { RecordUndo(); track.Name = name.Text; } };
        title.Children.Add(name);
        var mute = new CheckBox { Content = "M", IsChecked = track.Muted, Margin = new Thickness(8, 3, 0, 0), Foreground = Brushes.White };
        mute.Checked += (_, _) => { if (!track.Muted) RecordUndo(); track.Muted = true; };
        mute.Unchecked += (_, _) => { if (track.Muted) RecordUndo(); track.Muted = false; };
        title.Children.Add(mute);
        panel.Children.Add(title);
        var remove = new Button { Content = "×", Tag = trackIndex, Width = 24, Height = 22, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "トラックを削除" };
        remove.Click += DeleteTrack_Click;
        title.Children.Add(remove);
        var summary = new TextBlock { Text = $"音量 {track.Gain * 100:0}%   ·   パン {FormatPan(track.Pan)}", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(166, 171, 180)), Margin = new Thickness(2, 8, 0, 0) };
        _trackSummaries.Add(summary);
        panel.Children.Add(summary);
        panel.PreviewMouseLeftButtonDown += (_, _) => SelectTrack(trackIndex);
        return new Border
        {
            Background = new SolidColorBrush(trackIndex == _selectedTrack ? Color.FromRgb(47, 51, 59) : Color.FromRgb(39, 41, 47)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(54, 57, 64)), BorderThickness = new Thickness(0, 0, 1, 1),
            Child = panel
        };
    }

    private Border BuildClip(AudioClip clip, Canvas canvas)
    {
        var assetExists = _assets.ContainsKey(clip.File);
        var width = Math.Max(35, clip.DurationSeconds * _pixelsPerSecond);
        var border = new Border
        {
            Width = width, Height = 94, CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(assetExists ? Color.FromRgb(43, 99, 88) : Color.FromRgb(130, 55, 58)),
            BorderBrush = new SolidColorBrush(clip.Id == _selectedClipId ? Color.FromRgb(233, 195, 104) : assetExists ? Color.FromRgb(91, 171, 144) : Color.FromRgb(210, 105, 108)), BorderThickness = new Thickness(clip.Id == _selectedClipId ? 2 : 1),
            Cursor = Cursors.SizeAll, Tag = clip, ToolTip = assetExists ? clip.Name : "素材が見つかりません: " + clip.File
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
        if (assetExists) DrawWaveform(clip, border);
        border.MouseLeftButtonDown += (_, e) =>
        {
            var clipTrackIndex = _project.Tracks.FindIndex(t => t.Clips.Contains(clip));
            SelectTrack(clipTrackIndex, clip.Id);
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(233, 195, 104));
            border.BorderThickness = new Thickness(2);
            var point = e.GetPosition(canvas);
            if (_player.IsPlaying) StopForStructuralEdit();
            _clipDrag = new ClipDrag
            {
                Clip = clip, Canvas = canvas, Border = border, StartPoint = point,
                OriginalStart = clip.StartSeconds, OriginalDuration = clip.DurationSeconds,
                Resizing = point.X - Canvas.GetLeft(border) >= border.ActualWidth - 15
            };
            RecordUndo();
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
                clip.StartSeconds = TimelineEditing.Snap(_clipDrag.OriginalStart + delta, SnapCheck.IsChecked == true);
                Canvas.SetLeft(border, clip.StartSeconds * _pixelsPerSecond);
            }
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            if (_clipDrag?.Border != border) return;
            border.ReleaseMouseCapture();
            _clipDrag = null;
            RefreshTimeline();
            StatusText.Text = "クリップ位置または長さを変更しました。保存すると変更が保持されます。";
        };
        return border;
    }

    private void DrawWaveform(AudioClip clip, Border border)
    {
        if (!_assets.TryGetValue(clip.File, out var asset)) return;
        var canvas = new Canvas { IsHitTestVisible = false, Margin = new Thickness(6, 23, 5, 5), ClipToBounds = true };
        var waveWidth = Math.Max(1, border.Width - 14);
        var waveHeight = 60d;
        canvas.Width = waveWidth; canvas.Height = waveHeight;
        var peaks = WaveformPeaks.Calculate(asset, clip.SourceOffsetSeconds, clip.DurationSeconds, (int)waveWidth);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var center = waveHeight / 2;
            for (var x = 0; x < peaks.Length; x++)
            {
                var amp = Math.Max(1.2, peaks[x] * (center - 2));
                ctx.BeginFigure(new Point(x, center - amp), false, false);
                ctx.LineTo(new Point(x, center + amp), true, false);
            }
        }
        geometry.Freeze();
        canvas.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Stroke = new SolidColorBrush(Color.FromRgb(157, 220, 189)), StrokeThickness = 1, Opacity = 0.9 });
        var label = new TextBlock { Text = clip.Name, Margin = new Thickness(7, 4, 18, 0), Foreground = Brushes.White, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
        border.Child = new Grid { Children = { canvas, label } };
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
        _autosaveTimer.Stop();
        _player.Dispose();
        base.OnClosed(e);
    }
}
