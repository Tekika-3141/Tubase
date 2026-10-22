using System.Buffers.Binary;

namespace Tubase.Audio;

public static class WavFile
{
    public static AudioAsset Read(string path)
    {
        path = Path.GetFullPath(path);
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 12) throw new InvalidDataException("The file is too small to contain a WAV header.");
        if (ReadFourCc(reader) != "RIFF") throw new InvalidDataException("Not a RIFF WAV file.");
        var riffSize = reader.ReadUInt32();
        if (ReadFourCc(reader) != "WAVE") throw new InvalidDataException("Not a WAVE file.");
        var riffEnd = Math.Min(stream.Length, 8L + riffSize);

        ushort format = 0, channels = 0, bits = 0;
        ushort blockAlign = 0;
        int sampleRate = 0;
        long dataOffset = -1;
        long dataSize = 0;
        while (stream.Position + 8 <= riffEnd)
        {
            var id = ReadFourCc(reader);
            var size = reader.ReadUInt32();
            var chunkEnd = stream.Position + size;
            if (chunkEnd > riffEnd || chunkEnd > stream.Length)
                throw new InvalidDataException($"The WAV '{id}' chunk extends beyond the end of the file.");
            if (id == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("The WAV format chunk is shorter than 16 bytes.");
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = checked((int)reader.ReadUInt32());
                reader.ReadUInt32();
                blockAlign = reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (format == 0xFFFE && size >= 40)
                {
                    var extensionSize = reader.ReadUInt16();
                    if (extensionSize < 22) throw new InvalidDataException("The extensible WAV format extension is incomplete.");
                    reader.ReadUInt16(); // valid bits per sample
                    reader.ReadUInt32(); // channel mask
                    var subFormat = reader.ReadBytes(16);
                    if (subFormat.Length != 16 || !IsPcmOrFloatSubFormat(subFormat, out format))
                        throw new NotSupportedException("This extensible WAV uses an unsupported audio subformat.");
                }
            }
            else if (id == "data")
            {
                dataOffset = stream.Position;
                dataSize = size;
            }
            var nextChunk = chunkEnd + (size & 1);
            if (nextChunk > riffEnd || nextChunk > stream.Length)
                throw new InvalidDataException($"The WAV '{id}' chunk is missing its required padding byte.");
            stream.Position = nextChunk;
            if (dataOffset >= 0 && sampleRate > 0 && channels > 0) break;
        }

        if (dataOffset < 0 || channels == 0 || sampleRate <= 0)
            throw new InvalidDataException("WAV is missing format or audio data.");
        if (format != 1 && format != 3)
            throw new NotSupportedException($"Unsupported WAV encoding format {format}. Tubase supports uncompressed PCM and 32-bit IEEE float WAV files.");
        if (format == 3 && bits != 32 || format == 1 && bits is not (8 or 16 or 24 or 32))
            throw new NotSupportedException($"Unsupported WAV sample format: {bits}-bit, format tag {format}.");
        if (channels > 2) throw new NotSupportedException($"WAV files with {channels} channels are not supported. Import mono or stereo WAV files.");
        if (sampleRate is < 8000 or > 384000) throw new NotSupportedException($"Unsupported WAV sample rate: {sampleRate} Hz.");

        var bytesPerSample = bits / 8;
        var expectedBlockAlign = channels * bytesPerSample;
        if (blockAlign != expectedBlockAlign)
            throw new InvalidDataException($"Invalid WAV block alignment: expected {expectedBlockAlign} bytes but found {blockAlign}.");
        if (dataSize > int.MaxValue) throw new NotSupportedException("WAV files larger than 2 GiB cannot currently be imported.");
        if (dataSize % blockAlign != 0) throw new InvalidDataException("The WAV audio data ends in an incomplete sample frame.");
        var frames = checked((int)(dataSize / blockAlign));
        var left = new float[frames];
        var right = new float[frames];
        stream.Position = dataOffset;
        var bytes = reader.ReadBytes(checked(frames * blockAlign));
        if (bytes.Length != frames * blockAlign) throw new EndOfStreamException("The WAV audio data is truncated.");
        for (var f = 0; f < frames; f++)
        {
            left[f] = ReadSample(bytes, (f * channels) * bytesPerSample, format, bits);
            if (channels == 1) right[f] = left[f];
            else right[f] = ReadSample(bytes, (f * channels + 1) * bytesPerSample, format, bits);
        }
        return new AudioAsset { Left = left, Right = right, SampleRate = sampleRate };
    }

    private static string ReadFourCc(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4) throw new EndOfStreamException("The WAV file ended while reading a chunk identifier.");
        return System.Text.Encoding.ASCII.GetString(bytes);
    }

    private static bool IsPcmOrFloatSubFormat(byte[] guid, out ushort format)
    {
        format = 0;
        ReadOnlySpan<byte> tail = [0, 0, 0x10, 0, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71];
        if (!guid.AsSpan(4).SequenceEqual(tail)) return false;
        var subtype = BinaryPrimitives.ReadUInt32LittleEndian(guid);
        if (subtype is not (1u or 3u)) return false;
        format = (ushort)subtype;
        return true;
    }

    private static float ReadSample(byte[] data, int offset, ushort format, ushort bits)
    {
        if (format == 3) return BitConverter.ToSingle(data, offset);
        return bits switch
        {
            8 => (data[offset] - 128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2)) / 32768f,
            24 => Read24(data, offset) / 8388608f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)) / 2147483648f,
            _ => 0f
        };
    }

    private static int Read24(byte[] data, int offset)
    {
        var value = data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16;
        return (value & 0x800000) == 0 ? value : value | unchecked((int)0xFF000000);
    }

    public static void WritePcm16(string path, float[] left, float[] right, int sampleRate)
    {
        if (left.Length != right.Length) throw new ArgumentException("Channel lengths differ.");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var dataBytes = checked(left.Length * 4);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (var i = 0; i < left.Length; i++)
        {
            writer.Write(ToInt16(left[i]));
            writer.Write(ToInt16(right[i]));
        }
    }

    private static short ToInt16(float sample) =>
        (short)Math.Clamp((int)Math.Round(Math.Clamp(sample, -1f, 1f) * 32767f), short.MinValue, short.MaxValue);
}
