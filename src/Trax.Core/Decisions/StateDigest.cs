using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Trax.Core.Decisions;

/// <summary>
/// The hash of a decision's state that a replay compares (<see cref="DecisionMade.StateHash"/>):
/// SHA-256, as lower-case hex, over a canonical encoding of every instance field of the state's
/// runtime type, and of every value those fields hold, recursively.
/// </summary>
/// <remarks>
/// It reads fields, not what a serializer writes, because an in-process decider reads the whole
/// object: a public field, a private one, a property a serializer ignores, a tuple's items and the
/// members of a derived type held where a base type is declared all count. Two states hash alike
/// only when they are of the same runtime types throughout and hold the same values.
///
/// <para>It fails closed: anything it cannot encode the same way every time gives no hash (null),
/// so nothing recorded for that state is replayed. That covers a reference cycle, nesting deeper
/// than <see cref="MaxDepth"/>, more than <see cref="MaxValues"/> values or
/// <see cref="MaxBytes"/> bytes, a delegate, a pointer or native handle, a type, member or
/// assembly, a stream, wait handle, task or thread, a field whose read throws, and a collection
/// whose enumeration throws. It never throws.</para>
///
/// <para>Some states that are equal in meaning hash differently: a dictionary or set filled in
/// another order, a double that is -0 rather than 0. That only asks the decider again.</para>
/// </remarks>
internal static class StateDigest
{
    internal const int MaxDepth = 64;
    internal const int MaxValues = 1_000_000;
    internal const long MaxBytes = 16 * 1024 * 1024;

    private static readonly ConcurrentDictionary<Type, FieldInfo[]?> Plans = new();

    /// <summary>The hash of <paramref name="state"/>, or null when it cannot be encoded.</summary>
    public static string? Of(object? state)
    {
        try
        {
            using var writer = new Writer();
            return writer.Value(state, 0)
                ? Convert.ToHexStringLower(writer.Hash.GetHashAndReset())
                : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class Writer : IDisposable
    {
        private readonly System.Collections.Generic.HashSet<object> _path = new(
            ReferenceEqualityComparer.Instance
        );

        private long _bytes;
        private int _values;

        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void Dispose() => Hash.Dispose();

        /// <summary>Writes one value, tagged with its runtime type. False when it cannot be.</summary>
        public bool Value(object? value, int depth)
        {
            if (depth > MaxDepth || ++_values > MaxValues)
                return false;

            if (value is null)
                return Byte(0);

            var type = value.GetType();

            if (Refused(type))
                return false;

            Byte(1);
            Text(type.ToString());

            if (Leaf(value, type) is { } leaf)
                return leaf;

            // A value type cannot be on the path back to itself; a reference type can.
            var tracked = !type.IsValueType;

            if (tracked && !_path.Add(value))
                return false;

            try
            {
                return type.IsArray ? Array((Array)value, depth)
                    : Collection(type) ? Elements((IEnumerable)value, type, depth)
                    : Fields(value, type, depth);
            }
            finally
            {
                if (tracked)
                    _path.Remove(value);
            }
        }

        private bool Array(Array array, int depth)
        {
            Int(array.Rank);

            for (var dimension = 0; dimension < array.Rank; dimension++)
                Int(array.GetLength(dimension));

            foreach (var item in array)
                if (!Value(item, depth + 1))
                    return false;

            return true;
        }

        private bool Elements(IEnumerable items, Type type, int depth)
        {
            var count = 0;

            foreach (var item in items)
            {
                Byte(2);
                if (!Value(item, depth + 1))
                    return false;
                count++;
            }

            Byte(3);
            Int(count);

            // A dictionary or set that compares its keys another way answers lookups another way.
            var comparer = type.GetProperty(
                "Comparer",
                BindingFlags.Public | BindingFlags.Instance
            );
            return comparer is null || Value(comparer.GetValue(items), depth + 1);
        }

        private bool Fields(object value, Type type, int depth)
        {
            if (Plans.GetOrAdd(type, Plan) is not { } fields)
                return false;

            Int(fields.Length);

            foreach (var field in fields)
            {
                Text(field.DeclaringType!.ToString());
                Text(field.Name);

                if (!Value(field.GetValue(value), depth + 1))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Writes a value encoded directly rather than by its fields, and says whether it could be,
        /// or returns null for a value that is not one of these.
        /// </summary>
        private bool? Leaf(object value, Type type)
        {
            switch (value)
            {
                case string s:
                    Int(s.Length);
                    return Bytes(MemoryMarshal.AsBytes(s.AsSpan()));
                case bool b:
                    return Byte(b ? (byte)1 : (byte)0);
                case char c:
                    return Long(c);
                case byte n:
                    return Long(n);
                case sbyte n:
                    return Long(n);
                case short n:
                    return Long(n);
                case ushort n:
                    return Long(n);
                case int n:
                    return Long(n);
                case uint n:
                    return Long(n);
                case long n:
                    return Long(n);
                case ulong n:
                    return Long(unchecked((long)n));
                case float f:
                    return Long(BitConverter.SingleToInt32Bits(f));
                case double d:
                    return Long(BitConverter.DoubleToInt64Bits(d));
                case Half h:
                    return Long(BitConverter.HalfToInt16Bits(h));
                case decimal m:
                    foreach (var part in decimal.GetBits(m))
                        Int(part);
                    return true;
                case Int128 n:
                    return Text(n.ToString(CultureInfo.InvariantCulture));
                case UInt128 n:
                    return Text(n.ToString(CultureInfo.InvariantCulture));
                case BigInteger n:
                    return Text(n.ToString(CultureInfo.InvariantCulture));
                case DateTime t:
                    Long(t.Ticks);
                    return Int((int)t.Kind);
                case DateTimeOffset t:
                    Long(t.Ticks);
                    return Long(t.Offset.Ticks);
                case TimeSpan t:
                    return Long(t.Ticks);
                case DateOnly t:
                    return Int(t.DayNumber);
                case TimeOnly t:
                    return Long(t.Ticks);
                case Guid g:
                    return Bytes(g.ToByteArray());
                case Uri u:
                    return Text(u.OriginalString);
            }

            if (type.IsEnum)
                return Value(
                    Convert.ChangeType(
                        value,
                        Enum.GetUnderlyingType(type),
                        CultureInfo.InvariantCulture
                    ),
                    0
                );

            // Any other primitive (an IntPtr, say) is refused before this is reached.
            return null;
        }

        private bool Byte(byte b) => Bytes([b]);

        private bool Int(int n)
        {
            Span<byte> buffer = stackalloc byte[4];
            BitConverter.TryWriteBytes(buffer, n);
            return Bytes(buffer);
        }

        private bool Long(long n)
        {
            Span<byte> buffer = stackalloc byte[8];
            BitConverter.TryWriteBytes(buffer, n);
            return Bytes(buffer);
        }

        private bool Text(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            Int(bytes.Length);
            return Bytes(bytes);
        }

        private bool Bytes(ReadOnlySpan<byte> bytes)
        {
            _bytes += bytes.Length;

            if (_bytes > MaxBytes)
                throw new InvalidOperationException("the state is too large to hash");

            Hash.AppendData(bytes);
            return true;
        }
    }

    /// <summary>
    /// Every instance field of <paramref name="type"/>, from its most basic type to itself and by
    /// name within each, or null when one of them cannot be read the same way every time.
    /// </summary>
    private static FieldInfo[]? Plan(Type type)
    {
        var levels = new List<Type>();

        for (var level = type; level is not null; level = level.BaseType)
            levels.Insert(0, level);

        var fields = new List<FieldInfo>();

        foreach (var level in levels)
        {
            var declared = level
                .GetFields(
                    BindingFlags.Instance
                        | BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly
                )
                .OrderBy(f => f.Name, StringComparer.Ordinal);

            foreach (var field in declared)
            {
                if (field.FieldType.IsPointer || field.FieldType.IsByRefLike)
                    return null;

                fields.Add(field);
            }
        }

        return [.. fields];
    }

    /// <summary>Types whose value cannot be encoded the same way every time, or at all.</summary>
    private static bool Refused(Type type) =>
        type == typeof(IntPtr)
        || type == typeof(UIntPtr)
        || type.IsPointer
        || typeof(Pointer).IsAssignableFrom(type)
        || typeof(Delegate).IsAssignableFrom(type)
        || typeof(MemberInfo).IsAssignableFrom(type)
        || typeof(Assembly).IsAssignableFrom(type)
        || typeof(Module).IsAssignableFrom(type)
        || typeof(Stream).IsAssignableFrom(type)
        || typeof(WaitHandle).IsAssignableFrom(type)
        || typeof(SafeHandle).IsAssignableFrom(type)
        || typeof(Task).IsAssignableFrom(type)
        || typeof(Thread).IsAssignableFrom(type)
        || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
        || type == typeof(ValueTask);

    /// <summary>
    /// A framework collection, written as its elements in enumeration order rather than by its
    /// fields, which hold capacity and version counters that differ between equal collections. A
    /// collection of the state's own is written by its fields, so nothing else it holds is missed.
    /// </summary>
    private static bool Collection(Type type) =>
        typeof(IEnumerable).IsAssignableFrom(type)
        && type.Namespace is { } ns
        && ns.StartsWith("System.Collections", StringComparison.Ordinal)
        && ns != "System.Collections.Specialized"
        && IsFramework(type.Assembly);

    private static bool IsFramework(Assembly assembly) =>
        assembly == typeof(object).Assembly
        || assembly.GetName().Name is { } name
            && (
                name.StartsWith("System.", StringComparison.Ordinal)
                || name == "System"
                || name == "netstandard"
            );
}
