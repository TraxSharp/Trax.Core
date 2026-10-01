using Trax.Core.Utils;

namespace Trax.Core.Decisions;

/// <summary>
/// Declares the questions a <c>Decide</c> step asks about one <typeparamref name="TState"/>, all in
/// one call to the decider.
/// </summary>
/// <remarks>
/// Each answer becomes a typed decision in Memory (<see cref="ChoiceDecision{TTrack}"/>,
/// <see cref="ScoreDecision{TLevel}"/> or <see cref="YesNoDecision{TQuestion}"/>) that a later
/// <c>Switch</c>, <c>Scale</c> or <c>Gate</c> routes on, or that a junction reads.
/// </remarks>
public sealed class Questions<TState>
{
    internal Questions() { }

    private readonly List<QuestionSpec> _specs = [];

    private readonly List<Type> _shadows = [];

    private readonly List<string> _problems = [];

    internal IReadOnlyList<QuestionSpec> Specs => _specs;

    internal IReadOnlyList<Type> Shadows => _shadows;

    internal Type Decider { get; private set; } = typeof(IDecider);

    private bool _deciderNamed;

    /// <summary>What is wrong with the declaration, phrased for whoever has to fix it.</summary>
    internal IEnumerable<string> Problems =>
        _problems
            .Concat(_specs.Select(s => s.Problem).OfType<string>())
            .Concat(_specs.Count == 0 ? ["asks no questions. Add one."] : []);

    /// <summary>Asks which member of <typeparamref name="TTrack"/> applies.</summary>
    /// <param name="asking">The question. Defaults to the <see cref="AsksAttribute"/> on <typeparamref name="TTrack"/>.</param>
    public Questions<TState> Choice<TTrack>(string? asking = null)
        where TTrack : struct, Enum => Add(new ChoiceSpec<TTrack>(asking));

    /// <summary>
    /// Asks where the state falls on the levels of <typeparamref name="TLevel"/>, lowest value
    /// first.
    /// </summary>
    /// <param name="asking">The question. Defaults to the <see cref="AsksAttribute"/> on <typeparamref name="TLevel"/>.</param>
    public Questions<TState> Score<TLevel>(string? asking = null)
        where TLevel : struct, Enum => Add(new ScoreSpec<TLevel>(asking));

    /// <summary>Asks how likely the answer to <typeparamref name="TQuestion"/> is yes.</summary>
    /// <param name="asking">The question. Defaults to the <see cref="AsksAttribute"/> on <typeparamref name="TQuestion"/>.</param>
    /// <param name="yes">What a yes means. Defaults to the attribute's.</param>
    /// <param name="no">What a no means. Defaults to the attribute's.</param>
    public Questions<TState> YesNo<TQuestion>(
        string? asking = null,
        string? yes = null,
        string? no = null
    ) => Add(new YesNoSpec<TQuestion>(asking, yes, no));

    /// <summary>
    /// Asks <typeparamref name="TDecider"/> instead of the registered <see cref="IDecider"/>.
    /// </summary>
    public Questions<TState> DecidedBy<TDecider>()
        where TDecider : class, IDecider
    {
        if (_deciderNamed)
            _problems.Add("names DecidedBy twice. One decider answers a Decide.");

        _deciderNamed = true;
        Decider = typeof(TDecider);
        return this;
    }

    /// <summary>
    /// Also asks <typeparamref name="TDecider"/> every question and records whether it agrees,
    /// without acting on its answers. A shadow that fails or disagrees never changes the run.
    /// </summary>
    public Questions<TState> Shadow<TDecider>()
        where TDecider : class, IDecider
    {
        if (_shadows.Contains(typeof(TDecider)))
            _problems.Add(
                $"shadows with '{typeof(TDecider).ReadableName()}' twice. Name each shadow once."
            );

        _shadows.Add(typeof(TDecider));
        return this;
    }

    internal Questions<TState> Add(QuestionSpec spec)
    {
        if (_specs.Any(s => s.Key == spec.Key))
            _problems.Add(
                $"asks about '{spec.On.ReadableName()}' twice. Ask each question once; its "
                    + "decision stays in Memory for every step after it."
            );

        _specs.Add(spec);
        return this;
    }
}
