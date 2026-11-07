using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Host;

namespace Companion.App.Jobs;

/// <summary>
/// Named-pipe job channel (SPEC 10.2, SEC-2): pipe <see cref="PipeProtocol.PipeName"/>, ACL = the interactive user
/// read/write, Administrators and SYSTEM full control, network logons denied; first-instance only (no squatting);
/// one request line per connection, read with a timeout, at most <see cref="PipeProtocol.MaxMessageBytes"/> bytes.
/// </summary>
internal sealed class PipeServer : IDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<PipeRequest, PipeResponse> _handler;
    private readonly IJournal _journal;
    private readonly IClock _clock;
    private readonly SecurityIdentifier _interactiveUser;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Creates the server (no pipe until <see cref="Start"/>).</summary>
    public PipeServer(Func<PipeRequest, PipeResponse> handler, IJournal journal, IClock clock, SecurityIdentifier interactiveUser)
    {
        _handler = handler;
        _journal = journal;
        _clock = clock;
        _interactiveUser = interactiveUser;
    }

    /// <summary>The pipe ACL.</summary>
    public static PipeSecurity BuildSecurity(SecurityIdentifier interactiveUser)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(interactiveUser, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return security;
    }

    /// <summary>Starts the accept loop.</summary>
    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // loop ended by cancellation
        }

        _cts.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var security = BuildSecurity(_interactiveUser);
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    PipeProtocol.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
                    PipeProtocol.MaxMessageBytes * 2,
                    PipeProtocol.MaxMessageBytes * 2,
                    security);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Name held by another process (or a previous instance not released yet): retry later, never share it.
                Journal("pipe.createFailed");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            await using (server.ConfigureAwait(false))
            {
                try
                {
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    await ServeOneAsync(server, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                    // client went away
                }
                catch (Exception)
                {
                    Journal("pipe.error");
                }
            }
        }
    }

    private async Task ServeOneAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadTimeout);
        PipeResponse response;
        var line = await ReadLineAsync(server, timeout.Token).ConfigureAwait(false);
        if (line.Code is not null)
        {
            response = new PipeResponse(false, line.Code);
        }
        else
        {
            var request = PipeCodec.ParseRequest(line.Bytes, out var code);
            if (request is null)
            {
                response = new PipeResponse(false, code);
                Journal(code);
            }
            else
            {
                try
                {
                    response = _handler(request);
                }
                catch (Exception)
                {
                    response = new PipeResponse(false, "host.error");
                }
            }
        }

        var bytes = PipeCodec.EncodeResponse(response);
        using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        writeTimeout.CancelAfter(ReadTimeout);
        await server.WriteAsync(bytes, writeTimeout.Token).ConfigureAwait(false);
        await server.WriteAsync("\n"u8.ToArray(), writeTimeout.Token).ConfigureAwait(false);
        await server.FlushAsync(writeTimeout.Token).ConfigureAwait(false);
        try
        {
            server.WaitForPipeDrain();
        }
        catch (IOException)
        {
            // client closed early
        }

        if (server.IsConnected)
        {
            server.Disconnect();
        }
    }

    /// <summary>Reads one line of at most MaxMessageBytes; Code is set when the read was refused.</summary>
    private static async Task<(byte[] Bytes, string? Code)> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var buffer = new byte[PipeProtocol.MaxMessageBytes + 2];
        var count = 0;
        try
        {
            while (count < buffer.Length)
            {
                var n = await s.ReadAsync(buffer.AsMemory(count, buffer.Length - count), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                var nl = Array.IndexOf(buffer, (byte)'\n', count, n);
                count += n;
                if (nl >= 0)
                {
                    var len = nl > 0 && buffer[nl - 1] == (byte)'\r' ? nl - 1 : nl;
                    return len > PipeProtocol.MaxMessageBytes ? ([], "bad.size") : (buffer[..len], null);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return ([], "bad.timeout");
        }

        if (count > PipeProtocol.MaxMessageBytes)
        {
            return ([], "bad.size");
        }

        return count == 0 ? ([], "bad.empty") : (buffer[..count], null);
    }

    private void Journal(string code)
    {
        try
        {
            _journal.Write(new JournalRecord(_clock.UtcNow, JournalKind.JobRefused, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code }));
        }
        catch (Exception)
        {
            // best effort
        }
    }
}
