using System.Runtime.InteropServices;
using TarkovMapCompanion.Diagnostics;

namespace TarkovMapCompanion.Audio;

/// <summary>
/// The chirp a ping makes.
/// </summary>
/// <remarks>
/// <para>
/// A ping you have to be looking at the map to notice is half a feature. The whole point is that a
/// teammate can get your attention while you are looking at the game.
/// </para>
/// <para>
/// The waveform is generated rather than shipped as a file, so there is no asset to embed and the
/// sound can be tuned by editing numbers. Two short rising tones: distinctive enough not to be
/// mistaken for a Windows notification, short enough not to talk over anything.
/// </para>
/// <para>
/// Played through winmm rather than by taking a dependency. Note the shape of this interop
/// deliberately: a byte array and three integers, no structs. The last hand-marshalled struct in
/// this project got its packing wrong and took the process down with an access violation that no
/// catch block could see, so anything here stays boring on purpose.
/// </para>
/// </remarks>
public static class PingSound
{
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    private const int SampleRate = 44100;

    private static readonly Lazy<byte[]> Waveform = new(Build);

    /// <summary>Turned off by the preference; silence is a legitimate choice on a second monitor.</summary>
    public static bool Enabled { get; set; } = true;

    public static void Play()
    {
        if (!Enabled || !OperatingSystem.IsWindows())
            return;

        try
        {
            // Asynchronous: this is called from the UI thread and must not hold it for the length
            // of the sound.
            PlaySound(Waveform.Value, IntPtr.Zero, SndMemory | SndAsync | SndNoDefault);
        }
        catch (Exception ex)
        {
            // A machine with no audio device is not a reason for a ping to fail.
            Log.Warn($"could not play the ping sound: {ex.Message}");
        }
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(byte[] data, IntPtr module, uint flags);

    /// <summary>Builds a 16-bit mono WAV in memory.</summary>
    private static byte[] Build()
    {
        var tones = new (double Frequency, double Seconds)[] { (988, 0.055), (1319, 0.075) };

        var samples = new List<short>();

        foreach (var (frequency, seconds) in tones)
        {
            var count = (int)(SampleRate * seconds);

            for (var i = 0; i < count; i++)
            {
                var t = i / (double)SampleRate;

                // A short attack and a longer decay. Starting or stopping a sine at full amplitude
                // puts a step in the waveform, which comes out of the speakers as a click.
                var progress = i / (double)count;
                var envelope = Math.Min(progress / 0.08, 1.0) * Math.Pow(1.0 - progress, 1.6);

                samples.Add((short)(Math.Sin(2 * Math.PI * frequency * t) * envelope * 9000));
            }
        }

        return Wrap(samples);
    }

    private static byte[] Wrap(List<short> samples)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        var dataBytes = samples.Count * 2;

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16);                       // PCM header size
        writer.Write((short)1);                 // PCM
        writer.Write((short)1);                 // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);           // bytes per second
        writer.Write((short)2);                 // block align
        writer.Write((short)16);                // bits per sample

        writer.Write("data"u8);
        writer.Write(dataBytes);

        foreach (var sample in samples)
            writer.Write(sample);

        writer.Flush();
        return stream.ToArray();
    }
}
