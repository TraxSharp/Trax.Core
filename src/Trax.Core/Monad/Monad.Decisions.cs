using Trax.Core.Decisions;
using Trax.Core.Exceptions;
using Trax.Core.Extensions;
using Trax.Core.Train;
using Trax.Core.Utils;

namespace Trax.Core.Monad;

public partial class Monad<TInput, TReturn>
{
    /// <summary>
    /// How many times each question has been asked in this run, so a replay can tell the first
    /// asking of a question from a later one.
    /// </summary>
    private readonly Dictionary<string, int> _askings = [];

    #region Public API

    /// <summary>
    /// Asks a decider every question in <paramref name="questions"/> about the
    /// <typeparamref name="TState"/> in Memory, in one call, and puts each typed decision in
    /// Memory.
    /// </summary>
    /// <remarks>
    /// An answer that does not fit its question (an option that does not exist, a probability
    /// outside 0 to 1, a score off the scale) fails the run rather than being acted on, as does a
    /// decider that throws. A later <see cref="Switch{TTrack}"/>, <see cref="Gate{TQuestion}"/> or
    /// <see cref="Scale{TLevel}"/> routes on the decisions without asking again.
    /// </remarks>
    public MonadTask<TInput, TReturn> Decide<TState>(
        Func<Questions<TState>, Questions<TState>> questions
    )
    {
        var declared = questions(new Questions<TState>());
        var step = $"Decide<{typeof(TState).ReadableName()}>";

        return Recorder is not null
            ? RecordDecide(declared, step)
            : new(DecideAsync(declared, step));
    }

    /// <summary>
    /// Sends the train down the track for the <see cref="ChoiceDecision{TTrack}"/> already in
    /// Memory, decided by an earlier <see cref="Decide{TState}"/>.
    /// </summary>
    public MonadTask<TInput, TReturn> Switch<TTrack>(
        Func<Tracks<TInput, TReturn, TTrack>, Tracks<TInput, TReturn, TTrack>> tracks
    )
        where TTrack : struct, Enum
    {
        var declared = tracks(new Tracks<TInput, TReturn, TTrack>());
        var step = $"Switch<{typeof(TTrack).ReadableName()}>";

        return Recorder is not null
            ? RecordRouting<TTrack>(
                ChainStepKind.Switch,
                step,
                typeof(ChoiceDecision<TTrack>),
                declared.Set,
                SwitchProblems(declared)
            )
            : new(SwitchAsync(declared, step));
    }

    /// <summary>
    /// Asks the registered decider which member of <typeparamref name="TTrack"/> applies to the
    /// <typeparamref name="TState"/> in Memory, offering only the members this switch has tracks
    /// for, and sends the train down the chosen track.
    /// </summary>
    /// <param name="tracks">Declares the tracks.</param>
    /// <param name="asking">The question. Defaults to the <see cref="AsksAttribute"/> on <typeparamref name="TTrack"/>.</param>
    public MonadTask<TInput, TReturn> Switch<TState, TTrack>(
        Func<Tracks<TInput, TReturn, TTrack>, Tracks<TInput, TReturn, TTrack>> tracks,
        string? asking = null
    )
        where TTrack : struct, Enum
    {
        var declared = tracks(new Tracks<TInput, TReturn, TTrack>());
        var question = new Questions<TState>().Add(
            new ChoiceSpec<TTrack>(asking, declared.Offered)
        );
        var step = $"Switch<{typeof(TState).ReadableName()}, {typeof(TTrack).ReadableName()}>";

        if (Recorder is not null)
        {
            RecordDecide(question, step);
            return RecordRouting<TTrack>(
                ChainStepKind.Switch,
                step,
                typeof(ChoiceDecision<TTrack>),
                declared.Set,
                SwitchProblems(declared)
            );
        }

        return new(AskThen(question, step, m => m.SwitchAsync(declared, step)));
    }

    /// <summary>
    /// Sends the train down the Yes, No or Unsure track for the
    /// <see cref="YesNoDecision{TQuestion}"/> already in Memory.
    /// </summary>
    public MonadTask<TInput, TReturn> Gate<TQuestion>(
        Func<GateTracks<TInput, TReturn>, GateTracks<TInput, TReturn>> gate
    )
    {
        var declared = gate(new GateTracks<TInput, TReturn>());
        var step = $"Gate<{typeof(TQuestion).ReadableName()}>";

        return Recorder is not null
            ? RecordRouting<TQuestion>(
                ChainStepKind.Gate,
                step,
                typeof(YesNoDecision<TQuestion>),
                declared.Set,
                declared.Problems
            )
            : new(GateAsync<TQuestion>(declared, step));
    }

    /// <summary>
    /// Asks the registered decider how likely the answer to <typeparamref name="TQuestion"/> is yes
    /// for the <typeparamref name="TState"/> in Memory, and sends the train down the Yes, No or
    /// Unsure track.
    /// </summary>
    /// <param name="gate">Declares the tracks and the bars between them.</param>
    /// <param name="asking">The question. Defaults to the <see cref="AsksAttribute"/> on <typeparamref name="TQuestion"/>.</param>
    public MonadTask<TInput, TReturn> Gate<TState, TQuestion>(
        Func<GateTracks<TInput, TReturn>, GateTracks<TInput, TReturn>> gate,
        string? asking = null
    )
    {
        var declared = gate(new GateTracks<TInput, TReturn>());
        var question = new Questions<TState>().Add(new YesNoSpec<TQuestion>(asking, null, null));
        var step = $"Gate<{typeof(TState).ReadableName()}, {typeof(TQuestion).ReadableName()}>";

        if (Recorder is not null)
        {
            RecordDecide(question, step);
            return RecordRouting<TQuestion>(
                ChainStepKind.Gate,
                step,
                typeof(YesNoDecision<TQuestion>),
                declared.Set,
                declared.Problems
            );
        }

        return new(AskThen(question, step, m => m.GateAsync<TQuestion>(declared, step)));
    }

    /// <summary>
    /// Sends the train down the track for the level the <see cref="ScoreDecision{TLevel}"/> already
    /// in Memory reaches.
    /// </summary>
    public MonadTask<TInput, TReturn> Scale<TLevel>(
        Func<ScaleTracks<TInput, TReturn, TLevel>, ScaleTracks<TInput, TReturn, TLevel>> scale
    )
        where TLevel : struct, Enum
    {
        var declared = scale(new ScaleTracks<TInput, TReturn, TLevel>());
        var step = $"Scale<{typeof(TLevel).ReadableName()}>";

        return Recorder is not null
            ? RecordRouting<TLevel>(
                ChainStepKind.Scale,
                step,
                typeof(ScoreDecision<TLevel>),
                declared.Set,
                declared.Problems
            )
            : new(ScaleAsync(declared, step));
    }

    /// <summary>
    /// Asks the registered decider where the <typeparamref name="TState"/> in Memory falls on the
    /// levels of <typeparamref name="TLevel"/>, and sends the train down the track for that level.
    /// </summary>
    /// <param name="scale">Declares the tracks.</param>
    /// <param name="asking">The question. Defaults to the <see cref="AsksAttribute"/> on <typeparamref name="TLevel"/>.</param>
    public MonadTask<TInput, TReturn> Scale<TState, TLevel>(
        Func<ScaleTracks<TInput, TReturn, TLevel>, ScaleTracks<TInput, TReturn, TLevel>> scale,
        string? asking = null
    )
        where TLevel : struct, Enum
    {
        var declared = scale(new ScaleTracks<TInput, TReturn, TLevel>());
        var question = new Questions<TState>().Add(new ScoreSpec<TLevel>(asking));
        var step = $"Scale<{typeof(TState).ReadableName()}, {typeof(TLevel).ReadableName()}>";

        if (Recorder is not null)
        {
            RecordDecide(question, step);
            return RecordRouting<TLevel>(
                ChainStepKind.Scale,
                step,
                typeof(ScoreDecision<TLevel>),
                declared.Set,
                declared.Problems
            );
        }

        return new(AskThen(question, step, m => m.ScaleAsync(declared, step)));
    }

    #endregion

    #region Asking

    private async Task<Monad<TInput, TReturn>> AskThen<TState>(
        Questions<TState> question,
        string step,
        Func<Monad<TInput, TReturn>, Task<Monad<TInput, TReturn>>> route
    )
    {
        var monad = await DecideAsync(question, step).ConfigureAwait(false);
        return await route(monad).ConfigureAwait(false);
    }

    private async Task<Monad<TInput, TReturn>> DecideAsync<TState>(
        Questions<TState> questions,
        string step
    )
    {
        if (Exception is not null)
            return this;

        var train = Train.GetType().ReadableName();
        var at = $"{step} (train '{train}')";

        // A declaration the startup check refuses is refused here too, for a host that skips it.
        if (questions.Problems.ToList() is { Count: > 0 } problems)
            return Refuse(step, $"{step} {string.Join(" ", problems)}");

        var state = this.ExtractTypeFromMemory<TState, TInput, TReturn>(missing =>
            $"{at} decides from '{missing.ReadableName()}'"
        );

        if (Exception is not null)
            return this;

        var specs = questions.Specs;
        var answers = new Dictionary<string, Answer>();
        var replayed = new System.Collections.Generic.HashSet<string>();

        try
        {
            var replay = Optional<IDecisionReplay>();

            foreach (var spec in specs)
            {
                var asking = _askings.GetValueOrDefault(spec.Key);
                _askings[spec.Key] = asking + 1;

                if (replay?.Replay(train, Train.ExternalId, spec.Key, asking) is { } earlier)
                {
                    answers[spec.Key] = earlier;
                    replayed.Add(spec.Key);
                }
            }
        }
        catch (Exception e)
        {
            return Failed(e, step);
        }

        var request = new DecisionRequest(
            train,
            state!,
            specs.Select(s => s.ToQuestion()).ToList()
        );

        // Shadows are asked everything, alongside the live decider, and never fail the run.
        var shadows = questions.Shadows.Select(t => AskShadow(t, request)).ToList();

        var pending = specs.Where(s => !replayed.Contains(s.Key)).ToList();
        Type? deciderType = null;

        if (pending.Count > 0)
        {
            var decider =
                MonadExtensions.ExtractTypeFromMemory(
                    this,
                    questions.Decider,
                    missing => $"{at} needs a decider '{missing.ReadableName()}'"
                ) as IDecider;

            if (decider is null)
            {
                await Task.WhenAll(shadows).ConfigureAwait(false);
                return this;
            }

            deciderType = decider.GetType();

            try
            {
                var live = request with
                {
                    Questions = request.Questions.Where(q => !replayed.Contains(q.Key)).ToList(),
                };
                var result = await decider.Decide(live, CancellationToken).ConfigureAwait(false);

                foreach (var spec in pending)
                    if (result?.Answers?.GetValueOrDefault(spec.Key) is { } answer)
                        answers[spec.Key] = answer;
            }
            catch (Exception e)
            {
                await Task.WhenAll(shadows).ConfigureAwait(false);
                return Failed(e, step);
            }
        }

        var shadowResults = await Task.WhenAll(shadows).ConfigureAwait(false);
        var decided = new List<(QuestionSpec Spec, Answer Answer, object Decision)>();

        foreach (var spec in specs)
        {
            if (!answers.TryGetValue(spec.Key, out var answer))
                return Refuse(
                    step,
                    $"{at}: the decider gave no answer to '{spec.Key}', so there is nothing to act on."
                );

            try
            {
                decided.Add((spec, answer, spec.ToDecision(answer)));
            }
            catch (InvalidAnswerException invalid)
            {
                return Refuse(step, $"{at}: the decider {invalid.Message}, so it is not acted on.");
            }
        }

        var observer = Optional<IDecisionObserver>();

        foreach (var (spec, answer, decision) in decided)
        {
            Memory[spec.DecisionType] = decision;

            var compared = shadowResults
                .Select(s =>
                    s.Answers?.GetValueOrDefault(spec.Key) is { } shadow
                        ? new ShadowAnswer(
                            s.Decider,
                            shadow,
                            spec.SameOutcome(answer, shadow),
                            null
                        )
                        : new ShadowAnswer(
                            s.Decider,
                            null,
                            false,
                            s.Error ?? $"gave no answer to '{spec.Key}'"
                        )
                )
                .ToList();

            Observe(
                observer,
                o =>
                    o.Decided(
                        new DecisionMade(
                            train,
                            Train.ExternalId,
                            request.Questions.First(q => q.Key == spec.Key),
                            answer,
                            replayed.Contains(spec.Key) ? null : deciderType,
                            replayed.Contains(spec.Key),
                            compared
                        )
                    )
            );
        }

        return this;
    }

    private sealed record ShadowResult(
        Type Decider,
        IReadOnlyDictionary<string, Answer>? Answers,
        string? Error
    );

    /// <summary>
    /// Asks a shadow decider, turning every way it can fail into a result, so the run never
    /// depends on it.
    /// </summary>
    private async Task<ShadowResult> AskShadow(Type deciderType, DecisionRequest request)
    {
        try
        {
            if (Optional(deciderType) is not IDecider shadow)
                return new(
                    deciderType,
                    null,
                    "is not in Memory or the container, so it was not asked"
                );

            var result = await shadow.Decide(request, CancellationToken).ConfigureAwait(false);
            return new(deciderType, result?.Answers, null);
        }
        catch (Exception e)
        {
            return new(deciderType, null, $"failed: {e.Message}");
        }
    }

    #endregion

    #region Routing

    private static IEnumerable<string> SwitchProblems<TTrack>(
        Tracks<TInput, TReturn, TTrack> tracks
    )
        where TTrack : struct, Enum =>
        tracks.Set.Tracks.Count == 0
            ? tracks.Set.Problems.Append("declares no tracks. Add one with When.")
            : tracks.Set.Problems;

    private Task<Monad<TInput, TReturn>> SwitchAsync<TTrack>(
        Tracks<TInput, TReturn, TTrack> tracks,
        string step
    )
        where TTrack : struct, Enum
    {
        if (Exception is not null)
            return Task.FromResult(this);

        if (SwitchProblems(tracks).ToList() is { Count: > 0 } problems)
            return Task.FromResult(Refuse(step, $"{step} {string.Join(" ", problems)}"));

        var decision = Decision<ChoiceDecision<TTrack>>(step);

        if (decision is null)
            return Task.FromResult(this);

        var chosen = tracks.Set.Tracks.Find(t => t.Name == decision.Choice.ToString());
        var required = chosen?.RequireConfidence ?? tracks.MinimumConfidence;

        var reason =
            chosen is null ? $"the decision was '{decision.Choice}', which has no track here"
            : decision.Confidence < required
                ? $"the decision was '{decision.Choice}' with a confidence of "
                    + $"{QuestionSpec.Format(decision.Confidence)}, below the "
                    + $"{QuestionSpec.Format(required)} its track requires"
            : null;

        return Take<TTrack>(
            step,
            reason is null ? chosen : tracks.Set.Fallback,
            reason,
            $"{reason}, and it declares no Otherwise track to take instead."
        );
    }

    private Task<Monad<TInput, TReturn>> GateAsync<TQuestion>(
        GateTracks<TInput, TReturn> gate,
        string step
    )
    {
        if (Exception is not null)
            return Task.FromResult(this);

        if (gate.Problems.ToList() is { Count: > 0 } problems)
            return Task.FromResult(Refuse(step, $"{step} {string.Join(" ", problems)}"));

        var decision = Decision<YesNoDecision<TQuestion>>(step);

        if (decision is null)
            return Task.FromResult(this);

        var p = decision.Probability;

        // Unsure is a declared outcome, not a decision overruled, so it carries no fallback reason.
        var taken =
            p >= gate.YesAtLeast ? gate.YesTrack
            : p < gate.NoBelow ? gate.NoTrack
            : gate.Set.Fallback;

        return Take<TQuestion>(
            step,
            taken,
            null,
            $"the probability of yes was {QuestionSpec.Format(p)}, between the No bar "
                + $"({QuestionSpec.Format(gate.NoBelow)}) and the Yes bar "
                + $"({QuestionSpec.Format(gate.YesAtLeast)}), and it declares no Unsure track."
        );
    }

    private Task<Monad<TInput, TReturn>> ScaleAsync<TLevel>(
        ScaleTracks<TInput, TReturn, TLevel> scale,
        string step
    )
        where TLevel : struct, Enum
    {
        if (Exception is not null)
            return Task.FromResult(this);

        if (scale.Problems.ToList() is { Count: > 0 } problems)
            return Task.FromResult(Refuse(step, $"{step} {string.Join(" ", problems)}"));

        var decision = Decision<ScoreDecision<TLevel>>(step);

        if (decision is null)
            return Task.FromResult(this);

        var reached = EnumMembers<TLevel>.IndexOf(decision.Nearest);

        var band = scale
            .Set.Tracks.Select(t =>
                (
                    Track: t,
                    Index: EnumMembers<TLevel>.TryParse(t.Name, out var level)
                        ? EnumMembers<TLevel>.IndexOf(level)
                        : int.MaxValue
                )
            )
            .Where(b => b.Index <= reached)
            .MaxBy(b => b.Index)
            .Track;

        var reason =
            decision.Confidence < scale.MinimumConfidence
                ? $"the score {QuestionSpec.Format(decision.Score)} came with a confidence of "
                    + $"{QuestionSpec.Format(decision.Confidence)}, below the "
                    + $"{QuestionSpec.Format(scale.MinimumConfidence)} this scale requires"
                : null;

        return Take<TLevel>(
            step,
            reason is null ? band : scale.Set.Fallback,
            reason,
            $"{reason}, and it declares no Otherwise track to take instead."
        );
    }

    /// <summary>
    /// Records which track is taken and runs it, or fails the run when there is none to take.
    /// </summary>
    private async Task<Monad<TInput, TReturn>> Take<TKey>(
        string step,
        DeclaredTrack<TInput, TReturn>? taken,
        string? fallbackReason,
        string noTrack
    )
    {
        var at = $"{step} (train '{Train.GetType().ReadableName()}')";

        if (taken is null)
            return Refuse(step, $"{at}: {noTrack}");

        Memory[typeof(TrackTaken<TKey>)] = new TrackTaken<TKey>(taken.Name, fallbackReason);

        Observe(
            Optional<IDecisionObserver>(),
            o =>
                o.Routed(
                    new TrackRouted(
                        Train.GetType().ReadableName(),
                        Train.ExternalId,
                        typeof(TKey),
                        taken.Name,
                        fallbackReason
                    )
                )
        );

        return await taken
            .Body(new MonadTask<TInput, TReturn>(Task.FromResult(this)))
            .ConfigureAwait(false);
    }

    private TDecision? Decision<TDecision>(string step)
        where TDecision : class =>
        this.ExtractTypeFromMemory<TDecision, TInput, TReturn>(missing =>
            $"{step} (train '{Train.GetType().ReadableName()}') routes on "
            + $"'{missing.ReadableName()}', which nothing decided"
        );

    #endregion

    #region Recording

    private MonadTask<TInput, TReturn> RecordDecide<TState>(
        Questions<TState> questions,
        string step
    )
    {
        var recorder = Recorder!;

        foreach (var problem in questions.Problems)
            recorder.RefuseStep(ChainStepKind.Decide, questions.Decider, $"{step} {problem}");

        var first = recorder.Steps.Count;

        if (questions.Specs.Count == 0)
            recorder.Record(ChainStepKind.Decide, questions.Decider, typeof(TState), null);

        foreach (var spec in questions.Specs)
            recorder.Record(
                ChainStepKind.Decide,
                questions.Decider,
                typeof(TState),
                spec.DecisionType
            );

        foreach (var shadow in questions.Shadows)
            recorder.RecordRequirement(first, shadow);

        return new MonadTask<TInput, TReturn>(Task.FromResult(this));
    }

    private MonadTask<TInput, TReturn> RecordRouting<TKey>(
        ChainStepKind kind,
        string step,
        Type decision,
        TrackSet<TInput, TReturn> set,
        IEnumerable<string> problems
    )
    {
        var recorder = Recorder!;

        foreach (var problem in problems)
            recorder.RefuseStep(kind, null, $"{step} {problem}");

        var index = recorder.Steps.Count;
        recorder.Record(kind, null, decision, typeof(TrackTaken<TKey>));

        var recorded = new List<ChainTrack>();

        foreach (var track in set.All)
        {
            var steps = RecordTrack(track);

            // A track's refusals are the chain's refusals, carried at the routing step.
            foreach (var refusal in steps.RecordedRefusals)
                recorder.RefuseRecordedStep(
                    index,
                    kind,
                    refusal.Junction,
                    $"{step}, track '{track.Name}': {refusal.Reason}"
                );

            recorded.Add(
                new ChainTrack(track.Name, track.Description, track == set.Fallback, steps)
            );
        }

        recorder.RecordTracks(index, recorded);

        return new MonadTask<TInput, TReturn>(Task.FromResult(this));
    }

    /// <summary>
    /// Records one track's body into a recorder of its own. Recording is synchronous and runs
    /// nothing.
    /// </summary>
    private ChainRecorder RecordTrack(DeclaredTrack<TInput, TReturn> track)
    {
        var steps = new ChainRecorder();
        var outer = Recorder;
        Recorder = steps;

        try
        {
            _ = track.Body(new MonadTask<TInput, TReturn>(Task.FromResult(this)));
        }
        finally
        {
            Recorder = outer;
        }

        return steps;
    }

    #endregion

    #region Helpers

    /// <summary>A service that may be absent: in Memory, then the container, else null.</summary>
    private T? Optional<T>()
        where T : class => Optional(typeof(T)) as T;

    private object? Optional(Type type) =>
        Memory.GetValueOrDefault(type) ?? this.ExtractTypeFromServiceProvider(type);

    private static void Observe(IDecisionObserver? observer, Action<IDecisionObserver> tell)
    {
        if (observer is null)
            return;

        try
        {
            tell(observer);
        }
        catch
        {
            // Recording a decision must never change it, or fail the run that made it.
        }
    }

    /// <summary>
    /// Fails the run for a decision Trax will not act on. Asking again would get the same answer
    /// from the same declaration, so the failure is classified permanent.
    /// </summary>
    private Monad<TInput, TReturn> Refuse(string step, string reason)
    {
        var refusal = new TrainException(reason);
        refusal.Data["TrainExceptionData"] = ExceptionData(refusal, step, FailureClass.Permanent);
        Exception ??= refusal;
        return this;
    }

    /// <summary>
    /// Fails the run with what a decider or replay threw, carrying the step the way a junction's
    /// failure carries its junction. Cancellation passes through unwrapped, as it does there.
    /// </summary>
    private Monad<TInput, TReturn> Failed(Exception e, string step)
    {
        if (!(e is OperationCanceledException && CancellationToken.IsCancellationRequested))
            e.Data["TrainExceptionData"] = ExceptionData(e, step, FailureClassification.Carried(e));

        Exception ??= e;
        return this;
    }

    private TrainExceptionData ExceptionData(Exception e, string step, FailureClass? failure) =>
        new()
        {
            TrainName = Train.GetType().Name,
            TrainExternalId = Train.ExternalId,
            Junction = step,
            Type = e.GetType().Name,
            Message = e.Message,
            StackTrace = e.StackTrace,
            FailureClass = failure,
        };

    #endregion
}
