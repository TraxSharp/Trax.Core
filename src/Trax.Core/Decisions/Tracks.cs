using Trax.Core.Train;

namespace Trax.Core.Decisions;

/// <summary>
/// The tracks a routing step declares, shared by <see cref="Tracks{TInput, TReturn, TTrack}"/>,
/// <see cref="GateTracks{TInput, TReturn}"/> and <see cref="ScaleTracks{TInput, TReturn, TLevel}"/>.
/// </summary>
internal sealed class TrackSet<TInput, TReturn>
{
    public List<DeclaredTrack<TInput, TReturn>> Tracks { get; } = [];

    public DeclaredTrack<TInput, TReturn>? Fallback { get; private set; }

    public List<string> Problems { get; } = [];

    public void Add(DeclaredTrack<TInput, TReturn> track)
    {
        if (Tracks.Any(t => t.Name == track.Name))
            Problems.Add($"declares the track '{track.Name}' twice. Declare each track once.");

        Tracks.Add(track);
    }

    public void SetFallback(
        string name,
        Func<MonadTask<TInput, TReturn>, MonadTask<TInput, TReturn>> body
    )
    {
        if (Fallback is not null)
            Problems.Add($"declares {name} twice. Declare one.");

        Fallback = new DeclaredTrack<TInput, TReturn>(name, null, body, null);
    }

    public static string? ConfidenceProblem(double? value) =>
        value is { } v && (double.IsNaN(v) || v < 0 || v > 1)
            ? $"requires a confidence of {QuestionSpec.Format(v)}, which is not between 0 and 1. "
                + "Confidence is a probability."
            : null;

    /// <summary>Every track, with the fallback last.</summary>
    public IEnumerable<DeclaredTrack<TInput, TReturn>> All =>
        Fallback is null ? Tracks : Tracks.Append(Fallback);
}

/// <summary>One declared track: its name, what it is for, its body, and its own confidence bar.</summary>
internal sealed record DeclaredTrack<TInput, TReturn>(
    string Name,
    string? Description,
    Func<MonadTask<TInput, TReturn>, MonadTask<TInput, TReturn>> Body,
    double? RequireConfidence
);

/// <summary>
/// Declares the tracks of a <c>Switch</c>: one per <typeparamref name="TTrack"/> member the
/// decision may choose, and optionally an <see cref="Otherwise"/> track for when the decision is
/// not followed.
/// </summary>
/// <remarks>
/// Each track is written the way the rest of the chain is, on the parameter it is handed
/// (<c>t =&gt; t.Chain&lt;IssueRefund&gt;()</c>). Every track is read when the chain is declared and
/// verified at startup; only the chosen one runs. A track may be empty (<c>t =&gt; t</c>).
/// </remarks>
public sealed class Tracks<TInput, TReturn, TTrack>
    where TTrack : struct, Enum
{
    internal Tracks() { }

    internal TrackSet<TInput, TReturn> Set { get; } = new();

    internal double MinimumConfidence { get; private set; }

    /// <summary>
    /// Declares the track the train takes when the decision is <paramref name="track"/>.
    /// </summary>
    /// <param name="track">The member that selects this track.</param>
    /// <param name="then">The junctions this track runs.</param>
    /// <param name="description">
    /// What the track is for, offered to the decider when the switch asks its own question.
    /// Defaults to the member's <see cref="System.ComponentModel.DescriptionAttribute"/>.
    /// </param>
    /// <param name="requireConfidence">
    /// The confidence this track needs, overriding <see cref="RequireConfidence"/>. Give the track
    /// with the gravest consequence the highest bar, and the safest the lowest.
    /// </param>
    public Tracks<TInput, TReturn, TTrack> When(
        TTrack track,
        Func<MonadTask<TInput, TReturn>, MonadTask<TInput, TReturn>> then,
        string? description = null,
        double? requireConfidence = null
    )
    {
        if (TrackSet<TInput, TReturn>.ConfidenceProblem(requireConfidence) is { } problem)
            Set.Problems.Add($"track '{track}' {problem}");

        Set.Add(
            new DeclaredTrack<TInput, TReturn>(
                track.ToString(),
                description,
                then,
                requireConfidence
            )
        );
        return this;
    }

    /// <summary>
    /// Declares where the train goes when the decision chooses a member this switch has no track
    /// for, or chooses one with less confidence than that track requires. Without it, either
    /// case fails the run.
    /// </summary>
    public Tracks<TInput, TReturn, TTrack> Otherwise(
        Func<MonadTask<TInput, TReturn>, MonadTask<TInput, TReturn>> then
    )
    {
        Set.SetFallback("Otherwise", then);
        return this;
    }

    /// <summary>
    /// The confidence, from 0 to 1, below which a choice is not followed, for every track that
    /// does not set its own. Defaults to 0.
    /// </summary>
    public Tracks<TInput, TReturn, TTrack> RequireConfidence(double minimum)
    {
        if (TrackSet<TInput, TReturn>.ConfidenceProblem(minimum) is { } problem)
            Set.Problems.Add(problem);
        else
            MinimumConfidence = minimum;

        return this;
    }

    internal IReadOnlyList<(TTrack, string?)> Offered =>
        Set
            .Tracks.Select(t =>
                (EnumMembers<TTrack>.TryParse(t.Name, out var v) ? v : default, t.Description)
            )
            .ToList();
}

/// <summary>
/// Declares the tracks of a <c>Gate</c>, which routes on the probability that the answer to a
/// yes/no question is yes.
/// </summary>
/// <remarks>
/// A probability at or above the <see cref="Yes"/> bar takes the yes track; one below the
/// <see cref="No"/> bar takes the no track; anything between takes <see cref="Unsure"/>, or fails
/// the run when no unsure track is declared. Both bars default to 0.5, which leaves no band
/// between them.
/// </remarks>
public sealed class GateTracks<TInput, TReturn>
{
    internal GateTracks() { }

    internal TrackSet<TInput, TReturn> Set { get; } = new();

    internal double YesAtLeast { get; private set; } = 0.5;

    internal double NoBelow { get; private set; } = 0.5;

    internal DeclaredTrack<TInput, TReturn>? YesTrack => Set.Tracks.Find(t => t.Name == "Yes");

    internal DeclaredTrack<TInput, TReturn>? NoTrack => Set.Tracks.Find(t => t.Name == "No");

    internal IEnumerable<string> Problems
    {
        get
        {
            foreach (var problem in Set.Problems)
                yield return problem;

            if (YesTrack is null)
                yield return "declares no Yes track. Declare one, even if empty.";

            if (NoTrack is null)
                yield return "declares no No track. Declare one, even if empty.";

            if (NoBelow > YesAtLeast)
                yield return $"takes No below {QuestionSpec.Format(NoBelow)}, above where it takes "
                    + $"Yes ({QuestionSpec.Format(YesAtLeast)}), so some probabilities would take "
                    + "both.";
        }
    }

    /// <summary>The track taken when the probability of yes is at least <paramref name="atLeast"/>.</summary>
    public GateTracks<TInput, TReturn> Yes(
        Func<MonadTask<TInput, TReturn>, MonadTask<TInput, TReturn>> then,
        double atLeast = 0.5
    )
    {
        if (double.IsNaN(atLeast) || atLeast <= 0 || atLeast > 1)
            Set.Problems.Add(
                $"takes Yes at {QuestionSpec.Format(atLeast)}, which is not above 0 and at most 1."
            );
        else
            YesAtLeast = atLeast;

        Set.Add(new DeclaredTrack<TInput, TReturn>("Yes", null, then, null));
        return this;
    }

    /// <summary>The track taken when the probability of yes is below <paramref name="below"/>.</summary>
    public GateTracks<TInput, TReturn> No(
        Func<MonadTask<TInput, TReturn>, MonadTask<TInput, TReturn>> then,
        double below = 0.5
    )
    {
        if (double.IsNaN(below) || below < 0 || below >= 1)
            Set.Problems.Add(
                $"takes No below {QuestionSpec.Format(below)}, which is not at least 0 and "
                    + "below 1."
            );
        else
            NoBelow = below;

        Set.Add(new DeclaredTrack<TInput, TReturn>("No", null, then, null));
        return this;
    }

    /// <summary>The track taken when the probability falls between the No and Yes bars.</summary>
    public GateTracks<TInput, TReturn> Unsure(
        Func<MonadTask<TInput, TReturn>, MonadTask<TInput, TReturn>> then
    )
    {
        Set.SetFallback("Unsure", then);
        return this;
    }
}

/// <summary>
/// Declares the tracks of a <c>Scale</c>, which routes on where the state falls on the ordered
/// levels of <typeparamref name="TLevel"/>.
/// </summary>
/// <remarks>
/// The score rounds to its nearest level, and the train takes the track declared for the highest
/// level at or below it. The lowest level must have a track, so every score has one.
/// </remarks>
public sealed class ScaleTracks<TInput, TReturn, TLevel>
    where TLevel : struct, Enum
{
    internal ScaleTracks() { }

    internal TrackSet<TInput, TReturn> Set { get; } = new();

    internal double MinimumConfidence { get; private set; }

    internal IEnumerable<string> Problems =>
        Set.Problems.Concat(
            Set.Tracks.Any(t => t.Name == EnumMembers<TLevel>.Ordered[0].ToString())
                ? []
                :
                [
                    $"has no track from its lowest level, '{EnumMembers<TLevel>.Ordered[0]}', so "
                        + "a low score has nowhere to go. Declare AtLeast for it.",
                ]
        );

    /// <summary>
    /// Declares the track for a score that rounds to <paramref name="level"/> or above, up to the
    /// next level with a track of its own.
    /// </summary>
    public ScaleTracks<TInput, TReturn, TLevel> AtLeast(
        TLevel level,
        Func<MonadTask<TInput, TReturn>, MonadTask<TInput, TReturn>> then
    )
    {
        Set.Add(new DeclaredTrack<TInput, TReturn>(level.ToString(), null, then, null));
        return this;
    }

    /// <summary>
    /// Declares where the train goes when the score's confidence is below
    /// <see cref="RequireConfidence"/>. Without it, that fails the run.
    /// </summary>
    public ScaleTracks<TInput, TReturn, TLevel> Otherwise(
        Func<MonadTask<TInput, TReturn>, MonadTask<TInput, TReturn>> then
    )
    {
        Set.SetFallback("Otherwise", then);
        return this;
    }

    /// <summary>The confidence, from 0 to 1, below which the score is not followed. Defaults to 0.</summary>
    public ScaleTracks<TInput, TReturn, TLevel> RequireConfidence(double minimum)
    {
        if (TrackSet<TInput, TReturn>.ConfidenceProblem(minimum) is { } problem)
            Set.Problems.Add(problem);
        else
            MinimumConfidence = minimum;

        return this;
    }
}
