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
    public string Name { get; set; } = "Audio Track";
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
        if (File.Exists(path))
            File.Replace(temp, path, path + ".bak", true);
        else
            File.Move(temp, path);
    }

    public static ProjectDocument Load(string manifestPath)
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
}
