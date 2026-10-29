using System;
using System.Collections.Generic;
using System.Text;
using TERN.Compiler;

namespace TERN.Runtime
{
    /// <summary>Growable array of TERN values. Indexing supports negative offsets from the end.</summary>
    public sealed class TernList
    {
        private Value[] _items;
        private int _count;

        public TernList(int capacity = 0)
        {
            _items = capacity > 0 ? new Value[capacity] : Array.Empty<Value>();
        }

        public TernList(Value[] items, int count)
        {
            _items = items;
            _count = count;
        }

        public int Count => _count;

        public Value[] Buffer => _items;

        public ref Value ItemRef(int index) => ref _items[index];

        public Value this[int index]
        {
            get => _items[index];
            set => _items[index] = value;
        }

        public void Add(in Value value)
        {
            if (_count == _items.Length)
            {
                Grow(_count + 1);
            }

            _items[_count++] = value;
        }

        public void AddRange(ReadOnlySpan<Value> values)
        {
            EnsureCapacity(_count + values.Length);
            values.CopyTo(_items.AsSpan(_count));
            _count += values.Length;
        }

        public void EnsureCapacity(int capacity)
        {
            if (capacity > _items.Length)
            {
                Grow(capacity);
            }
        }

        private void Grow(int minimum)
        {
            var size = _items.Length == 0 ? 4 : _items.Length * 2;
            if (size < minimum)
            {
                size = minimum;
            }

            var replacement = new Value[size];
            Array.Copy(_items, replacement, _count);
            _items = replacement;
        }

        public void Insert(int index, in Value value)
        {
            if (index < 0 || index > _count)
            {
                throw new TernError("IndexError", $"insert index {index} out of range for list of length {_count}");
            }

            if (_count == _items.Length)
            {
                Grow(_count + 1);
            }

            if (index < _count)
            {
                Array.Copy(_items, index, _items, index + 1, _count - index);
            }

            _items[index] = value;
            _count++;
        }

        public void RemoveAt(int index)
        {
            if (index < 0 || index >= _count)
            {
                throw new TernError("IndexError", $"remove index {index} out of range for list of length {_count}");
            }

            _count--;
            if (index < _count)
            {
                Array.Copy(_items, index + 1, _items, index, _count - index);
            }

            _items[_count] = Value.Nil;
        }

        public void Clear()
        {
            Array.Clear(_items, 0, _count);
            _count = 0;
        }

        public bool TryGet(long index, out Value value)
        {
            var resolved = index < 0 ? index + _count : index;
            if (resolved < 0 || resolved >= _count)
            {
                value = Value.Nil;
                return false;
            }

            value = _items[resolved];
            return true;
        }

        public int IndexOf(in Value value)
        {
            for (var i = 0; i < _count; i++)
            {
                if (_items[i].Equals(value))
                {
                    return i;
                }
            }

            return -1;
        }

        public TernList Slice(long start, long end)
        {
            var count = _count;
            var from = start < 0 ? start + count : start;
            var to = end < 0 ? end + count : end;

            if (from < 0)
            {
                from = 0;
            }

            if (to > count)
            {
                to = count;
            }

            if (from > to)
            {
                return new TernList();
            }

            var result = new TernList((int)(to - from));
            for (var i = from; i < to; i++)
            {
                result.Add(_items[i]);
            }

            return result;
        }

        public void Reverse()
        {
            Array.Reverse(_items, 0, _count);
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append('[');
            for (var i = 0; i < _count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                AppendNested(builder, _items[i]);
            }

            builder.Append(']');
            return builder.ToString();
        }

        internal static void AppendNested(StringBuilder builder, in Value value)
        {
            var text = value.ToDebugString();
            builder.Append(text);
        }
    }

    /// <summary>
    /// Insertion ordered hash map with open addressing for lookups. Keeping the
    /// entries array in insertion order makes iteration deterministic, which is
    /// what scripts expect when they print a map.
    /// </summary>
    public sealed class TernMap
    {
        private struct Entry
        {
            public long Hash;
            public Value Key;
            public Value Value;
            public bool Dead;
            public bool Used;
        }

        private Entry[] _entries;
        private int _count;
        private int _dead;
        private int[] _buckets;   // entry index + 1; 0 = empty, -1 = tombstone

        public TernMap(int capacity = 0)
        {
            var size = capacity > 0 ? NextPowerOfTwo(Math.Max(4, capacity * 2)) : 8;
            _entries = new Entry[Math.Max(4, capacity)];
            _buckets = new int[size];
        }

        public int Count => _count;

        public int SlotCount => _entries.Length;

        private static int NextPowerOfTwo(int value)
        {
            var size = 4;
            while (size < value)
            {
                size <<= 1;
            }

            return size;
        }

        private static long NormalizeHash(int hash) => hash == 0 ? 1L : hash;

        public bool TryGet(in Value key, out Value value)
        {
            var hash = NormalizeHash(key.GetHashCode());
            var mask = _buckets.Length - 1;
            var slot = (int)((ulong)hash & (ulong)mask);

            while (true)
            {
                var bucket = _buckets[slot];
                if (bucket == 0)
                {
                    value = Value.Nil;
                    return false;
                }

                if (bucket > 0)
                {
                    ref var entry = ref _entries[bucket - 1];
                    if (entry.Hash == hash && entry.Key.Equals(key))
                    {
                        value = entry.Value;
                        return true;
                    }
                }

                slot = (slot + 1) & mask;
            }
        }

        public bool ContainsKey(in Value key) => TryGet(key, out _);

        public Value GetOrNil(in Value key)
        {
            TryGet(key, out var value);
            return value;
        }

        public void Set(in Value key, in Value value)
        {
            var hash = NormalizeHash(key.GetHashCode());
            var mask = _buckets.Length - 1;
            var slot = (int)((ulong)hash & (ulong)mask);
            var tombstone = -1;

            while (true)
            {
                var bucket = _buckets[slot];
                if (bucket == 0)
                {
                    if (_count + 1 > _entries.Length)
                    {
                        Grow();
                        SetCore(key, hash, value);
                        return;
                    }

                    var index = _count++;
                    _entries[index] = new Entry { Hash = hash, Key = key, Value = value, Used = true };
                    _buckets[tombstone >= 0 ? tombstone : slot] = index + 1;
                    return;
                }

                if (bucket == -1)
                {
                    if (tombstone < 0)
                    {
                        tombstone = slot;
                    }

                    slot = (slot + 1) & mask;
                    continue;
                }

                ref var entry = ref _entries[bucket - 1];
                if (entry.Hash == hash && entry.Key.Equals(key))
                {
                    entry.Value = value;
                    return;
                }

                slot = (slot + 1) & mask;
            }
        }

        private void SetCore(in Value key, long hash, in Value value)
        {
            var mask = _buckets.Length - 1;
            var slot = (int)((ulong)hash & (ulong)mask);
            while (_buckets[slot] > 0)
            {
                slot = (slot + 1) & mask;
            }

            var index = _count++;
            _entries[index] = new Entry { Hash = hash, Key = key, Value = value, Used = true };
            _buckets[slot] = index + 1;
        }

        private void Grow()
        {
            var previous = _entries;
            var size = previous.Length * 2;
            _entries = new Entry[size];

            // Compact live entries and rebuild buckets.
            _count = 0;
            _dead = 0;
            _buckets = new int[NextPowerOfTwo(size * 2)];

            for (var i = 0; i < previous.Length; i++)
            {
                ref var entry = ref previous[i];
                if (!entry.Used || entry.Dead)
                {
                    continue;
                }

                SetCore(entry.Key, entry.Hash, entry.Value);
            }
        }

        public bool Remove(in Value key)
        {
            var hash = NormalizeHash(key.GetHashCode());
            var mask = _buckets.Length - 1;
            var slot = (int)((ulong)hash & (ulong)mask);

            while (true)
            {
                var bucket = _buckets[slot];
                if (bucket == 0)
                {
                    return false;
                }

                if (bucket > 0)
                {
                    ref var entry = ref _entries[bucket - 1];
                    if (entry.Hash == hash && entry.Key.Equals(key))
                    {
                        entry.Dead = true;
                        _buckets[slot] = -1;
                        _count--;
                        _dead++;

                        if (_dead > 32 && _dead > _count)
                        {
                            Grow();
                        }

                        return true;
                    }
                }

                slot = (slot + 1) & mask;
            }
        }

        public void Clear()
        {
            _entries = new Entry[4];
            _count = 0;
            _dead = 0;
            Array.Clear(_buckets, 0, _buckets.Length);
        }

        /// <summary>Copies all live keys into a list (insertion order).</summary>
        public TernList Keys()
        {
            var list = new TernList(_count);
            for (var i = 0; i < _entries.Length; i++)
            {
                ref var entry = ref _entries[i];
                if (entry.Used && !entry.Dead)
                {
                    list.Add(entry.Key);
                }
            }

            return list;
        }

        public TernList Values()
        {
            var list = new TernList(_count);
            for (var i = 0; i < _entries.Length; i++)
            {
                ref var entry = ref _entries[i];
                if (entry.Used && !entry.Dead)
                {
                    list.Add(entry.Value);
                }
            }

            return list;
        }

        public bool TryGetEntry(int index, out Value key, out Value value)
        {
            if ((uint)index < (uint)_entries.Length)
            {
                ref var entry = ref _entries[index];
                if (entry.Used && !entry.Dead)
                {
                    key = entry.Key;
                    value = entry.Value;
                    return true;
                }
            }

            key = Value.Nil;
            value = Value.Nil;
            return false;
        }

        public TernMap Clone()
        {
            var copy = new TernMap(Math.Max(4, _count));
            for (var i = 0; i < _entries.Length; i++)
            {
                ref var entry = ref _entries[i];
                if (entry.Used && !entry.Dead)
                {
                    copy.Set(entry.Key, entry.Value);
                }
            }

            return copy;
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append('{');
            var first = true;

            for (var i = 0; i < _entries.Length; i++)
            {
                ref var entry = ref _entries[i];
                if (!entry.Used || entry.Dead)
                {
                    continue;
                }

                if (!first)
                {
                    builder.Append(", ");
                }

                first = false;
                builder.Append(entry.Key.ToDebugString()).Append(": ").Append(entry.Value.ToDebugString());
            }

            builder.Append('}');
            return builder.ToString();
        }
    }

    public sealed class TernRange
    {
        public TernRange(long start, long end, bool inclusive, long step = 1)
        {
            Start = start;
            End = end;
            Inclusive = inclusive;
            Step = step == 0 ? 1 : step;
        }

        public long Start { get; }
        public long End { get; }
        public bool Inclusive { get; }
        public long Step { get; }

        public long Length
        {
            get
            {
                var span = Step > 0
                    ? (Inclusive ? End - Start + 1 : End - Start)
                    : (Inclusive ? Start - End + 1 : Start - End);

                if (span <= 0)
                {
                    return 0;
                }

                var step = Math.Abs(Step);
                return (span + step - 1) / step;
            }
        }

        public TernList ToList()
        {
            var length = Length;
            var list = new TernList((int)Math.Min(length, int.MaxValue));
            var position = Start;
            for (long i = 0; i < length; i++)
            {
                list.Add(new Value(position));
                position += Step;
            }

            return list;
        }

        public bool Contains(long value)
        {
            if (Step > 0)
            {
                if (value < Start || (Inclusive ? value > End : value >= End))
                {
                    return false;
                }
            }
            else
            {
                if (value > Start || (Inclusive ? value < End : value <= End))
                {
                    return false;
                }
            }

            return (value - Start) % Step == 0;
        }

        public override string ToString() => $"{Start}..{(Inclusive ? string.Empty : "<")}{End}";
    }

    /// <summary>A struct instance: a type plus a flat field array indexed by declaration order.</summary>
    public sealed class TernInstance
    {
        public TernInstance(TernType type, Value[] fields)
        {
            Type = type;
            Fields = fields;
        }

        public TernType Type { get; }

        public Value[] Fields { get; }

        public Value GetField(int index) => Fields[index];

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append(Type.Name).Append('(');
            for (var i = 0; i < Fields.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(Type.FieldNames[i]).Append(": ").Append(Fields[i].ToDebugString());
            }

            builder.Append(')');
            return builder.ToString();
        }
    }

    /// <summary>Runtime representation of a `struct` declaration.</summary>
    public sealed class TernType
    {
        public TernType(string name, string[] fieldNames, Value[] defaults, Dictionary<string, TernClosure> methods,
            FunctionProto?[]? defaultPrototypes = null)
        {
            Name = name;
            FieldNames = fieldNames;
            Defaults = defaults;
            DefaultPrototypes = defaultPrototypes ?? new FunctionProto?[fieldNames.Length];
            Methods = methods;

            FieldIndex = new Dictionary<string, int>(fieldNames.Length, StringComparer.Ordinal);
            for (var i = 0; i < fieldNames.Length; i++)
            {
                FieldIndex[fieldNames[i]] = i;
            }
        }

        public string Name { get; }
        public string[] FieldNames { get; }
        public Value[] Defaults { get; }

        /// <summary>Defaults that are not compile time constants, evaluated per instance.</summary>
        public FunctionProto?[] DefaultPrototypes { get; }

        public Dictionary<string, TernClosure> Methods { get; }
        public Dictionary<string, int> FieldIndex { get; }
        public int FieldCount => FieldNames.Length;

        public bool TryGetField(string name, out int index) => FieldIndex.TryGetValue(name, out index);

        public TernInstance Create(VM vm)
        {
            var fields = new Value[Defaults.Length];
            Array.Copy(Defaults, fields, Defaults.Length);

            for (var i = 0; i < DefaultPrototypes.Length; i++)
            {
                var prototype = DefaultPrototypes[i];
                if (prototype is not null)
                {
                    fields[i] = vm.CallProto(prototype);
                }
            }

            return new TernInstance(this, fields);
        }
    }

    public sealed class TernClosure
    {
        public TernClosure(FunctionProto proto, Upvalue[] upvalues)
        {
            Proto = proto;
            Upvalues = upvalues;
        }

        public FunctionProto Proto { get; }

        /// <summary>Module whose globals this function resolves against.</summary>
        public TernModule? Module { get; set; }

        public Upvalue[] Upvalues { get; }

        public override string ToString() => $"<fn {Proto.Name ?? "anonymous"}>";
    }

    /// <summary>Captured variable cell. Starts open (pointing into a frame) and is closed on return.</summary>
    public sealed class Upvalue
    {
        public Value[]? Array;
        public int Index;
        public Value Closed;
        public bool IsClosed;

        public Value Get() => IsClosed ? Closed : Array![Index];

        public void Set(in Value value)
        {
            if (IsClosed)
            {
                Closed = value;
            }
            else
            {
                Array![Index] = value;
            }
        }

        public void Close()
        {
            if (!IsClosed)
            {
                Closed = Array![Index];
                Array = null;
                IsClosed = true;
            }
        }
    }

    public delegate Value NativeFn(VM vm, ReadOnlySpan<Value> args);

    /// <summary>A host function exposed to TERN.</summary>
    public sealed class TernNative
    {
        public TernNative(string name, NativeFn function, int minArguments = 0, int maxArguments = -1, string? documentation = null)
        {
            Name = name;
            Function = function;
            MinArguments = minArguments;
            MaxArguments = maxArguments;
            Documentation = documentation;
        }

        public string Name { get; }
        public NativeFn Function { get; }
        public int MinArguments { get; }
        public int MaxArguments { get; }   // -1 means variadic
        public string? Documentation { get; }

        public override string ToString() => $"<native {Name}>";
    }

    /// <summary>A callable with its receiver already bound (e.g. `list.push`).</summary>
    public sealed class TernBoundMethod
    {
        public TernBoundMethod(in Value receiver, in Value callee)
        {
            Receiver = receiver;
            Callee = callee;
        }

        public Value Receiver { get; }

        public Value Callee { get; }

        public string Name => Callee.Kind switch
        {
            ValueKind.Native => Callee.AsNative().Name,
            ValueKind.Closure => Callee.AsClosure().Proto.Name ?? "anonymous",
            _ => "bound",
        };
    }

    public enum IteratorKind
    {
        List,
        Map,
        String,
        Range,
        Instance,
    }

    /// <summary>Iterator used by `for ... in` loops. Produces one or two values per step.</summary>
    public sealed class TernIterator
    {
        public IteratorKind Kind;
        public Value Target;
        public int Index;
        public long RangeValue;
        public long RangeEnd;
        public long RangeStep;
        public bool RangeInclusive;

        public static TernIterator ForList(TernList list) =>
            new() { Kind = IteratorKind.List, Target = Value.Of(list) };

        public static TernIterator ForMap(TernMap map) =>
            new() { Kind = IteratorKind.Map, Target = Value.Of(map) };

        public static TernIterator ForString(string text) =>
            new() { Kind = IteratorKind.String, Target = Value.Str(text) };

        public static TernIterator ForRange(TernRange range) => new()
        {
            Kind = IteratorKind.Range,
            Target = Value.Of(range),
            RangeValue = range.Start,
            RangeEnd = range.End,
            RangeStep = range.Step,
            RangeInclusive = range.Inclusive,
        };

        public bool Next(out Value first, out Value second)
        {
            second = Value.Nil;

            switch (Kind)
            {
                case IteratorKind.List:
                {
                    var list = Target.AsList();
                    if (Index >= list.Count)
                    {
                        first = Value.Nil;
                        return false;
                    }

                    first = list[Index++];
                    return true;
                }

                case IteratorKind.Map:
                {
                    var map = Target.AsMap();
                    var slots = map.SlotCount;
                    while (Index < slots)
                    {
                        var index = Index++;
                        if (map.TryGetEntry(index, out first, out second))
                        {
                            return true;
                        }
                    }

                    first = Value.Nil;
                    return false;
                }

                case IteratorKind.String:
                {
                    var text = Target.AsString();
                    if (Index >= text.Length)
                    {
                        first = Value.Nil;
                        return false;
                    }

                    first = Value.Str(text[Index++].ToString());
                    return true;
                }

                case IteratorKind.Range:
                {
                    var done = RangeStep > 0
                        ? (RangeInclusive ? RangeValue > RangeEnd : RangeValue >= RangeEnd)
                        : (RangeInclusive ? RangeValue < RangeEnd : RangeValue <= RangeEnd);

                    if (done)
                    {
                        first = Value.Nil;
                        return false;
                    }

                    first = new Value(RangeValue);
                    RangeValue += RangeStep;
                    return true;
                }

                default:
                    first = Value.Nil;
                    return false;
            }
        }
    }
}
