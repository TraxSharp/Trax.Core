using System.Runtime.CompilerServices;
using LanguageExt;
using Trax.Core.Extensions;

namespace Trax.Core.Monad;

/// <summary>
/// One thing wrong with a declared chain.
/// </summary>
/// <param name="StepIndex">Position of the offending step, counting from zero.</param>
/// <param name="Kind">Which chain primitive declared the step.</param>
/// <param name="Junction">The junction the step names, or null for a step that names none.</param>
/// <param name="Reason">What is wrong, phrased for whoever has to fix it.</param>
public readonly record struct ChainFault(
    int StepIndex,
    ChainStepKind Kind,
    Type? Junction,
    string Reason
);

/// <summary>
/// Replays a declared chain over the types Memory would hold, without running anything.
/// </summary>
/// <remarks>
/// Memory is keyed by type, so a chain either does or does not line up, and that is decidable
/// from the declaration alone. A junction whose input never reaches Memory fails at runtime only
/// on the path that reaches it; replayed here, it is a fact about the train that a host can check
/// for every train it has registered before it serves any traffic.
///
/// <para>The replay is conservative in one direction. It knows the types a chain declares, not
/// the concrete types that will flow, so a junction declaring an interface its runtime value
/// implements only incidentally reads as a fault. That is the same blind spot
/// <c>TrainChainAnalyzer</c> has, and the reason a fault is reported rather than assumed
/// fatal.</para>
/// </remarks>
public static class ChainVerification
{
    /// <summary>
    /// Replays <paramref name="chain"/> for a train taking <paramref name="input"/> and producing
    /// <paramref name="output"/>, returning everything that does not line up.
    /// </summary>
    public static IReadOnlyList<ChainFault> Verify(ChainRecorder chain, Type input, Type output)
    {
        var memory = new System.Collections.Generic.HashSet<Type> { typeof(Unit) };
        Remember(memory, input);

        var faults = new List<ChainFault>();
        var steps = chain.Steps;

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];

            if (step.Kind == ChainStepKind.Resolve)
            {
                // A short circuit supplies the return value itself, so a chain carrying one can
                // reach its end without the return type ever entering Memory.
                var shortCircuited = steps.Take(i).Any(s => s.Kind == ChainStepKind.ShortCircuit);

                if (!shortCircuited && !memory.Contains(output))
                    faults.Add(
                        new ChainFault(
                            i,
                            step.Kind,
                            null,
                            $"the chain ends without '{Name(output)}' in Memory, so there is "
                                + "nothing for Resolve to return. Chain a junction that produces it."
                        )
                    );

                continue;
            }

            if (step.In is { } required && !memory.Contains(required))
                faults.Add(
                    new ChainFault(
                        i,
                        step.Kind,
                        step.Junction,
                        $"needs '{Name(required)}' in Memory and nothing before it puts one "
                            + "there. Chain a junction that produces it first."
                    )
                );

            if (step.Out is { } produced)
                Remember(memory, produced);
        }

        return faults;
    }

    /// <summary>
    /// Mirrors what a value entering Memory makes available: its own type and every interface it
    /// implements, with a tuple contributing each of its elements rather than itself.
    /// </summary>
    private static void Remember(System.Collections.Generic.HashSet<Type> memory, Type type)
    {
        if (type.IsTuple())
        {
            foreach (var element in type.GetGenericArguments())
                Remember(memory, element);

            return;
        }

        memory.Add(type);

        foreach (var contract in type.GetInterfaces())
            memory.Add(contract);
    }

    private static string Name(Type type) => type.FullName ?? type.Name;
}
