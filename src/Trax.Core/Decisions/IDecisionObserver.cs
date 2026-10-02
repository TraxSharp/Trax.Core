namespace Trax.Core.Decisions;

/// <summary>
/// Told about every decision a train makes and every track it takes, so a host can record them
/// against the run.
/// </summary>
/// <remarks>
/// Optional: found in Memory, then in the container, and skipped when neither holds one. It is
/// awaited on the train's path, so it should record and return.
///
/// <para>By default an observer is best effort: whatever it throws is logged and ignored, because
/// recording a decision must never change it. An observer whose record the host depends on, such
/// as one that a later replay reads, sets <see cref="Required"/>, and then a failure to record
/// fails the step before the train acts on the decision, classified as the exception says or as
/// <c>Transient</c> when it says nothing. An observer that cannot even be resolved fails the step
/// either way, because whether it was required cannot be read from an observer that was never
/// built.</para>
/// </remarks>
public interface IDecisionObserver
{
    /// <summary>
    /// True when a failure to record must fail the step rather than be ignored. Defaults to false.
    /// </summary>
    bool Required => false;

    /// <summary>
    /// A question was answered (or replayed), after the answer was checked and before any track
    /// is taken on it.
    /// </summary>
    Task Decided(DecisionMade decision, CancellationToken cancellationToken);

    /// <summary>A routing step is sending the train down a track, before the track runs.</summary>
    Task Routed(TrackRouted routing, CancellationToken cancellationToken);
}

/// <summary>
/// One answered question.
/// </summary>
/// <param name="Train">The train that asked.</param>
/// <param name="RunId">The run's external id, which ties the decision to the run that made it.</param>
/// <param name="Question">The question as it was asked.</param>
/// <param name="Occurrence">
/// How many times this run asked the question before, from 0: the number
/// <see cref="IDecisionReplay.Replay"/> is given to find this answer again.
/// </param>
/// <param name="Answer">The answer the run acts on.</param>
/// <param name="Decider">The decider that answered, or null when the answer was replayed.</param>
/// <param name="Replayed">True when the answer came from an earlier run rather than a decider.</param>
/// <param name="Shadows">
/// What each shadow decider answered to the same question, for comparison only. Empty when the
/// answer was replayed, because shadows are not asked a replayed question.
/// </param>
/// <param name="ReplayRefused">
/// Why an answer recorded for this question by an earlier run was not replayed, so the decider
/// was asked afresh, or null when there was none to refuse.
/// </param>
public sealed record DecisionMade(
    string Train,
    string RunId,
    Question Question,
    int Occurrence,
    Answer Answer,
    Type? Decider,
    bool Replayed,
    IReadOnlyList<ShadowAnswer> Shadows,
    string? ReplayRefused = null
);

/// <summary>
/// A shadow decider's answer, which the run never acts on.
/// </summary>
/// <param name="Decider">The shadow decider.</param>
/// <param name="Answer">Its answer, or null when it failed or did not answer in time.</param>
/// <param name="Agrees">
/// Whether its answer would have done what the live answer did. When the step that asked also
/// routes (<c>Switch</c>, <c>Gate</c> or <c>Scale</c> with a state), that is taking the same
/// track by the step's own bars and bands. For a plain <c>Decide</c>, whose routing comes later,
/// it is the same option, the same nearest level, or the same side of one half for a yes/no.
/// False when it did not answer, answered with something that does not fit the question, or when
/// the step had no track to take on the live answer.
/// </param>
/// <param name="Error">Why it gave no answer, or null when it did.</param>
public sealed record ShadowAnswer(Type Decider, Answer? Answer, bool Agrees, string? Error);

/// <summary>
/// One routing step's outcome.
/// </summary>
/// <param name="Train">The train that was routed.</param>
/// <param name="RunId">The run's external id.</param>
/// <param name="On">The enum or marker type the routing was on.</param>
/// <param name="Track">The track taken.</param>
/// <param name="FallbackReason">Why the decision was not followed, or null when it was.</param>
public sealed record TrackRouted(
    string Train,
    string RunId,
    Type On,
    string Track,
    string? FallbackReason
);

/// <summary>
/// Supplies answers a run already gave, so a run that is repeated takes the same tracks instead of
/// asking again and possibly being answered differently.
/// </summary>
/// <remarks>
/// Optional: found in Memory, then in the container. A host that requeues runs implements it from
/// what it recorded through <see cref="IDecisionObserver"/>, keyed by
/// <see cref="DecisionMade.Occurrence"/>. A replayed answer is checked exactly as a fresh one is;
/// one that no longer fits the question (an option renamed, removed or no longer offered, a scale
/// with fewer levels, a different kind of question) is not acted on, and the decider is asked
/// afresh, with the reason in <see cref="DecisionMade.ReplayRefused"/>. Shadows are not asked a
/// question whose answer is replayed.
/// </remarks>
public interface IDecisionReplay
{
    /// <summary>
    /// The answer to replay for the <paramref name="occurrence"/>th asking (from 0, as
    /// <see cref="DecisionMade.Occurrence"/> numbers it) of the question <paramref name="key"/> in
    /// the run <paramref name="runId"/>, or null to ask the decider.
    /// </summary>
    Answer? Replay(string train, string runId, string key, int occurrence);
}
