using System.Text.RegularExpressions;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Features;
using Companion.Contracts.Safety;

namespace Companion.Features;

/// <summary>Static validation of manifests, job definitions and parameter specs (codes only, no exceptions).</summary>
internal static class ManifestRules
{
    public static readonly Regex FeatureId = new("^[a-z][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant);
    public static readonly Regex SemVer = new(@"^(0|[1-9]\d{0,3})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,5})$", RegexOptions.CultureInvariant);
    public static readonly Regex JobName = new("^[A-Za-z][A-Za-z0-9]{0,39}$", RegexOptions.CultureInvariant);
    public static readonly Regex ParamName = new("^[a-zA-Z][a-zA-Z0-9]{0,31}$", RegexOptions.CultureInvariant);
    public static readonly Regex EnumValue = new(@"^[A-Za-z0-9_.\-]{1,32}$", RegexOptions.CultureInvariant);
    public static readonly Regex BuildCode = new("^[0-9a-f-]{1,80}$", RegexOptions.CultureInvariant);

    /// <summary>Relative data path: segments of [A-Za-z0-9_.-], no '..', separated by / or \.</summary>
    public static readonly Regex RelativePath = new(@"^[A-Za-z0-9_\-][A-Za-z0-9_.\-]{0,63}([\\/][A-Za-z0-9_\-][A-Za-z0-9_.\-]{0,63}){0,3}$", RegexOptions.CultureInvariant);

    public const int MaxDescription = 200;
    public const int MaxParams = 16;
    public const int MaxParamLength = 64;

    /// <summary>Validates a manifest (API version checked separately). Returns a reason code or null.</summary>
    public static string? Validate(FeatureManifest m)
    {
        if (m.Id is null || !FeatureId.IsMatch(m.Id))
        {
            return "manifest.invalid:id";
        }

        if (m.Version is null || !SemVer.IsMatch(m.Version))
        {
            return "manifest.invalid:version";
        }

        if (m.Description is null || m.Description.Length > MaxDescription)
        {
            return "manifest.invalid:description";
        }

        if (!Enum.IsDefined(m.Switch))
        {
            return "manifest.invalid:switch";
        }

        if (m.ValidatedGameBuild is not null && !BuildCode.IsMatch(m.ValidatedGameBuild))
        {
            return "manifest.invalid:validatedGameBuild";
        }

        if (m.Requires is null)
        {
            return "manifest.invalid:requires";
        }

        var deps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in m.Requires)
        {
            if (d is null || d.FeatureId is null || !FeatureId.IsMatch(d.FeatureId) || d.MinVersion is null || !SemVer.IsMatch(d.MinVersion))
            {
                return "manifest.invalid:requires";
            }

            if (string.Equals(d.FeatureId, m.Id, StringComparison.Ordinal))
            {
                return "manifest.invalid:selfDependency";
            }

            if (!deps.Add(d.FeatureId))
            {
                return "manifest.invalid:duplicateDependency";
            }
        }

        if (m.Data is null)
        {
            return "manifest.invalid:data";
        }

        foreach (var file in new[] { m.Data.UiMap, m.Data.Templates, m.Data.Procedures, m.Data.Readers })
        {
            if (file is not null && !IsSafeRelativePath(file, ".json"))
            {
                return "manifest.invalid:dataPath";
            }
        }

        return null;
    }

    /// <summary>True for a relative path inside the feature folder with the given extension.</summary>
    public static bool IsSafeRelativePath(string path, string? extension)
    {
        if (!RelativePath.IsMatch(path))
        {
            return false;
        }

        foreach (var seg in path.Split('/', '\\'))
        {
            if (seg is "." or ".." || seg.EndsWith('.'))
            {
                return false;
            }
        }

        return extension is null || path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Validates a job definition for a feature. Returns a reason code or null.</summary>
    public static string? ValidateJob(JobDefinition j, FeatureManifest owner)
    {
        if (j.Name is null || !JobName.IsMatch(j.Name))
        {
            return "job.invalid:name";
        }

        var n = j.Name;
        if (!string.Equals(j.FeatureId, owner.Id, StringComparison.Ordinal))
        {
            return "job.invalid:featureId:" + n;
        }

        if (j.Description is null || j.Description.Length > MaxDescription)
        {
            return "job.invalid:description:" + n;
        }

        if (!Enum.IsDefined(j.Kind) || !Enum.IsDefined(j.Switch))
        {
            return "job.invalid:kind:" + n;
        }

        // GATE-01: a commit job is always gated, and only by its feature's own switch.
        if (j.Kind == JobKind.Commit && j.Switch == AutomationSwitch.None)
        {
            return "job.invalid:commitWithoutSwitch:" + n;
        }

        if (j.Switch != AutomationSwitch.None && j.Switch != owner.Switch)
        {
            return "job.invalid:switchMismatch:" + n;
        }

        if (j.MaxPerHour < 1 || j.MaxPerHour > HardRules.MaxJobsPerHour)
        {
            return "job.invalid:maxPerHour:" + n;
        }

        if (j.Params is null || j.Params.Count > MaxParams)
        {
            return "job.invalid:params:" + n;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in j.Params)
        {
            var reason = ValidateParam(p);
            if (reason is not null)
            {
                return reason + ":" + n;
            }

            if (!names.Add(p.Name))
            {
                return "param.invalid:duplicate:" + n;
            }
        }

        return null;
    }

    private static string? ValidateParam(JobParamSpec? p)
    {
        if (p is null || p.Name is null || !ParamName.IsMatch(p.Name))
        {
            return "param.invalid:name";
        }

        if (p.MaxLength < 1 || p.MaxLength > MaxParamLength)
        {
            return "param.invalid:maxLength";
        }

        switch (p.Type)
        {
            case JobParamType.Int:
                // Typed AND bounded (SEC-2).
                if (p.Min is not { } min || p.Max is not { } max || min > max)
                {
                    return "param.invalid:bounds";
                }

                break;
            case JobParamType.Enum:
                if (p.Allowed is null || p.Allowed.Count == 0 || p.Allowed.Any(a => a is null || !EnumValue.IsMatch(a)))
                {
                    return "param.invalid:allowed";
                }

                break;
            case JobParamType.Bool:
            case JobParamType.ItemKey:
                break;
            default:
                return "param.invalid:type";
        }

        return null;
    }

    /// <summary>Compares two semantic versions (both valid).</summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (var i = 0; i < 3; i++)
        {
            var c = long.Parse(pa[i], System.Globalization.CultureInfo.InvariantCulture)
                .CompareTo(long.Parse(pb[i], System.Globalization.CultureInfo.InvariantCulture));
            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    }
}
