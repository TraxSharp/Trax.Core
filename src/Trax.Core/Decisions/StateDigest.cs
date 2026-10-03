using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Trax.Core.Decisions;

/// <summary>
/// The hash of a decision's state that a replay compares (<see cref="DecisionMade.StateHash"/>),
/// over a canonical encoding of every instance field of the state's runtime type, and of every
/// value those fields hold, recursively. Under a <see cref="StateHashKey"/> it is an HMAC-SHA256
/// written <c>k1:</c> and lower-case hex; without one it is a SHA-256 written <c>s1:</c> and
/// lower-case hex. The two never compare equal.
/// </summary>
/// <remarks>
/// It reads fields, not what a serializer writes, because an in-process decider reads the whole
/// object: a public field, a private one, a property a serializer ignores, a tuple's items, every
/// element of an inline array or fixed buffer, and the members of a derived type held where a base
/// type is declared all count. Two states hash alike only when they are of the same runtime types
/// throughout (named with their assemblies) and hold the same values. It covers the state's value
/// only: what a decider looks up elsewhere is not part of it.
///
/// <para>A framework collection is written as its elements rather than its fields. One whose
/// order is not part of what it means (a dictionary, a hash set, a bag) is written with its
/// elements sorted by their encoding, so the same contents hash alike however they were added;
/// any other (a list, an array, a sorted or ordered collection) is written in order. Its comparers
/// are written with it, because a collection that compares keys another way answers lookups
/// another way. A read-only wrapper is written as what it wraps.</para>
///
/// <para>It fails closed: anything it cannot encode the same way every time gives no hash (null),
/// so nothing recorded for that state is replayed. That covers a reference cycle, nesting deeper
/// than <see cref="MaxDepth"/>, more than <see cref="MaxValues"/> values or
/// <see cref="MaxBytes"/> bytes of encoding, a delegate, a pointer or native handle, a type,
/// member or assembly, a stream, wait handle, task or thread, a non-generic hashtable, a field
/// whose read throws, and a collection whose enumeration throws. It never throws and never runs
/// the state's own code: it reads fields, and calls only framework collections.</para>
///
/// <para>Some states that are equal in meaning hash differently, such as a double that is -0
/// rather than 0. That only asks the decider again.</para>
/// </remarks>
internal static class StateDigest
{
    internal const int MaxDepth = 64;
    internal const int MaxValues = 1_000_000;
    internal const long MaxBytes = 16 * 1024 * 1024;

    internal const string KeyedPrefix = "k1:";
    internal const string UnkeyedPrefix = "s1:";

    /// <summary>
    /// How each type is written, worked out once per type. Weakly keyed, so a type from a
    /// collectible assembly can still be unloaded.
    /// </summary>
    private static readonly ConditionalWeakTable<Type, TypePlan> Plans = new();

    /// <summary>
    /// The hash of <paramref name="state"/>, keyed when <paramref name="key"/> is given, or null
    /// when it cannot be encoded.
    /// </summary>
    public static string? Of(object? state, StateHashKey? key = null)
    {
        try
        {
            using var hash = key is null
                ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                : IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key.Key);

            var writer = new Writer(hash);

            if (!writer.Value(state, 0))
                return null;

            writer.Finish();

            return (key is null ? UnkeyedPrefix : KeyedPrefix)
                + Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch
        {
            return null;
        }
    }

    private enum Kind : byte
    {
        Refused,
        Leaf,
        Enum,
        Array,
        InlineArray,
        Collection,
        Wrapper,
        Fields,
    }

    /// <summary>How values of one runtime type are written.</summary>
    private sealed class TypePlan
    {
        public Kind Kind { get; init; }

        /// <summary>
        /// The type's identity and shape, written the first time a hash meets the type; after
        /// that the hash writes the type's index in its own table.
        /// </summary>
        public byte[] Header { get; init; } = [];

        public TypeCode EnumCode { get; init; }

        /// <summary>The fields written after the value itself: all of them for a plain object.</summary>
        public FieldPlan[] Fields { get; init; } = [];

        public bool Unordered { get; init; }

        public PropertyInfo[] Comparers { get; init; } = [];

        /// <summary>What a read-only wrapper wraps.</summary>
        public PropertyInfo? Inner { get; init; }

        /// <summary>Every element of an inline array.</summary>
        public Func<object, object?[]>? Spread { get; init; }
    }

    /// <summary>One field, and for a fixed buffer, how to read every element of it.</summary>
    private sealed record FieldPlan(FieldInfo Field, Func<object, object?[]>? Spread);

    private sealed class Writer(IncrementalHash hash)
    {
        private const int Chunk = 64 * 1024;

        private readonly System.Collections.Generic.HashSet<object> _path = new(
            ReferenceEqualityComparer.Instance
        );

        private readonly Dictionary<Type, TypePlan> _plans = [];

        // The types this hash has written in full, by the index it writes for them after that.
        private readonly Dictionary<Type, int> _types = [];
        private readonly List<Type> _introduced = [];

        private readonly ArrayBufferWriter<byte> _root = new(Chunk);

        // One scratch buffer per level of unordered collection being written.
        private readonly List<ArrayBufferWriter<byte>> _scratch = [];
        private int _level;

        private long _bytes;
        private int _values;

        public void Finish()
        {
            hash.AppendData(_root.WrittenSpan);
            _root.ResetWrittenCount();
        }

        /// <summary>Writes one value, tagged with its runtime type. False when it cannot be.</summary>
        public bool Value(object? value, int depth)
        {
            if (depth > MaxDepth || ++_values > MaxValues)
                return false;

            if (value is null)
            {
                Byte(0);
                return true;
            }

            var type = value.GetType();
            var plan = PlanOf(type);

            if (plan.Kind == Kind.Refused)
                return false;

            Tag(type, plan);

            switch (plan.Kind)
            {
                case Kind.Leaf:
                    return Leaf(value);
                case Kind.Enum:
                    return Enum(value, plan.EnumCode);
            }

            // A value type cannot be on the path back to itself; a reference type can.
            var tracked = !type.IsValueType;

            if (tracked && !_path.Add(value))
                return false;

            try
            {
                return plan.Kind switch
                {
                    Kind.Array => Array((Array)value, depth),
                    Kind.InlineArray => Elements(plan.Spread!(value), depth),
                    Kind.Wrapper => Value(plan.Inner!.GetValue(value), depth + 1)
                        && Fields(value, plan, depth),
                    Kind.Collection => Collection((IEnumerable)value, plan, depth)
                        && Fields(value, plan, depth),
                    _ => Fields(value, plan, depth),
                };
            }
            finally
            {
                if (tracked)
                    _path.Remove(value);
            }
        }

        private TypePlan PlanOf(Type type)
        {
            if (!_plans.TryGetValue(type, out var plan))
                _plans[type] = plan = Plans.GetValue(type, Build);

            return plan;
        }

        /// <summary>
        /// The type in full the first time this hash meets it, and its index after that, so a
        /// large state pays for each type's name once.
        /// </summary>
        private void Tag(Type type, TypePlan plan)
        {
            if (_types.TryGetValue(type, out var index))
            {
                Byte(2);
                Int(index);
                return;
            }

            _types[type] = _introduced.Count;
            _introduced.Add(type);
            Byte(1);
            Bytes(plan.Header);
        }

        private bool Array(Array array, int depth)
        {
            Int(array.Rank);

            for (var dimension = 0; dimension < array.Rank; dimension++)
            {
                Int(array.GetLength(dimension));
                Int(array.GetLowerBound(dimension));
            }

            foreach (var item in array)
                if (!Value(item, depth + 1))
                    return false;

            return true;
        }

        private bool Elements(object?[] elements, int depth)
        {
            Int(elements.Length);

            foreach (var element in elements)
                if (!Value(element, depth + 1))
                    return false;

            return true;
        }

        private bool Collection(IEnumerable items, TypePlan plan, int depth)
        {
            if (!(plan.Unordered ? Unordered(items, depth) : Ordered(items, depth)))
                return false;

            // A collection that compares its keys another way answers lookups another way.
            foreach (var comparer in plan.Comparers)
                if (!Value(comparer.GetValue(items), depth + 1))
                    return false;

            return true;
        }

        private bool Ordered(IEnumerable items, int depth)
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
            return true;
        }

        /// <summary>
        /// Each element encoded on its own, then all of them in order of their encoding, so the
        /// order they were added in, or the order this process happens to enumerate them in, does
        /// not count. An element's encoding depends on nothing written by its siblings: a type
        /// first met inside one element is forgotten again before the next.
        /// </summary>
        private bool Unordered(IEnumerable items, int depth)
        {
            if (_scratch.Count == _level)
                _scratch.Add(new ArrayBufferWriter<byte>());

            var scratch = _scratch[_level];
            var encoded = new List<byte[]>();

            foreach (var item in items)
            {
                var known = _introduced.Count;
                scratch.ResetWrittenCount();
                _level++;

                bool written;

                try
                {
                    written = Value(item, depth + 1);
                }
                finally
                {
                    _level--;
                    Forget(known);
                }

                if (!written)
                    return false;

                encoded.Add(scratch.WrittenSpan.ToArray());
            }

            scratch.ResetWrittenCount();
            encoded.Sort(static (a, b) => a.AsSpan().SequenceCompareTo(b));

            Int(encoded.Count);

            // Already counted against the size cap as each was written.
            foreach (var element in encoded)
            {
                Int(element.Length);
                Put(element);
            }

            return true;
        }

        private void Forget(int known)
        {
            for (var i = known; i < _introduced.Count; i++)
                _types.Remove(_introduced[i]);

            _introduced.RemoveRange(known, _introduced.Count - known);
        }

        private bool Fields(object value, TypePlan plan, int depth)
        {
            foreach (var (field, spread) in plan.Fields)
            {
                var held = field.GetValue(value);

                if (spread is null ? !Value(held, depth + 1) : !Elements(spread(held!), depth))
                    return false;
            }

            return true;
        }

        /// <summary>Writes a value encoded directly rather than by its fields.</summary>
        private bool Leaf(object value)
        {
            switch (value)
            {
                case string s:
                    Int(s.Length);
                    Bytes(MemoryMarshal.AsBytes(s.AsSpan()));
                    return true;
                case bool b:
                    Byte(b ? (byte)1 : (byte)0);
                    return true;
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
                    Span<int> parts = stackalloc int[4];
                    decimal.GetBits(m, parts);
                    foreach (var part in parts)
                        Int(part);
                    return true;
                case Int128 n:
                    Long(unchecked((long)(ulong)(n >> 64)));
                    return Long(unchecked((long)(ulong)n));
                case UInt128 n:
                    Long(unchecked((long)(ulong)(n >> 64)));
                    return Long(unchecked((long)(ulong)n));
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
                    Span<byte> guid = stackalloc byte[16];
                    g.TryWriteBytes(guid);
                    Bytes(guid);
                    return true;
                case Uri u:
                    return Text(u.OriginalString);
                case CultureInfo c:
                    return Text(c.Name);
                case CompareInfo c:
                    return Text(c.Name);
            }

            // Nothing else is planned as a leaf.
            return false;
        }

        private bool Enum(object value, TypeCode code) =>
            code switch
            {
                TypeCode.Byte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64 => Long(
                    unchecked((long)Convert.ToUInt64(value, CultureInfo.InvariantCulture))
                ),
                _ => Long(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            };

        private void Byte(byte b) => Bytes([b]);

        private bool Int(int n)
        {
            Span<byte> buffer = stackalloc byte[4];
            BitConverter.TryWriteBytes(buffer, n);
            Bytes(buffer);
            return true;
        }

        private bool Long(long n)
        {
            Span<byte> buffer = stackalloc byte[8];
            BitConverter.TryWriteBytes(buffer, n);
            Bytes(buffer);
            return true;
        }

        private bool Text(string s)
        {
            var length = Encoding.UTF8.GetByteCount(s);
            Int(length);
            Span<byte> buffer = length <= 256 ? stackalloc byte[length] : new byte[length];
            Encoding.UTF8.GetBytes(s, buffer);
            Bytes(buffer);
            return true;
        }

        /// <summary>Writes bytes, counted against <see cref="MaxBytes"/>.</summary>
        private void Bytes(ReadOnlySpan<byte> bytes)
        {
            _bytes += bytes.Length;

            if (_bytes > MaxBytes)
                throw new InvalidOperationException("the state is too large to hash");

            Put(bytes);
        }

        /// <summary>Writes bytes to the element being encoded, or to the hash.</summary>
        private void Put(ReadOnlySpan<byte> bytes)
        {
            if (_level > 0)
            {
                _scratch[_level - 1].Write(bytes);
                return;
            }

            _root.Write(bytes);

            if (_root.WrittenCount >= Chunk)
            {
                hash.AppendData(_root.WrittenSpan);
                _root.ResetWrittenCount();
            }
        }
    }

    private static readonly TypePlan Refused = new() { Kind = Kind.Refused };

    private static TypePlan Build(Type type)
    {
        try
        {
            return Plan(type);
        }
        catch
        {
            return Refused;
        }
    }

    private static TypePlan Plan(Type type)
    {
        if (IsRefused(type))
            return Refused;

        if (IsLeaf(type))
            return new TypePlan { Kind = Kind.Leaf, Header = Header(type, Kind.Leaf) };

        if (type.IsEnum)
            return new TypePlan
            {
                Kind = Kind.Enum,
                Header = Header(type, Kind.Enum),
                EnumCode = Type.GetTypeCode(System.Enum.GetUnderlyingType(type)),
            };

        if (type.IsArray)
            return new TypePlan { Kind = Kind.Array, Header = Header(type, Kind.Array) };

        if (type.GetCustomAttribute<InlineArrayAttribute>() is { } inline)
        {
            var element = type.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                )
                .Single()
                .FieldType;

            if (element.IsPointer || element.IsByRefLike)
                return Refused;

            return new TypePlan
            {
                Kind = Kind.InlineArray,
                Header = Header(type, Kind.InlineArray, length: inline.Length),
                Spread = Spreader(type, element, inline.Length),
            };
        }

        var collection = FrameworkCollection(type);

        if (collection is not null && Wrapped(collection) is { } inner)
            return Fields(type, collection, Kind.Wrapper, inner: inner);

        if (collection is not null)
            return Fields(type, collection, Kind.Collection);

        return Fields(type, null, Kind.Fields);
    }

    /// <summary>
    /// The plan of a type written by its fields: all of them, or, for a type that is or derives
    /// from a framework collection, only those declared below that collection.
    /// </summary>
    private static TypePlan Fields(
        Type type,
        Type? collection,
        Kind kind,
        PropertyInfo? inner = null
    )
    {
        var levels = new List<Type>();

        for (var level = type; level is not null && level != collection; level = level.BaseType)
            levels.Insert(0, level);

        var fields = new List<FieldPlan>();

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
                if (field.GetCustomAttribute<FixedBufferAttribute>() is { } buffer)
                {
                    fields.Add(
                        new FieldPlan(
                            field,
                            Spreader(field.FieldType, buffer.ElementType, buffer.Length)
                        )
                    );
                    continue;
                }

                if (field.FieldType.IsPointer || field.FieldType.IsByRefLike)
                    return Refused;

                fields.Add(new FieldPlan(field, null));
            }
        }

        var unordered =
            collection is not null && kind == Kind.Collection && IsUnordered(collection);
        var comparers =
            collection is not null && kind == Kind.Collection
                ? ComparerNames
                    .Select(name =>
                        collection.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                    )
                    .Where(p => p is not null && p.GetIndexParameters().Length == 0)
                    .Select(p => p!)
                    .ToArray()
                : [];

        return new TypePlan
        {
            Kind = kind,
            Header = Header(
                type,
                kind,
                fields: fields,
                unordered: unordered,
                comparers: comparers,
                collection: collection
            ),
            Fields = [.. fields],
            Unordered = unordered,
            Comparers = comparers,
            Inner = inner,
        };
    }

    private static readonly string[] ComparerNames = ["Comparer", "KeyComparer", "ValueComparer"];

    /// <summary>
    /// What identifies a type and how it is written: its name with its assembly's (and its type
    /// arguments', each with theirs), how it is written, and the fields it is written with, so two
    /// types or two versions of one that differ in shape never share an encoding.
    /// </summary>
    private static byte[] Header(
        Type type,
        Kind kind,
        int length = 0,
        IReadOnlyList<FieldPlan>? fields = null,
        bool unordered = false,
        PropertyInfo[]? comparers = null,
        Type? collection = null
    )
    {
        var header = new ArrayBufferWriter<byte>();

        void Write(ReadOnlySpan<byte> bytes) => header.Write(bytes);

        void Int(int n)
        {
            Span<byte> buffer = stackalloc byte[4];
            BitConverter.TryWriteBytes(buffer, n);
            Write(buffer);
        }

        void Text(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            Int(bytes.Length);
            Write(bytes);
        }

        Text(Identity(type));
        Write([(byte)kind]);

        switch (kind)
        {
            case Kind.InlineArray:
                Int(length);
                break;
            case Kind.Collection:
            case Kind.Wrapper:
            case Kind.Fields:
                Text(collection is null ? "" : Identity(collection));
                Write([unordered ? (byte)1 : (byte)0]);
                Int(comparers?.Length ?? 0);
                foreach (var comparer in comparers ?? [])
                    Text(comparer.Name);

                Int(fields?.Count ?? 0);
                foreach (var (field, spread) in fields ?? [])
                {
                    Text(Identity(field.DeclaringType!));
                    Text(field.Name);
                    Write([spread is null ? (byte)0 : (byte)1]);
                }
                break;
        }

        return header.WrittenSpan.ToArray();
    }

    /// <summary>
    /// A type's name, qualified by its assembly's simple name, and its type arguments', so
    /// same-named types from two assemblies differ while a new build of the same assembly does not.
    /// </summary>
    internal static string Identity(Type type)
    {
        if (type.IsArray)
        {
            var element = Identity(type.GetElementType()!);
            return type.IsSZArray ? $"[{element}][]"
                : type.GetArrayRank() == 1 ? $"[{element}][*]"
                : $"[{element}][{new string(',', type.GetArrayRank() - 1)}]";
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
            return Identity(type.GetGenericTypeDefinition())
                + "["
                + string.Join(",", type.GetGenericArguments().Select(a => $"[{Identity(a)}]"))
                + "]";

        return $"{type.FullName ?? type.Name}, {type.Assembly.GetName().Name}";
    }

    /// <summary>
    /// Reads every element of an inline array or fixed buffer, which reflection sees as one field.
    /// </summary>
    private static Func<object, object?[]> Spreader(Type buffer, Type element, int length) =>
        (Func<object, object?[]>)
            typeof(StateDigest)
                .GetMethod(nameof(Spread), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(buffer, element)
                .Invoke(null, [length])!;

    private static Func<object, object?[]> Spread<TBuffer, TElement>(int length)
        where TBuffer : struct
    {
        if ((long)length * Unsafe.SizeOf<TElement>() > Unsafe.SizeOf<TBuffer>())
            throw new InvalidOperationException("the buffer is smaller than its declared length");

        return boxed =>
        {
            var buffer = (TBuffer)boxed;
            var elements = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<TBuffer, TElement>(ref buffer),
                length
            );
            var spread = new object?[length];

            for (var i = 0; i < length; i++)
                spread[i] = elements[i];

            return spread;
        };
    }

    /// <summary>
    /// Types whose value cannot be encoded the same way every time, or at all. A non-generic
    /// hashtable holds its entries where their hash codes put them, which differs from process to
    /// process, and its comparer cannot be read without running it.
    /// </summary>
    private static bool IsRefused(Type type) =>
        type == typeof(IntPtr)
        || type == typeof(UIntPtr)
        || type.IsPointer
        || type.IsByRefLike
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
        || typeof(Hashtable).IsAssignableFrom(type)
        || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
        || type == typeof(ValueTask)
        || type.IsDefined(typeof(UnsafeValueTypeAttribute), inherit: false);

    /// <summary>
    /// Values written directly. Only these exact types: a subclass of <see cref="Uri"/> or
    /// <see cref="CultureInfo"/> may hold more than its name, so it is written by its fields.
    /// </summary>
    private static bool IsLeaf(Type type) =>
        type.IsPrimitive
        || type == typeof(string)
        || type == typeof(decimal)
        || type == typeof(Half)
        || type == typeof(Int128)
        || type == typeof(UInt128)
        || type == typeof(BigInteger)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(TimeSpan)
        || type == typeof(DateOnly)
        || type == typeof(TimeOnly)
        || type == typeof(Guid)
        || type == typeof(Uri)
        || type == typeof(CultureInfo)
        || type == typeof(CompareInfo);

    /// <summary>
    /// The framework collection <paramref name="type"/> is, or derives from, written as its
    /// elements rather than its fields, which hold capacity and version counters that differ
    /// between equal collections. Null for anything else, which is written by its fields.
    /// </summary>
    private static Type? FrameworkCollection(Type type)
    {
        for (var level = type; level is not null; level = level.BaseType)
            if (IsFrameworkCollection(level))
                return level;

        return null;
    }

    private static bool IsFrameworkCollection(Type type) =>
        typeof(IEnumerable).IsAssignableFrom(type)
        && type != typeof(string)
        && type.Namespace is { } ns
        && ns.StartsWith("System.Collections", StringComparison.Ordinal)
        && ns != "System.Collections.Specialized"
        // A default ImmutableArray cannot be enumerated; its one field, an array, can be read.
        && !(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
        && IsFramework(type.Assembly);

    private static bool IsFramework(Assembly assembly) =>
        assembly == typeof(object).Assembly
        || assembly.GetName().Name is { } name
            && (
                name.StartsWith("System.", StringComparison.Ordinal)
                || name == "System"
                || name == "netstandard"
            );

    /// <summary>The collection a read-only wrapper wraps, or null for any other collection.</summary>
    private static PropertyInfo? Wrapped(Type collection)
    {
        for (var level = collection; level is not null; level = level.BaseType)
        {
            if (!level.IsGenericType)
                continue;

            var definition = level.GetGenericTypeDefinition();
            var name =
                definition == typeof(ReadOnlyCollection<>) ? "Items"
                : definition == typeof(ReadOnlyDictionary<,>) ? "Dictionary"
                : definition == typeof(ReadOnlySet<>) ? "Set"
                : null;

            if (name is not null)
                return level.GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance);
        }

        return null;
    }

    private static readonly Type[] Ordered =
    [
        typeof(SortedSet<>),
        typeof(SortedDictionary<,>),
        typeof(SortedList<,>),
        typeof(SortedList),
        typeof(ImmutableSortedSet<>),
        typeof(ImmutableSortedDictionary<,>),
        typeof(OrderedDictionary<,>),
    ];

    /// <summary>
    /// Whether a framework collection's enumeration order is not part of what it holds: a set, a
    /// dictionary or a bag, unless it is sorted or ordered by contract. So is a view of one, such
    /// as a dictionary's keys.
    /// </summary>
    private static bool IsUnordered(Type collection)
    {
        for (var at = collection; at is not null; at = at.DeclaringType)
        {
            var definition = at.IsGenericType ? at.GetGenericTypeDefinition() : at;

            if (Ordered.Contains(definition))
                return false;

            if (
                definition == typeof(ConcurrentBag<>)
                || typeof(IDictionary).IsAssignableFrom(at)
                || at.GetInterfaces()
                    .Any(i =>
                        i.IsGenericType
                        && i.GetGenericTypeDefinition() is var open
                        && (
                            open == typeof(ISet<>)
                            || open == typeof(IReadOnlySet<>)
                            || open == typeof(IDictionary<,>)
                            || open == typeof(IReadOnlyDictionary<,>)
                        )
                    )
            )
                return true;
        }

        return false;
    }
}
