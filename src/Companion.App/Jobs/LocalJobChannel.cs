using System.Text.Json;
using Companion.Contracts.Host;

namespace Companion.App.Jobs;

/// <summary>
/// The job channel for the companion's own windows (the tray's Crafting window). A request takes exactly the path of a
/// named-pipe request after its transport: it is encoded as one wire line the way the CLI encodes it, validated by
/// <see cref="PipeCodec.ParseRequest"/> as the pipe server validates it, then handed to the same handler
/// (<see cref="JobHost.Handle"/>: whitelist, host state, automation switch, parameter validation, rate and
/// queue limits, journal). Nothing is skipped and nothing is added. Thread-safe as the handler is.
/// </summary>
internal sealed class LocalJobChannel
{
    private readonly Func<PipeRequest, PipeResponse> _handler;

    /// <summary>Creates the channel over the job host's request handler.</summary>
    public LocalJobChannel(Func<PipeRequest, PipeResponse> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>One request, one response (never throws; a refused request carries a "bad.*" code).</summary>
    public PipeResponse Send(PipeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[] line;
        try
        {
            line = JsonSerializer.SerializeToUtf8Bytes(request, PipeCodec.WireOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return new PipeResponse(false, "bad.json");
        }

        if (line.Length > PipeProtocol.MaxMessageBytes)
        {
            return new PipeResponse(false, "bad.size");
        }

        var parsed = PipeCodec.ParseRequest(line, out var code);
        if (parsed is null)
        {
            return new PipeResponse(false, code);
        }

        try
        {
            return _handler(parsed);
        }
        catch (Exception)
        {
            return new PipeResponse(false, "host.error");
        }
    }
}
