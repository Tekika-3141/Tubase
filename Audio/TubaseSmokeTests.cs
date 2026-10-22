namespace Tubase.Audio;

public static class TubaseSmokeTests
{
    public static bool Run()
    {
        var lines = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "Tubase-Smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Audio"));
            var source = Path.Combine(root, "source.wav");
            var left = Enumerable.Repeat(0.5f, 44100).ToArray();
            var right = Enumerable.Repeat(0.5f, 44100).ToArray();
            WavFile.WritePcm16(source, left, right, 44100);
            var decoded = WavFile.Read(source);
            Require(decoded.SampleRate == 44100 && decoded.Frames == 44100, "WAV PCM16 read");
            Require(Math.Abs(decoded.Left[100] - 0.5f) < 0.001f, "WAV sample decode");
            lines.Add("PASS WAV PCM16 read/decode");

            var extensible = Path.Combine(root, "extensible.wav");
            WriteExtensiblePcm16(extensible);
            var extensibleDecoded = WavFile.Read(extensible);
            Require(extensibleDecoded.Frames == 1 && Math.Abs(extensibleDecoded.Left[0] - 0.5f) < 0.001f,
                "WAVE_FORMAT_EXTENSIBLE PCM decode");
            lines.Add("PASS WAVE_FORMAT_EXTENSIBLE PCM read/decode");

            var source2 = Path.Combine(root, "second tone.wav");
            WavFile.WritePcm16(source2, Enumerable.Repeat(0.25f, 44100).ToArray(),
                Enumerable.Repeat(0.25f, 44100).ToArray(), 44100);
            var project = new ProjectDocument { Name = "Smoke", SampleRate = 44100 };
            var assetMap = new Dictionary<string, AudioAsset>(StringComparer.OrdinalIgnoreCase);
            var import = WavImportService.Import(project, assetMap, root, [source, source2], 0, 0.25,
                putEachFileOnItsOwnTrack: true);
            Require(import.ImportedCount == 2 && import.Failures.Count == 0, "Two-file WAV import");
            Require(project.Tracks.Count == 2 && project.Tracks.All(t => t.Clips.Count == 1),
                "Imported WAV clips on separate tracks");
            Require(assetMap.Count == 2 && project.Tracks.All(t => assetMap.ContainsKey(t.Clips[0].File)),
                "Imported assets registered for playback");
            lines.Add("PASS two-file WAV import and separate-track clip registration");
            foreach (var track in project.Tracks) track.Clips[0].DurationSeconds = 0.5;

            var invalid = Path.Combine(root, "invalid.wav");
            File.WriteAllText(invalid, "not a wave file");
            var failedImport = WavImportService.Import(project, assetMap, root, [invalid], 0, 0,
                putEachFileOnItsOwnTrack: false);
            Require(failedImport.ImportedCount == 0 && failedImport.Failures.Count == 1
                    && failedImport.Failures[0].Reason.Contains("InvalidDataException", StringComparison.Ordinal),
                "Import failure reports the file-specific cause");
            Require(project.Tracks.All(t => t.Clips.Count == 1), "Failed import does not create a clip");
            lines.Add("PASS invalid WAV failure reports cause without creating a clip");

            project.Tracks[0].Gain = 0.5f;
            project.Tracks[0].Pan = -1f;
            project.Tracks[1].Gain = 0.5f;
            project.Tracks[1].Pan = 1f;
            ProjectStore.Save(project, root);
            var loaded = ProjectStore.Load(ProjectStore.ManifestPath(root));
            Require(loaded.Tracks.Count == 2 && loaded.Tracks[0].Clips.Count == 1, "Project JSON save/load");
            Require(loaded.Tracks[0].Clips[0].StartSeconds == 0.25, "Clip timeline position persistence");
            lines.Add("PASS two-track project save/load");

            assetMap = loaded.Tracks.SelectMany(t => t.Clips)
                .DistinctBy(c => c.File)
                .ToDictionary(c => c.File, c => WavFile.Read(Path.GetFullPath(Path.Combine(root, c.File))), StringComparer.OrdinalIgnoreCase);
            var mixL = new float[44100];
            var mixR = new float[44100];
            Mixer.Render(loaded, assetMap, mixL, mixR, 0, 44100);
            Require(Math.Abs(mixL[100] + mixR[100]) < 0.0001f, "Clip silence before its timeline position");
            Require(Math.Abs(mixL[15000] - 0.25f) < 0.02f && Math.Abs(mixR[15000] - 0.125f) < 0.02f,
                "Track gain and pan mixing");
            Require(Math.Abs(mixL[35000]) < 0.0001f && Math.Abs(mixR[35000]) < 0.0001f,
                "Clip end boundary");
            lines.Add("PASS timeline placement, stereo mixing, gain and pan");

            var export = Path.Combine(root, "export.wav");
            var frames = (int)Math.Ceiling(Mixer.ProjectDuration(loaded) * 44100);
            WavFile.WritePcm16(export, mixL.Take(frames).ToArray(), mixR.Take(frames).ToArray(), 44100);
            var exported = WavFile.Read(export);
            Require(exported.Frames == frames && exported.SampleRate == 44100, "WAV export header/duration");
            Require(exported.Left[15000] > 0.22f && exported.Right[15000] > 0.10f, "WAV export audio content");
            lines.Add("PASS stereo PCM16 WAV export/read-back");
            lines.Add("RESULT: PASS");
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "Tubase-smoke-test.txt"), lines);
            return true;
        }
        catch (Exception ex)
        {
            lines.Add("FAIL " + ex);
            try { File.WriteAllLines(Path.Combine(Path.GetTempPath(), "Tubase-smoke-test.txt"), lines); } catch { }
            return false;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) throw new InvalidOperationException("Smoke test failed: " + check);
    }

    private static void WriteExtensiblePcm16(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(64u);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(40u);
        writer.Write((ushort)0xFFFE); writer.Write((ushort)2); writer.Write(44100u);
        writer.Write(176400u); writer.Write((ushort)4); writer.Write((ushort)16);
        writer.Write((ushort)22); writer.Write((ushort)16); writer.Write(3u);
        writer.Write(new byte[] { 1, 0, 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71 });
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(4u);
        writer.Write((short)16384); writer.Write((short)-16384);
    }
}
