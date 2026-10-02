namespace Trax.Core.Decisions;

/// <summary>
/// The key a question is asked under, which is the full name of the type it is about.
/// </summary>
/// <remarks>
/// A decider finds each answer's question by <see cref="Question.Key"/>, a decision model's adapter
/// sends it as the question's id, and a replay looks answers up by it, so it has to be unique to
/// the type and the same on every run. It is the namespace and every enclosing type, joined by
/// dots, with any generic arguments in square brackets, separated by commas and written the same
/// way: <c>Shop.Orders.Priority</c>, <c>Shop.Orders.Queue.Lane</c> for an enum nested in a class,
/// <c>Shop.Orders.Flag[Shop.Orders.Refund]</c> for a generic marker. It holds nothing but the
/// characters of the type names and <c>.</c>, <c>,</c>, <c>[</c> and <c>]</c>, so it can be used
/// as a JSON property name or a log field as it is.
///
/// <para>Renaming or moving the type changes the key, so a run after the change asks afresh
/// rather than replaying an answer recorded under the old one.</para>
/// </remarks>
public static class QuestionKey
{
    /// <summary>The key of the question about <typeparamref name="T"/>.</summary>
    public static string For<T>() => For(typeof(T));

    /// <summary>The key of the question about <paramref name="type"/>.</summary>
    public static string For(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return Name(type, type.IsGenericType ? type.GetGenericArguments() : []);
    }

    private static string Name(Type type, Type[] arguments)
    {
        if (type.IsArray)
            return For(type.GetElementType()!) + "[]";

        if (type.IsGenericParameter)
            return type.Name;

        var prefix = string.IsNullOrEmpty(type.Namespace) ? "" : type.Namespace + ".";
        var inherited = 0;

        // A type nested in a generic type takes the outer type's arguments ahead of its own.
        if (type.IsNested && type.DeclaringType is { } outer)
        {
            if (outer.IsGenericType)
                inherited = Math.Min(outer.GetGenericArguments().Length, arguments.Length);

            prefix = Name(outer, arguments[..inherited]) + ".";
        }

        var name = type.Name;
        var tick = name.IndexOf('`');

        if (tick >= 0)
            name = name[..tick];

        var own = arguments[inherited..];

        return own.Length == 0
            ? prefix + name
            : $"{prefix}{name}[{string.Join(",", own.Select(For))}]";
    }
}
