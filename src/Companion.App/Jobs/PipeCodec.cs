using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Companion.Contracts;
using Companion.Contracts.Host;

namespace Companion.App.Jobs;

/// <summary>
/// Wire format of the job pipe (SPEC 10.2, SEC-2): one JSON object per line, at most
/// <see cref="PipeProtocol.MaxMessageBytes"/> bytes. Requests are validated strictly before anything else sees them:
/// unknown fields, fields the op does not need, wrong types, duplicates, over-long values and malformed ids are
/// rejected with a code (the caller's text is never echoed back or journaled).
/// </summary>
internal static partial class PipeCodec
{
    /// <summary>Max parameters per submit (same as the feature manifest rule).</summary>
    public const int MaxParams = 16;

    /// <summary>Max raw parameter value length.</summary>
    public const int MaxParamValueLength = 64;

    private static readonly Dictionary<string, PipeOp> Ops = new(StringComparer.Ordinal)
    {
        ["submit"] = PipeOp.Submit,
        ["status"] = PipeOp.Status,
        ["result"] = PipeOp.Result,
        ["cancel"] = PipeOp.Cancel,
        ["stop"] = PipeOp.Stop,
        ["pause"] = PipeOp.Pause,
        ["resume"] = PipeOp.Resume,
        ["show"] = PipeOp.Show,
    };

    private static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal) { "op", "jobId", "jobName", "params" };

    private static readonly Dictionary<PipeOp, HashSet<string>> AllowedFields = new()
    {
        [PipeOp.Submit] = new(StringComparer.Ordinal) { "op", "jobId", "jobName", "params" },
        [PipeOp.Status] = new(StringComparer.Ordinal) { "op" },
        [PipeOp.Result] = new(StringComparer.Ordinal) { "op", "jobId" },
        [PipeOp.Cancel] = new(StringComparer.Ordinal) { "op", "jobId" },
        [PipeOp.Stop] = new(StringComparer.Ordinal) { "op" },
        [PipeOp.Pause] = new(StringComparer.Ordinal) { "op" },
        [PipeOp.Resume] = new(StringComparer.Ordinal) { "op" },
        [PipeOp.Show] = new(StringComparer.Ordinal) { "op", "jobName" },
    };

    /// <summary>Compact (single-line) copy of the contract serializer.</summary>
    public static JsonSerializerOptions WireOptions { get; } = CreateWire();

    [GeneratedRegex(PipeProtocol.JobIdPattern, RegexOptions.CultureInvariant)]
    private static partial Regex JobIdRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex JobNameRegex();

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex ParamNameRegex();

    /// <summary>True if <paramref name="jobId"/> is 32 lower-case hex characters.</summary>
    public static bool IsJobId(string? jobId) => jobId is not null && JobIdRegex().IsMatch(jobId);

    /// <summary>
    /// Parses and validates one request line (without its terminating newline). Returns null and a "bad.*" code when the
    /// request is refused.
    /// </summary>
    public static PipeRequest? ParseRequest(ReadOnlySpan<byte> line, out string code)
    {
        if (line.Length == 0)
        {
            code = "bad.empty";
            return null;
        }

        if (line.Length > PipeProtocol.MaxMessageBytes)
        {
            code = "bad.size";
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions { MaxDepth = 4, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException)
        {
            code = "bad.json";
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                code = "bad.shape";
                return null;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in root.EnumerateObject())
            {
                if (!KnownFields.Contains(p.Name))
                {
                    code = "bad.unknownField";
                    return null;
                }

                if (!seen.Add(p.Name))
                {
                    code = "bad.duplicateField";
                    return null;
                }
            }

            if (!root.TryGetProperty("op", out var opEl) || opEl.ValueKind != JsonValueKind.String || !Ops.TryGetValue(opEl.GetString()!, out var op))
            {
                code = "bad.op";
                return null;
            }

            foreach (var name in seen)
            {
                if (!AllowedFields[op].Contains(name))
                {
                    code = "bad.fieldForOp";
                    return null;
                }
            }

            string? jobId = null;
            if (op is PipeOp.Submit or PipeOp.Result or PipeOp.Cancel)
            {
                if (!root.TryGetProperty("jobId", out var idEl) || idEl.ValueKind != JsonValueKind.String || !IsJobId(idEl.GetString()))
                {
                    code = "bad.jobId";
                    return null;
                }

                jobId = idEl.GetString();
            }

            string? jobName = null;
            Dictionary<string, string>? parameters = null;
            if (op == PipeOp.Show && root.TryGetProperty("jobName", out var tabEl))
            {
                // the tab to show (optional), validated like a job name
                if (tabEl.ValueKind != JsonValueKind.String || !JobNameRegex().IsMatch(tabEl.GetString()!))
                {
                    code = "bad.jobName";
                    return null;
                }

                jobName = tabEl.GetString();
            }

            if (op == PipeOp.Submit)
            {
                if (!root.TryGetProperty("jobName", out var nameEl) || nameEl.ValueKind != JsonValueKind.String || !JobNameRegex().IsMatch(nameEl.GetString()!))
                {
                    code = "bad.jobName";
                    return null;
                }

                jobName = nameEl.GetString();
                if (root.TryGetProperty("params", out var paramsEl))
                {
                    parameters = ParseParams(paramsEl, out var paramCode);
                    if (parameters is null)
                    {
                        code = paramCode;
                        return null;
                    }
                }
            }

            code = "ok";
            return new PipeRequest(op, jobId, jobName, parameters);
        }
    }

    private static Dictionary<string, string>? ParseParams(JsonElement el, out string code)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            code = "bad.params";
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in el.EnumerateObject())
        {
            if (result.Count >= MaxParams)
            {
                code = "bad.params.tooMany";
                return null;
            }

            if (!ParamNameRegex().IsMatch(p.Name) || result.ContainsKey(p.Name))
            {
                code = "bad.params.name";
                return null;
            }

            if (p.Value.ValueKind != JsonValueKind.String)
            {
                code = "bad.params.type";
                return null;
            }

            var v = p.Value.GetString()!;
            if (v.Length == 0 || v.Length > MaxParamValueLength || v.Any(char.IsControl))
            {
                code = "bad.params.value";
                return null;
            }

            result[p.Name] = v;
        }

        code = "ok";
        return result;
    }

    /// <summary>
    /// Encodes a response as one line (no newline included) of at most <see cref="PipeProtocol.MaxMessageBytes"/> bytes.
    /// When the full response is too large, the least important parts are dropped in this order: job data, the job
    /// whitelist in the status, the engine status, the HALT report; finally only ok + code remain.
    /// </summary>
    public static byte[] EncodeResponse(PipeResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var attempts = new List<PipeResponse> { response };
        if (response.Job?.Data is not null)
        {
            attempts.Add(response with { Job = response.Job with { Data = null } });
        }

        var last = attempts[^1];
        if (last.Status is { } st)
        {
            for (var keep = st.Jobs.Count - 1; keep >= 0; keep--)
            {
                attempts.Add(last with { Status = st with { Jobs = st.Jobs.Take(keep).ToArray() } });
            }

            var noJobs = st with { Jobs = [] };
            attempts.Add(last with { Status = noJobs with { Engine = null } });
            attempts.Add(last with { Status = noJobs with { Engine = null, Halt = null } });
        }

        attempts.Add(new PipeResponse(response.Ok, response.Code));
        foreach (var a in attempts)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(a, WireOptions);
            if (bytes.Length <= PipeProtocol.MaxMessageBytes)
            {
                return bytes;
            }
        }

        return Encoding.UTF8.GetBytes("{\"ok\":false,\"code\":\"response.tooLarge\"}");
    }

    private static JsonSerializerOptions CreateWire()
    {
        var o = new JsonSerializerOptions(ContractJson.Options) { WriteIndented = false };
        o.MakeReadOnly();
        return o;
    }
}
