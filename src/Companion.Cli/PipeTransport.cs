using System.IO.Pipes;
using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Host;

namespace Companion.Cli;

/// <summary>Sends one request to the host and returns its response (null + error code when the host is unreachable).</summary>
internal interface IPipeTransport
{
    /// <summary>One round trip.</summary>
    PipeResponse? Send(PipeRequest request, out string? error);
}

/// <summary>
/// Named-pipe client (SPEC 10.2/10.5): connect within <see cref="PipeProtocol.ConnectTimeoutMs"/> (else the host is not
/// running), one request line, one response line, both at most <see cref="PipeProtocol.MaxMessageBytes"/> bytes.
/// </summary>
internal sealed class NamedPipeTransport : IPipeTransport
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Single-line copy of the contract serializer.</summary>
    public static JsonSerializerOptions WireOptions { get; } = CreateWire();

    /// <inheritdoc />
    public PipeResponse? Send(PipeRequest request, out string? error)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, WireOptions);
        if (bytes.Length > PipeProtocol.MaxMessageBytes)
        {
            error = "request.tooLarge";
            return null;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(PipeProtocol.ConnectTimeoutMs);
            using var cts = new CancellationTokenSource(ResponseTimeout);
            client.WriteAsync(bytes, cts.Token).AsTask().GetAwaiter().GetResult();
            client.WriteAsync("\n"u8.ToArray(), cts.Token).AsTask().GetAwaiter().GetResult();
            client.Flush();

            var buffer = new byte[PipeProtocol.MaxMessageBytes + 2];
            var count = 0;
            while (count < buffer.Length)
            {
                var n = client.ReadAsync(buffer.AsMemory(count), cts.Token).AsTask().GetAwaiter().GetResult();
                if (n == 0)
                {
                    break;
                }

                count += n;
                if (Array.IndexOf(buffer, (byte)'\n', 0, count) >= 0)
                {
                    break;
                }
            }

            var end = Array.IndexOf(buffer, (byte)'\n', 0, count);
            var length = end >= 0 ? end : count;
            if (length == 0 || length > PipeProtocol.MaxMessageBytes)
            {
                error = "response.invalid";
                return null;
            }

            return Decode(buffer.AsSpan(0, length), out error);
        }
        catch (TimeoutException)
        {
            error = "host.notRunning";
            return null;
        }
        catch (OperationCanceledException)
        {
            error = "host.timeout";
            return null;
        }
        catch (IOException)
        {
            error = "host.notRunning";
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            error = "host.accessDenied";
            return null;
        }
    }

    /// <summary>Parses a response line.</summary>
    public static PipeResponse? Decode(ReadOnlySpan<byte> line, out string? error)
    {
        try
        {
            var response = JsonSerializer.Deserialize<PipeResponse>(line, ContractJson.Options);
            error = response is null || response.Code is null ? "response.invalid" : null;
            return error is null ? response : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            error = "response.invalid";
            return null;
        }
    }

    private static JsonSerializerOptions CreateWire()
    {
        var o = new JsonSerializerOptions(ContractJson.Options) { WriteIndented = false };
        o.MakeReadOnly();
        return o;
    }
}
