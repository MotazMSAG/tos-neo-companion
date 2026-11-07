namespace Companion.CaptureProbe;

/// <summary>
/// Entry point of the capture probe (see <see cref="ProbeOptions.Usage"/>). Operator-run only, elevated, with the
/// companion host stopped; never started by the host, the build or the tests.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "--help" or "-h" or "/?"))
        {
            Console.WriteLine(ProbeOptions.Usage);
            return args.Length == 0 ? ExitCodes.Usage : ExitCodes.Ok;
        }

        ProbeOptions options;
        try
        {
            options = ProbeOptions.Parse(args);
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine("usage error: " + e.Message);
            Console.Error.WriteLine(ProbeOptions.Usage);
            return ExitCodes.Usage;
        }

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            // Ctrl+C: stop the sequence; the window is still moved back home and verified.
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            return new ProbeRunner(options, Console.Out, cts.Token).Run();
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}
