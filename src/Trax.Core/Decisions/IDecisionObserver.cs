namespace Trax.Core.Decisions;

/// <summary>
/// Told about every decision a train makes and every track it takes, so a host can record them
/// against the run.
/// </summary>
/// <remarks>
/// Optional: found in Memory, then in the container, and skipped when neither holds one. It is
/// called synchronously on the train's path, so it should record and return. Whatever it throws
/// is ignored, because recording a decision must never change it.
/// </remarks>
public interface IDecisionObserver
{
    /// <summary>A question was answered (or replayed), after the answer was checked.</summary>
    void Decided(DecisionMade decision);

    /// <summary>A routing step sent the train down a track.</summary>
    void Routed(TrackRouted routing);
}

/// <summary>
/// One answered question.
/// </summary>
/// <param name="Train">The train that asked.</param>
/// <param name="RunId">The run's external id, which ties the decision to the run that made it.</param>
/// <param name="Question">The question as it was asked.</param>
/// <param name="Answer">The answer the run acts on.</param>
/// <param name="Decider">The decider that answered, or null when the answer was replayed.</param>
/// <param name="Replayed">True when the answer came from an earlier run rather than a decider.</param>
/// <param name="Shadows">What each shadow decider answered to the same question, for comparison only.</param>
public sealed record DecisionMade(
    string Train,
    string RunId,
    Question Question,
    Answer Answer,
    Type? Decider,
    bool Replayed,
    IReadOnlyList<ShadowAnswer> Shadows
);

/// <summary>
/// A shadow decider's answer, which the run never acts on.
/// </summary>
/// <param name="Decider">The shadow decider.</param>
/// <param name="Answer">Its answer, or null when it failed or did not answer.</param>
/// <param name="Agrees">
/// Whether it reached the same outcome: the same option, the same nearest level, or the same side
/// of one half for a yes/no. False when it did not answer.
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
/// what it recorded through <see cref="IDecisionObserver"/>. A replayed answer is checked exactly as
/// a fresh one is.
/// </remarks>
public interface IDecisionReplay
{
    /// <summary>
    /// The answer to replay for the <paramref name="occurrence"/>th asking (from 0) of the
    /// question <paramref name="key"/> in the run <paramref name="runId"/>, or null to ask the
    /// decider.
    /// </summary>
    Answer? Replay(string train, string runId, string key, int occurrence);
}
