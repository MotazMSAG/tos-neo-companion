using Companion.Contracts.Engine;
using Companion.Features.Kit;

namespace Companion.Features.Tests;

/// <summary>
/// The Gathering module's pure parts (2026-10-15): the keypad typing of a target, the step subsets (a repeated digit
/// gets its own step id), the press point on a tile and the wait bound.
/// </summary>
[TestClass]
public sealed class GatheringTests
{
    [TestMethod]
    public void TypingATarget_ClearsTypesEachDigitAndConfirms()
    {
        CollectionAssert.AreEqual(
            new[] { "qty", "qty.settle", "key.clear", "key.1", "key.0", "key.0", "key.ok", "set.confirm", "set.settle" },
            Gathering.TypeSteps(100));
    }

    [TestMethod]
    public void ARepeatedStep_GetsItsOwnId_AndAMissingStepThrows()
    {
        var step = (string id) => new StepDefinition(id, StatePredicate.Any, new WaitAction(1), Expectation.Nothing);
        var data = new ProcedureDefinition("crafting.gather", "test", [step("key.0"), step("key.ok")], ProcedureLimits.Default);

        CollectionAssert.AreEqual(new[] { "key.0", "key.0-2", "key.ok" }, Gathering.Steps(data, "key.0", "key.0", "key.ok").Steps.Select(s => s.Id).ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => Gathering.Steps(data, "key.9"));
    }

    [TestMethod]
    public void ATileIsPressedOnItsPicture_AndTheWaitIsBounded()
    {
        Assert.AreEqual(new Companion.Contracts.PointI(393, 657), Gathering.PressPoint(new Companion.Contracts.RectI(343, 617, 443, 720)));
        Assert.AreEqual(TimeSpan.FromSeconds(1100), Gathering.WaitLimit(20));
        Assert.AreEqual(TimeSpan.FromHours(4), Gathering.WaitLimit(100_000));
    }

    [TestMethod]
    [DataRow("Stock Limit: 353/500", 353L, 500L)]
    [DataRow("Stock Limit: 353 / 500", 353L, 500L)]
    [DataRow("Stock Limit: 0/300", 0L, 300L)]
    public void TheStockLimitLine_GivesOwnedAndLimit(string text, long owned, long limit) =>
        Assert.AreEqual(((long, long)?)(owned, limit), Gathering.ParseLimit(text));

    [TestMethod]
    [DataRow("Stock Limit: 501/500")]
    [DataRow("Stock Limit: 5/0")]
    [DataRow("Stock Limit")]
    [DataRow("")]
    public void ANonsenseLimit_IsNull(string text) => Assert.IsNull(Gathering.ParseLimit(text));

    [TestMethod]
    [DataRow("147", 147L)]
    [DataRow(" 20 ", 20L)]
    [DataRow("2O", null)]
    [DataRow("", null)]
    [DataRow("123456", null)]
    public void TheQuantityBox_GivesTheRoom(string text, long? room) => Assert.AreEqual(room, Gathering.ParseRoom(text));

    [TestMethod]
    [DataRow("0/416", 416L)]
    [DataRow("200/200", 200L)]
    [DataRow(" 0 / 18 ", 18L)]
    [DataRow("0/1", 1L)]
    [DataRow("0/", null)]
    [DataRow("Begin", null)]
    [DataRow("", null)]
    public void AQueueSlot_GivesTheTargetTheGameAccepted(string text, long? target) => Assert.AreEqual(target, Gathering.ParseSlot(text));
}
