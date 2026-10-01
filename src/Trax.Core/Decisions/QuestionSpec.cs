using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Trax.Core.Utils;

namespace Trax.Core.Decisions;

/// <summary>
/// One typed question as a train declares it: how to ask it, and how to turn the answer into the
/// typed decision Memory holds, refusing an answer that does not fit.
/// </summary>
internal abstract class QuestionSpec
{
    /// <summary>The key the question is asked under, unique within one request.</summary>
    public abstract string Key { get; }

    /// <summary>The enum or marker type the question is about.</summary>
    public abstract Type On { get; }

    /// <summary>The decision type the answer becomes in Memory.</summary>
    public abstract Type DecisionType { get; }

    /// <summary>What is wrong with the declaration, or null.</summary>
    public abstract string? Problem { get; }

    public abstract Question ToQuestion();

    /// <summary>The typed decision for <paramref name="answer"/>.</summary>
    /// <exception cref="InvalidAnswerException">The answer does not fit the question.</exception>
    public abstract object ToDecision(Answer answer);

    /// <summary>Whether two answers reach the same outcome, for comparing a shadow decider.</summary>
    public abstract bool SameOutcome(Answer live, Answer shadow);

    protected static string? Asks(Type type, string? asking) =>
        asking ?? type.GetCustomAttribute<AsksAttribute>()?.Question;

    protected static string MissingQuestion(Type type) =>
        $"asks nothing about '{type.ReadableName()}'. A model sees only the question, never the "
        + "type's name: pass asking: or put [Asks(\"...\")] on the type.";

    protected static void CheckProbability(double value, string what)
    {
        if (double.IsNaN(value) || value is < 0 or > 1)
            throw new InvalidAnswerException(
                $"answered with {what} {Format(value)}, which is not between 0 and 1"
            );
    }

    internal static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>An answer that does not fit its question. The run fails rather than act on it.</summary>
internal sealed class InvalidAnswerException(string message) : Exception(message);

/// <summary>Reads an enum's members, in order of their values, and the description each carries.</summary>
internal static class EnumMembers<T>
    where T : struct, Enum
{
    public static readonly IReadOnlyList<T> Ordered = Enum.GetValues<T>().Distinct().ToList();

    private static readonly Dictionary<string, T> ByName = Enum.GetNames<T>()
        .ToDictionary(n => n, Enum.Parse<T>, StringComparer.Ordinal);

    public static bool TryParse(string name, out T value) => ByName.TryGetValue(name, out value);

    public static string? Description(T value) =>
        typeof(T)
            .GetField(value.ToString(), BindingFlags.Public | BindingFlags.Static)
            ?.GetCustomAttribute<DescriptionAttribute>()
            ?.Description;

    public static int IndexOf(T value) => Ordered.ToList().IndexOf(value);
}

internal sealed class ChoiceSpec<TTrack>(
    string? asking,
    IReadOnlyList<(TTrack Option, string? Description)>? offered = null
) : QuestionSpec
    where TTrack : struct, Enum
{
    private readonly string? _instructions = Asks(typeof(TTrack), asking);

    private readonly IReadOnlyList<(TTrack Option, string? Description)> _offered =
        offered ?? EnumMembers<TTrack>.Ordered.Select(o => (o, (string?)null)).ToList();

    public override string Key => typeof(TTrack).Name;

    public override Type On => typeof(TTrack);

    public override Type DecisionType => typeof(ChoiceDecision<TTrack>);

    public override string? Problem =>
        _instructions is null ? MissingQuestion(typeof(TTrack))
        : _offered.Count == 0 ? $"offers no options for '{typeof(TTrack).ReadableName()}'."
        : null;

    public override Question ToQuestion() =>
        new ChoiceQuestion(
            Key,
            _instructions!,
            _offered
                .Select(o => new Criterion(
                    o.Option.ToString(),
                    o.Description ?? EnumMembers<TTrack>.Description(o.Option)
                ))
                .ToList()
        );

    public override object ToDecision(Answer answer)
    {
        if (answer is not ChoiceAnswer choice)
            throw new InvalidAnswerException(
                $"answered '{Key}' with a {answer.GetType().Name}, not a choice"
            );

        // A name that is not a member cannot be represented, so it is refused. A member that was
        // not offered is kept: routing treats it as a choice it has no track for.
        if (!EnumMembers<TTrack>.TryParse(choice.Choice ?? "", out var chosen))
            throw new InvalidAnswerException(
                $"answered '{Key}' with '{choice.Choice}', which is not one of its options"
            );

        CheckProbability(choice.Confidence, "a confidence of");

        Dictionary<TTrack, double>? probabilities = null;

        if (choice.Probabilities is { } given)
        {
            probabilities = [];

            // Names the question did not offer are ignored: they say nothing about these options.
            foreach (var (name, p) in given)
                if (EnumMembers<TTrack>.TryParse(name, out var option))
                {
                    CheckProbability(p, $"a probability for '{name}' of");
                    probabilities[option] = p;
                }
        }

        return new ChoiceDecision<TTrack>(chosen, choice.Confidence, probabilities, choice.Model);
    }

    public override bool SameOutcome(Answer live, Answer shadow) =>
        live is ChoiceAnswer a && shadow is ChoiceAnswer b && a.Choice == b.Choice;
}

internal sealed class ScoreSpec<TLevel>(string? asking) : QuestionSpec
    where TLevel : struct, Enum
{
    private readonly string? _instructions = Asks(typeof(TLevel), asking);

    private static IReadOnlyList<TLevel> Levels => EnumMembers<TLevel>.Ordered;

    public override string Key => typeof(TLevel).Name;

    public override Type On => typeof(TLevel);

    public override Type DecisionType => typeof(ScoreDecision<TLevel>);

    public override string? Problem =>
        _instructions is null ? MissingQuestion(typeof(TLevel))
        : Levels.Count < 2
            ? $"rates on '{typeof(TLevel).ReadableName()}', which has fewer than two levels."
        : null;

    public override Question ToQuestion() =>
        new ScoreQuestion(
            Key,
            _instructions!,
            Levels
                .Select(l => new Criterion(l.ToString(), EnumMembers<TLevel>.Description(l)))
                .ToList()
        );

    public override object ToDecision(Answer answer)
    {
        if (answer is not ScoreAnswer score)
            throw new InvalidAnswerException(
                $"answered '{Key}' with a {answer.GetType().Name}, not a score"
            );

        var top = Levels.Count - 1;

        if (double.IsNaN(score.Score) || score.Score < 0 || score.Score > top)
            throw new InvalidAnswerException(
                $"scored '{Key}' at {Format(score.Score)}, outside its levels 0 to {top}"
            );

        CheckProbability(score.Confidence, "a confidence of");

        Dictionary<TLevel, double>? probabilities = null;

        if (score.Probabilities is { } given)
        {
            if (given.Count != Levels.Count)
                throw new InvalidAnswerException(
                    $"gave {given.Count} probabilities for '{Key}', which has {Levels.Count} levels"
                );

            probabilities = [];

            for (var i = 0; i < given.Count; i++)
            {
                CheckProbability(given[i], $"a probability for '{Levels[i]}' of");
                probabilities[Levels[i]] = given[i];
            }
        }

        return new ScoreDecision<TLevel>(
            score.Score,
            Levels[Nearest(score.Score)],
            score.Confidence,
            probabilities,
            score.Model
        );
    }

    /// <summary>The level a score rounds to, halves rounding up.</summary>
    internal static int Nearest(double score) => (int)Math.Floor(score + 0.5);

    public override bool SameOutcome(Answer live, Answer shadow) =>
        live is ScoreAnswer a && shadow is ScoreAnswer b && Nearest(a.Score) == Nearest(b.Score);
}

internal sealed class YesNoSpec<TQuestion>(string? asking, string? yes, string? no) : QuestionSpec
{
    private static readonly AsksAttribute? Attribute =
        typeof(TQuestion).GetCustomAttribute<AsksAttribute>();

    private readonly string? _instructions = Asks(typeof(TQuestion), asking);

    public override string Key => typeof(TQuestion).Name;

    public override Type On => typeof(TQuestion);

    public override Type DecisionType => typeof(YesNoDecision<TQuestion>);

    public override string? Problem =>
        _instructions is null ? MissingQuestion(typeof(TQuestion)) : null;

    public override Question ToQuestion() =>
        new YesNoQuestion(Key, _instructions!, yes ?? Attribute?.Yes, no ?? Attribute?.No);

    public override object ToDecision(Answer answer)
    {
        if (answer is not YesNoAnswer yesNo)
            throw new InvalidAnswerException(
                $"answered '{Key}' with a {answer.GetType().Name}, not a yes/no"
            );

        CheckProbability(yesNo.Probability, "a probability of");

        return new YesNoDecision<TQuestion>(yesNo.Probability, yesNo.Model);
    }

    public override bool SameOutcome(Answer live, Answer shadow) =>
        live is YesNoAnswer a
        && shadow is YesNoAnswer b
        && a.Probability >= 0.5 == b.Probability >= 0.5;
}
