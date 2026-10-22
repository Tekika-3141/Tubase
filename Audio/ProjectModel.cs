using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tubase.Audio;

public sealed class ProjectDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "Untitled";
    public int SampleRate { get; set; } = 44100;
    public List<AudioTrack> Tracks { get; set; } = new();
}

public sealed class AudioTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "オーディオトラック";
    public float Gain { get; set; } = 1f;
    public float Pan { get; set; }
    public bool Muted { get; set; }
    public List<AudioClip> Clips { get; set; } = new();
}

public sealed class AudioClip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Audio";
    public string File { get; set; } = "";
    public double StartSeconds { get; set; }
    public double SourceOffsetSeconds { get; set; }
    public double DurationSeconds { get; set; }
}

public sealed class AudioAsset
{
    public required float[] Left { get; init; }
    public required float[] Right { get; init; }
    public required int SampleRate { get; init; }
    public int Frames => Left.Length;
    public double Duration => (double)Frames / SampleRate;
}

public static class ProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ManifestPath(string projectDirectory) =>
        Path.Combine(projectDirectory, "project.json");

    public static void Save(ProjectDocument project, string directory)
    {
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "Audio"));
        Directory.CreateDirectory(Path.Combine(directory, "Peaks"));
        Directory.CreateDirectory(Path.Combine(directory, "Autosaves"));
        var path = ManifestPath(directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(project, JsonOptions));
        // Read the prepared file before touching the last good project copy.
        _ = LoadDocument(temp);

        var backup = path + ".bak";
        var hadPrevious = File.Exists(path);
        if (hadPrevious)
        {
            try { _ = LoadDocument(path); }
            catch (Exception ex)
            {
                throw new IOException($"The existing project manifest '{path}' is invalid; save was stopped to preserve its backup. Prepared edits remain at '{temp}'.", ex);
            }
            try { File.Copy(path, backup, overwrite: true); }
            catch (Exception ex) { throw new IOException($"Could not preserve the previous project manifest at '{backup}'. Prepared edits remain at '{temp}'.", ex); }
        }
        try
        {
            // File.Move/File.Replace are denied by some sandboxed Windows temp locations.
            // Copy works there; keep the previous manifest as a rollback copy until verified.
            File.Copy(temp, path, overwrite: true);
            _ = LoadDocument(path);
        }
        catch (Exception saveError)
        {
            try
            {
                if (hadPrevious && File.Exists(backup)) File.Copy(backup, path, overwrite: true);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException($"Saving '{path}' failed and the previous manifest could not be restored. Prepared data remains at '{temp}'.", saveError, rollbackError);
            }
            throw new IOException($"Could not save project manifest to '{path}'. Prepared data remains at '{temp}'.", saveError);
        }
        File.Delete(temp);
    }

    public static ProjectDocument Load(string manifestPath)
    {
        try { return LoadDocument(manifestPath); }
        catch (Exception primary) when (File.Exists(manifestPath + ".bak"))
        {
            try
            {
                var project = LoadDocument(manifestPath + ".bak");
                File.Copy(manifestPath + ".bak", manifestPath, overwrite: true);
                return project;
            }
            catch (Exception backup) { throw new InvalidDataException("The project could not be recovered from its manifest and backup.", new AggregateException(primary, backup)); }
        }
    }

    private static ProjectDocument LoadDocument(string manifestPath)
    {
        var project = JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(manifestPath), JsonOptions)
                      ?? throw new InvalidDataException("Project file is empty or invalid.");
        if (project.SchemaVersion != 1)
            throw new InvalidDataException("Unsupported project schema version: " + project.SchemaVersion);
        project.Tracks ??= new List<AudioTrack>();
        foreach (var track in project.Tracks)
            track.Clips ??= new List<AudioClip>();
        return project;
    }

    public static string RecoveryPath(string directory) => Path.Combine(directory, "Autosaves", "recovery.json");

    public static void SaveRecovery(ProjectDocument project, string directory)
    {
        var path = RecoveryPath(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        var backup = path + ".bak";
        File.WriteAllText(temp, JsonSerializer.Serialize(project, JsonOptions));
        _ = LoadDocument(temp);
        var hadPrevious = File.Exists(path);
        if (hadPrevious)
        {
            try { File.Copy(path, backup, overwrite: true); }
            catch (Exception ex) { throw new IOException($"Could not preserve the previous recovery snapshot at '{backup}'. Prepared snapshot remains at '{temp}'.", ex); }
        }
        try
        {
            File.Copy(temp, path, overwrite: true);
            _ = LoadDocument(path);
        }
        catch (Exception saveError)
        {
            try
            {
                if (hadPrevious && File.Exists(backup)) File.Copy(backup, path, overwrite: true);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException($"Saving recovery snapshot '{path}' failed and its previous copy could not be restored. Prepared snapshot remains at '{temp}'.", saveError, rollbackError);
            }
            throw new IOException($"Could not save recovery snapshot to '{path}'. Prepared snapshot remains at '{temp}'.", saveError);
        }
        File.Delete(temp);
    }

    public static ProjectDocument? LoadRecovery(string directory)
    {
        var path = RecoveryPath(directory);
        if (!File.Exists(path)) return null;
        try { return LoadDocument(path); }
        catch (Exception primary) when (File.Exists(path + ".bak"))
        {
            try
            {
                var project = LoadDocument(path + ".bak");
                File.Copy(path + ".bak", path, overwrite: true);
                return project;
            }
            catch (Exception backup) { throw new InvalidDataException("The recovery snapshot could not be recovered from its file and backup.", new AggregateException(primary, backup)); }
        }
    }
}

public sealed class ProjectEditHistory
{
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private readonly int _capacity;

    public ProjectEditHistory(int capacity = 100) => _capacity = Math.Max(1, capacity);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public void Clear() { _undo.Clear(); _redo.Clear(); }
    public void Record(string state)
    {
        _undo.Push(state);
        while (_undo.Count > _capacity)
        {
            var keepNewest = _undo.Take(_capacity).ToArray();
            _undo.Clear(); foreach (var item in keepNewest.Reverse()) _undo.Push(item);
        }
        _redo.Clear();
    }
    public bool Undo(string current, out string previous)
    {
        if (!CanUndo) { previous = current; return false; }
        _redo.Push(current); previous = _undo.Pop(); return true;
    }
    public bool Redo(string current, out string next)
    {
        if (!CanRedo) { next = current; return false; }
        _undo.Push(current); next = _redo.Pop(); return true;
    }
}

public static class TimelineEditing
{
    public static double Snap(double seconds, bool enabled, double gridSeconds = 0.25) =>
        enabled && gridSeconds > 0 ? Math.Max(0, Math.Round(seconds / gridSeconds, MidpointRounding.AwayFromZero) * gridSeconds) : Math.Max(0, seconds);

    public static void RemoveTrack(ProjectDocument project, int index)
    {
        if (index < 0 || index >= project.Tracks.Count) throw new ArgumentOutOfRangeException(nameof(index));
        project.Tracks.RemoveAt(index);
        if (project.Tracks.Count == 0) project.Tracks.Add(new AudioTrack { Name = "オーディオ 1" });
    }

    public static bool RemoveClip(ProjectDocument project, Guid clipId)
    {
        foreach (var track in project.Tracks)
        {
            var clip = track.Clips.FirstOrDefault(item => item.Id == clipId);
            if (clip is not null) return track.Clips.Remove(clip);
        }
        return false;
    }
}

public static class WaveformPeaks
{
    public static float[] Calculate(AudioAsset asset, double sourceOffsetSeconds, double durationSeconds, int pixelCount)
    {
        if (pixelCount <= 0) return Array.Empty<float>();
        var peaks = new float[pixelCount];
        var start = Math.Clamp((int)(sourceOffsetSeconds * asset.SampleRate), 0, asset.Frames);
        var available = Math.Clamp((int)(durationSeconds * asset.SampleRate), 0, asset.Frames - start);
        for (var x = 0; x < pixelCount; x++)
        {
            var from = start + (int)((long)x * available / pixelCount);
            var to = start + (int)((long)(x + 1) * available / pixelCount);
            for (var i = from; i < to; i++) peaks[x] = Math.Max(peaks[x], Math.Max(Math.Abs(asset.Left[i]), Math.Abs(asset.Right[i])));
        }
        return peaks;
    }
}
