namespace Trax.Core.Exceptions;

/// <summary>
/// Thrown when a train's route declaration reads per-execution state.
/// </summary>
/// <remarks>
/// A route is a declaration, not a step of the work. It is read once at startup, with no input
/// and no metadata, so that every train can be checked before the host serves traffic. Reading
/// the input while declaring the route would make the route depend on the value being processed,
/// which means there is no single route to check: the one verified at startup need not be the
/// one that runs.
///
/// Move the work into a junction. A junction receives the input as its argument and is the place
/// per-execution decisions belong.
/// </remarks>
public sealed class ChainDeclarationException(string trainName, string member)
    : Exception(
        $"{trainName} read '{member}' while declaring its route. A route is a declaration and "
            + "cannot depend on the value being processed, because a route that varies by input "
            + $"cannot be verified at startup. Move the work that needs '{member}' into a junction, "
            + "which receives the input as its argument."
    );
