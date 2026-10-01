namespace Trax.Core.Decisions;

/// <summary>
/// Asks a fast decider first, and a slower one only about the questions the first was unsure of.
/// </summary>
/// <remarks>
/// The common shape is a typed decision model in front of a large language model: the model
/// settles most questions in a fraction of the time and cost, and the larger one reasons about the
/// rest. A question is escalated when its choice or score confidence is below
/// <c>escalateBelow</c>, when its probability of yes falls strictly between
/// <c>unsureAbove</c> and <c>unsureBelow</c>, or when the first decider did not answer it.
///
/// <para>The second decider's answer replaces the first's. If the second decider fails, the
/// cascade fails, and so does the run: the first answer was not good enough to act on, which is why
/// it was escalated. A switch's own confidence bars and fallback tracks still apply to whatever
/// answer the cascade returns, which is how a person ends up as the third tier.</para>
/// </remarks>
/// <param name="first">The decider asked first.</param>
/// <param name="then">The decider asked about whatever <paramref name="first"/> was unsure of.</param>
/// <param name="escalateBelow">The choice or score confidence below which a question is escalated.</param>
/// <param name="unsureAbove">The lower edge of the yes/no band that is escalated.</param>
/// <param name="unsureBelow">The upper edge of the yes/no band that is escalated.</param>
public sealed class CascadingDecider(
    IDecider first,
    IDecider then,
    double escalateBelow = 0.8,
    double unsureAbove = 0.2,
    double unsureBelow = 0.8
) : IDecider
{
    /// <inheritdoc />
    public async Task<DecisionResult> Decide(
        DecisionRequest request,
        CancellationToken cancellationToken
    )
    {
        var firstResult = await first.Decide(request, cancellationToken).ConfigureAwait(false);
        var answers = new Dictionary<string, Answer>(
            firstResult?.Answers ?? new Dictionary<string, Answer>()
        );

        var unsure = request
            .Questions.Where(q => !answers.TryGetValue(q.Key, out var a) || IsUnsure(a))
            .ToList();

        if (unsure.Count == 0)
            return new DecisionResult(answers);

        var escalated = await then.Decide(request with { Questions = unsure }, cancellationToken)
            .ConfigureAwait(false);

        foreach (var question in unsure)
            if (escalated?.Answers?.GetValueOrDefault(question.Key) is { } answer)
                answers[question.Key] = answer;
            else
                answers.Remove(question.Key);

        return new DecisionResult(answers);
    }

    private bool IsUnsure(Answer answer) =>
        answer switch
        {
            ChoiceAnswer c => c.Confidence < escalateBelow,
            ScoreAnswer s => s.Confidence < escalateBelow,
            YesNoAnswer y => y.Probability > unsureAbove && y.Probability < unsureBelow,
            _ => true,
        };
}
