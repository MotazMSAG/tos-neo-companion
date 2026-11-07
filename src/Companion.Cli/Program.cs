namespace Companion.Cli;

/// <summary>Entry point of the non-elevated client (SPEC 10.5).</summary>
internal static class Program
{
    private static int Main(string[] args) =>
        new CliRunner(new NamedPipeTransport(), Console.Out, Console.Error).Run(args);
}
