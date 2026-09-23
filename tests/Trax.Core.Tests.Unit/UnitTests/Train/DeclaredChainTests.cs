using FluentAssertions;
using LanguageExt;
using Trax.Core.Junction;
using Trax.Core.Monad;
using Trax.Core.Train;

namespace Trax.Core.Tests.Unit.UnitTests.Train;

/// <summary>
/// Reading a train's chain without running it.
///
/// <para>A chain is a declaration: a sequence of junction types. Reading it answers every chain
/// call by recording type arguments, so the whole chain can be read for every registered train
/// at host startup without resolving a junction from the container or performing any of the
/// work the chain describes.</para>
/// </summary>
public class DeclaredChainTests : TestSetup
{
    [Test]
    public void DeclaredChain_RecordsEveryStepInOrder()
    {
        var steps = new ThreeStepTrain().DeclaredChain().Steps;

        steps
            .Select(s => (s.Kind, s.Junction))
            .Should()
            .Equal(
                (ChainStepKind.Chain, typeof(StringLength)),
                (ChainStepKind.Chain, typeof(IntToBool)),
                (ChainStepKind.Resolve, null)
            );
    }

    [Test]
    public void DeclaredChain_CarriesTheInputAndOutputTypeOfEachJunction()
    {
        var steps = new ThreeStepTrain().DeclaredChain().Steps;

        steps[0].In.Should().Be(typeof(string));
        steps[0].Out.Should().Be(typeof(int));
        steps[1].In.Should().Be(typeof(int));
        steps[1].Out.Should().Be(typeof(bool));
        steps[2].Out.Should().Be(typeof(bool), "the terminal step names the train's return type");
    }

    [Test]
    public void DeclaredChain_RunsNoJunction()
    {
        CountingJunction.Runs = 0;

        new CountingTrain().DeclaredChain();

        CountingJunction
            .Runs.Should()
            .Be(0, "reading a chain must not perform the work the chain describes");
    }

    [Test]
    public void DeclaredChain_DoesNotNeedTheJunctionToBeConstructible()
    {
        var steps = new UnconstructibleTrain().DeclaredChain().Steps;

        steps
            .Should()
            .Contain(
                s => s.Junction == typeof(NeedsADependency),
                "a chain is read from types alone, so a junction that cannot be constructed here "
                    + "is still declared. Whether it resolves is a separate check the host makes "
                    + "against the container."
            );
    }

    [Test]
    public async Task DeclaredChain_LeavesTheTrainRunnable()
    {
        var train = new ThreeStepTrain();

        train.DeclaredChain();

        (await train.Run("hello"))
            .Should()
            .Be(true, "reading a chain must not leave the train in a declaring state");
    }

    [Test]
    public void DeclaredChain_ShortCircuitAndExtract_AreRecordedToo()
    {
        var steps = new MixedTrain().DeclaredChain().Steps;

        steps
            .Select(s => s.Kind)
            .Should()
            .Equal(ChainStepKind.ShortCircuit, ChainStepKind.Extract, ChainStepKind.Resolve);
    }

    [Test]
    public void DeclaredChain_TheExplicitTypedOverloads_RecordTheirTypeArguments()
    {
        var steps = new ExplicitTypedTrain().DeclaredChain().Steps;

        steps
            .Should()
            .Equal(
                new ChainStep(
                    ChainStepKind.Chain,
                    typeof(StringLength),
                    typeof(string),
                    typeof(int)
                ),
                new ChainStep(ChainStepKind.Chain, typeof(IntToBool), typeof(int), typeof(bool)),
                new ChainStep(ChainStepKind.Resolve, null, null, typeof(bool))
            );
    }

    [Test]
    public void DeclaredChain_AddServices_IsRecordedAsASeedUnderThePassedType()
    {
        var steps = new SeedingTrain().DeclaredChain().Steps;

        steps[0].Should().Be(new ChainStep(ChainStepKind.Seed, null, null, typeof(ICounter)));
        steps[1].Should().Be(new ChainStep(ChainStepKind.Seed, null, null, typeof(int)));
    }

    [Test]
    public void DeclaredChain_AnAsyncBodyThatDoesNotAwaitFirst_IsAnOrdinaryDeclaration()
    {
        var chain = new AsyncWrapperTrain().DeclaredChain();

        chain
            .Refusals.Should()
            .BeEmpty("an async body that awaits only the chain completes at once");
        chain.Steps.Should().HaveCount(3);
    }

    [Test]
    public void DeclaredChain_ABodyThatAwaitsBeforeReturning_IsRefused()
    {
        var chain = new AwaitingTrain().DeclaredChain();

        chain.Refusals.Should().ContainSingle().Which.Should().Contain("awaited something");
    }

    [Test]
    public void DeclaredChain_ABodyThatReturnsAResult_IsRefused()
    {
        var chain = new DirectReturnTrain().DeclaredChain();

        chain.Steps.Should().BeEmpty();
        chain.Refusals.Should().ContainSingle().Which.Should().Contain("returned a result");
    }

    [Test]
    public void DeclaredChain_EndingWithResolveOfAValue_IsRefused()
    {
        var chain = new ValueResolvingTrain().DeclaredChain();

        chain.Refusals.Should().ContainSingle().Which.Should().Contain("Resolve(value)");
    }

    [Test]
    public void DeclaredChain_AnAsyncBodyThatThrows_Rethrows()
    {
        var act = () => new ThrowingAsyncTrain().DeclaredChain();

        act.Should()
            .Throw<InvalidOperationException>(
                "an async body's exception lands in its task, and a clean result would hide it"
            )
            .WithMessage("declaration failed");
    }

    private class StringLength : Junction<string, int>
    {
        public override Task<int> Run(string input) => Task.FromResult(input.Length);
    }

    private class IntToBool : Junction<int, bool>
    {
        public override Task<bool> Run(int input) => Task.FromResult(input > 0);
    }

    private class CountingJunction : Junction<string, int>
    {
        public static int Runs;

        public override Task<int> Run(string input)
        {
            Runs++;
            return Task.FromResult(input.Length);
        }
    }

    private class NeedsADependency(string required) : Junction<string, int>
    {
        public override Task<int> Run(string input) => Task.FromResult(required.Length);
    }

    private record Wrapper(string Value);

    private class ThreeStepTrain : Train<string, bool>
    {
        protected override Task<Either<Exception, bool>> Junctions() =>
            Chain<StringLength>().Chain<IntToBool>().Resolve();
    }

    private class CountingTrain : Train<string, int>
    {
        protected override Task<Either<Exception, int>> Junctions() =>
            Chain<CountingJunction>().Resolve();
    }

    private class UnconstructibleTrain : Train<string, int>
    {
        protected override Task<Either<Exception, int>> Junctions() =>
            Chain<NeedsADependency>().Resolve();
    }

    private class MixedTrain : Train<Wrapper, string>
    {
        protected override Task<Either<Exception, string>> Junctions() =>
            ShortCircuit<WrapperPassthrough>().Extract<Wrapper, string>().Resolve();
    }

    private class WrapperPassthrough : Junction<Wrapper, Wrapper>
    {
        public override Task<Wrapper> Run(Wrapper input) => Task.FromResult(input);
    }

    private interface ICounter;

    private class Counter : ICounter;

    private record Holder(int Count);

    private class ExplicitTypedTrain : Train<string, bool>
    {
        protected override Task<Either<Exception, bool>> Junctions() =>
            Chain<StringLength, string, int>().Chain<IntToBool, int, bool>().Resolve();
    }

    private class SeedingTrain : Train<string, bool>
    {
        protected override Task<Either<Exception, bool>> Junctions() =>
            AddServices<ICounter>(new Counter())
                .Extract<Holder, int>(new Holder(3))
                .Chain<IntToBool>()
                .Resolve();
    }

    private class AsyncWrapperTrain : Train<string, bool>
    {
        protected override async Task<Either<Exception, bool>> Junctions() =>
            await Chain<StringLength>().Chain<IntToBool>().Resolve();
    }

    private class AwaitingTrain : Train<string, bool>
    {
        protected override async Task<Either<Exception, bool>> Junctions()
        {
            await Task.Yield();
            return await Chain<StringLength>().Chain<IntToBool>().Resolve();
        }
    }

    private class DirectReturnTrain : Train<string, bool>
    {
        protected override Task<Either<Exception, bool>> Junctions() =>
            Task.FromResult<Either<Exception, bool>>(true);
    }

    private class ValueResolvingTrain : Train<string, bool>
    {
        protected override Task<Either<Exception, bool>> Junctions() =>
            Chain<StringLength>().Resolve(true);
    }

    private class ThrowingAsyncTrain : Train<string, bool>
    {
        protected override async Task<Either<Exception, bool>> Junctions()
        {
            // An already-completed await keeps the body synchronous, so the throw lands in a
            // finished task rather than propagating out of the call.
            await Task.CompletedTask;
            throw new InvalidOperationException("declaration failed");
        }
    }
}
