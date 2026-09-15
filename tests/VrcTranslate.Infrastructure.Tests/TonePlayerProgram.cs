using System.Globalization;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VrcTranslate.Infrastructure.Tests;

/// <summary>
/// Entry point of this test assembly when it is started as a process of its own.
/// Process loopback can only be proven with a second, independent process that
/// renders a known tone, so the integration tests start this assembly again
/// through the .NET host and let it play. The test runner loads the assembly as
/// a library, so <see cref="Main"/> is never called during a normal test run.
/// </summary>
internal static class TonePlayerProgram
{
    /// <summary>Line printed once the tone is really rendering.</summary>
    public const string ReadyMarker = "tone-ready";

    /// <summary>Long enough for any single test; the test kills the process when it is done.</summary>
    public const int DefaultSeconds = 120;

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            // Loaded as a library by the test runner: nothing to play.
            return 0;
        }

        try
        {
            return Run(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var options = ToneOptions.Parse(args);
        if (options.Silent)
        {
            // A live target with no render stream at all: its process loopback
            // has to deliver silence instead of the system mix.
            Announce();
            Thread.Sleep(options.Duration);
            return 0;
        }

        // The tone is generated at the endpoint's own mix rate so the shared
        // mode stream can never be rejected for a format mismatch.
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        int sampleRate;
        int channels;
        using (var mixClient = device.AudioClient)
        {
            var mixFormat = mixClient.MixFormat;
            sampleRate = mixFormat.SampleRate;
            channels = mixFormat.Channels;
        }

        using var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
        var signal = new SignalGenerator(sampleRate, channels)
        {
            Frequency = options.Frequency,
            Gain = 0.3,
            Type = SignalGeneratorType.Sin
        };
        output.Init(signal.ToWaveProvider());
        output.Play();

        // Let the engine start the stream so the first captured packet already
        // carries the tone instead of the silence in front of it.
        Thread.Sleep(300);
        Announce();
        Thread.Sleep(options.Duration);
        output.Stop();
        return 0;
    }

    private static void Announce()
    {
        Console.Out.WriteLine(ReadyMarker);
        Console.Out.Flush();
    }

    /// <summary>Command line of one helper process.</summary>
    private readonly record struct ToneOptions(double Frequency, bool Silent, TimeSpan Duration)
    {
        public static ToneOptions Parse(string[] args)
        {
            var frequency = 440d;
            var silent = false;
            var seconds = DefaultSeconds;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--tone":
                        frequency = double.Parse(Require(args, ref index), CultureInfo.InvariantCulture);
                        break;
                    case "--silent":
                        silent = true;
                        break;
                    case "--seconds":
                        seconds = int.Parse(Require(args, ref index), CultureInfo.InvariantCulture);
                        break;
                    default:
                        throw new ArgumentException($"未知的测试音参数：{args[index]}");
                }
            }

            if (!silent && frequency is <= 0 or > 20_000)
            {
                throw new ArgumentOutOfRangeException(nameof(args), frequency, "测试音频率必须在 0 到 20 kHz 之间。");
            }

            return new ToneOptions(frequency, silent, TimeSpan.FromSeconds(seconds));
        }

        private static string Require(string[] args, ref int index)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"参数 {args[index]} 缺少取值。");
            }

            return args[++index];
        }
    }
}
