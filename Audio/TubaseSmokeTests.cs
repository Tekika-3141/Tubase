namespace Tubase.Audio;

using Tubase;
using System.Windows.Input;
using System.Text.Json;

public static class TubaseSmokeTests
{
    public static bool Run()
    {
        var lines = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "Tubase-Smoke-" + Guid.NewGuid().ToString("N"));
        var stage = "create test workspace";
        try
        {
            Require(!Directory.Exists(root), "Unique test workspace path");
            Directory.CreateDirectory(Path.Combine(root, "Audio"));
            var source = Path.Combine(root, "source.wav");
            var left = Enumerable.Repeat(0.5f, 44100).ToArray();
            var right = Enumerable.Repeat(0.5f, 44100).ToArray();
            WavFile.WritePcm16(source, left, right, 44100);
            var decoded = WavFile.Read(source);
            Require(decoded.SampleRate == 44100 && decoded.Frames == 44100, "WAV PCM16 read");
            Require(Math.Abs(decoded.Left[100] - 0.5f) < 0.001f, "WAV sample decode");
            lines.Add("PASS WAV PCM16 read/decode");
            var peaks = WaveformPeaks.Calculate(decoded, 0, decoded.Duration, 128);
            Require(peaks.Length == 128 && peaks.All(p => Math.Abs(p - 0.5f) < 0.002f), "Waveform peak calculation");
            lines.Add("PASS waveform peak data used by UI drawing");

            Require(TimelineEditing.Snap(1.13, true) == 1.25, "Snap enabled");
            Require(TimelineEditing.Snap(0.125, true) == 0.25, "Snap midpoint rounds to the next grid point");
            Require(Math.Abs(TimelineEditing.Snap(1.13, false) - 1.13) < 0.000001, "Snap disabled");
            lines.Add("PASS quarter-second grid on/off");
            Require(EditorInput.Resolve(Key.S, ModifierKeys.Control, false) == EditorCommand.Save
                    && EditorInput.Resolve(Key.Z, ModifierKeys.Control, false) == EditorCommand.Undo
                    && EditorInput.Resolve(Key.Y, ModifierKeys.Control, false) == EditorCommand.Redo
                    && EditorInput.Resolve(Key.Space, ModifierKeys.None, false) == EditorCommand.TogglePlayback
                    && EditorInput.Resolve(Key.Space, ModifierKeys.None, true) == EditorCommand.None,
                "Keyboard shortcut routing");
            lines.Add("PASS Ctrl+S/Z/Y and Space shortcut routing");
            var boundedHistory = new ProjectEditHistory(2);
            boundedHistory.Record("one"); boundedHistory.Record("two"); boundedHistory.Record("three");
            Require(boundedHistory.Undo("four", out var newest) && newest == "three"
                    && boundedHistory.Undo(newest, out var older) && older == "two"
                    && !boundedHistory.Undo(older, out _), "Undo history retains newest states within capacity");
            lines.Add("PASS Undo history capacity keeps latest edits");

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
            stage = "first project manifest save";
            ProjectStore.Save(project, root);
            var loaded = ProjectStore.Load(ProjectStore.ManifestPath(root));
            Require(loaded.Tracks.Count == 2 && loaded.Tracks[0].Clips.Count == 1, "Project JSON save/load");
            Require(loaded.Tracks[0].Clips[0].StartSeconds == 0.25, "Clip timeline position persistence");
            lines.Add("PASS two-track project save/load");

            var tubasePath = ProjectStore.TubasePath(root);
            ProjectStore.SaveTubase(loaded, tubasePath);
            using (var tubaseJson = JsonDocument.Parse(File.ReadAllText(tubasePath)))
                Require(tubaseJson.RootElement.GetProperty("SchemaVersion").GetInt32() == 1,
                    "Tubase file contains valid JSON");
            var tubaseLoaded = ProjectStore.Load(tubasePath);
            Require(tubaseLoaded.Tracks.Count == loaded.Tracks.Count
                    && tubaseLoaded.Tracks[0].Clips[0].StartSeconds == loaded.Tracks[0].Clips[0].StartSeconds,
                "Tubase save/load preserves tracks and clip position");
            lines.Add("PASS .tubase JSON save/load and legacy JSON compatibility");

            var history = new ProjectEditHistory();
            history.Record(JsonSerializer.Serialize(loaded));
            loaded.Tracks[0].Gain = 0.75f;
            loaded.Tracks[0].Pan = 0.4f;
            Require(history.Undo(JsonSerializer.Serialize(loaded), out var beforeMixEdit), "Track mix undo available");
            loaded = JsonSerializer.Deserialize<ProjectDocument>(beforeMixEdit)!;
            Require(Math.Abs(loaded.Tracks[0].Gain - 0.5f) < 0.0001f
                    && Math.Abs(loaded.Tracks[0].Pan + 1f) < 0.0001f
                    && Math.Abs(loaded.Tracks[1].Gain - 0.5f) < 0.0001f
                    && Math.Abs(loaded.Tracks[1].Pan - 1f) < 0.0001f,
                "Track mix undo restores selected track only");
            Require(history.Redo(JsonSerializer.Serialize(loaded), out var afterMixEdit), "Track mix redo available");
            loaded = JsonSerializer.Deserialize<ProjectDocument>(afterMixEdit)!;
            Require(Math.Abs(loaded.Tracks[0].Gain - 0.75f) < 0.0001f
                    && Math.Abs(loaded.Tracks[0].Pan - 0.4f) < 0.0001f
                    && Math.Abs(loaded.Tracks[1].Gain - 0.5f) < 0.0001f
                    && Math.Abs(loaded.Tracks[1].Pan - 1f) < 0.0001f,
                "Track mix redo does not affect another track");
            ProjectStore.Save(loaded, root);
            loaded = ProjectStore.Load(ProjectStore.ManifestPath(root));
            Require(Math.Abs(loaded.Tracks[0].Gain - 0.75f) < 0.0001f
                    && Math.Abs(loaded.Tracks[0].Pan - 0.4f) < 0.0001f
                    && Math.Abs(loaded.Tracks[1].Gain - 0.5f) < 0.0001f
                    && Math.Abs(loaded.Tracks[1].Pan - 1f) < 0.0001f,
                "Track mix values persist on save and reload");
            lines.Add("PASS selected-track gain/pan undo/redo and independent save/reload");

            var originalClip = loaded.Tracks[0].Clips[0];
            history.Record(JsonSerializer.Serialize(loaded));
            originalClip.StartSeconds = TimelineEditing.Snap(0.63, true);
            Require(history.Undo(JsonSerializer.Serialize(loaded), out var beforeMove), "Clip move undo available");
            loaded = JsonSerializer.Deserialize<ProjectDocument>(beforeMove)!;
            Require(Math.Abs(loaded.Tracks[0].Clips[0].StartSeconds - 0.25) < 0.000001, "Clip move undo state");
            Require(history.Redo(JsonSerializer.Serialize(loaded), out var afterMove), "Clip move redo available");
            loaded = JsonSerializer.Deserialize<ProjectDocument>(afterMove)!;
            Require(Math.Abs(loaded.Tracks[0].Clips[0].StartSeconds - 0.75) < 0.000001, "Clip move redo state");

            history.Record(JsonSerializer.Serialize(loaded));
            var deletedClipId = loaded.Tracks[0].Clips[0].Id;
            Require(TimelineEditing.RemoveClip(loaded, deletedClipId), "Clip delete operation");
            Require(history.Undo(JsonSerializer.Serialize(loaded), out var beforeDelete), "Clip delete undo available");
            loaded = JsonSerializer.Deserialize<ProjectDocument>(beforeDelete)!;
            var restoredClip = loaded.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == deletedClipId);
            Require(assetMap.ContainsKey(restoredClip.File), "Restored clip retains registered audio asset");
            Require(history.Redo(JsonSerializer.Serialize(loaded), out var afterDelete), "Clip delete redo available");
            loaded = JsonSerializer.Deserialize<ProjectDocument>(afterDelete)!;
            Require(loaded.Tracks.SelectMany(t => t.Clips).All(c => c.Id != deletedClipId), "Clip delete redo state");

            loaded = ProjectStore.Load(ProjectStore.ManifestPath(root));
            history.Record(JsonSerializer.Serialize(loaded));
            var removedTrackId = loaded.Tracks[0].Id;
            TimelineEditing.RemoveTrack(loaded, 0);
            Require(loaded.Tracks.Count == 1 && loaded.Tracks.All(t => t.Id != removedTrackId), "Track remove operation");
            Require(history.Undo(JsonSerializer.Serialize(loaded), out var beforeTrackDelete), "Track delete undo available");
            loaded = JsonSerializer.Deserialize<ProjectDocument>(beforeTrackDelete)!;
            Require(loaded.Tracks.Count == 2 && loaded.Tracks[0].Id == removedTrackId
                    && assetMap.ContainsKey(loaded.Tracks[0].Clips[0].File), "Track delete undo restores clip and audio mapping");
            Require(history.Redo(JsonSerializer.Serialize(loaded), out var afterTrackDelete), "Track delete redo available");
            loaded = JsonSerializer.Deserialize<ProjectDocument>(afterTrackDelete)!;
            Require(loaded.Tracks.Count == 1 && loaded.Tracks.All(t => t.Id != removedTrackId), "Track delete redo state");
            TimelineEditing.RemoveTrack(loaded, 0);
            Require(loaded.Tracks.Count == 1 && loaded.Tracks[0].Name == "オーディオ 1", "Deleting the last track leaves a Japanese empty track");
            lines.Add("PASS clip move/delete and track delete undo/redo with audio references");

            stage = "recovery snapshot write/read";
            ProjectStore.SaveRecovery(loaded, root);
            var recovery = ProjectStore.LoadRecovery(root);
            Require(recovery is not null && recovery.Tracks.Count == loaded.Tracks.Count
                    && recovery.Tracks[0].Name == "オーディオ 1",
                "Recovery snapshot round-trip");
            loaded.Name = "Recovery Updated";
            ProjectStore.SaveRecovery(loaded, root);
            File.WriteAllText(ProjectStore.RecoveryPath(root), "broken recovery snapshot");
            Require(ProjectStore.LoadRecovery(root)?.Name == "Smoke"
                    && File.ReadAllText(ProjectStore.RecoveryPath(root)).Contains("\"Name\": \"Smoke\"", StringComparison.Ordinal),
                "Corrupt recovery snapshot uses and restores previous valid backup");
            lines.Add("PASS recovery snapshot write/read");

            loaded = ProjectStore.Load(ProjectStore.ManifestPath(root));
            stage = "subsequent project save and backup validation";
            loaded.Name = "Smoke Updated";
            ProjectStore.Save(loaded, root);
            Require(ProjectStore.Load(ProjectStore.ManifestPath(root)).Name == "Smoke Updated", "Repeated save replaces manifest");
            Require(File.Exists(ProjectStore.ManifestPath(root) + ".bak"), "Previous manifest backup retained");
            var blocked = Path.Combine(root, "blocked-save");
            Directory.CreateDirectory(ProjectStore.ManifestPath(blocked));
            var unsaved = new ProjectDocument { Name = "Keep These Edits", Tracks = [new AudioTrack()] };
            var failedAsExpected = false;
            try { ProjectStore.Save(unsaved, blocked); }
            catch (IOException ex) { failedAsExpected = ex.Message.Contains(ProjectStore.ManifestPath(blocked), StringComparison.Ordinal); }
            Require(failedAsExpected
                    && File.ReadAllText(ProjectStore.ManifestPath(blocked) + ".tmp").Contains("Keep These Edits", StringComparison.Ordinal),
                "Failed save reports destination and preserves prepared edits");
            lines.Add("PASS failed save reports exact path and retains prepared edits");

            loaded = ProjectStore.Load(ProjectStore.ManifestPath(root));
            loaded.Tracks[0].Gain = 0.5f;
            loaded.Tracks[0].Pan = -1f;
            loaded.Tracks[1].Gain = 0.5f;
            loaded.Tracks[1].Pan = 1f;

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
            File.WriteAllText(ProjectStore.ManifestPath(root), "broken manifest");
            Require(ProjectStore.Load(ProjectStore.ManifestPath(root)).Name == "Smoke"
                    && File.ReadAllText(ProjectStore.ManifestPath(root)).Contains("\"Name\": \"Smoke\"", StringComparison.Ordinal),
                "Corrupt manifest recovers and restores backup");
            lines.Add("PASS corrupt manifest is restored from verified backup");
            lines.Add("RESULT: PASS");
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "Tubase-smoke-test.txt"), lines);
            return true;
        }
        catch (Exception ex)
        {
            lines.Add("FAIL stage: " + stage);
            lines.Add("TEST ROOT: " + root);
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
