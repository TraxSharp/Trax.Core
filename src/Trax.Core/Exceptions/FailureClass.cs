namespace Trax.Core.Exceptions;

/// <summary>
/// What kind of failure a train hit, so a consumer can decide whether to retry, rebase, or give up
/// without matching on exception messages at every call site.
/// </summary>
/// <remarks>
/// Trax supplies the vocabulary; a consumer supplies the matching through <c>IFailureClassifier</c>,
/// because only the consumer knows what its remote system's errors mean.
/// </remarks>
public enum FailureClass
{
    /// <summary>
    /// No classifier was registered, or it did not recognise this failure. Means "decide as you
    /// would have before this existed" — the default, so behaviour is unchanged until something
    /// deliberately acts on a classification.
    /// </summary>
    Unclassified = 0,

    /// <summary>
    /// Expected to succeed if attempted again — a timeout, a throttle, a lost connection.
    /// </summary>
    Transient = 1,

    /// <summary>
    /// Someone else changed the thing first. The work is valid but was built on a state that has
    /// moved, so the answer is usually to rebuild against current state and try again, not to
    /// compensate.
    /// </summary>
    Conflict = 2,

    /// <summary>
    /// Will fail the same way every time — a validation error, a missing record, a rejected
    /// credential. Retrying is waste.
    /// </summary>
    Permanent = 3,
}
