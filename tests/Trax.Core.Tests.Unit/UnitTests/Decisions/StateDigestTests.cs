using System.Text.Json.Serialization;
using FluentAssertions;
using Trax.Core.Decisions;

namespace Trax.Core.Tests.Unit.UnitTests.Decisions;

/// <summary>
/// The state hash a replay compares covers every field of the state's runtime type, so two states
/// a decider could tell apart never hash alike, and it gives no hash, rather than a wrong one or an
/// exception, for a state it cannot read the same way every time. Pins core/0004
/// (docs/adr/0004-a-recorded-answer-replays-only-into-the-same-state.md).
/// </summary>
[Property("adr", "docs/adr/0004-a-recorded-answer-replays-only-into-the-same-state.md")]
public class StateDigestTests : TestSetup
{
    private const string Adr = "0004-a-recorded-answer-replays-only-into-the-same-state.md";

    [TestCaseSource(nameof(Shapes))]
    public void Of_StatesThatDifferOnlyInAmount_HashDifferently(Func<decimal, object> make)
    {
        StateDigest
            .Of(make(20))
            .Should()
            .NotBeNull()
            .And.NotBe(
                StateDigest.Of(make(2000)),
                $"{Adr}: a decider can read the amount, so the hash must cover it"
            );
    }

    [TestCaseSource(nameof(Shapes))]
    public void Of_EqualStatesBuiltSeparately_HashAlike(Func<decimal, object> make)
    {
        StateDigest
            .Of(make(20))
            .Should()
            .NotBeNull()
            .And.Be(
                StateDigest.Of(make(20)),
                $"{Adr}: the same state must replay, however many times it is built"
            );
    }

    /// <summary>Each shape a JSON hash would have written the same for 20 and 2000.</summary>
    public static IEnumerable<TestCaseData> Shapes()
    {
        yield return Shape("ATuple", a => (new Order(a), new Customer("ann")));
        yield return Shape("APublicField", a => new FieldOrder { Amount = a });
        yield return Shape(
            "ADerivedTypeHeldAsItsAbstractBase",
            a => new Checkout { Payment = new Card { Amount = a } }
        );
        yield return Shape(
            "ADerivedTypeHeldAsAnInterface",
            a => new Wallet { Payment = new CardPayment(a) }
        );
        yield return Shape("AJsonIgnoredProperty", a => new IgnoredOrder { Amount = a });
        yield return Shape("APrivateField", a => new PrivateOrder(a));
        yield return Shape("AnAnonymousType", a => new { Amount = a });
        yield return Shape("AList", a => new List<Order> { new(1), new(a) });
        yield return Shape(
            "ADictionary",
            a => new Dictionary<string, decimal> { ["one"] = 1, ["amount"] = a }
        );
    }

    private static TestCaseData Shape(string name, Func<decimal, object> make) =>
        new TestCaseData(make).SetArgDisplayNames(name);

    [Test]
    public void Of_ADifferentRuntimeTypeWithTheSameFields_HashesDifferently()
    {
        StateDigest
            .Of(new Checkout { Payment = new Card { Amount = 20 } })
            .Should()
            .NotBe(StateDigest.Of(new Checkout { Payment = new Voucher { Amount = 20 } }), Adr);
    }

    [Test]
    public void Of_AListWhoseCapacityDiffers_HashesAlike()
    {
        var grown = new List<int>(100) { 1, 2 };
        grown.Add(3);
        grown.RemoveAt(2);

        StateDigest
            .Of(grown)
            .Should()
            .Be(
                StateDigest.Of(new List<int> { 1, 2 }),
                $"{Adr}: a framework collection is its elements, not its capacity or version"
            );
    }

    [Test]
    public void Of_ADictionaryThatComparesKeysAnotherWay_HashesDifferently()
    {
        StateDigest
            .Of(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["a"] = 1 })
            .Should()
            .NotBeNull()
            .And.NotBe(
                StateDigest.Of(new Dictionary<string, int>(StringComparer.Ordinal) { ["a"] = 1 }),
                $"{Adr}: a lookup answers differently under another comparer"
            );
    }

    [Test]
    public void Of_ACycle_GivesNoHash()
    {
        var node = new Node();
        node.Next = node;

        StateDigest.Of(node).Should().BeNull($"{Adr}: a cycle cannot be written in full");
    }

    [Test]
    public void Of_AnObjectSharedTwiceWithoutACycle_Hashes()
    {
        var shared = new Order(20);

        StateDigest.Of((shared, shared)).Should().NotBeNull(Adr);
    }

    [Test]
    public void Of_ALazyLoadingProxyHoldingADelegate_GivesNoHash()
    {
        StateDigest
            .Of(new ProxyOrder { Amount = 20 })
            .Should()
            .BeNull($"{Adr}: what a delegate would load cannot be hashed");
    }

    [Test]
    public void Of_NestingDeeperThanTheLimit_GivesNoHash()
    {
        var head = new Node();
        var at = head;

        for (var i = 0; i < StateDigest.MaxDepth + 5; i++)
            at = at.Next = new Node();

        StateDigest.Of(head).Should().BeNull(Adr);
    }

    [Test]
    public void Of_AFieldWhoseValueCannotBeRead_GivesNoHash()
    {
        StateDigest.Of(new HoldsAType { Kind = typeof(string) }).Should().BeNull(Adr);
        StateDigest.Of(new HoldsAStream()).Should().BeNull(Adr);
    }

    [Test]
    public void Of_IsAHashOfTheState_NotTheState()
    {
        StateDigest.Of(new Customer("refund 2000")).Should().MatchRegex("^[0-9a-f]{64}$", Adr);
    }

    public sealed record Order(decimal Amount);

    public sealed record Customer(string Name);

    public sealed class FieldOrder
    {
        public decimal Amount;
    }

    public abstract class Payment
    {
        public string Kind { get; set; } = "x";
    }

    public sealed class Card : Payment
    {
        public decimal Amount { get; set; }
    }

    public sealed class Voucher : Payment
    {
        public decimal Amount { get; set; }
    }

    public sealed class Checkout
    {
        public Payment Payment { get; set; } = null!;
    }

    public interface IPayment;

    public sealed record CardPayment(decimal Amount) : IPayment;

    public sealed class Wallet
    {
        public IPayment Payment { get; set; } = null!;
    }

    public sealed class IgnoredOrder
    {
        [JsonIgnore]
        public decimal Amount { get; set; }
    }

    public sealed class PrivateOrder(decimal amount)
    {
        private readonly decimal _amount = amount;

        public bool IsLarge() => _amount > 100;
    }

    public sealed class Node
    {
        public Node? Next { get; set; }
    }

    /// <summary>Shaped like an ORM's lazy-loading proxy: a value, and a loader for the rest.</summary>
    public sealed class ProxyOrder
    {
        public decimal Amount { get; set; }

        public Func<object, string, object?> LazyLoader { get; set; } = (_, _) => null;
    }

    public sealed class HoldsAType
    {
        public Type Kind { get; set; } = null!;
    }

    public sealed class HoldsAStream
    {
        public Stream Body { get; set; } = new MemoryStream();
    }
}
