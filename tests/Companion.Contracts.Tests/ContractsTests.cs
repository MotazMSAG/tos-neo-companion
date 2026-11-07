using System.Reflection;
using System.Text.Json;
using Companion.Contracts;
using Companion.Contracts.Engine;
using Companion.Contracts.Logs;
using Companion.Contracts.Safety;
using Companion.Contracts.Vision;

namespace Companion.Contracts.Tests;

[TestClass]
public sealed class ContractsTests
{
    [TestMethod]
    public void EveryOutcomeHasAClass()
    {
        foreach (StepOutcome o in Enum.GetValues<StepOutcome>())
        {
            var c = OutcomePolicy.ClassOf(o);
            Assert.IsTrue(Enum.IsDefined(c), o.ToString());
        }
        Assert.AreEqual(OutcomeClass.Counted, OutcomePolicy.ClassOf(StepOutcome.None));
        Assert.AreEqual(OutcomeClass.Wait, OutcomePolicy.ClassOf(StepOutcome.NotSent));
        Assert.AreEqual(OutcomeClass.Halt, OutcomePolicy.ClassOf(StepOutcome.ForbiddenUi));
        Assert.AreEqual(OutcomeClass.Halt, OutcomePolicy.ClassOf(StepOutcome.WorldPress));
        Assert.AreEqual(OutcomeClass.Terminal, OutcomePolicy.ClassOf(StepOutcome.GameExited));
        Assert.AreEqual(OutcomeClass.Ok, OutcomePolicy.ClassOf(StepOutcome.LateSuccess));
    }

    [TestMethod]
    public void HardRuleConstantsMatchTheProofs()
    {
        CollectionAssert.AreEquivalent(new uint[] { 0x0215, 0x4215 }, HardRules.AllowedSetWindowPosFlags.ToArray());
        CollectionAssert.AreEquivalent(new uint[] { 0x0214, 0x4214 }, HardRules.EmergencySetWindowPosFlags.ToArray());
        Assert.IsFalse(HardRules.AllowedMessages.Intersect(HardRules.ForbiddenMessages).Any());
        Assert.IsFalse(HardRules.AllowedMessages.Contains(HardRules.WM_SIZE));
        foreach (var k in HardRules.AllowedKeys.Values)
        {
            Assert.IsFalse(HardRules.DeniedKeys.Contains(k), k.ToString());
        }
        var b = HardRules.AllowedKeys[GameKey.B];
        Assert.AreEqual(0x00300001L, HardRules.KeyDownLParam(b));
        Assert.AreEqual(0xC0300001L, HardRules.KeyUpLParam(b));
        Assert.AreEqual((314L << 16) | 1748L, HardRules.MouseLParam(new PointI(1748, 314)));
        Assert.HasCount(4, HardRules.FocusOnSequence);
        Assert.AreEqual(HardRules.WM_KILLFOCUS, HardRules.FocusOffSequence[0].Message);
    }

    [TestMethod]
    public void PressBlockMapsToClientLikeRun3()
    {
        var file = new LogFileIdentity(DateTime.UnixEpoch, 1, "x");
        var e = new PressBlockLogEvent(new LogPosition(file, 10), 0, DateTime.Now, "Label_Text", "Canvas_Bag", new PointD(1748.0, 694.0));
        Assert.AreEqual(new PointI(1748, 314), e.ToClient(1009));
        var run1 = e with { ScreenPosition = new PointD(1732.0, 564.0) };
        Assert.AreEqual(new PointI(1732, 444), run1.ToClient(1009));
    }

    [TestMethod]
    public void UiListsDoNotOverlapDangerously()
    {
        foreach (var n in UiCatalog.EscSafe)
        {
            Assert.IsFalse(UiCatalog.ForbiddenExact.Contains(n), n);
            Assert.IsFalse(UiCatalog.ForbiddenContains.Any(c => n.Contains(c, StringComparison.Ordinal)), n);
            Assert.IsFalse(UiCatalog.SocialExact.Contains(n), n);
        }
        foreach (var n in UiCatalog.OverlaySeed)
        {
            Assert.IsFalse(UiCatalog.StackSeed.Contains(n), n);
        }
    }

    [TestMethod]
    public void ProcedureJsonRoundTripsWithPolymorphicActions()
    {
        var p = new ProcedureDefinition(
            "OpenBagKey",
            "Reset, b, expect UI_BAG_MAIN, Esc",
            [
                new StepDefinition("reset", StatePredicate.Any, new ResetAction(), new Expectation([UiExpectation.HiddenTop(null)])),
                new StepDefinition("key-b", new StatePredicate(HudClean: true), new KeyAction(GameKey.B), new Expectation(UiExpectation.Opened("UI_BAG_MAIN"))),
                new StepDefinition("toggle", StatePredicate.Any, new ToggleHudAction(HudState.Expanded), new Expectation(Hud: HudState.Expanded), 3000, StepFlags.SkipIfSatisfied),
                new StepDefinition("click", StatePredicate.Any, new ClickAction("hud.bag"), Expectation.Nothing),
                new StepDefinition("stub", StatePredicate.Any, new NeedsSurveyAction("bag.grid"), Expectation.Nothing),
                new StepDefinition("cell", StatePredicate.Any, new ClickAction("craft.fav.anchor", new PointI(-227, -258)), Expectation.Nothing),
            ],
            ProcedureLimits.Default);
        var json = JsonSerializer.Serialize(p, ContractJson.Options);
        StringAssert.Contains(json, "\"type\": \"key\"");
        var back = JsonSerializer.Deserialize<ProcedureDefinition>(json, ContractJson.Options);
        Assert.IsNotNull(back);
        Assert.IsInstanceOfType<KeyAction>(back.Steps[1].Action);
        Assert.AreEqual(GameKey.B, ((KeyAction)back.Steps[1].Action).Key);
        Assert.IsInstanceOfType<NeedsSurveyAction>(back.Steps[4].Action);
        // An offset click (grid cell from a located anchor) round-trips; a plain click keeps a null offset.
        Assert.AreEqual(new ClickAction("craft.fav.anchor", new PointI(-227, -258)), back.Steps[5].Action);
        Assert.IsNull(((ClickAction)back.Steps[3].Action).Offset);
        var plain = JsonSerializer.Deserialize<StepAction>("{\"type\":\"click\",\"templateId\":\"hud.bag\"}", ContractJson.Options);
        Assert.AreEqual(new ClickAction("hud.bag"), plain);
        Assert.AreEqual(HudState.Expanded, back.Steps[2].Expect.Hud);
        Assert.AreEqual(UiVerb.HideTop, back.Steps[0].Expect.UiEvents![0].Verb);
        Assert.IsNull(back.Steps[0].Expect.UiEvents![0].UiName);
    }

    [TestMethod]
    public void ContractsDeclareNoPInvoke()
    {
        var asm = typeof(HardRules).Assembly;
        foreach (var t in asm.GetTypes())
        {
            foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                Assert.IsFalse(m.Attributes.HasFlag(MethodAttributes.PinvokeImpl), $"{t.FullName}.{m.Name}");
            }
        }
    }

    [TestMethod]
    public void AllowedPInvokesDoNotTripForbiddenTokens()
    {
        foreach (var (project, entries) in StaticRules.PInvokeAllowlist)
        {
            foreach (var entry in entries)
            {
                var name = entry[(entry.IndexOf('!') + 1)..];
                string[] callSites = [$"{name}(", $" {name}(", $".{name}(", $"\"{name}\"", $"static extern bool {name}(IntPtr h);"];
                foreach (var site in callSites)
                {
                    foreach (var token in StaticRules.ForbiddenTokens)
                    {
                        Assert.IsFalse(site.Contains(token, StringComparison.Ordinal), $"{project}: '{site}' trips '{token}'");
                    }
                }
            }
        }
    }

    [TestMethod]
    public void RectHelpers()
    {
        var r = RectI.FromXywh(-8, -8, 1936, 1048);
        Assert.AreEqual(new RectI(-8, -8, 1928, 1040), r);
        Assert.AreEqual(new RectI(-838, 327, 1098, 1375), r.Offset(-830, 335));
        Assert.IsTrue(r.ContainsWithMargin(new PointI(918, 672), 16));
    }
}
