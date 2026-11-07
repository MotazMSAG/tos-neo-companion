using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.GameIo;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;
using Companion.GameIO.Geometry;
using Companion.GameIO.Tests.Fakes;

namespace Companion.GameIO.Tests;

/// <summary>
/// The off-screen anchor patch rule of the slid verification (SPEC 7.3, live run 2026-10-14, ScanRecipeList card 6 of job
/// 35e59bca) against the fake Win32 sink: the 2nd-row card (364,669) needs the slide dx 963, dy -215, which puts the list
/// anchor (485,123) above the top of every monitor; the fake capture draws only the client part on the slid monitor
/// (c13), so the anchor is black while the card under the cursor is drawn.
/// </summary>
[TestClass]
public sealed class OffscreenAnchorClickTests
{
    private static bool IsDown(Call c) => c.Kind == "post" && c.Msg == HardRules.WM_LBUTTONDOWN;

    private static ClickRequest Card(Frame located, PointI? press = null, string template = CardScene.AnchorId, bool patch = true, PointI? center = null, bool near = false) =>
        new("card", template, press ?? CardScene.Card6, FireOn.Up, located.LogAt, located.CapturedAtMonotonic, [], near, center ?? CardScene.Anchor, false, located, patch);

    private static JournalRecord InputRecord(Rig rig) => rig.Journal.Records.Last(j => j.Kind == JournalKind.Input);

    [TestMethod]
    public void OffscreenAnchorCardClickIsPressedAfterThePatchCheck()
    {
        // The live card 6 slides were dx 963, dy -209..-217 (resting cursor y about 475-483): the anchor is above the top.
        foreach (var (cursorY, dy) in new[] { (477, -215), (482, -210), (475, -217) })
        {
            var (rig, _) = CardScene.Create(new PointI(1327, cursorY));
            using var disposeRig = rig;
            var located = CardScene.Locate(rig);

            var r = rig.Io.SlideClick(Card(located), CancellationToken.None);

            var name = "dy " + dy;
            Assert.AreEqual(InputDisposition.Sent, r.Disposition, name + " " + r.Abort);
            Assert.IsTrue(r.DownPosted && r.UpPosted && r.UnfocusSentWhileSlid, name);
            Assert.AreEqual((963, dy), (r.Slide!.Plan.Dx, r.Slide.Plan.Dy), "the card 6 slide of the live run");
            Assert.AreEqual(r.Slide.Plan.HomeMonitor, r.Slide.Plan.SlidMonitor, "same-monitor slide: the cross-monitor rule does not apply");
            Assert.IsTrue(23 + dy + CardScene.Anchor.Y < 0, name + ": the anchor's screen y is above the top");
            Assert.IsFalse(r.SlidVerification!.Found, "the failed anchor check is reported as it was");
            Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect, name);
            Assert.IsTrue(r.Slide.BackAtHome, name);
            Assert.IsFalse(File.Exists(rig.Paths.SlideMarkerPath), name);

            // Same timeline as a verified click: focus, slide, capture, MOVE, DOWN, UP, unfocus while slid, slide back.
            var lp = HardRules.MouseLParam(CardScene.Card6);
            CollectionAssert.AreEqual(
                new[]
                {
                    "send(0x1C,1,0)", "send(0x86,1,0)", "send(0x6,1,0)", "send(0x7,0,0)",
                    $"swp(955,{-8 + dy},0x0215)", "print",
                    $"post(0x200,0,{lp})", $"post(0x201,1,{lp})", $"post(0x202,0,{lp})",
                    "send(0x8,0,0)", "send(0x6,0,0)", "send(0x86,0,0)", "send(0x1C,0,0)",
                    "swp(-8,-8,0x0215)",
                },
                rig.Acting.Select(c => c.ToString()).ToArray(),
                string.Join(" ", rig.Acting));

            // GameIO re-proved the located frame at home (aim tolerance) before anything moved, then checked the slid capture.
            CollectionAssert.AreEqual(
                new[] { (false, CardScene.Anchor, HardRules.AimTolerancePx), (true, CardScene.Anchor, 8) },
                rig.Vision.Verifies.ToArray(),
                name);

            var input = InputRecord(rig);
            Assert.AreEqual(SlidVerifyRules.PatchCode, input.Fields["verify"], name);
            Assert.AreEqual("patch.anchorOffscreen", input.Fields["verify"], name);
            Assert.AreEqual("allowed", input.Fields["patch"], name);
            Assert.IsNull(input.Fields["navFixed"], name);
            Assert.IsTrue((double)input.Fields["patchScore"]! >= SlidVerifyRules.PatchMinNcc, $"{input.Fields["patchScore"]}");
            Assert.IsTrue((double)input.Fields["patchStd"]! >= SlidVerifyRules.PatchMinStd, name);
            Assert.IsTrue((double)input.Fields["patchDelta"]! <= 1.0, name);
            var age = (int)input.Fields["patchAgeMs"]!;
            Assert.IsTrue(age is >= 300 and <= 1500, $"located age at the slid capture: {age} ms");
        }
    }

    [TestMethod]
    public void ShadowPatchNeverChangesAVerifiedClick()
    {
        // A first-row card: the anchor stays on screen and the slid check passes; the patch rule only records its numbers.
        foreach (var (name, shift, patchCode) in new[] { ("same content", 0, "allowed"), ("list moved 3 px", 3, "patch.mismatch") })
        {
            var (rig, scene) = CardScene.Create(slidAnchorFails: false);
            using var disposeRig = rig;
            scene.SlidShiftX = shift;
            var located = CardScene.Locate(rig);

            var r = rig.Io.SlideClick(Card(located, CardScene.Card1), CancellationToken.None);

            Assert.AreEqual(InputDisposition.Sent, r.Disposition, name + " " + r.Abort);
            Assert.AreEqual(-74, r.Slide!.Plan.Dy, name);
            var input = InputRecord(rig);
            Assert.AreEqual("ok", input.Fields["verify"], name);
            Assert.AreEqual(patchCode, input.Fields["patch"], name);
            Assert.IsInstanceOfType<double>(input.Fields["patchScore"], name);
            Assert.IsInstanceOfType<int>(input.Fields["patchAgeMs"], name);
        }
    }

    [TestMethod]
    public void AVerifiedClickWithoutTheOptInJournalsNoPatch()
    {
        using var rig = new Rig();
        rig.ConnectAndSettle();

        var r = rig.Io.SlideClick(rig.BagClick(), CancellationToken.None);

        Assert.AreEqual(InputDisposition.Sent, r.Disposition);
        var input = InputRecord(rig);
        Assert.AreEqual("ok", input.Fields["verify"]);
        foreach (var key in new[] { "patch", "patchScore", "patchStd", "patchDelta", "patchAgeMs" })
        {
            Assert.IsTrue(input.Fields.ContainsKey(key), key);
            Assert.IsNull(input.Fields[key], key);
        }

        Assert.AreEqual(1, rig.Vision.VerifyCalls, "no located-frame check without the opt-in");
    }

    [TestMethod]
    public void OffscreenAnchorPatchRefusalsAbortBeforeDown()
    {
        var patchRect = RectI.CenteredAt(CardScene.Card6, SlidVerifyRules.PatchSide, SlidVerifyRules.PatchSide);
        var cases = new (string Name, string? Patch, PointI? Cursor, Func<Rig, CardScene, Frame, ClickRequest> Request)[]
        {
            ("engine did not opt in", null, null, (_, _, f) => Card(f, patch: false)),
            ("no located frame", "located.missing", null, (_, _, f) => Card(f) with { LocatedFrame = null }),
            ("a copy of GameIO's frame", "located.notOurs", null, (_, _, f) => Card(f) with { LocatedFrame = f with { } }),
            ("a later capture under the first one's time", "located.mismatch", null, (r, _, f) =>
            {
                r.Clock.Advance(TimeSpan.FromMilliseconds(100));
                return Card(f) with { LocatedFrame = CardScene.Locate(r) };
            }),
            ("anchor not in the located frame", "located.anchorMissing", null, (r, _, f) =>
            {
                r.Vision.VerifyHook = (_, id, c) => CardScene.Miss(id, c);
                return Card(f);
            }),
            ("commit anchor", "commit", null, (r, _, f) =>
            {
                r.Templates.Add(FakeTemplates.Def("test.list.commit", 485, 123, TargetTags.ScreenAnchor | TargetTags.Commit, AutomationSwitch.Crafting));
                return Card(f, template: "test.list.commit");
            }),
            ("gated anchor", "gated", null, (r, _, f) =>
            {
                r.Templates.Add(FakeTemplates.Def("test.list.gated", 485, 123, TargetTags.ScreenAnchor, AutomationSwitch.Crafting));
                return Card(f, template: "test.list.gated");
            }),
            ("not a screen anchor", "tag.notAnchor", null, (_, _, f) => Card(f, template: "hud.bag")),
            ("aim inside the anchor", "aimOnly", null, (_, _, f) => Card(f, center: CardScene.Card6.Offset(-12, 8))),
            ("near a deny zone", "nearDenyZone", null, (_, _, f) => Card(f, near: true)),
            ("the list moved one card", "patch.mismatch", null, (_, s, f) =>
            {
                s.SlidShiftX = 113;
                return Card(f);
            }),
            ("the list moved 3 px", "patch.mismatch", null, (_, s, f) =>
            {
                s.SlidShiftX = 3;
                return Card(f);
            }),
            ("a dimming overlay", "patch.dimmed", null, (_, s, f) =>
            {
                s.SlidGain = 0.5;
                return Card(f);
            }),
            ("press point black", "patch.black", null, (_, s, f) =>
            {
                s.SlidBlack = patchRect;
                return Card(f);
            }),
            ("off-monitor part drawn (a real mismatch)", "anchor.drawn", null, (_, s, f) =>
            {
                s.DrawEverything = true;
                return Card(f);
            }),
            ("anchor on screen (a real mismatch)", "anchor.onDrawnMonitor", null, (_, _, f) => Card(f, CardScene.Card1)),
            ("located too old at the slid capture", "located.tooOld", null, (r, _, f) =>
            {
                // P0 passes, the patch rule's own P0 estimate too (1.0 s + 450 ms); a slow slide call (100 ms) then makes the
                // located capture 1.55 s old at the slid capture.
                r.Clock.Advance(TimeSpan.FromMilliseconds(1000));
                var slowSlide = true;
                r.OnCall = c =>
                {
                    if (c.Kind == "swp" && slowSlide)
                    {
                        slowSlide = false;
                        r.Clock.Advance(TimeSpan.FromMilliseconds(100));
                    }
                };
                return Card(f);
            }),
            ("the located-frame check threw", "rule.exception", null, (r, _, f) =>
            {
                r.Vision.VerifyHook = (fr, id, c) => fr.WhileSlid ? CardScene.Miss(id, c) : throw new InvalidOperationException("test");
                return Card(f);
            }),
            ("anchor check failed without a reason", "verify.unexpected", null, (r, _, f) =>
            {
                r.Vision.VerifyHook = (fr, id, c) => fr.WhileSlid ? new MatchResult(id, false, c, 0.12, 0.05, 0, MatchFailure.None) : null;
                return Card(f);
            }),
            ("slid capture invalid, press point black", "patch.black", null, (r, s, f) =>
            {
                r.Vision.VerifyHook = (fr, id, c) => fr.WhileSlid ? CardScene.Miss(id, c, MatchFailure.FrameInvalid) : null;
                s.SlidBlack = patchRect;
                return Card(f);
            }),
            ("anchor found outside the radius", "verify.outsideRadius", null, (r, _, f) =>
            {
                r.Vision.VerifyHook = (fr, id, c) => fr.WhileSlid ? CardScene.Miss(id, c, MatchFailure.OutsideRadius) : null;
                return Card(f);
            }),
            ("anchor found with a low margin", "verify.lowMargin", null, (r, _, f) =>
            {
                r.Vision.VerifyHook = (fr, id, c) => fr.WhileSlid ? CardScene.Miss(id, c, MatchFailure.LowMargin) : null;
                return Card(f);
            }),
            ("cursor near the drawn monitor's edge", "patch.notDrawn", new PointI(1900, 477), (_, _, f) => Card(f)),
        };

        foreach (var (name, patch, cursor, request) in cases)
        {
            var (rig, scene) = CardScene.Create(cursor);
            using var disposeRig = rig;
            var located = CardScene.Locate(rig);
            var req = request(rig, scene, located);

            var r = rig.Io.SlideClick(req, CancellationToken.None);

            Assert.AreEqual(InputDisposition.AbortedAfterFocus, r.Disposition, name);
            Assert.AreEqual(AbortReason.SlidTemplateMismatch, r.Abort, name);
            Assert.IsFalse(rig.Acting.Any(c => c.Kind == "post"), name + ": no button message at all");
            Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect, name);
            Assert.AreEqual(4, rig.Acting.Count(c => c.IsFocusOff), name + ": unfocus after the abort");
            var input = InputRecord(rig);
            Assert.AreEqual("mismatch", input.Fields["verify"], name);
            Assert.AreEqual(patch, input.Fields["patch"], name);
        }
    }

    [TestMethod]
    public void AnInvalidSlidCaptureCountsAsTheAnchorNotSeen()
    {
        // VerifyAt reports FrameInvalid for a mostly black or uniform slid capture (the gap): the press patch still decides.
        var (rig, _) = CardScene.Create();
        using var disposeRig = rig;
        rig.Vision.VerifyHook = (f, id, c) => f.WhileSlid ? CardScene.Miss(id, c, MatchFailure.FrameInvalid) : null;

        var r = rig.Io.SlideClick(Card(CardScene.Locate(rig)), CancellationToken.None);

        Assert.AreEqual(InputDisposition.Sent, r.Disposition, r.Abort.ToString());
        Assert.IsTrue(r.DownPosted && r.UpPosted);
        Assert.AreEqual(MatchFailure.FrameInvalid, r.SlidVerification!.Failure);
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
        var input = InputRecord(rig);
        Assert.AreEqual(SlidVerifyRules.PatchCode, input.Fields["verify"]);
        Assert.AreEqual("allowed", input.Fields["patch"]);
    }

    [TestMethod]
    public void ALocatedCaptureTooOldForTheSlidCaptureSendsNothing()
    {
        // The slid capture comes at least FocusSettle + SlideSettle (450 ms) after P0: a located capture over 1.05 s old at
        // P0 can never meet the patch rule's 1.5 s, so GameIO sends nothing (no focus pair, slide or pacing slot) and the
        // engine locates afresh on its retry.
        var (rig, _) = CardScene.Create();
        using (rig)
        {
            var located = CardScene.Locate(rig);
            rig.Clock.Advance(TimeSpan.FromMilliseconds(1051));

            var r = rig.Io.SlideClick(Card(located), CancellationToken.None);

            Assert.AreEqual(InputDisposition.NotSent, r.Disposition);
            Assert.AreEqual(AbortReason.LocateTooOld, r.Abort);
            Assert.AreEqual(0, r.FocusMessagesSent);
            Assert.AreEqual(0, rig.Acting.Count, string.Join(" ", rig.Acting));
            Assert.AreEqual(0, rig.Vision.VerifyCalls, "refused before the located-frame check");
            Assert.AreEqual(TimeSpan.Zero, rig.Io.Pacing.NextAllowedIn, "no pacing slot taken");
            var input = InputRecord(rig);
            Assert.AreEqual(nameof(AbortReason.LocateTooOld), input.Fields["abort"]);
            Assert.AreEqual("patch.locatedTooOld", input.Fields["detail"]);
        }

        // 1.05 s at P0 is 1.5 s at the slid capture at the earliest: still pressed.
        var (rig2, _) = CardScene.Create();
        using (rig2)
        {
            var located = CardScene.Locate(rig2);
            rig2.Clock.Advance(TimeSpan.FromMilliseconds(1050));

            var r = rig2.Io.SlideClick(Card(located), CancellationToken.None);

            Assert.AreEqual(InputDisposition.Sent, r.Disposition, r.Abort.ToString());
            Assert.AreEqual(1500, InputRecord(rig2).Fields["patchAgeMs"]);
        }

        // Only an opted-in press whose anchor will be off the drawn monitor: without the opt-in the strict check aborts as
        // before, and a first-row card (anchor on screen) is verified strictly (the shadow patch records the age).
        var (rig3, _) = CardScene.Create();
        using (rig3)
        {
            var located = CardScene.Locate(rig3);
            rig3.Clock.Advance(TimeSpan.FromMilliseconds(1200));

            var r = rig3.Io.SlideClick(Card(located, patch: false), CancellationToken.None);

            Assert.AreEqual(AbortReason.SlidTemplateMismatch, r.Abort);
            Assert.IsNull(InputRecord(rig3).Fields["patch"]);
        }

        var (rig4, _) = CardScene.Create(slidAnchorFails: false);
        using (rig4)
        {
            var located = CardScene.Locate(rig4);
            rig4.Clock.Advance(TimeSpan.FromMilliseconds(1200));

            var r = rig4.Io.SlideClick(Card(located, CardScene.Card1), CancellationToken.None);

            Assert.AreEqual(InputDisposition.Sent, r.Disposition, r.Abort.ToString());
            Assert.AreEqual("ok", InputRecord(rig4).Fields["verify"]);
            Assert.AreEqual("located.tooOld", InputRecord(rig4).Fields["patch"]);
        }
    }

    [TestMethod]
    public void ALocatedFrameRefusalIsJournaledWhenTheRunEndsBeforeTheSlidCheck()
    {
        // A wiring fault (a frame that is not GameIO's own capture) stays visible when the cursor moves during the focus.
        var (rig, _) = CardScene.Create();
        using var disposeRig = rig;
        var located = CardScene.Locate(rig);
        rig.OnCall = c =>
        {
            if (c.IsFocusOn)
            {
                rig.Win32.Cursor = CardScene.Cursor.Offset(3, 0);
            }
        };

        var r = rig.Io.SlideClick(Card(located) with { LocatedFrame = located with { } }, CancellationToken.None);

        Assert.AreNotEqual(InputDisposition.Sent, r.Disposition);
        Assert.IsFalse(rig.Acting.Any(c => c.Kind is "swp" or "print" or "post"), string.Join(" ", rig.Acting));
        var input = InputRecord(rig);
        Assert.IsNull(input.Fields["verify"], "the slid check was not reached");
        Assert.AreEqual("located.notOurs", input.Fields["patch"]);
        Assert.IsNull(input.Fields["patchScore"]);
    }

    [TestMethod]
    public void PatchRefusalsAfterTheMeasurementJournalTheNumbers()
    {
        var (rig, scene) = CardScene.Create();
        using var disposeRig = rig;
        scene.SlidGain = 0.5;
        var located = CardScene.Locate(rig);

        rig.Io.SlideClick(Card(located), CancellationToken.None);

        var input = InputRecord(rig);
        Assert.AreEqual("patch.dimmed", input.Fields["patch"]);
        Assert.IsTrue((double)input.Fields["patchScore"]! > 0.99, "a dimmed patch still correlates: the mean test catches it");
        Assert.IsTrue((double)input.Fields["patchDelta"]! > SlidVerifyRules.PatchMaxMeanDelta);
    }

    [TestMethod]
    public void NotClickableAndCaptureFailureStillAbortAsBefore()
    {
        var (rig, _) = CardScene.Create();
        using (rig)
        {
            rig.Vision.VerifyHook = (f, id, c) => f.WhileSlid ? CardScene.Miss(id, c, MatchFailure.NotClickable) : null;
            var r = rig.Io.SlideClick(Card(CardScene.Locate(rig)), CancellationToken.None);

            Assert.AreEqual(InputDisposition.Refused, r.Disposition);
            Assert.IsFalse(rig.Acting.Any(IsDown));
            Assert.AreEqual("notClickable", InputRecord(rig).Fields["verify"]);
            Assert.IsNull(InputRecord(rig).Fields["patch"]);
        }

        var (rig2, _) = CardScene.Create();
        using (rig2)
        {
            var located = CardScene.Locate(rig2);
            rig2.Win32.PrintFails = true;
            var r = rig2.Io.SlideClick(Card(located), CancellationToken.None);

            Assert.AreEqual(AbortReason.SlidCaptureFailed, r.Abort);
            Assert.IsFalse(rig2.Acting.Any(IsDown));
            Assert.AreEqual("captureFailed", InputRecord(rig2).Fields["verify"]);
            Assert.IsNull(InputRecord(rig2).Fields["patch"]);
            Assert.AreEqual(Rig.HomeRect, rig2.GameWindow.Rect);
        }
    }

    [TestMethod]
    public void PopupSinceTheLocateStillAbortsAPatchVerifiedClick()
    {
        var (rig, _) = CardScene.Create();
        using var disposeRig = rig;
        var located = CardScene.Locate(rig);
        rig.Log.Append((p, s, t) => new UiLogEvent(p, s, t, UiVerb.PopupRealShow, "UI_ITEM_REWARD", null));

        var r = rig.Io.SlideClick(Card(located), CancellationToken.None);

        Assert.AreEqual(AbortReason.LogGatePopup, r.Abort);
        Assert.IsFalse(rig.Acting.Any(IsDown));
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
        Assert.AreEqual("allowed", InputRecord(rig).Fields["patch"], "the patch passed; the popup gate still runs after it");
    }

    [TestMethod]
    public void CursorMoveWhileSlidStillAbortsAPatchVerifiedClick()
    {
        var (rig, _) = CardScene.Create();
        using var disposeRig = rig;
        var located = CardScene.Locate(rig);
        rig.OnCall = c =>
        {
            if (c.Kind == "print")
            {
                rig.Win32.Cursor = CardScene.Cursor.Offset(3, 0);
            }
        };

        var r = rig.Io.SlideClick(Card(located), CancellationToken.None);

        Assert.AreEqual(AbortReason.CursorMoved, r.Abort);
        Assert.IsFalse(rig.Acting.Any(IsDown));
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
    }

    [TestMethod]
    public void CrossMonitorSlideWithTheAnchorDrawnIsARealMismatch()
    {
        // c13 cursor: the press (1855,600) slides the window mostly onto DISPLAY1, where the anchor is drawn: the failed
        // check is a real mismatch (the patch rule refuses it for that reason, not because the slide is cross-monitor).
        var (rig, _) = CardScene.Create(new PointI(867, 885));
        using var disposeRig = rig;
        var located = CardScene.Locate(rig);

        var r = rig.Io.SlideClick(Card(located, new PointI(1855, 600)), CancellationToken.None);

        Assert.AreEqual(AbortReason.SlidTemplateMismatch, r.Abort);
        Assert.AreEqual(@"\\.\DISPLAY1", r.Slide!.Plan.SlidMonitor);
        Assert.IsFalse(rig.Acting.Any(IsDown));
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
        var input = InputRecord(rig);
        Assert.AreEqual("notRequested", input.Fields["navFixed"]);
        Assert.AreEqual("anchor.onDrawnMonitor", input.Fields["patch"]);
    }

    [TestMethod]
    public void CrossMonitorSlideWithTheAnchorHiddenIsPressedOnThePatch()
    {
        // Cross-monitor slides are not excluded: with the cursor resting on DISPLAY1 the card 6 slide puts most of the window
        // there (the drawn monitor) and the anchor above its top, while the press patch is drawn under the cursor.
        var cursor = new PointI(-1000, 477);
        var (rig, _) = CardScene.Create(cursor);
        using var disposeRig = rig;
        rig.Win32.Foreground = Rig.CoverD1; // the user works in the DISPLAY1 window the cursor rests on
        rig.Settle(TimeSpan.FromSeconds(3));
        var located = CardScene.Locate(rig);

        var r = rig.Io.SlideClick(Card(located), CancellationToken.None);

        Assert.AreEqual(InputDisposition.Sent, r.Disposition, $"{r.Abort} {InputRecord(rig).Fields["detail"]}");
        Assert.IsTrue(r.DownPosted && r.UpPosted);
        Assert.AreEqual(@"\\.\DISPLAY1", r.Slide!.Plan.SlidMonitor);
        Assert.AreNotEqual(r.Slide.Plan.HomeMonitor, r.Slide.Plan.SlidMonitor, "a cross-monitor slide");
        Assert.AreEqual((cursor.X - CardScene.Card6.X, cursor.Y - CardScene.Card6.Y - 23), (r.Slide.Plan.Dx, r.Slide.Plan.Dy));
        Assert.AreEqual(Rig.HomeRect, rig.GameWindow.Rect);
        var input = InputRecord(rig);
        Assert.AreEqual("notRequested", input.Fields["navFixed"], "the cross-monitor NavFixed rule refused first");
        Assert.AreEqual(SlidVerifyRules.PatchCode, input.Fields["verify"]);
        Assert.AreEqual("allowed", input.Fields["patch"]);
    }

    [TestMethod]
    public void PatchJournalIsCodesAndNumbersOnly()
    {
        var (rig, _) = CardScene.Create();
        using var disposeRig = rig;
        rig.Io.SlideClick(Card(CardScene.Locate(rig)), CancellationToken.None);

        var code = new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9_.:,()\[\]/+\- ]{0,120}$");
        Assert.IsTrue(rig.Journal.Records.Count > 0);
        foreach (var rec in rig.Journal.Records)
        {
            foreach (var (k, v) in rec.Fields)
            {
                Assert.IsTrue(v is null or bool or int or long || (v is double d && double.IsFinite(d)) || (v is string s && code.IsMatch(s)), $"{k}={v}");
            }
        }
    }
}

/// <summary>The pure off-screen anchor patch rule (<see cref="SlidVerifyRules"/>), on the card 6 geometry.</summary>
[TestClass]
public sealed class PatchRuleTests
{
    private static readonly SizeI Client = new(1920, 1009);
    private static readonly RectI Display1 = new(-1920, 0, 0, 1080);
    private static readonly RectI Display2 = new(0, 0, 1920, 1080);
    private static readonly PointI HomeOrigin = new(0, 23);
    private static readonly PointI SlidOrigin = new(963, -192);
    private static readonly PointI Delta = new(963, -215);
    private static readonly TimeSpan LocatedAt = TimeSpan.FromSeconds(1000);
    private static readonly Lazy<byte[]> Texture = new(() => CardScene.FullTexture());

    private static readonly Frame Located = new(1920, 1009, 1920 * 4, Texture.Value, LocatedAt, DateTimeOffset.UnixEpoch, null, HomeOrigin, false);

    private static ClickRequest Req(bool patch = true, PointI? center = null, bool near = false, bool located = true) =>
        new("card", CardScene.AnchorId, CardScene.Card6, FireOn.Up, null, TimeSpan.Zero, [], near, center, false, located ? Located : null, patch);

    private static TemplateDefinition Anchor(TargetTags tags = TargetTags.ScreenAnchor) => FakeTemplates.Def(CardScene.AnchorId, 485, 123, tags);

    /// <summary>The texture as the slid capture shows it: drawn only where the slid client lies on <paramref name="drawn"/>.</summary>
    private static byte[] SlidPixels(RectI drawn, Func<int, int, byte[], int, bool>? edit = null, byte[]? source = null)
    {
        var src = source ?? Texture.Value;
        var px = (byte[])src.Clone();
        for (var y = 0; y < Client.Height; y++)
        {
            for (var x = 0; x < Client.Width; x++)
            {
                var i = ((y * Client.Width) + x) * 4;
                if (!drawn.Contains(new PointI(SlidOrigin.X + x, SlidOrigin.Y + y)))
                {
                    px[i] = 0;
                    px[i + 1] = 0;
                    px[i + 2] = 0;
                }
                else
                {
                    edit?.Invoke(x, y, px, i);
                }
            }
        }

        return px;
    }

    private static Frame Slid(byte[] px, int ageMs = 700) =>
        new(1920, 1009, 1920 * 4, px, LocatedAt + TimeSpan.FromMilliseconds(ageMs), DateTimeOffset.UnixEpoch, null, SlidOrigin, true);

    private static PatchVerdict Verdict(Frame slid, Frame? located = null, PointI? press = null, PointI? cursor = null, RectI? drawn = null, PointI? delta = null, bool noMonitor = false) =>
        SlidVerifyRules.PressPatchVerdict(
            slid, located ?? Located, press ?? CardScene.Card6, cursor ?? CardScene.Cursor, noMonitor ? null : drawn ?? Display2, delta ?? Delta, HardRules.MaxLocateAge);

    private static void Fill(byte[] px, RectI region, Func<int, int, int, byte> value)
    {
        for (var y = region.Top; y < region.Bottom; y++)
        {
            for (var x = region.Left; x < region.Right; x++)
            {
                var i = ((y * 1920) + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    px[i + c] = value(x, y, c);
                }
            }
        }
    }

    [TestMethod]
    public void PatchRequestRefusalCodes()
    {
        Assert.IsNull(SlidVerifyRules.PatchRequestRefusal(Req(center: CardScene.Anchor), Anchor(), true), "card 6 of the live run");
        Assert.AreEqual("notRequested", SlidVerifyRules.PatchRequestRefusal(Req(patch: false, center: CardScene.Anchor), Anchor(), true));
        Assert.AreEqual("located.missing", SlidVerifyRules.PatchRequestRefusal(Req(center: CardScene.Anchor, located: false), Anchor(), true));
        Assert.AreEqual("template.missing", SlidVerifyRules.PatchRequestRefusal(Req(center: CardScene.Anchor), null, false));
        Assert.AreEqual("template.notClickable", SlidVerifyRules.PatchRequestRefusal(Req(center: CardScene.Anchor), Anchor(), false));
        Assert.AreEqual("template.notClickable", SlidVerifyRules.PatchRequestRefusal(Req(center: CardScene.Anchor), Anchor() with { Status = TemplateStatus.Provisional }, true));
        Assert.AreEqual("tag.notAnchor", SlidVerifyRules.PatchRequestRefusal(Req(center: CardScene.Anchor), Anchor(TargetTags.Nav | TargetTags.HudAnchor | TargetTags.NavFixed), true));
        Assert.AreEqual("commit", SlidVerifyRules.PatchRequestRefusal(Req(center: CardScene.Anchor), Anchor(TargetTags.ScreenAnchor | TargetTags.Commit), true));
        Assert.AreEqual("commit", SlidVerifyRules.PatchRequestRefusal(Req(center: CardScene.Anchor), Anchor(TargetTags.ScreenAnchor | TargetTags.Forbidden), true));
        Assert.AreEqual("gated", SlidVerifyRules.PatchRequestRefusal(Req(center: CardScene.Anchor), Anchor() with { Gate = AutomationSwitch.Crafting }, true));
        Assert.AreEqual("anchored", SlidVerifyRules.PatchRequestRefusal(Req(center: CardScene.Anchor), Anchor() with { AnchorId = "hud.bag", OffsetFromAnchor = new PointI(1, 1) }, true));
        Assert.AreEqual("notOffset", SlidVerifyRules.PatchRequestRefusal(Req(center: null), Anchor(), true));
        Assert.AreEqual("notOffset", SlidVerifyRules.PatchRequestRefusal(Req(center: CardScene.Card6), Anchor(), true));
        Assert.AreEqual("aimOnly", SlidVerifyRules.PatchRequestRefusal(Req(center: CardScene.Card6.Offset(18, -10)), Anchor(), true), "the largest aim");
        Assert.AreEqual("aimOnly", SlidVerifyRules.PatchRequestRefusal(Req(center: CardScene.Card6.Offset(-12, 8)), Anchor(), true));
        Assert.IsNull(SlidVerifyRules.PatchRequestRefusal(Req(center: CardScene.Card6.Offset(19, 0)), Anchor(), true), "one pixel beyond an aim");
        Assert.IsNull(SlidVerifyRules.PatchRequestRefusal(Req(center: CardScene.Card6.Offset(0, 11)), Anchor(), true));
        Assert.AreEqual("nearDenyZone", SlidVerifyRules.PatchRequestRefusal(Req(center: CardScene.Anchor, near: true), Anchor(), true));
    }

    [TestMethod]
    public void PatchLocateTooOldOnlyWhenThePatchRuleMustDecideAndCannotMeetTheAge()
    {
        Assert.AreEqual(TimeSpan.FromMilliseconds(450), SlidVerifyRules.MinTimeToSlidCapture, "FocusSettle + SlideSettle");

        static bool TooOld(int ageMs, PointI? press = null, RectI? drawn = null, bool noMonitor = false) =>
            SlidVerifyRules.PatchLocateTooOld(
                CardScene.Anchor, press ?? CardScene.Card6, CardScene.Cursor, noMonitor ? null : drawn ?? Display2, TimeSpan.FromMilliseconds(ageMs), HardRules.MaxLocateAge);

        Assert.IsTrue(TooOld(1051), "card 6: the anchor will be above the screen and 1.051 s + 450 ms > 1.5 s");
        Assert.IsFalse(TooOld(1050), "1.5 s at the slid capture is still allowed");
        Assert.IsFalse(TooOld(0));
        Assert.IsFalse(TooOld(1400, CardScene.Card1), "first-row card: the anchor stays on screen, the strict check decides");
        Assert.IsTrue(TooOld(1051, drawn: Display1), "anchor off the drawn monitor (a cross-monitor slide)");
        Assert.IsFalse(TooOld(1400, noMonitor: true), "drawn monitor unknown: the slid checks decide");
    }

    [TestMethod]
    public void AnchorHiddenNeedsTheCentreOffTheDrawnMonitorAndBlack()
    {
        var slid = SlidPixels(Display2);
        Assert.IsNull(SlidVerifyRules.AnchorHiddenRefusal(slid, Client, CardScene.Anchor, SlidOrigin, Display2), "card 6: the anchor is above the screen");
        Assert.IsNull(SlidVerifyRules.AnchorHiddenRefusal(slid, Client, new PointI(485, 190), SlidOrigin, Display2), "only the square's off-monitor part counts");
        // live 2026-10-15: the centre a few px on screen, the top of the anchor cut by the screen edge: still hidden
        Assert.IsNull(SlidVerifyRules.AnchorHiddenRefusal(slid, Client, new PointI(485, 200), SlidOrigin, Display2), "a half-hidden anchor is hidden");
        // live 2026-10-15: a 110 px wide anchor whose centre square fits on screen but whose right part is cut by the edge
        var nearRight = new PointI(1920 - SlidOrigin.X - 40, 400);
        Assert.AreEqual("anchor.onDrawnMonitor", SlidVerifyRules.AnchorHiddenRefusal(slid, Client, nearRight, SlidOrigin, Display2), "the 56 px square alone fits");
        Assert.IsNull(SlidVerifyRules.AnchorHiddenRefusal(slid, Client, nearRight, SlidOrigin, Display2, new SizeI(110, 45)), "the whole 110 px box does not");

        Assert.AreEqual("frame.short", SlidVerifyRules.AnchorHiddenRefusal(new byte[16], Client, CardScene.Anchor, SlidOrigin, Display2));
        Assert.AreEqual("frame.short", SlidVerifyRules.AnchorHiddenRefusal(null, Client, CardScene.Anchor, SlidOrigin, Display2));
        Assert.AreEqual("slidMonitor.unknown", SlidVerifyRules.AnchorHiddenRefusal(slid, Client, CardScene.Anchor, SlidOrigin, null));
        Assert.AreEqual("anchor.outsideClient", SlidVerifyRules.AnchorHiddenRefusal(slid, Client, new PointI(485, -3), SlidOrigin, Display2));
        Assert.AreEqual("anchor.onDrawnMonitor", SlidVerifyRules.AnchorHiddenRefusal(slid, Client, new PointI(485, 400), SlidOrigin, Display2));
        Assert.AreEqual("anchor.drawn", SlidVerifyRules.AnchorHiddenRefusal(Texture.Value, Client, CardScene.Anchor, SlidOrigin, Display2), "drawn off-monitor: a real mismatch");
        var fewDrawn = SlidPixels(Display2);
        Fill(fewDrawn, new RectI(470, 100, 500, 130), (x, y, c) => CardScene.Texel(x, y, c));
        Assert.AreEqual("anchor.drawn", SlidVerifyRules.AnchorHiddenRefusal(fewDrawn, Client, CardScene.Anchor, SlidOrigin, Display2), "more than 2 % drawn is enough to refuse");
    }

    [TestMethod]
    public void PressPatchAllowsTheCard6Geometry()
    {
        var v = Verdict(Slid(SlidPixels(Display2)));

        Assert.IsTrue(v.Allowed, v.Code);
        Assert.AreEqual(SlidVerifyRules.PatchAllowed, v.Code);
        Assert.IsTrue(v.Score >= 0.999, $"{v.Score}");
        Assert.IsTrue(v.MinStd >= SlidVerifyRules.PatchMinStd, $"{v.MinStd}");
        Assert.IsTrue(v.MeanDelta <= 1, $"{v.MeanDelta}");
        Assert.AreEqual(700, v.AgeMs);
        Assert.IsTrue(Verdict(Slid(SlidPixels(Display2), 1500)).Allowed, "1.5 s at the slid capture is still allowed");
    }

    [TestMethod]
    public void PressPatchRefusalCodes()
    {
        var patch = RectI.CenteredAt(CardScene.Card6, SlidVerifyRules.PatchSide, SlidVerifyRules.PatchSide);
        var ok = SlidPixels(Display2);
        var nearRightEdge = new PointI(950, 669); // screen x 1913: the patch reaches past DISPLAY2's right edge
        var cases = new (string Name, Func<PatchVerdict> Run, string Code)[]
        {
            ("short slid buffer", () => Verdict(Slid(ok) with { Bgra = new byte[16] }), "frame.short"),
            ("slid frame with row padding", () => Verdict(Slid(ok) with { Stride = (1920 * 4) + 4 }), "frame.short"),
            ("located frame of the wrong size", () => Verdict(Slid(ok), Located with { Height = 1008 }), "located.invalid"),
            ("located frame with row padding", () => Verdict(Slid(ok), Located with { Stride = (1920 * 4) + 4 }), "located.invalid"),
            ("located after the slid capture", () => Verdict(Slid(ok, -1)), "located.future"),
            ("located 1501 ms before", () => Verdict(Slid(ok, 1501)), "located.tooOld"),
            ("slid origin is not home + slide", () => Verdict(Slid(ok), delta: new PointI(963, -214)), "origin.mismatch"),
            ("press not under the cursor", () => Verdict(Slid(ok), cursor: CardScene.Cursor.Offset(1, 0)), "press.notUnderCursor"),
            ("slid monitor unknown", () => Verdict(Slid(ok), noMonitor: true), "slidMonitor.unknown"),
            ("patch outside the client", () => Verdict(Slid(ok), press: new PointI(10, 669), cursor: new PointI(973, 477)), "patch.outsideClient"),
            ("patch past the drawn monitor's edge", () => Verdict(Slid(ok), press: nearRightEdge, cursor: nearRightEdge.Offset(963, -192)), "patch.notDrawn"),
            ("patch on the other monitor", () => Verdict(Slid(ok), drawn: Display1), "patch.notDrawn"),
            ("slid patch black", () => Verdict(Slid(SlidPixels(Display2, (x, y, px, i) =>
            {
                if (patch.Contains(new PointI(x, y)))
                {
                    px[i] = px[i + 1] = px[i + 2] = 0;
                }

                return true;
            }))), "patch.black"),
            ("located patch black", () =>
            {
                var px = (byte[])Texture.Value.Clone();
                Fill(px, patch, (_, _, _) => 0);
                return Verdict(Slid(ok), Located with { Bgra = px });
            }, "located.black"),
            ("both patches uniform", () =>
            {
                var home = (byte[])Texture.Value.Clone();
                Fill(home, patch, (_, _, _) => 120);
                var slid = SlidPixels(Display2, source: home);
                return Verdict(Slid(slid), Located with { Bgra = home });
            }, "patch.uniform"),
            ("slid patch dimmed (NCC stays about 1)", () => Verdict(Slid(SlidPixels(Display2, (_, _, px, i) =>
            {
                px[i] = (byte)(px[i] / 2);
                px[i + 1] = (byte)(px[i + 1] / 2);
                px[i + 2] = (byte)(px[i + 2] / 2);
                return true;
            }))), "patch.dimmed"),
            ("the list moved one card (113 px)", () => Verdict(Slid(SlidPixels(Display2, source: CardScene.FullTexture(113)))), "patch.mismatch"),
            ("the list moved 3 px", () => Verdict(Slid(SlidPixels(Display2, source: CardScene.FullTexture(3)))), "patch.mismatch"),
        };

        foreach (var (name, run, code) in cases)
        {
            var v = run();
            Assert.IsFalse(v.Allowed, name);
            Assert.AreEqual(code, v.Code, name);
        }
    }

    [TestMethod]
    public void GrayHelpers()
    {
        var a = new byte[8 * 8 * 4];
        var b = new byte[8 * 8 * 4];
        var flat = new byte[8 * 8 * 4];
        for (var i = 0; i < 64; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                var v = (byte)((i * 37 % 200) + (c * 10));
                a[(i * 4) + c] = v;
                b[(i * 4) + c] = (byte)((v * 0.9) + 5);
                flat[(i * 4) + c] = 77;
            }
        }

        var all = new RectI(0, 0, 8, 8);
        Assert.IsTrue(SlidVerifyRules.GrayNcc(a, b, 8, all) > 0.999, "gain and offset do not change the NCC");
        Assert.AreEqual(1.0, SlidVerifyRules.GrayNcc(a, a, 8, all), 1e-12);
        Assert.AreEqual(0.0, SlidVerifyRules.GrayNcc(a, flat, 8, all), "no variance: 0, never NaN");
        Assert.AreEqual((77.0, 0.0), SlidVerifyRules.GrayStats(flat, 8, all));
        var (mean, std) = SlidVerifyRules.GrayStats(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 }, 2, new RectI(0, 0, 2, 1));
        Assert.AreEqual((127.5, 127.5), (mean, std), "population standard deviation");

        // Same integer luma as the vision unit's grayscale.
        Assert.AreEqual(0, SlidVerifyRules.Luma(0, 0, 0));
        Assert.AreEqual(255, SlidVerifyRules.Luma(255, 255, 255));
        Assert.AreEqual(127, SlidVerifyRules.Luma(10, 200, 30));
    }

    [TestMethod]
    public void AimLimitIsTheHardRule()
    {
        Assert.AreEqual(new SizeI(HardRules.MaxAimOffsetXPx, HardRules.MaxAimOffsetYPx), SlidVerifyRules.MaxAimOffset);
        Assert.AreEqual((18, 10), (HardRules.MaxAimOffsetXPx, HardRules.MaxAimOffsetYPx));
    }
}
