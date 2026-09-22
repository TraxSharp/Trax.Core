using FluentAssertions;
using LanguageExt;
using Trax.Core.Junction;
using Trax.Core.Monad;
using Trax.Core.Train;

namespace Trax.Core.Tests.Unit.UnitTests.Train;

/// <summary>
/// Replaying a declared chain over the types Memory would hold.
///
/// <para>Memory is keyed by type, so whether a chain lines up is decidable from the declaration
/// alone. These pin the cases a host needs to be right about before it can refuse to start on
/// the strength of them.</para>
/// </summary>
public class ChainVerificationTests : TestSetup
{
    private static IReadOnlyList<ChainFault> Verify<TTrain, TIn, TOut>()
        where TTrain : Train<TIn, TOut>, new() =>
        ChainVerification.Verify(new TTrain().DeclaredChain(), typeof(TIn), typeof(TOut));

    [Test]
    public void Verify_AChainThatLinesUp_ReportsNothing() =>
        Verify<WellFormedTrain, string, bool>().Should().BeEmpty();

    [Test]
    public void Verify_AJunctionWhoseInputNeverReachesMemory_IsReported()
    {
        var faults = Verify<MissingInputTrain, string, bool>();

        faults.Should().HaveCount(1);
        faults[0].Junction.Should().Be(typeof(IntToBool));
        faults[0].StepIndex.Should().Be(0, "the very first junction is the one that cannot run");
        faults[0]
            .Reason.Should()
            .Contain("Int32")
            .And.Contain("Chain a junction that produces it first");
    }

    [Test]
    public void Verify_AChainEndingWithoutItsReturnType_IsReported()
    {
        var faults = Verify<NeverProducesReturnTrain, string, bool>();

        faults.Should().ContainSingle(f => f.Kind == ChainStepKind.Resolve);
        faults
            .Single(f => f.Kind == ChainStepKind.Resolve)
            .Reason.Should()
            .Contain("nothing for Resolve to return");
    }

    [Test]
    public void Verify_AShortCircuitSupplyingTheReturnValue_IsNotReported() =>
        Verify<ShortCircuitTrain, string, bool>()
            .Should()
            .BeEmpty(
                "a short circuit supplies the return value itself, so the chain can end without "
                    + "the return type ever entering Memory"
            );

    [Test]
    public void Verify_ATupleOutput_MakesItsElementsAvailable() =>
        Verify<TupleProducingTrain, string, bool>()
            .Should()
            .BeEmpty("a tuple contributes each of its elements to Memory, not itself");

    [Test]
    public void Verify_AnInterfaceTheInputImplements_CountsAsPresent() =>
        Verify<InterfaceConsumingTrain, Ingredient, bool>()
            .Should()
            .BeEmpty("a value in Memory is reachable by every interface it implements");

    [Test]
    public void Verify_AJunctionTakingATuple_IsSatisfiedByItsElements() =>
        ChainVerification
            .Verify(new TupleConsumingTrain().DeclaredChain(), typeof(string), typeof(bool))
            .Should()
            .BeEmpty(
                "a tuple input is assembled from its elements rather than looked up whole, so "
                    + "each element being present is what satisfies it"
            );

    [Test]
    public void Verify_AJunctionTakingAnInjectedService_IsSatisfiedByTheContainer()
    {
        var chain = new ServiceConsumingTrain().DeclaredChain();

        ChainVerification
            .Verify(chain, typeof(string), typeof(bool))
            .Should()
            .ContainSingle("without the container, an injected input looks like a missing one");

        ChainVerification
            .Verify(chain, typeof(string), typeof(bool), type => type == typeof(IAmbient))
            .Should()
            .BeEmpty("a junction input not in Memory falls back to the container");
    }

    private interface IIngredient;

    private record Ingredient : IIngredient;

    private class StringLength : Junction<string, int>
    {
        public override Task<int> Run(string input) => Task.FromResult(input.Length);
    }

    private class IntToBool : Junction<int, bool>
    {
        public override Task<bool> Run(int input) => Task.FromResult(input > 0);
    }

    private class StringToUnit : Junction<string, LanguageExt.Unit>
    {
        public override Task<LanguageExt.Unit> Run(string input) =>
            Task.FromResult(LanguageExt.Unit.Default);
    }

    private class StringToPair : Junction<string, (int Length, bool Empty)>
    {
        public override Task<(int, bool)> Run(string input) =>
            Task.FromResult((input.Length, input.Length == 0));
    }

    private class IngredientToBool : Junction<IIngredient, bool>
    {
        public override Task<bool> Run(IIngredient input) => Task.FromResult(true);
    }

    private interface IAmbient;

    private class PairToFlag : Junction<(int Length, bool Empty), bool>
    {
        public override Task<bool> Run((int Length, bool Empty) input) =>
            Task.FromResult(!input.Empty);
    }

    private class AmbientToFlag : Junction<IAmbient, bool>
    {
        public override Task<bool> Run(IAmbient input) => Task.FromResult(true);
    }

    private class TupleConsumingTrain : Train<string, bool>
    {
        protected override Task<Either<Exception, bool>> Junctions() =>
            Chain<StringToPair>().Chain<PairToFlag>().Resolve();
    }

    private class ServiceConsumingTrain : Train<string, bool>
    {
        protected override Task<Either<Exception, bool>> Junctions() =>
            Chain<AmbientToFlag>().Resolve();
    }

    private class WellFormedTrain : Train<string, bool>
    {
        protected override Task<Either<Exception, bool>> Junctions() =>
            Chain<StringLength>().Chain<IntToBool>().Resolve();
    }

    private class MissingInputTrain : Train<string, bool>
    {
        // IntToBool needs an int and nothing produces one.
        protected override Task<Either<Exception, bool>> Junctions() =>
            Chain<IntToBool>().Resolve();
    }

    private class NeverProducesReturnTrain : Train<string, bool>
    {
        protected override Task<Either<Exception, bool>> Junctions() =>
            Chain<StringToUnit>().Resolve();
    }

    private class ShortCircuitTrain : Train<string, bool>
    {
        protected override Task<Either<Exception, bool>> Junctions() =>
            ShortCircuit<StringToUnit>().Resolve();
    }

    private class TupleProducingTrain : Train<string, bool>
    {
        protected override Task<Either<Exception, bool>> Junctions() =>
            Chain<StringToPair>().Resolve();
    }

    private class InterfaceConsumingTrain : Train<Ingredient, bool>
    {
        protected override Task<Either<Exception, bool>> Junctions() =>
            Chain<IngredientToBool>().Resolve();
    }
}
