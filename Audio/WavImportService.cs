namespace Tubase.Audio;

public sealed record WavImportFailure(string SourcePath, string Reason);

public sealed record WavImportResult(int ImportedCount, IReadOnlyList<WavImportFailure> Failures);

public static class WavImportService
{
    public static WavImportResult Import(
        ProjectDocument project,
        IDictionary<string, AudioAsset> assets,
        string projectDirectory,
        IEnumerable<string> sourcePaths,
        int targetTrackIndex,
        double startSeconds,
        bool putEachFileOnItsOwnTrack)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(assets);
        var sources = sourcePaths.ToArray();
        var failures = new List<WavImportFailure>();
        var imported = 0;
        if (sources.Length == 0) return new WavImportResult(0, failures);

        projectDirectory = Path.GetFullPath(projectDirectory);
        Directory.CreateDirectory(Path.Combine(projectDirectory, "Audio"));
        targetTrackIndex = Math.Clamp(targetTrackIndex, 0, Math.Max(0, project.Tracks.Count - 1));

        for (var index = 0; index < sources.Length; index++)
        {
            var source = Path.GetFullPath(sources[index]);
            string? destination = null;
            string? relative = null;
            try
            {
                if (!File.Exists(source)) throw new FileNotFoundException("The selected WAV file does not exist.", source);

                // Decode the source before copying it, so unsupported/corrupt files do not leave
                // orphaned media in the project directory.
                var asset = WavFile.Read(source);
                var assetName = Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(source);
                relative = Path.Combine("Audio", assetName);
                destination = Path.GetFullPath(Path.Combine(projectDirectory, relative));
                File.Copy(source, destination, false);

                var trackIndex = putEachFileOnItsOwnTrack ? targetTrackIndex + index : targetTrackIndex;
                while (project.Tracks.Count <= trackIndex)
                    project.Tracks.Add(new AudioTrack { Name = Path.GetFileNameWithoutExtension(source) });

                var track = project.Tracks[trackIndex];
                track.Clips.Add(new AudioClip
                {
                    Name = Path.GetFileNameWithoutExtension(source),
                    File = relative,
                    StartSeconds = Math.Max(0, startSeconds),
                    DurationSeconds = asset.Duration
                });
                assets[relative] = asset;
                imported++;
            }
            catch (Exception ex)
            {
                if (destination is not null)
                {
                    try { File.Delete(destination); } catch { }
                }
                var failure = new WavImportFailure(source, $"{ex.GetType().Name}: {ex.Message}");
                failures.Add(failure);
                WriteFailureLog(failure);
            }
        }

        return new WavImportResult(imported, failures);
    }

    private static void WriteFailureLog(WavImportFailure failure)
    {
        try
        {
            var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tubase", "Logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(Path.Combine(logDirectory, "import.log"),
                $"{DateTimeOffset.Now:O}\t{failure.SourcePath}\t{failure.Reason}{Environment.NewLine}");
        }
        catch
        {
            // The UI also displays the original exception; logging failure must not hide it.
        }
    }
}
