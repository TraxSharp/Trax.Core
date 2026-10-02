using FluentAssertions;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using Trax.Core.Decisions;
using Trax.Core.Exceptions;
using Trax.Core.Junction;
using Trax.Core.Monad;
using Trax.Core.Train;

namespace Trax.Core.Tests.Unit.UnitTests.Train;

/// <summary>
/// What a deciding run does around the decision itself: shadows that must never hold it up,
/// replays that no longer fit, observers that fail, a cancelled run, and how a shadow's agreement
/// is judged against the step's own routing.
/// </summary>
public class DecisionRuntimeTests : TestSetup
{
    /// <summary>Long enough never to be reached by a run that is working, so it only names a hang.</summary>
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(30);

    #region Shadows

    [Test]
    public async Task Shadow_ThatNeverAnswersAndIgnoresCancellation_DoesNotHoldUpTheRun()
    {
        var shadow = new HangingShadow();
        var observer = new RecordingObserver();
        var log = new List<string>();

        var result = await Run(
                t =>
                    t.Decide<string>(q =>
                            q.Choice<Lane>().Shadow<IShadow>().WaitForShadows(TimeSpan.Zero)
                        )
                        .Switch<Lane>(s => Lanes(s, log)),
                new Services()
                    .With<IDecider>(new ScriptedDecider().Choose(Lane.Left))
                    .With<IShadow>(shadow)
                    .With<IDecisionObserver>(observer)
            )
            .WaitAsync(Hang);

        result.IsRight.Should().BeTrue();
        log.Should().Equal("Left");
        shadow.Token.IsCancellationRequested.Should().BeTrue("a shadow not waited for is stopped");
        observer
            .Decisions.Should()
            .ContainSingle()
            .Which.Shadows.Should()
            .ContainSingle()
            .Which.Should()
            .Match<ShadowAnswer>(s =>
                s.Answer == null && !s.Agrees && s.Error!.Contains("did not answer within 0s")
            );
    }

    [Test]
    public async Task Shadow_ThatNeverAnswers_DoesNotHoldUpALiveDeciderThatFails()
    {
        var result = await Run(
                t =>
                    t.Decide<string>(q => q.Choice<Lane>().Shadow<IShadow>())
                        .Switch<Lane>(s => Lanes(s, [])),
                new Services()
                    .With<IDecider>(new ScriptedDecider().Throws(new TimeoutException("down")))
                    .With<IShadow>(new HangingShadow())
            )
            .WaitAsync(Hang);

        Failure(result).Should().BeOfType<TimeoutException>();
    }

    [Test]
    public async Task Shadow_IsNotAskedAQuestionWhoseAnswerIsReplayed()
    {
        var shadow = new ScriptedShadow(new ScriptedDecider().Choose(Lane.Right).YesNo<Flag>(0.9));
        var observer = new RecordingObserver();

        var result = await Run(
            t =>
                t.Decide<string>(q => q.Choice<Lane>().YesNo<Flag>().Shadow<IShadow>())
                    .Switch<Lane>(s => Lanes(s, [])),
            new Services()
                .With<IDecider>(new ScriptedDecider().YesNo<Flag>(0.1))
                .With<IShadow>(shadow)
                .With<IDecisionReplay>(
                    new FixedReplay(QuestionKey.For<Lane>(), new ChoiceAnswer("Left"))
                )
                .With<IDecisionObserver>(observer)
        );

        result.IsRight.Should().BeTrue();
        shadow
            .Inner.Requests.Should()
            .ContainSingle()
            .Which.Questions.Select(q => q.Key)
            .Should()
            .Equal(QuestionKey.For<Flag>());
        observer
            .Decisions.Select(d => (d.Question.Key, d.Replayed, d.Shadows.Count))
            .Should()
            .Equal((QuestionKey.For<Lane>(), true, 0), (QuestionKey.For<Flag>(), false, 1));
    }

    [Test]
    public async Task Shadow_WhenEveryAnswerIsReplayed_IsNotAskedAtAll()
    {
        var shadow = new ScriptedShadow(new ScriptedDecider().Choose(Lane.Right));

        var result = await Run(
            t =>
                t.Decide<string>(q => q.Choice<Lane>().Shadow<IShadow>())
                    .Switch<Lane>(s => Lanes(s, [])),
            new Services()
                .With<IDecider>(new ScriptedDecider())
                .With<IShadow>(shadow)
                .With<IDecisionReplay>(
                    new FixedReplay(QuestionKey.For<Lane>(), new ChoiceAnswer("Left"))
                )
        );

        result.IsRight.Should().BeTrue();
        shadow.Inner.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task Shadow_NobodyRegistered_IsRefusedByTheRunAsTheStartupCheckRefusesIt()
    {
        var failure = Failure(
            await Run(
                t =>
                    t.Decide<string>(q => q.Choice<Lane>().Shadow<IShadow>())
                        .Switch<Lane>(s => Lanes(s, [])),
                new Services().With<IDecider>(new ScriptedDecider().Choose(Lane.Left))
            )
        );

        failure.Message.Should().Contain("needs a shadow decider").And.Contain("IShadow");
        ClassOf(failure).Should().Be(FailureClass.Permanent);
    }

    [Test]
    public void WaitForShadows_ANegativeWait_IsRefused() =>
        ChainVerification
            .Verify(
                new DecidingTrain(
                    t =>
                        t.Decide<string>(q =>
                                q.Choice<Lane>()
                                    .Shadow<IShadow>()
                                    .WaitForShadows(TimeSpan.FromSeconds(-1))
                            )
                            .Switch<Lane>(s => Lanes(s, [])),
                    new Services()
                ).DeclaredChain(),
                typeof(string),
                typeof(bool)
            )
            .Should()
            .Contain(f => f.IsRefusal && f.Reason.Contains("Wait a bounded, non-negative time"));

    #endregion

    #region Shadow agreement follows the step's routing

    [TestCase(0.55, 0.45, true, Description = "both fall in the Unsure band")]
    [TestCase(0.75, 0.65, false, Description = "Yes against Unsure, both above one half")]
    [TestCase(0.2, 0.1, true, Description = "both below the No bar")]
    public async Task ShadowAgreement_OnAGate_IsTakingTheSameTrack(
        double live,
        double shadow,
        bool agrees
    )
    {
        var observer = new RecordingObserver();

        await Run(
            t =>
                t.Gate<string, Flag>(g =>
                        g.Yes(y => y, atLeast: 0.7)
                            .No(n => n, below: 0.3)
                            .Unsure(u => u)
                            .Shadow<IShadow>()
                    )
                    .Chain<StringToBool>(),
            Shadowed(
                new ScriptedDecider().YesNo<Flag>(live),
                new ScriptedDecider().YesNo<Flag>(shadow),
                observer
            )
        );

        observer.Decisions.Single().Shadows.Single().Agrees.Should().Be(agrees);
    }

    [TestCase(0.0, 1.0, true, Description = "Low and Medium share the Low track")]
    [TestCase(1.0, 2.0, false, Description = "Medium takes Low, High takes High")]
    public async Task ShadowAgreement_OnAScale_IsReachingTheSameBand(
        double live,
        double shadow,
        bool agrees
    )
    {
        var observer = new RecordingObserver();

        await Run(
            t =>
                t.Scale<string, Level>(s =>
                        s.AtLeast(Level.Low, l => l).AtLeast(Level.High, h => h).Shadow<IShadow>()
                    )
                    .Chain<StringToBool>(),
            Shadowed(
                new ScriptedDecider().Score<Level>(live),
                new ScriptedDecider().Score<Level>(shadow),
                observer
            )
        );

        observer.Decisions.Single().Shadows.Single().Agrees.Should().Be(agrees);
    }

    [TestCase(0.9, 0.5, false, Description = "the shadow's Left is below Left's own bar")]
    [TestCase(0.5, 0.4, true, Description = "both below the bar, so both go to Otherwise")]
    public async Task ShadowAgreement_OnASwitch_HonoursEachTracksBar(
        double live,
        double shadow,
        bool agrees
    )
    {
        var observer = new RecordingObserver();

        await Run(
            t =>
                t.Switch<string, Lane>(s =>
                        s.When(Lane.Left, l => l, requireConfidence: 0.8)
                            .When(Lane.Right, r => r)
                            .Otherwise(o => o)
                            .Shadow<IShadow>()
                    )
                    .Chain<StringToBool>(),
            Shadowed(
                new ScriptedDecider().Choose(Lane.Left, confidence: live),
                new ScriptedDecider().Choose(Lane.Left, confidence: shadow),
                observer
            )
        );

        observer.Decisions.Single().Shadows.Single().Agrees.Should().Be(agrees);
    }

    [Test]
    public async Task ShadowAgreement_OnAGateWithNoUnsureTrack_InTheBand_IsNotAgreement()
    {
        var observer = new RecordingObserver();

        var result = await Run(
            t =>
                t.Gate<string, Flag>(g =>
                        g.Yes(y => y, atLeast: 0.7).No(n => n, below: 0.3).Shadow<IShadow>()
                    )
                    .Chain<StringToBool>(),
            Shadowed(
                new ScriptedDecider().YesNo<Flag>(0.5),
                new ScriptedDecider().YesNo<Flag>(0.6),
                observer
            )
        );

        result.IsLeft.Should().BeTrue("the live answer has no track to take");
        observer
            .Decisions.Single()
            .Shadows.Single()
            .Agrees.Should()
            .BeFalse("both answers failing the run is not agreeing on a track");
    }

    [Test]
    public async Task ShadowAgreement_OnASwitch_ChoosingAMemberWithNoTrackAndNoOtherwise_IsNotAgreement()
    {
        var observer = new RecordingObserver();

        var result = await Run(
            t =>
                t.Switch<string, Lane>(s => s.When(Lane.Left, l => l).Shadow<IShadow>())
                    .Chain<StringToBool>(),
            Shadowed(
                new ScriptedDecider().Choose(Lane.Right),
                new ScriptedDecider().Choose(Lane.Right),
                observer
            )
        );

        result.IsLeft.Should().BeTrue("Right has no track and there is no Otherwise");
        observer.Decisions.Single().Shadows.Single().Agrees.Should().BeFalse();
    }

    [Test]
    public async Task ShadowAgreement_ForAPlainDecide_ComparesTheAnswers()
    {
        var observer = new RecordingObserver();

        await Run(
            t =>
                t.Decide<string>(q => q.YesNo<Flag>().Shadow<IShadow>())
                    .Gate<Flag>(g => g.Yes(y => y, atLeast: 0.7).No(n => n).Unsure(u => u))
                    .Chain<StringToBool>(),
            Shadowed(
                new ScriptedDecider().YesNo<Flag>(0.55),
                new ScriptedDecider().YesNo<Flag>(0.45),
                observer
            )
        );

        observer
            .Decisions.Single()
            .Shadows.Single()
            .Agrees.Should()
            .BeFalse("a Decide cannot see the Gate after it, so it compares sides of one half");
    }

    [Test]
    public async Task ShadowAgreement_AShadowAnswerThatDoesNotFit_NeverAgrees()
    {
        var observer = new RecordingObserver();

        await Run(
            t =>
                t.Switch<string, Lane>(s =>
                        s.When(Lane.Left, l => l).Otherwise(o => o).Shadow<IShadow>()
                    )
                    .Chain<StringToBool>(),
            Shadowed(
                new ScriptedDecider().Choose(Lane.Left),
                new ScriptedDecider().Answer(
                    QuestionKey.For<Lane>(),
                    _ => new ChoiceAnswer("Banana")
                ),
                observer
            )
        );

        observer.Decisions.Single().Shadows.Single().Agrees.Should().BeFalse();
    }

    [Test]
    public async Task Shadow_OnARoutingStepThatAsksNothing_IsRefused() =>
        Failure(
            await Run(
                t =>
                    t.Decide<string>(q => q.Choice<Lane>())
                        .Switch<Lane>(s => Lanes(s, []).Shadow<IShadow>()),
                Shadowed(
                    new ScriptedDecider().Choose(Lane.Left),
                    new ScriptedDecider(),
                    new RecordingObserver()
                )
            )
        )
            .Message.Should()
            .Contain("routes on a decision made earlier and asks nothing to compare");

    #endregion

    #region Replays that no longer fit

    [TestCaseSource(nameof(StaleReplays))]
    public async Task Replay_ThatNoLongerFits_IsAskedAfresh(Answer recorded, string why)
    {
        var decider = new ScriptedDecider().Choose(Lane.Left);
        var observer = new RecordingObserver();
        var log = new List<string>();

        var result = await Run(
            t =>
                t.Switch<string, Lane>(s =>
                        s.When(Lane.Left, l => l.Chain(new Mark(log, "Left")))
                            .Otherwise(o => o.Chain(new Mark(log, "Otherwise")))
                    )
                    .Chain<StringToBool>(),
            new Services()
                .With<IDecider>(decider)
                .With<IDecisionReplay>(new FixedReplay(QuestionKey.For<Lane>(), recorded))
                .With<IDecisionObserver>(observer)
        );

        result.IsRight.Should().BeTrue();
        log.Should().Equal("Left");
        decider.Requests.Should().ContainSingle();

        var made = observer.Decisions.Should().ContainSingle().Subject;
        made.Replayed.Should().BeFalse();
        made.Decider.Should().Be(typeof(ScriptedDecider));
        made.ReplayRefused.Should().Contain("no longer fits").And.Contain(why);
        made.ReplayRefused.Should().NotContain("the decider");
    }

    private static IEnumerable<TestCaseData> StaleReplays()
    {
        yield return new TestCaseData(
            new ChoiceAnswer("Middle"),
            "'Middle', which is not one of its options"
        ).SetName("Replay_AskedAfresh_AnOptionSinceRemoved");
        yield return new TestCaseData(new YesNoAnswer(0.9), "not a choice").SetName(
            "Replay_AskedAfresh_AnotherKindOfAnswer"
        );
        yield return new TestCaseData(
            new ChoiceAnswer("Right"),
            "'Right', which the question no longer offers"
        ).SetName("Replay_AskedAfresh_AnOptionNoLongerOffered");
    }

    [Test]
    public async Task Replay_AScoreOffTheScaleNow_IsAskedAfresh()
    {
        var observer = new RecordingObserver();

        var result = await Run(
            t => t.Scale<string, Level>(s => s.AtLeast(Level.Low, l => l)).Chain<StringToBool>(),
            new Services()
                .With<IDecider>(new ScriptedDecider().Score<Level>(1))
                .With<IDecisionReplay>(
                    new FixedReplay(QuestionKey.For<Level>(), new ScoreAnswer(4))
                )
                .With<IDecisionObserver>(observer)
        );

        result.IsRight.Should().BeTrue();
        observer
            .Decisions.Single()
            .Should()
            .Match<DecisionMade>(d =>
                !d.Replayed && d.ReplayRefused!.Contains("outside its levels 0 to 2")
            );
    }

    #endregion

    #region Observers

    [Test]
    public async Task Observer_ThatCannotBeResolved_FailsTheStepBeforeTheDeciderIsAsked()
    {
        var decider = new ScriptedDecider().Choose(Lane.Left);

        var failure = Failure(
            await Run(
                t => t.Switch<string, Lane>(s => Lanes(s, [])),
                new Services()
                    .With<IDecider>(decider)
                    .Throwing<IDecisionObserver>(new InvalidOperationException("bad registration"))
            )
        );

        failure.Message.Should().Be("bad registration");
        Data(failure).Junction.Should().Be("Switch<String, Lane>");
        ClassOf(failure).Should().Be(FailureClass.Transient);
        decider.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task Observer_NotRequired_ThatThrows_DoesNotChangeTheRun()
    {
        var log = new List<string>();

        var result = await Run(
            t => t.Switch<string, Lane>(s => Lanes(s, log)),
            new Services()
                .With<IDecider>(new ScriptedDecider().Choose(Lane.Right))
                .With<IDecisionObserver>(
                    new RecordingObserver { Throws = new IOException("disk full") }
                )
        );

        result.IsRight.Should().BeTrue();
        log.Should().Equal("Right");
    }

    [TestCase(false, FailureClass.Transient)]
    [TestCase(true, FailureClass.Permanent)]
    public async Task Observer_Required_ThatFailsToRecordTheDecision_FailsTheStepBeforeAnyTrack(
        bool classified,
        FailureClass expected
    )
    {
        var thrown = new IOException("journal unavailable");

        if (classified)
            thrown.Data["TrainExceptionData"] = new TrainExceptionData
            {
                TrainName = "elsewhere",
                TrainExternalId = "",
                Junction = "elsewhere",
                Type = nameof(IOException),
                Message = thrown.Message,
                FailureClass = FailureClass.Permanent,
            };

        var log = new List<string>();

        var failure = Failure(
            await Run(
                t => t.Switch<string, Lane>(s => Lanes(s, log)),
                new Services()
                    .With<IDecider>(new ScriptedDecider().Choose(Lane.Left))
                    .With<IDecisionObserver>(
                        new RecordingObserver { IsRequired = true, Throws = thrown }
                    )
            )
        );

        failure.Should().BeSameAs(thrown);
        Data(failure).Junction.Should().Be("Switch<String, Lane>");
        ClassOf(failure).Should().Be(expected);
        log.Should().BeEmpty();
    }

    [Test]
    public async Task Observer_Required_ThatFailsToRecordTheRouting_KeepsTheTrainOffTheTrack()
    {
        var log = new List<string>();

        var failure = Failure(
            await Run(
                t => t.Decide<string>(q => q.Choice<Lane>()).Switch<Lane>(s => Lanes(s, log)),
                new Services()
                    .With<IDecider>(new ScriptedDecider().Choose(Lane.Left))
                    .With<IDecisionObserver>(
                        new RecordingObserver
                        {
                            IsRequired = true,
                            ThrowsOnRouted = new IOException("journal unavailable"),
                        }
                    )
            )
        );

        failure.Message.Should().Be("journal unavailable");
        ClassOf(failure).Should().Be(FailureClass.Transient);
        log.Should().BeEmpty();
    }

    [Test]
    public async Task Observer_IsToldHowManyTimesTheRunAskedTheQuestionBefore()
    {
        var observer = new RecordingObserver();
        var replay = new CountingReplay();

        await Run(
            t =>
                t.Decide<string>(q => q.Choice<Lane>())
                    .Decide<string>(q => q.Choice<Lane>())
                    .Switch<Lane>(s => Lanes(s, [])),
            new Services()
                .With<IDecider>(new ScriptedDecider().Choose(Lane.Left))
                .With<IDecisionReplay>(replay)
                .With<IDecisionObserver>(observer)
        );

        observer.Decisions.Select(d => d.Occurrence).Should().Equal(0, 1);
        replay.Occurrences.Should().Equal(0, 1);
    }

    #endregion

    #region Cancellation

    [Test]
    public async Task Cancelled_BeforeASwitchAsksItsQuestion_AsksNothingTellsNobodyAndTakesNoTrack()
    {
        using var cts = new CancellationTokenSource();
        var decider = new ScriptedDecider().Choose(Lane.Left);
        var observer = new RecordingObserver();
        var log = new List<string>();

        var train = new DecidingTrain(
            t => t.Chain(new CancelsRun(cts)).Switch<string, Lane>(s => Lanes(s, log)),
            new Services().With<IDecider>(decider).With<IDecisionObserver>(observer)
        )
        {
            CancellationToken = cts.Token,
        };

        Failure(await train.RunEither("x")).Should().BeAssignableTo<OperationCanceledException>();
        decider.Requests.Should().BeEmpty();
        observer.Decisions.Should().BeEmpty();
        observer.Routings.Should().BeEmpty();
        log.Should().BeEmpty();
    }

    [Test]
    public async Task Cancelled_BeforeARouting_TellsNobodyAndTakesNoTrack()
    {
        using var cts = new CancellationTokenSource();
        var observer = new RecordingObserver();
        var log = new List<string>();

        var train = new DecidingTrain(
            t =>
                t.Decide<string>(q => q.Choice<Lane>())
                    .Chain(new CancelsRun(cts))
                    .Switch<Lane>(s => Lanes(s, log)),
            new Services()
                .With<IDecider>(new ScriptedDecider().Choose(Lane.Left))
                .With<IDecisionObserver>(observer)
        )
        {
            CancellationToken = cts.Token,
        };

        Failure(await train.RunEither("x")).Should().BeAssignableTo<OperationCanceledException>();
        observer.Decisions.Should().ContainSingle();
        observer.Routings.Should().BeEmpty();
        log.Should().BeEmpty();
    }

    [Test]
    public async Task Cancelled_WhileTheDeciderAnswers_PutsNothingInMemoryAndTellsNobody()
    {
        using var cts = new CancellationTokenSource();
        var observer = new RecordingObserver();

        var train = new DecidingTrain(
            t => t.Switch<string, Lane>(s => Lanes(s, [])),
            new Services()
                .With<IDecider>(new CancellingDecider(cts, new ChoiceAnswer("Left")))
                .With<IDecisionObserver>(observer)
        )
        {
            CancellationToken = cts.Token,
        };

        Failure(await train.RunEither("x")).Should().BeAssignableTo<OperationCanceledException>();
        observer.Decisions.Should().BeEmpty();
    }

    #endregion

    #region Verification

    [Test]
    public void Verify_ADecideFromAMissingState_IsReportedOnceNotOncePerQuestion() =>
        ChainVerification
            .Verify(
                new DecidingTrain(
                    t =>
                        t.Decide<int>(q => q.Choice<Lane>().YesNo<Flag>().Score<Level>())
                            .Chain<StringToBool>(),
                    new Services()
                ).DeclaredChain(),
                typeof(string),
                typeof(bool),
                new Registered(typeof(IDecider))
            )
            .Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Contain("decides from 'System.Int32'");

    [Test]
    public void Verify_ADecideWithNoDecider_IsReportedOnceNotOncePerQuestion() =>
        ChainVerification
            .Verify(
                new DecidingTrain(
                    t =>
                        t.Decide<string>(q => q.Choice<Lane>().YesNo<Flag>().Score<Level>())
                            .Chain<StringToBool>(),
                    new Services()
                ).DeclaredChain(),
                typeof(string),
                typeof(bool),
                new Registered()
            )
            .Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Contain("needs a decider");

    #endregion

    #region Fixtures

    private static Exception Failure(Either<Exception, bool> result) =>
        result.IsLeft
            ? result.Swap().ValueUnsafe()
            : throw new AssertionException("the run did not fail");

    private static TrainExceptionData Data(Exception e) =>
        (TrainExceptionData)e.Data["TrainExceptionData"]!;

    private static FailureClass? ClassOf(Exception e) => Data(e).FailureClass;

    private static Task<Either<Exception, bool>> Run(
        Func<MonadTask<string, bool>, MonadTask<string, bool>> chain,
        Services services
    ) => new DecidingTrain(chain, services).RunEither("x");

    private static Services Shadowed(
        ScriptedDecider live,
        ScriptedDecider shadow,
        IDecisionObserver observer
    ) =>
        new Services()
            .With<IDecider>(live)
            .With<IShadow>(new ScriptedShadow(shadow))
            .With<IDecisionObserver>(observer);

    /// <summary>A Left and a Right track, each noting that it ran, then a value to finish on.</summary>
    private static Tracks<string, bool, Lane> Lanes(
        Tracks<string, bool, Lane> tracks,
        List<string> log
    ) =>
        tracks
            .When(Lane.Left, l => l.Chain(new Mark(log, "Left")).Chain<StringToBool>())
            .When(Lane.Right, r => r.Chain(new Mark(log, "Right")).Chain<StringToBool>());

    [Asks("Which lane?")]
    public enum Lane
    {
        Left,
        Right,
    }

    [Asks("How high?")]
    public enum Level
    {
        Low,
        Medium,
        High,
    }

    [Asks("Is the flag up?")]
    public sealed class Flag;

    public interface IShadow : IDecider;

    /// <summary>
    /// A train whose services come from a container, so a test can register anything, including a
    /// registration that throws.
    /// </summary>
    private sealed class DecidingTrain(
        Func<MonadTask<string, bool>, MonadTask<string, bool>> chain,
        IServiceProvider services
    ) : Train<string, bool>
    {
        protected override Task<Either<Exception, bool>> Junctions() =>
            chain(AddServices(services).Chain<Start>()).Resolve();
    }

    private sealed class Services : IServiceProvider
    {
        private readonly Dictionary<Type, Func<object>> _registered = [];

        public Services With<T>(T service)
            where T : class
        {
            _registered[typeof(T)] = () => service;
            return this;
        }

        public Services Throwing<T>(Exception failure)
        {
            _registered[typeof(T)] = () => throw failure;
            return this;
        }

        public object? GetService(Type serviceType) =>
            _registered.TryGetValue(serviceType, out var make) ? make() : null;
    }

    /// <summary>Says which services a container holds, for verification.</summary>
    private sealed class Registered(params Type[] types)
        : Microsoft.Extensions.DependencyInjection.IServiceProviderIsService
    {
        public bool IsService(Type serviceType) => types.Contains(serviceType);
    }

    private sealed class HangingShadow : IShadow
    {
        public CancellationToken Token { get; private set; }

        public Task<DecisionResult> Decide(DecisionRequest request, CancellationToken ct)
        {
            Token = ct;
            return new TaskCompletionSource<DecisionResult>().Task;
        }
    }

    private sealed class ScriptedShadow(ScriptedDecider inner) : IShadow
    {
        public ScriptedDecider Inner => inner;

        public Task<DecisionResult> Decide(DecisionRequest request, CancellationToken ct) =>
            inner.Decide(request, ct);
    }

    /// <summary>Cancels the run while it is being asked, then answers anyway.</summary>
    private sealed class CancellingDecider(CancellationTokenSource cts, Answer answer) : IDecider
    {
        public async Task<DecisionResult> Decide(DecisionRequest request, CancellationToken ct)
        {
            await cts.CancelAsync();
            return new DecisionResult(request.Questions.ToDictionary(q => q.Key, _ => answer));
        }
    }

    private sealed class RecordingObserver : IDecisionObserver
    {
        public List<DecisionMade> Decisions { get; } = [];

        public List<TrackRouted> Routings { get; } = [];

        public bool IsRequired { get; init; }

        public Exception? Throws { get; init; }

        public Exception? ThrowsOnRouted { get; init; }

        public bool Required => IsRequired;

        public Task Decided(DecisionMade decision, CancellationToken cancellationToken)
        {
            if (Throws is not null)
                throw Throws;

            Decisions.Add(decision);
            return Task.CompletedTask;
        }

        public Task Routed(TrackRouted routing, CancellationToken cancellationToken)
        {
            if (ThrowsOnRouted is not null)
                return Task.FromException(ThrowsOnRouted);

            Routings.Add(routing);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedReplay(string key, Answer answer) : IDecisionReplay
    {
        public Answer? Replay(string train, string runId, string question, int occurrence) =>
            question == key && occurrence == 0 ? answer : null;
    }

    private sealed class CountingReplay : IDecisionReplay
    {
        public List<int> Occurrences { get; } = [];

        public Answer? Replay(string train, string runId, string key, int occurrence)
        {
            Occurrences.Add(occurrence);
            return null;
        }
    }

    private class Start : Junction<string, string>
    {
        public override Task<string> Run(string input) => Task.FromResult(input);
    }

    private class StringToBool : Junction<string, bool>
    {
        public override Task<bool> Run(string input) => Task.FromResult(true);
    }

    private class Mark(List<string> log, string name) : Junction<string, string>
    {
        public override Task<string> Run(string input)
        {
            log.Add(name);
            return Task.FromResult(input);
        }
    }

    private class CancelsRun(CancellationTokenSource cts) : Junction<string, string>
    {
        public override async Task<string> Run(string input)
        {
            await cts.CancelAsync();
            return input;
        }
    }

    #endregion
}
