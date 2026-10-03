using System.Collections;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using AwesomeAssertions;
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
        StateDigest.Of(new Customer("refund 2000")).Should().MatchRegex("^s1:[0-9a-f]{64}$", Adr);
    }

    private static readonly StateHashKey TestKey = new([
        .. Enumerable.Range(0, 32).Select(i => (byte)i),
    ]);

    /// <summary>
    /// Pins the encoding: a change to it changes these, and every hash a host has recorded with
    /// them. The string-keyed sets and dictionaries are written in an order this process chose at
    /// random, so matching them here also pins that the hash does not depend on it.
    /// </summary>
    [TestCaseSource(nameof(Golden))]
    public void Of_PinnedStates_HashToTheirPinnedValues(object? state, string unkeyed, string keyed)
    {
        StateDigest.Of(state).Should().Be(unkeyed, Adr);
        StateDigest.Of(state, TestKey).Should().Be(keyed, Adr);
    }

    public static IEnumerable<TestCaseData> Golden()
    {
        yield return Pinned(
            "Null",
            null,
            "s1:6e340b9cffb37a989ca544e6bb780a2c78901d3fb33738768511a30617afa01d",
            "k1:e711546e3faad4c7c4aa756bc26cad6abea8241984a0f6b0839c70ca61c4ef88"
        );
        yield return Pinned(
            "AnInt",
            42,
            "s1:b2a2626f1ebcb7d4937d16ee697c025193af4aa5fad7ce07049152d0ebaf8d88",
            "k1:b4a9200aabee5b449912d34d058a6d73b50fa798dbc8e28ea9ceb5f69dfbe833"
        );
        yield return Pinned(
            "AString",
            "refund",
            "s1:b0543f011252a42dfa0606788cf294f3b40cb0700ae712c2c2bd97eb92ba714d",
            "k1:e174679cf0b6d3f0d85f0b71055fe5834c11b5859a4daa1473e6a9e5c91275aa"
        );
        yield return Pinned(
            "ATuple",
            (1, "a", 2.5m),
            "s1:b91a5421fe1cdfabf3898f433617fa541170a05dfc155f5f9102bf0c1eb32082",
            "k1:d4f25ef7a7f3e35908c85408467fb06257ed42723681cb7334d7e3d2b1a2097c"
        );
        yield return Pinned(
            "AList",
            new List<int> { 1, 2, 3 },
            "s1:b61f084dadaeb548bc0152185ade5113b366a40f60b7fd385fc0a7783d0a508d",
            "k1:b2b6d565568c7f530a03bb5433e15e377f602898b95776064c833428a850e7bf"
        );
        yield return Pinned(
            "ADictionary",
            new Dictionary<string, int>
            {
                ["a"] = 1,
                ["b"] = 2,
                ["c"] = 3,
            },
            "s1:4120c1e843459babb3cb5758c2d7268e308f9ff3474c4da12647e0b4f585eb18",
            "k1:75934e5313e5cc9b6dcdaf253306aa66a3e08d5faf95a9dc0eefa1ce9bfdf895"
        );
        yield return Pinned(
            "AnImmutableHashSet",
            ImmutableHashSet.Create(StringComparer.Ordinal, "x", "y", "z", "w"),
            "s1:53ff01d3688aa7646245446244d1b194b817ec1a449c2bc2812e0df06b1a0079",
            "k1:aa335fcdcc1677e8071c26de6cc929bf74670e918e909a981d714239f868c131"
        );
    }

    private static TestCaseData Pinned(string name, object? state, string unkeyed, string keyed) =>
        new TestCaseData(state, unkeyed, keyed).SetArgDisplayNames(name);

    [Test]
    public void Of_KeyedAndUnkeyed_NeverMatch()
    {
        var state = new Order(20);

        StateDigest.Of(state).Should().MatchRegex("^s1:[0-9a-f]{64}$");
        StateDigest.Of(state, TestKey).Should().MatchRegex("^k1:[0-9a-f]{64}$");
        StateDigest
            .Of(state, TestKey)
            .Should()
            .NotBe(StateDigest.Of(state, new StateHashKey(new byte[32])), Adr);
        StateDigest.Of(state, TestKey).Should().Be(StateDigest.Of(new Order(20), TestKey));
    }

    [Test]
    public void StateHashKey_RefusesAShortOrMissingKey_AndCopiesIt()
    {
        FluentActions.Invoking(() => new StateHashKey(null!)).Should().Throw<ArgumentException>();
        FluentActions
            .Invoking(() => new StateHashKey(new byte[31]))
            .Should()
            .Throw<ArgumentException>();

        var bytes = new byte[32];
        var key = new StateHashKey(bytes);
        var before = StateDigest.Of(1, key);
        bytes[0] = 1;

        StateDigest.Of(1, key).Should().Be(before, "the key holds its own copy");
    }

    [Test]
    public void Of_AsManyValuesAsTheCapAllows_Hashes_AndOneMoreGivesNoHash()
    {
        // The list itself is one value, and each element another.
        StateDigest
            .Of(new List<int>(Enumerable.Range(0, StateDigest.MaxValues - 1)))
            .Should()
            .NotBeNull("the documented value cap must be reachable within the byte cap");
        StateDigest
            .Of(new List<int>(Enumerable.Range(0, StateDigest.MaxValues)))
            .Should()
            .BeNull(Adr);
    }

    [Test]
    public void Of_MoreBytesThanTheCap_GivesNoHash()
    {
        StateDigest
            .Of(new string('x', (int)(StateDigest.MaxBytes / 2) - 1024))
            .Should()
            .NotBeNull();
        StateDigest.Of(new string('x', (int)(StateDigest.MaxBytes / 2))).Should().BeNull(Adr);
    }

    [Test]
    public void Of_ASharedGraphThatWouldBeWalkedExponentiallyOften_GivesNoHashQuickly()
    {
        var node = new Fork();

        for (var i = 0; i < 50; i++)
            node = new Fork { Left = node, Right = node };

        var watch = System.Diagnostics.Stopwatch.StartNew();

        StateDigest.Of(node).Should().BeNull($"{Adr}: the value cap bounds the walk");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Test]
    public void Of_AnInlineArray_CoversEveryElement()
    {
        var first = new Four();
        var second = new Four();
        first[3] = 1;
        second[3] = 2;

        StateDigest.Of(first).Should().NotBeNull().And.NotBe(StateDigest.Of(second), Adr);
        StateDigest
            .Of(new HoldsFour { Values = first })
            .Should()
            .NotBe(StateDigest.Of(new HoldsFour { Values = second }));
    }

    [Test]
    public unsafe void Of_AFixedBuffer_CoversEveryElement()
    {
        var first = new Fixed();
        var second = new Fixed();
        first.Values[3] = 1;
        second.Values[3] = 2;

        StateDigest.Of(first).Should().NotBeNull().And.NotBe(StateDigest.Of(second), Adr);
        second.Values[3] = 1;
        StateDigest.Of(first).Should().Be(StateDigest.Of(second));
    }

    [Test]
    public void Of_ImmutableDictionariesThatCompareKeysAnotherWay_HashDifferently()
    {
        StateDigest
            .Of(ImmutableDictionary.Create<string, int>(StringComparer.Ordinal).Add("a", 1))
            .Should()
            .NotBeNull()
            .And.NotBe(
                StateDigest.Of(
                    ImmutableDictionary
                        .Create<string, int>(StringComparer.OrdinalIgnoreCase)
                        .Add("a", 1)
                ),
                Adr
            );
        StateDigest
            .Of(ImmutableHashSet.Create(StringComparer.Ordinal, "a"))
            .Should()
            .NotBe(StateDigest.Of(ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "a")));
        StateDigest
            .Of(new SortedSet<string>(StringComparer.Ordinal) { "a" })
            .Should()
            .NotBe(StateDigest.Of(new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "a" }));
        StateDigest
            .Of(new Dictionary<string, int>(StringComparer.InvariantCulture) { ["a"] = 1 })
            .Should()
            .NotBeNull("a culture comparer is written by its culture")
            .And.NotBe(
                StateDigest.Of(
                    new Dictionary<string, int>(StringComparer.InvariantCultureIgnoreCase)
                    {
                        ["a"] = 1,
                    }
                )
            );
    }

    [Test]
    public void Of_AReadOnlyWrapper_IsWrittenAsWhatItWraps()
    {
        ReadOnlyDictionary<string, int> Wrap(StringComparer comparer)
        {
            var wrapped = new ReadOnlyDictionary<string, int>(
                new Dictionary<string, int>(comparer) { ["a"] = 1 }
            );
            _ = wrapped.Keys; // fills a cache the wrapper keeps
            return wrapped;
        }

        StateDigest
            .Of(Wrap(StringComparer.Ordinal))
            .Should()
            .NotBeNull()
            .And.Be(
                StateDigest.Of(
                    new ReadOnlyDictionary<string, int>(
                        new Dictionary<string, int>(StringComparer.Ordinal) { ["a"] = 1 }
                    )
                )
            )
            .And.NotBe(StateDigest.Of(Wrap(StringComparer.OrdinalIgnoreCase)), Adr);
        StateDigest
            .Of(new List<int> { 1, 2 }.AsReadOnly())
            .Should()
            .NotBe(StateDigest.Of(new List<int> { 1, 3 }.AsReadOnly()));
    }

    [Test]
    public void Of_ANonGenericHashtable_GivesNoHash()
    {
        StateDigest.Of(new Hashtable { ["a"] = 1 }).Should().BeNull(Adr);
    }

    [Test]
    public void Of_AUriSubclass_IsWrittenByItsFields()
    {
        StateDigest
            .Of(new TaggedUri("https://example.com/", "one"))
            .Should()
            .NotBe(StateDigest.Of(new TaggedUri("https://example.com/", "two")), Adr)
            .And.NotBe(StateDigest.Of(new Uri("https://example.com/")));
    }

    [Test]
    public void Of_ArraysThatDifferOnlyInLowerBounds_HashDifferently()
    {
        var zero = Array.CreateInstance(typeof(int), [2, 2], [0, 0]);
        var one = Array.CreateInstance(typeof(int), [2, 2], [1, 0]);

        StateDigest.Of(zero).Should().NotBeNull().And.NotBe(StateDigest.Of(one), Adr);
        StateDigest
            .Of(new int[2, 3])
            .Should()
            .NotBe(StateDigest.Of(new int[3, 2]), "each dimension's length counts");
    }

    [Test]
    public void Of_UnorderedCollectionsFilledInAnotherOrder_HashAlike()
    {
        StateDigest
            .Of(new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 })
            .Should()
            .Be(StateDigest.Of(new Dictionary<string, int> { ["b"] = 2, ["a"] = 1 }), Adr);
        StateDigest
            .Of(new HashSet<object> { new Order(1), new Customer("ann") })
            .Should()
            .Be(
                StateDigest.Of(new HashSet<object> { new Customer("ann"), new Order(1) }),
                "an element's encoding does not depend on which types its siblings wrote first"
            );
        StateDigest
            .Of(ImmutableHashSet.CreateRange(Enumerable.Range(0, 100).Select(i => $"k{i}")))
            .Should()
            .Be(
                StateDigest.Of(
                    ImmutableHashSet.CreateRange(
                        Enumerable.Range(0, 100).Reverse().Select(i => $"k{i}")
                    )
                )
            );
        StateDigest
            .Of(new List<int> { 1, 2 })
            .Should()
            .NotBe(StateDigest.Of(new List<int> { 2, 1 }), "a list's order is part of it");
    }

    [Test]
    public void Identity_NamesTheAssemblyOfTheTypeAndOfItsArguments()
    {
        StateDigest
            .Identity(typeof(List<Order>))
            .Should()
            .Contain("System.Private.CoreLib")
            .And.Contain(typeof(Order).Assembly.GetName().Name!);
    }

    public sealed class Fork
    {
        public Fork? Left { get; set; }
        public Fork? Right { get; set; }
    }

    [InlineArray(4)]
    public struct Four
    {
        private int _element;
    }

    public sealed class HoldsFour
    {
        public Four Values;
    }

    public unsafe struct Fixed
    {
        public fixed int Values[4];
    }

    public sealed class TaggedUri(string uri, string tag) : Uri(uri)
    {
        public string Tag { get; } = tag;
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
