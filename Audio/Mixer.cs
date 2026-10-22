namespace Tubase.Audio;

public static class Mixer
{
    public static double ProjectDuration(ProjectDocument project) =>
        project.Tracks.SelectMany(t => t.Clips)
            .Select(c => c.StartSeconds + c.DurationSeconds)
            .DefaultIfEmpty(0).Max();

    public static void Render(ProjectDocument project, IReadOnlyDictionary<string, AudioAsset> assets,
        float[] outputLeft, float[] outputRight, int startFrame, int sampleRate)
    {
        Array.Clear(outputLeft);
        Array.Clear(outputRight);
        foreach (var track in project.Tracks)
        {
            if (track.Muted) continue;
            var pan = Math.Clamp(track.Pan, -1f, 1f);
            var panLeft = pan > 0 ? 1f - pan : 1f;
            var panRight = pan < 0 ? 1f + pan : 1f;            var gainL = Math.Max(0f, track.Gain) * panLeft;
            var gainR = Math.Max(0f, track.Gain) * panRight;
            foreach (var clip in track.Clips)
            {
                if (!assets.TryGetValue(clip.File, out var asset)) continue;
                var clipStart = (int)Math.Round(clip.StartSeconds * sampleRate);
                var sourceOffset = clip.SourceOffsetSeconds * asset.SampleRate;
                var clipFrames = (int)Math.Min(clip.DurationSeconds * sampleRate,
                    Math.Max(0, (asset.Frames - sourceOffset) * sampleRate / asset.SampleRate));
                var from = Math.Max(startFrame, clipStart);
                var to = Math.Min(startFrame + outputLeft.Length, clipStart + clipFrames);
                for (var frame = from; frame < to; frame++)
                {
                    var sourcePosition = sourceOffset + (frame - clipStart) * (double)asset.SampleRate / sampleRate;
                    var index = (int)sourcePosition;
                    var frac = (float)(sourcePosition - index);
                    if (index >= asset.Frames) continue;
                    var next = Math.Min(index + 1, asset.Frames - 1);
                    var l = asset.Left[index] + (asset.Left[next] - asset.Left[index]) * frac;
                    var r = asset.Right[index] + (asset.Right[next] - asset.Right[index]) * frac;
                    var outIndex = frame - startFrame;
                    outputLeft[outIndex] += l * gainL;
                    outputRight[outIndex] += r * gainR;
                }
            }
        }
    }
}
