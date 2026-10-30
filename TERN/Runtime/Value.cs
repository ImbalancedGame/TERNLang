using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using TERN.Compiler;

namespace TERN.Runtime
{
    public enum ValueKind : byte
    {
        Nil = 0,
        Bool = 1,
        Int = 2,
        Float = 3,
        Str = 4,
        List = 5,
        Map = 6,
        Instance = 7,
        Closure = 8,
        Native = 9,
        Bound = 10,
        Range = 11,
        Missing = 12,   // internal sentinel for omitted call arguments
        Module = 13,
        Iterator = 14,
        Type = 15,
    }

    /// <summary>
    /// A TERN value. This is a 16 byte discriminated union: numbers live in a
    /// machine word and only heap types touch the object reference, so arithmetic
    /// on ints/floats never allocates.
    /// </summary>
    public readonly struct Value : IEquatable<Value>
    {
        public readonly ValueKind Kind;
        private readonly long _bits;
        private readonly object? _ref;

        public Value(long integer)
        {
            Kind = ValueKind.Int;
            _bits = integer;
            _ref = null;
        }

        public Value(double number)
        {
            Kind = ValueKind.Float;
            _bits = BitConverter.DoubleToInt64Bits(number);
            _ref = null;
        }

        private Value(ValueKind kind, long bits, object? reference)
        {
            Kind = kind;
            _bits = bits;
            _ref = reference;
        }

        public static readonly Value Nil = new(ValueKind.Nil, 0, null);
        public static readonly Value True = new(ValueKind.Bool, 1, null);
        public static readonly Value False = new(ValueKind.Bool, 0, null);
        public static readonly Value Missing = new(ValueKind.Missing, 0, null);

        public static Value Bool(bool value) => value ? True : False;

        public static Value Str(string value) => new(ValueKind.Str, 0, value);

        public static Value Of(TernList list) => new(ValueKind.List, 0, list);

        public static Value Of(TernMap map) => new(ValueKind.Map, 0, map);

        public static Value Of(TernInstance instance) => new(ValueKind.Instance, 0, instance);

        public static Value Of(TernClosure closure) => new(ValueKind.Closure, 0, closure);

        public static Value Of(TernNative native) => new(ValueKind.Native, 0, native);

        public static Value Of(TernBoundMethod bound) => new(ValueKind.Bound, 0, bound);

        public static Value Of(TernRange range) => new(ValueKind.Range, 0, range);

        public static Value Of(TernModule module) => new(ValueKind.Module, 0, module);

        public static Value Of(TernIterator iterator) => new(ValueKind.Iterator, 0, iterator);

        public static Value Of(TernType type) => new(ValueKind.Type, 0, type);

        public bool IsNil => Kind == ValueKind.Nil;

        public bool IsNumber => Kind is ValueKind.Int or ValueKind.Float;

        public bool IsCallable => Kind is ValueKind.Closure or ValueKind.Native or ValueKind.Bound or ValueKind.Instance;

        public bool AsBool() => _bits != 0;

        public long AsInt() => _bits;

        public double AsDouble() => Kind == ValueKind.Float ? BitConverter.Int64BitsToDouble(_bits) : _bits;

        public string AsString() => (string)_ref!;

        public TernList AsList() => (TernList)_ref!;

        public TernMap AsMap() => (TernMap)_ref!;

        public TernInstance AsInstance() => (TernInstance)_ref!;

        public TernClosure AsClosure() => (TernClosure)_ref!;

        public TernNative AsNative() => (TernNative)_ref!;

        public TernBoundMethod AsBound() => (TernBoundMethod)_ref!;

        public TernRange AsRange() => (TernRange)_ref!;

        public TernModule AsModule() => (TernModule)_ref!;

        public TernIterator AsIterator() => (TernIterator)_ref!;

        public TernType AsType() => (TernType)_ref!;

        public object? AsObject() => _ref;

        /// <summary>Truthiness: only nil and false are falsy.</summary>
        public bool Truthy => Kind switch
        {
            ValueKind.Nil => false,
            ValueKind.Bool => _bits != 0,
            _ => true,
        };

        /// <summary>Numeric comparison helper. Returns false when either side is not numeric.</summary>
        public static bool TryCompare(in Value left, in Value right, out int result)
        {
            if (left.Kind == ValueKind.Int && right.Kind == ValueKind.Int)
            {
                result = left._bits.CompareTo(right._bits);
                return true;
            }

            if (left.IsNumber && right.IsNumber)
            {
                var a = left.AsDouble();
                var b = right.AsDouble();
                result = a < b ? -1 : a > b ? 1 : 0;
                return true;
            }

            result = 0;
            return false;
        }

        public static bool NumericEquals(in Value left, in Value right)
        {
            if (left.Kind == ValueKind.Int && right.Kind == ValueKind.Int)
            {
                return left._bits == right._bits;
            }

            if (left.IsNumber && right.IsNumber)
            {
                return left.AsDouble() == right.AsDouble();
            }

            return false;
        }

        public bool Equals(Value other)
        {
            if (Kind != other.Kind)
            {
                // int/float equality is numeric
                if (IsNumber && other.IsNumber)
                {
                    return NumericEquals(this, other);
                }

                return false;
            }

            switch (Kind)
            {
                case ValueKind.Nil:
                case ValueKind.Missing:
                    return true;
                case ValueKind.Bool:
                case ValueKind.Int:
                    return _bits == other._bits;
                case ValueKind.Float:
                    return AsDouble().Equals(other.AsDouble());
                case ValueKind.Str:
                    return string.Equals((string)_ref!, (string)other._ref!, StringComparison.Ordinal);
                default:
                    return ReferenceEquals(_ref, other._ref);
            }
        }

        public override bool Equals(object? obj) => obj is Value other && Equals(other);

        public override int GetHashCode()
        {
            switch (Kind)
            {
                case ValueKind.Nil:
                case ValueKind.Missing:
                    return 0;
                case ValueKind.Bool:
                    return _bits != 0 ? 1 : 2;
                case ValueKind.Int:
                    return Mix(_bits);
                case ValueKind.Float:
                {
                    var number = AsDouble();
                    // Integral floats must hash like the equivalent int.
                    if (number >= -9.2233720368547758E18 && number <= 9.2233720368547758E18 &&
                        Math.Floor(number) == number)
                    {
                        return Mix((long)number);
                    }

                    return Mix(BitConverter.DoubleToInt64Bits(number));
                }
                case ValueKind.Str:
                    return StringHash((string)_ref!);
                default:
                    return RuntimeHelpers.GetHashCode(_ref!);
            }
        }

        internal static int Mix(long value)
        {
            unchecked
            {
                var z = unchecked((ulong)value) * 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9L;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBL;
                z ^= z >> 31;
                return (int)z;
            }
        }

        /// <summary>FNV-1a over the UTF-16 characters of a string.</summary>
        public static int StringHash(string text)
        {
            unchecked
            {
                var hash = 2166136261u;
                var span = text.AsSpan();
                for (var i = 0; i < span.Length; i++)
                {
                    hash ^= span[i];
                    hash *= 16777619u;
                }

                return (int)hash;
            }
        }

        /// <summary>Human readable rendering used by print/string conversion.</summary>
        public string ToDisplayString() => Kind == ValueKind.Str ? AsString() : ToDebugString();

        /// <summary>Rendering that quotes strings; used inside containers and for string() coercion.</summary>
        public string ToDebugString()
        {
            switch (Kind)
            {
                case ValueKind.Nil:
                    return "nil";
                case ValueKind.Missing:
                    return "<missing>";
                case ValueKind.Bool:
                    return _bits != 0 ? "true" : "false";
                case ValueKind.Int:
                    return _bits.ToString(CultureInfo.InvariantCulture);
                case ValueKind.Float:
                    return FormatDouble(AsDouble());
                case ValueKind.Str:
                    return QuoteString(AsString());
                case ValueKind.List:
                    return AsList().ToString();
                case ValueKind.Map:
                    return AsMap().ToString();
                case ValueKind.Instance:
                    return AsInstance().ToString();
                case ValueKind.Closure:
                    return $"<fn {AsClosure().Proto.Name ?? "anonymous"}>";
                case ValueKind.Native:
                    return $"<native {AsNative().Name}>";
                case ValueKind.Bound:
                    return $"<method {AsBound().Name}>";
                case ValueKind.Range:
                    return AsRange().ToString();
                case ValueKind.Module:
                    return $"<module {AsModule().Name}>";
                case ValueKind.Iterator:
                    return "<iterator>";
                case ValueKind.Type:
                    return $"<struct {AsType().Name}>";
                default:
                    return "nil";
            }
        }

        public static string FormatDouble(double number)
        {
            if (double.IsNaN(number))
            {
                return "nan";
            }

            if (double.IsPositiveInfinity(number))
            {
                return "inf";
            }

            if (double.IsNegativeInfinity(number))
            {
                return "-inf";
            }

            var text = number.ToString("R", CultureInfo.InvariantCulture);
            if (text.IndexOfAny(new[] { '.', 'E', 'e' }) < 0)
            {
                text += ".0";
            }

            return text;
        }

        public static string QuoteString(string text)
        {
            var builder = new StringBuilder(text.Length + 2);
            builder.Append('"');
            foreach (var c in text)
            {
                builder.Append(c switch
                {
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    '\\' => "\\\\",
                    '"' => "\\\"",
                    '\0' => "\\0",
                    _ => c.ToString(),
                });
            }

            builder.Append('"');
            return builder.ToString();
        }

        public override string ToString() => ToDebugString();
    }
}
