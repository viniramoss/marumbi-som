using System.Buffers.Binary;

namespace MarumbiSom.Agent.Playback;

/// <summary>Gera o "som de teste" da configuração local (seção 8.4): dois bipes curtos em WAV, sem depender de arquivo externo.</summary>
public static class TestSound
{
    private const int SampleRate = 44100;

    public static string EnsureFile(string directory)
    {
        var path = Path.Combine(directory, "som-de-teste.wav");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, Generate());
        }

        return path;
    }

    public static byte[] Generate()
    {
        var tones = new (double Frequency, double Seconds)[] { (880, 0.35), (0, 0.15), (1320, 0.5) };
        var samples = new List<short>();
        foreach (var (frequency, seconds) in tones)
        {
            var count = (int)(SampleRate * seconds);
            for (var i = 0; i < count; i++)
            {
                // Rampa de 20 ms no início e no fim de cada bipe para não estalar.
                var edge = Math.Min(1.0, Math.Min(i, count - i) / (SampleRate * 0.02));
                var value = frequency <= 0 ? 0 : Math.Sin(2 * Math.PI * frequency * i / SampleRate) * 0.5 * edge;
                samples.Add((short)(value * short.MaxValue));
            }
        }

        var data = samples.Count * 2;
        var bytes = new byte[44 + data];
        var span = bytes.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + data);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], data);
        for (var i = 0; i < samples.Count; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + (i * 2))..], samples[i]);
        }

        return bytes;
    }
}
