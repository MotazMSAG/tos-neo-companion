using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Companion.Contracts;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.Vision.Internal;

namespace Companion.TemplateCutter;

/// <summary>One template to cut.</summary>
/// <param name="Id">Template id.</param>
/// <param name="SourceName">Capture name recorded in the provenance (e.g. "proofs/out/c1_ability.png").</param>
/// <param name="Layout">How the capture maps onto the client.</param>
/// <param name="CropOffset">Client position of a crop's top-left (ClientCropFull).</param>
/// <param name="ClientRect">Template rect in CLIENT coordinates (its centre becomes the expected centre).</param>
/// <param name="Status">Requested status (Verified is refused for non-full-scale sources).</param>
/// <param name="MaskRects">Ignore rects relative to the template's top-left.</param>
/// <param name="Hud">HUD state the template belongs to.</param>
/// <param name="Tags">Tags.</param>
/// <param name="SearchRadius">Search radius.</param>
/// <param name="Threshold">NCC threshold.</param>
/// <param name="MinMargin">Margin.</param>
/// <param name="FireOn">Down or Up.</param>
/// <param name="Gate">Automation switch for Commit targets.</param>
/// <param name="Note">Short provenance note.</param>
/// <param name="GameBuild">Game build fingerprint, if known.</param>
internal sealed record CutRequest(
    string Id,
    string SourceName,
    SourceLayout Layout,
    PointI CropOffset,
    RectI ClientRect,
    TemplateStatus Status,
    IReadOnlyList<RectI> MaskRects,
    HudState? Hud,
    TargetTags Tags,
    int SearchRadius = 24,
    double Threshold = 0.90,
    double MinMargin = 0.10,
    FireOn FireOn = FireOn.Down,
    AutomationSwitch Gate = AutomationSwitch.None,
    string? Note = null,
    string? GameBuild = null);

/// <summary>Result of a cut: a definition and its PNG, or a refusal code.</summary>
internal sealed record CutResult(TemplateDefinition? Definition, byte[]? Png, string? Refusal)
{
    public bool Ok => Refusal is null;
}

/// <summary>Cutting rules (pure; no file access).</summary>
internal static partial class TemplateCutterCore
{
    /// <summary>Staging folder name under templates\.</summary>
    public const string StagingFolder = "staging";

    /// <summary>Cuts one template from capture bytes.</summary>
    public static CutResult Cut(byte[] sourcePng, CutRequest req)
    {
        if (!IdPattern().IsMatch(req.Id))
        {
            return Refuse("bad-id");
        }

        var scale = SourceImages.ScaleOf(req.Layout);
        if (req.Status == TemplateStatus.Verified && scale != 1)
        {
            return Refuse("verified-needs-full-scale-source");
        }

        if (req.ClientRect.Width < 8 || req.ClientRect.Height < 8 || req.ClientRect.Width > 256 || req.ClientRect.Height > 256)
        {
            return Refuse("bad-rect-size");
        }

        if (req.Tags.HasFlag(TargetTags.Commit) && req.Gate == AutomationSwitch.None)
        {
            return Refuse("commit-needs-gate");
        }

        if (req.Tags.HasFlag(TargetTags.NavFixed) && ((req.Tags & (TargetTags.Commit | TargetTags.Forbidden)) != 0 || req.Gate != AutomationSwitch.None))
        {
            return Refuse("navfixed-needs-plain-navigation");
        }

        BgraImage client;
        RectI covered;
        try
        {
            client = SourceImages.ToClient(sourcePng, req.Layout, req.CropOffset, out covered);
        }
        catch (ArgumentException)
        {
            return Refuse("source-layout-mismatch");
        }

        if (covered.Intersect(req.ClientRect) != req.ClientRect)
        {
            return Refuse("rect-outside-source");
        }

        var png = PngCodec.Encode24(client.CropCopy(req.ClientRect));
        var r = req.ClientRect;
        var provenance = $"{req.SourceName} {req.Layout} offset ({req.CropOffset.X},{req.CropOffset.Y}) scale {scale}; client rect ({r.Left},{r.Top})-({r.Right},{r.Bottom})";
        if (!string.IsNullOrWhiteSpace(req.Note))
        {
            provenance += "; " + req.Note;
        }

        var def = new TemplateDefinition(
            req.Id,
            req.Id + ".png",
            null,
            req.MaskRects.Count == 0 ? null : req.MaskRects.ToArray(),
            r.Center,
            req.SearchRadius,
            req.Threshold,
            req.MinMargin,
            req.Status,
            scale,
            req.Hud,
            req.Tags,
            req.FireOn,
            req.Gate,
            null,
            null,
            provenance,
            req.GameBuild);
        return new CutResult(def, png, null);
    }

    /// <summary>Serialises definitions with the contract options, dropping RectI's computed properties.</summary>
    public static string ToJson(IEnumerable<TemplateDefinition> defs)
    {
        var node = JsonSerializer.SerializeToNode(defs.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray(), ContractJson.Options)!;
        foreach (var item in node.AsArray())
        {
            if (item?["maskRects"] is JsonArray rects)
            {
                foreach (var rect in rects.OfType<JsonObject>())
                {
                    foreach (var computed in new[] { "width", "height", "area", "isEmpty", "center", "size" })
                    {
                        rect.Remove(computed);
                    }
                }
            }
        }

        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    /// <summary>Parses a templates.json document.</summary>
    public static TemplateDefinition[] FromJson(string json) =>
        JsonSerializer.Deserialize<TemplateDefinition[]>(json, ContractJson.Options) ?? [];

    /// <summary>
    /// Resolves the staging folder for a templates folder and checks that <paramref name="fileName"/> stays inside it.
    /// </summary>
    public static string StagingPath(string templatesDir, string fileName)
    {
        var staging = Path.GetFullPath(Path.Combine(templatesDir, StagingFolder));
        var full = Path.GetFullPath(Path.Combine(staging, fileName));
        if (!full.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("refused: the cutter writes only inside templates\\staging");
        }

        return full;
    }

    /// <summary>The SPEC 14 seed set, cut from the proof captures.</summary>
    public static IReadOnlyList<(string File, CutRequest Request)> SeedRequests() =>
    [
        // hud.bag: c3b strip (full scale, client x 1440). Bag glyph measured at crop (285..329, 292..342); the
        // rect is centred on SPEC's (1748,312) (2 px from the proven c3 click at (1748,314)). Badge dot ring
        // crop (326..351, 279..304) masked.
        ("c3b_hud_right_x1440.png", new CutRequest(
            "hud.bag", "proofs/out/c3b_hud_right_x1440.png", SourceLayout.ClientCropFull, new PointI(1440, 0),
            new RectI(1718, 282, 1778, 342), TemplateStatus.Verified, [new RectI(46, 0, 60, 24)], null,
            TargetTags.Nav | TargetTags.HudAnchor | TargetTags.NavFixed, 24, 0.90, 0.10, FireOn.Down, AutomationSwitch.None,
            "badge dot masked; label excluded; per-pixel mask hud.bag.mask.png (see-through background removed; glyph stable across c3b, cal_collapsed_street, cal_expanded_street and live_hud_expanded_barrels)")),

        // hud.toggle.collapsed: four-petal icon measured at crop (393..437, 297..341), centre client (1855,318).
        // Badge dot ring crop (431..454, 274..296) masked.
        ("c3b_hud_right_x1440.png", new CutRequest(
            "hud.toggle.collapsed", "proofs/out/c3b_hud_right_x1440.png", SourceLayout.ClientCropFull, new PointI(1440, 0),
            new RectI(1827, 290, 1883, 346), TemplateStatus.Verified, [new RectI(42, 0, 56, 9)], HudState.Collapsed,
            TargetTags.Toggle | TargetTags.HudAnchor | TargetTags.NavFixed, 24, 0.90, 0.10, FireOn.Down, AutomationSwitch.None,
            "badge dot masked; per-pixel mask hud.toggle.collapsed.mask.png (see-through background removed; glyph stable across c3b and cal_collapsed_street)")),

        // hud.toggle.expanded: X icon in the OLDER layout, half (918..937, 151..169) -> client centre (1855,320).
        // Provisional (half-scale source): classification only. Replace from Calibrate's expanded.png.
        ("c1_ability.png", new CutRequest(
            "hud.toggle.expanded", "proofs/out/c1_ability.png", SourceLayout.ClientHalf, default,
            new RectI(1827, 292, 1883, 348), TemplateStatus.Provisional, [new RectI(42, 0, 56, 8)], HudState.Expanded,
            TargetTags.Toggle | TargetTags.HudAnchor | TargetTags.NavFixed, 24, 0.90, 0.10, FireOn.Down, AutomationSwitch.None,
            "provisional: half-scale, older layout; badge dot masked")),

        // menu.ability: OLDER layout, half (853..881, 212..237) -> client centre (1734,448). Stale: (1732,444) is
        // Homestead in the current layout (c3 run 1). Never clickable.
        ("c1_ability.png", new CutRequest(
            "menu.ability", "proofs/out/c1_ability.png", SourceLayout.ClientHalf, default,
            new RectI(1706, 420, 1762, 476), TemplateStatus.Provisional, [new RectI(40, 0, 56, 14)], HudState.Expanded,
            TargetTags.Nav | TargetTags.NavFixed, 24, 0.90, 0.10, FireOn.Down, AutomationSwitch.None,
            "provisional: half-scale, older layout (stale position); badge dot masked")),
    ];

    private static CutResult Refuse(string code) => new(null, null, code);

    [GeneratedRegex(@"^[a-z0-9]+(?:[._-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
