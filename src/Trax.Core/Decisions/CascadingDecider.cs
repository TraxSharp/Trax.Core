using Microsoft.Extensions.Logging;
using Trax.Core.Utils;

namespace Trax.Core.Decisions;

/// <summary>
/// Asks a fast decider first, and a slower one only about the questions the first was unsure of.
/// </summary>
/// <remarks>
/// The common shape is a typed decision model in front of a large language model: the model
/// settles most questions in a fraction of the time and cost, and the larger one reasons about the
/// rest. A question is escalated when its choice or score confidence is below
/// <c>escalateBelow</c> (or is not a number), when its probability of yes falls strictly between
/// <c>unsureAbove</c> and <c>unsureBelow</c> (or is not a number), or when the first decider did
/// not answer it.
///
/// <para>If the first decider fails, every question is escalated: the first tier being down is
/// what the second one is for. Cancellation is the exception, and passes straight through. The
/// second decider's answer replaces the first's. If the second decider fails, the cascade fails,
/// and so does the run: the first answer was not good enough to act on, which is why it was
/// escalated. A switch's own confidence bars and fallback tracks still apply to whatever answer
/// the cascade returns, which is how a person ends up as the third tier.</para>
/// </remarks>
public sealed class CascadingDecider : IDecider
{
    private readonly IDecider _first;
    private readonly IDecider _then;
    private readonly double _escalateBelow;
    private readonly double _unsureAbove;
    private readonly double _unsureBelow;
    private readonly ILogger? _logger;

    /// <param name="first">The decider asked first.</param>
    /// <param name="then">The decider asked about whatever <paramref name="first"/> was unsure of.</param>
    /// <param name="escalateBelow">The choice or score confidence below which a question is escalated, from 0 to 1.</param>
    /// <param name="unsureAbove">The lower edge of the yes/no band that is escalated, from 0 to 1.</param>
    /// <param name="unsureBelow">The upper edge of the yes/no band that is escalated, from 0 to 1 and not below <paramref name="unsureAbove"/>.</param>
    /// <param name="logger">Told when the first decider fails and every question is escalated.</param>
    /// <exception cref="ArgumentException">A bound is not a probability, or the yes/no band is inverted.</exception>
    public CascadingDecider(
        IDecider first,
        IDecider then,
        double escalateBelow = 0.8,
        double unsureAbove = 0.2,
        double unsureBelow = 0.8,
        ILogger? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(then);
        CheckProbability(escalateBelow, nameof(escalateBelow));
        CheckProbability(unsureAbove, nameof(unsureAbove));
        CheckProbability(unsureBelow, nameof(unsureBelow));

        if (unsureAbove > unsureBelow)
            throw new ArgumentException(
                $"The yes/no band to escalate runs from {QuestionSpec.Format(unsureAbove)} up to "
                    + $"{QuestionSpec.Format(unsureBelow)}, which is backwards. Give the lower edge "
                    + "as unsureAbove.",
                nameof(unsureBelow)
            );

        _first = first;
        _then = then;
        _escalateBelow = escalateBelow;
        _unsureAbove = unsureAbove;
        _unsureBelow = unsureBelow;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DecisionResult> Decide(
        DecisionRequest request,
        CancellationToken cancellationToken
    )
    {
        var answers = new Dictionary<string, Answer>();

        try
        {
            var firstResult = await _first.Decide(request, cancellationToken).ConfigureAwait(false);

            if (firstResult?.Answers is { } given)
                foreach (var (key, answer) in given)
                    answers[key] = answer;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // Every question goes to the second decider, as if the first had answered none.
            answers.Clear();
            _logger?.LogWarning(
                e,
                "{Decider} failed, so every question in {Train}'s request goes to {Then}",
                _first.GetType().ReadableName(),
                request.Train,
                _then.GetType().ReadableName()
            );
        }

        var unsure = request
            .Questions.Where(q => !answers.TryGetValue(q.Key, out var a) || IsUnsure(a))
            .ToList();

        if (unsure.Count == 0)
            return new DecisionResult(answers);

        var escalated = await _then
            .Decide(request with { Questions = unsure }, cancellationToken)
            .ConfigureAwait(false);

        foreach (var question in unsure)
            if (escalated?.Answers?.GetValueOrDefault(question.Key) is { } answer)
                answers[question.Key] = answer;
            else
                answers.Remove(question.Key);

        return new DecisionResult(answers);
    }

    // Written so that NaN, which compares false with everything, counts as unsure.
    private bool IsUnsure(Answer answer) =>
        answer switch
        {
            ChoiceAnswer c => !(c.Confidence >= _escalateBelow),
            ScoreAnswer s => !(s.Confidence >= _escalateBelow),
            YesNoAnswer y => !(y.Probability <= _unsureAbove || y.Probability >= _unsureBelow),
            _ => true,
        };

    private static void CheckProbability(double value, string name)
    {
        if (double.IsNaN(value) || value is < 0 or > 1)
            throw new ArgumentException(
                $"{name} is {QuestionSpec.Format(value)}, which is not between 0 and 1.",
                name
            );
    }
}
