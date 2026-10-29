using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TERN.Runtime;

namespace TERN.Builtins
{
    /// <summary>
    /// Member functions available on built in value types. Lookups happen by
    /// (kind, name), so `list.push` does not require building a dictionary per value.
    /// </summary>
    public static class BuiltinMethods
    {
        private static readonly Dictionary<ValueKind, Dictionary<string, TernNative>> Tables = new();
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        static BuiltinMethods()
        {
            var stringMethods = Table(ValueKind.Str);
            Add(stringMethods, "upper", 1, 1, (vm, a) => Value.Str(a[0].AsString().ToUpperInvariant()), "Uppercased copy.");
            Add(stringMethods, "lower", 1, 1, (vm, a) => Value.Str(a[0].AsString().ToLowerInvariant()), "Lowercased copy.");
            Add(stringMethods, "trim", 1, 1, (vm, a) => Value.Str(a[0].AsString().Trim()), "Removes leading and trailing whitespace.");
            Add(stringMethods, "trimStart", 1, 1, (vm, a) => Value.Str(a[0].AsString().TrimStart()), "Removes leading whitespace.");
            Add(stringMethods, "trimEnd", 1, 1, (vm, a) => Value.Str(a[0].AsString().TrimEnd()), "Removes trailing whitespace.");
            Add(stringMethods, "split", 1, 2, (vm, a) =>
            {
                var text = a[0].AsString();
                var separator = a.Length > 1 ? a[1].AsString() : " ";
                var pieces = separator.Length == 0
                    ? SplitChars(text)
                    : text.Split(new[] { separator }, StringSplitOptions.None);
                var list = new TernList(pieces.Length);
                foreach (var piece in pieces)
                {
                    list.Add(Value.Str(piece));
                }

                return Value.Of(list);
            }, "Splits on a separator (default a space); an empty separator splits into characters.");
            Add(stringMethods, "lines", 1, 1, (vm, a) =>
            {
                var pieces = a[0].AsString().Replace("\r\n", "\n").Split('\n');
                var list = new TernList(pieces.Length);
                foreach (var piece in pieces)
                {
                    if (piece.Length > 0)
                    {
                        list.Add(Value.Str(piece));
                    }
                }

                return Value.Of(list);
            }, "Splits into lines, handling \\n and \\r\\n.");
            Add(stringMethods, "replace", 3, 3, (vm, a) =>
                Value.Str(a[0].AsString().Replace(a[1].AsString(), a[2].AsString(), StringComparison.Ordinal)), "Replaces every occurrence of a substring.");
            Add(stringMethods, "replaceFirst", 3, 3, (vm, a) => Value.Str(ReplaceFirst(a[0].AsString(), a[1].AsString(), a[2].AsString())), "Replaces the first occurrence of a substring.");
            Add(stringMethods, "startsWith", 2, 2, (vm, a) => Value.Bool(a[0].AsString().StartsWith(a[1].AsString(), StringComparison.Ordinal)), "True when the string begins with the prefix.");
            Add(stringMethods, "endsWith", 2, 2, (vm, a) => Value.Bool(a[0].AsString().EndsWith(a[1].AsString(), StringComparison.Ordinal)), "True when the string ends with the suffix.");
            Add(stringMethods, "contains", 2, 2, (vm, a) => Value.Bool(a[0].AsString().Contains(a[1].AsString(), StringComparison.Ordinal)), "True when the substring occurs.");
            Add(stringMethods, "indexOf", 2, 2, (vm, a) => new Value(a[0].AsString().IndexOf(a[1].AsString(), StringComparison.Ordinal)), "Index of the first occurrence, or -1.");
            Add(stringMethods, "lastIndexOf", 2, 2, (vm, a) => new Value(a[0].AsString().LastIndexOf(a[1].AsString(), StringComparison.Ordinal)), "Index of the last occurrence, or -1.");
            Add(stringMethods, "substring", 2, 3, (vm, a) =>
            {
                var text = a[0].AsString();
                var start = (int)RequireInt(a[1], "substring", 1);
                start = start < 0 ? start + text.Length : start;
                start = Math.Max(0, Math.Min(start, text.Length));
                var end = a.Length > 2 ? (int)RequireInt(a[2], "substring", 2) : text.Length;
                end = end < 0 ? end + text.Length : end;
                end = Math.Max(start, Math.Min(end, text.Length));
                return Value.Str(text.Substring(start, end - start));
            }, "Substring from a start index with an optional length.");
            Add(stringMethods, "slice", 1, 3, (vm, a) => Slice(a), "Substring from start to end, negative indices allowed.");
            Add(stringMethods, "chars", 1, 1, (vm, a) =>
            {
                var text = a[0].AsString();
                var list = new TernList(text.Length);
                for (var i = 0; i < text.Length; i++)
                {
                    list.Add(Value.Str(text[i].ToString()));
                }

                return Value.Of(list);
            }, "List of single character strings.");
            Add(stringMethods, "repeat", 2, 2, (vm, a) =>
            {
                var count = (int)RequireInt(a[1], "repeat", 1);
                if (count < 0)
                {
                    throw vm.Error("ArgumentError", "repeat count cannot be negative");
                }

                return Value.Str(string.Concat(System.Linq.Enumerable.Repeat(a[0].AsString(), count)));
            }, "Repeats the string n times.");
            Add(stringMethods, "padStart", 2, 3, (vm, a) => Value.Str(Pad(a[0].AsString(), (int)RequireInt(a[1], "padStart", 1),
                a.Length > 2 ? a[2].AsString() : " ", true)), "Left pads to a width with an optional fill.");
            Add(stringMethods, "padEnd", 2, 3, (vm, a) => Value.Str(Pad(a[0].AsString(), (int)RequireInt(a[1], "padEnd", 1),
                a.Length > 2 ? a[2].AsString() : " ", false)), "Right pads to a width with an optional fill.");
            Add(stringMethods, "reversed", 1, 1, (vm, a) =>
            {
                var characters = a[0].AsString().ToCharArray();
                Array.Reverse(characters);
                return Value.Str(new string(characters));
            }, "Reversed copy.");
            Add(stringMethods, "toInt", 1, 2, (vm, a) => ParseInteger(vm, a, "toInt"), "Parses the string as an integer.");
            Add(stringMethods, "toFloat", 1, 2, (vm, a) => ParseFloat(vm, a, "toFloat"), "Parses the string as a float.");
            Add(stringMethods, "count", 2, 2, (vm, a) =>
            {
                var text = a[0].AsString();
                var needle = a[1].AsString();
                if (needle.Length == 0)
                {
                    return new Value(0);
                }

                var count = 0;
                var index = 0;
                while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
                {
                    count++;
                    index += needle.Length;
                }

                return new Value(count);
            }, "Number of characters.");
            Add(stringMethods, "words", 1, 1, (vm, a) =>
            {
                var words = a[0].AsString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var list = new TernList(words.Length);
                foreach (var word in words)
                {
                    list.Add(Value.Str(word));
                }

                return Value.Of(list);
            }, "Splits on whitespace, dropping empty pieces.");
            Add(stringMethods, "toString", 1, 1, (vm, a) => a[0], "The string itself.");
            Add(stringMethods, "isEmpty", 1, 1, (vm, a) => Value.Bool(a[0].AsString().Length == 0), "True when the string has no characters.");

            var listMethods = Table(ValueKind.List);
            Add(listMethods, "push", 2, -1, (vm, a) =>
            {
                var list = a[0].AsList();
                for (var i = 1; i < a.Length; i++)
                {
                    list.Add(a[i]);
                }

                return new Value(list.Count);
            }, "Appends an item and returns its position.");
            Add(listMethods, "pop", 1, 1, (vm, a) =>
            {
                var list = a[0].AsList();
                if (list.Count == 0)
                {
                    return Value.Nil;
                }

                var value = list[list.Count - 1];
                list.RemoveAt(list.Count - 1);
                return value;
            }, "Removes and returns the last item.");
            Add(listMethods, "shift", 1, 1, (vm, a) =>
            {
                var list = a[0].AsList();
                if (list.Count == 0)
                {
                    return Value.Nil;
                }

                var value = list[0];
                list.RemoveAt(0);
                return value;
            }, "Removes and returns the first item.");
            Add(listMethods, "unshift", 2, -1, (vm, a) =>
            {
                var list = a[0].AsList();
                for (var i = 1; i < a.Length; i++)
                {
                    list.Insert(i - 1, a[i]);
                }

                return new Value(list.Count);
            }, "Prepends an item.");
            Add(listMethods, "insert", 3, 3, (vm, a) =>
            {
                a[0].AsList().Insert((int)RequireInt(a[1], "insert", 1), a[2]);
                return a[0];
            }, "Inserts an item at an index.");
            Add(listMethods, "remove", 2, 2, (vm, a) =>
            {
                var list = a[0].AsList();
                var index = (int)RequireInt(a[1], "remove", 1);
                if (index < 0)
                {
                    index += list.Count;
                }

                if (index < 0 || index >= list.Count)
                {
                    return Value.Nil;
                }

                var value = list[index];
                list.RemoveAt(index);
                return value;
            }, "Removes and returns the item at an index.");
            Add(listMethods, "removeValue", 2, 2, (vm, a) =>
            {
                var list = a[0].AsList();
                var index = list.IndexOf(a[1]);
                if (index < 0)
                {
                    return Value.False;
                }

                list.RemoveAt(index);
                return Value.True;
            }, "Removes the first occurrence of a value; returns true when found.");
            Add(listMethods, "indexOf", 2, 2, (vm, a) => new Value(a[0].AsList().IndexOf(a[1])), "Index of the first occurrence, or -1.");
            Add(listMethods, "contains", 2, 2, (vm, a) => Value.Bool(a[0].AsList().IndexOf(a[1]) >= 0), "True when the value is present.");
            Add(listMethods, "join", 1, 2, (vm, a) =>
            {
                var list = a[0].AsList();
                var separator = a.Length > 1 ? a[1].ToDisplayString() : ",";
                var builder = new StringBuilder();
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(separator);
                    }

                    builder.Append(list[i].ToDisplayString());
                }

                return Value.Str(builder.ToString());
            }, "Concatenates the items with a separator.");
            Add(listMethods, "reverse", 1, 1, (vm, a) =>
            {
                a[0].AsList().Reverse();
                return a[0];
            }, "Reverses in place.");
            Add(listMethods, "sort", 1, 2, (vm, a) => Sort(vm, a), "Stable in place sort, with an optional comparator.");
            Add(listMethods, "slice", 1, 3, (vm, a) =>
            {
                var list = a[0].AsList();
                var start = a.Length > 1 ? RequireInt(a[1], "slice", 1) : 0;
                var end = a.Length > 2 ? RequireInt(a[2], "slice", 2) : list.Count;
                return Value.Of(list.Slice(start, end));
            }, "Copy of the range [start, end).");
            Add(listMethods, "map", 2, 2, (vm, a) =>
            {
                var list = a[0].AsList();
                var callback = a[1];
                var result = new TernList(list.Count);
                var arguments = new Value[1];
                for (var i = 0; i < list.Count; i++)
                {
                    arguments[0] = list[i];
                    result.Add(vm.CallValue(callback, arguments.AsSpan(0, 1)));
                }

                return Value.Of(result);
            }, "New list of callback results.");
            Add(listMethods, "filter", 2, 2, (vm, a) =>
            {
                var list = a[0].AsList();
                var callback = a[1];
                var result = new TernList(list.Count);
                var arguments = new Value[1];
                for (var i = 0; i < list.Count; i++)
                {
                    arguments[0] = list[i];
                    if (vm.CallValue(callback, arguments.AsSpan(0, 1)).Truthy)
                    {
                        result.Add(list[i]);
                    }
                }

                return Value.Of(result);
            }, "New list of the items the callback accepts.");
            Add(listMethods, "reduce", 2, 3, (vm, a) =>
            {
                var list = a[0].AsList();
                var callback = a[1];
                var accumulator = a.Length > 2 ? a[2] : list.Count > 0 ? list[0] : Value.Nil;
                var start = a.Length > 2 ? 0 : 1;

                var arguments = new Value[2];
                for (var i = start; i < list.Count; i++)
                {
                    arguments[0] = accumulator;
                    arguments[1] = list[i];
                    accumulator = vm.CallValue(callback, arguments.AsSpan(0, 2));
                }

                return accumulator;
            }, "Folds the list with a callback, optionally from an initial value.");
            Add(listMethods, "each", 2, 2, (vm, a) =>
            {
                var list = a[0].AsList();
                var callback = a[1];
                var arguments = new Value[1];
                for (var i = 0; i < list.Count; i++)
                {
                    arguments[0] = list[i];
                    vm.CallValue(callback, arguments.AsSpan(0, 1));
                }

                return a[0];
            }, "Calls the callback for each item.");
            Add(listMethods, "any", 2, 2, (vm, a) =>
            {
                var list = a[0].AsList();
                var callback = a[1];
                var arguments = new Value[1];
                for (var i = 0; i < list.Count; i++)
                {
                    arguments[0] = list[i];
                    if (vm.CallValue(callback, arguments.AsSpan(0, 1)).Truthy)
                    {
                        return Value.True;
                    }
                }

                return Value.False;
            }, "True when the callback accepts at least one item.");
            Add(listMethods, "all", 2, 2, (vm, a) =>
            {
                var list = a[0].AsList();
                var callback = a[1];
                var arguments = new Value[1];
                for (var i = 0; i < list.Count; i++)
                {
                    arguments[0] = list[i];
                    if (!vm.CallValue(callback, arguments.AsSpan(0, 1)).Truthy)
                    {
                        return Value.False;
                    }
                }

                return Value.True;
            }, "True when the callback accepts every item.");
            Add(listMethods, "find", 2, 2, (vm, a) =>
            {
                var list = a[0].AsList();
                var callback = a[1];
                var arguments = new Value[1];
                for (var i = 0; i < list.Count; i++)
                {
                    arguments[0] = list[i];
                    if (vm.CallValue(callback, arguments.AsSpan(0, 1)).Truthy)
                    {
                        return list[i];
                    }
                }

                return Value.Nil;
            }, "First item the callback accepts, or nil.");
            Add(listMethods, "findIndex", 2, 2, (vm, a) =>
            {
                var list = a[0].AsList();
                var callback = a[1];
                var arguments = new Value[1];
                for (var i = 0; i < list.Count; i++)
                {
                    arguments[0] = list[i];
                    if (vm.CallValue(callback, arguments.AsSpan(0, 1)).Truthy)
                    {
                        return new Value(i);
                    }
                }

                return new Value(-1);
            }, "Index of the first item the callback accepts, or -1.");
            Add(listMethods, "sum", 1, 1, (vm, a) =>
            {
                var list = a[0].AsList();
                var total = new Value(0L);
                for (var i = 0; i < list.Count; i++)
                {
                    total = vm.AddValues(total, list[i]);
                }

                return total;
            }, "Sum of the numeric items.");
            Add(listMethods, "min", 1, 1, (vm, a) => Extreme(vm, a[0].AsList(), smallest: true), "Smallest item by natural order.");
            Add(listMethods, "max", 1, 1, (vm, a) => Extreme(vm, a[0].AsList(), smallest: false), "Largest item by natural order.");
            Add(listMethods, "first", 1, 1, (vm, a) =>
            {
                var list = a[0].AsList();
                return list.Count > 0 ? list[0] : Value.Nil;
            }, "First item, or nil when empty.");
            Add(listMethods, "last", 1, 1, (vm, a) =>
            {
                var list = a[0].AsList();
                return list.Count > 0 ? list[list.Count - 1] : Value.Nil;
            }, "Last item, or nil when empty.");
            Add(listMethods, "take", 2, 2, (vm, a) =>
            {
                var list = a[0].AsList();
                var count = (int)Math.Max(0, Math.Min(RequireInt(a[1], "take", 1), list.Count));
                return Value.Of(list.Slice(0, count));
            }, "Copy of the first n items.");
            Add(listMethods, "drop", 2, 2, (vm, a) =>
            {
                var list = a[0].AsList();
                var count = (int)Math.Max(0, Math.Min(RequireInt(a[1], "drop", 1), list.Count));
                return Value.Of(list.Slice(count, list.Count));
            }, "Copy without the first n items.");
            Add(listMethods, "flatten", 1, 2, (vm, a) =>
            {
                var depth = a.Length > 1 ? RequireInt(a[1], "flatten", 1) : 1;
                var result = new TernList();
                Flatten(a[0].AsList(), result, depth);
                return Value.Of(result);
            }, "Flattens one level of nested lists.");
            Add(listMethods, "unique", 1, 1, (vm, a) =>
            {
                var list = a[0].AsList();
                var seen = new TernMap(Math.Max(4, list.Count));
                var result = new TernList(list.Count);
                for (var i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    if (seen.ContainsKey(item))
                    {
                        continue;
                    }

                    seen.Set(item, Value.True);
                    result.Add(item);
                }

                return Value.Of(result);
            }, "New list without duplicates, order preserved.");
            Add(listMethods, "clone", 1, 1, (vm, a) =>
            {
                var list = a[0].AsList();
                var copy = new TernList(list.Count);
                for (var i = 0; i < list.Count; i++)
                {
                    copy.Add(list[i]);
                }

                return Value.Of(copy);
            }, "Shallow copy.");
            Add(listMethods, "clear", 1, 1, (vm, a) =>
            {
                a[0].AsList().Clear();
                return a[0];
            }, "Removes every item in place.");
            Add(listMethods, "concat", 2, 2, (vm, a) => vm.AddValues(a[0], a[1]), "New list with the items of another list appended.");
            Add(listMethods, "toString", 1, 1, (vm, a) => Value.Str(a[0].AsList().ToString()), "Debug rendering of the list.");
            Add(listMethods, "isEmpty", 1, 1, (vm, a) => Value.Bool(a[0].AsList().Count == 0), "True when the list has no items.");

            var mapMethods = Table(ValueKind.Map);
            Add(mapMethods, "keys", 1, 1, (vm, a) => Value.Of(a[0].AsMap().Keys()), "List of keys in insertion order.");
            Add(mapMethods, "values", 1, 1, (vm, a) => Value.Of(a[0].AsMap().Values()), "List of values in insertion order.");
            Add(mapMethods, "entries", 1, 1, (vm, a) =>
            {
                var map = a[0].AsMap();
                var result = new TernList(map.Count);
                var slots = map.SlotCount;
                for (var i = 0; i < slots; i++)
                {
                    if (!map.TryGetEntry(i, out var key, out var value))
                    {
                        continue;
                    }

                    var pair = new TernList(2);
                    pair.Add(key);
                    pair.Add(value);
                    result.Add(Value.Of(pair));
                }

                return Value.Of(result);
            }, "List of [key, value] pairs in insertion order.");
            Add(mapMethods, "has", 2, 2, (vm, a) => Value.Bool(a[0].AsMap().ContainsKey(a[1])), "True when the key exists.");
            Add(mapMethods, "get", 2, 3, (vm, a) =>
            {
                if (a[0].AsMap().TryGet(a[1], out var value))
                {
                    return value;
                }

                return a.Length > 2 ? a[2] : Value.Nil;
            }, "Value for a key, or a default when missing.");
            Add(mapMethods, "set", 3, 3, (vm, a) =>
            {
                a[0].AsMap().Set(a[1], a[2]);
                return a[0];
            }, "Inserts a key value pair and returns the map.");
            Add(mapMethods, "remove", 2, 2, (vm, a) => Value.Bool(a[0].AsMap().Remove(a[1])), "Deletes a key; returns the removed value or nil.");
            Add(mapMethods, "clear", 1, 1, (vm, a) =>
            {
                a[0].AsMap().Clear();
                return a[0];
            }, "Removes every key in place.");
            Add(mapMethods, "merge", 2, 2, (vm, a) => vm.AddValues(a[0], a[1]), "New map with another map's pairs merged in.");
            Add(mapMethods, "clone", 1, 1, (vm, a) => Value.Of(a[0].AsMap().Clone()), "Shallow copy.");
            Add(mapMethods, "toString", 1, 1, (vm, a) => Value.Str(a[0].AsMap().ToString()), "Debug rendering of the map.");
            Add(mapMethods, "isEmpty", 1, 1, (vm, a) => Value.Bool(a[0].AsMap().Count == 0), "True when the map has no keys.");

            var intMethods = Table(ValueKind.Int);
            Add(intMethods, "toString", 1, 1, (vm, a) => Value.Str(a[0].ToDisplayString()), "Decimal representation.");
            Add(intMethods, "toFloat", 1, 1, (vm, a) => new Value((double)a[0].AsInt()), "The same value as a float.");
            Add(intMethods, "abs", 1, 1, (vm, a) => new Value(Math.Abs(a[0].AsInt())), "Absolute value.");
            Add(intMethods, "times", 2, 2, (vm, a) =>
            {
                var count = a[0].AsInt();
                var callback = a[1];
                var arguments = new Value[1];
                for (long i = 0; i < count; i++)
                {
                    arguments[0] = new Value(i);
                    vm.CallValue(callback, arguments.AsSpan(0, 1));
                }

                return a[0];
            }, "Calls the callback n times with the index.");
            Add(intMethods, "clamp", 3, 3, (vm, a) =>
            {
                var value = a[0].AsInt();
                var low = RequireInt(a[1], "clamp", 1);
                var high = RequireInt(a[2], "clamp", 2);
                return new Value(value < low ? low : value > high ? high : value);
            }, "Constrains the value to [low, high].");

            var floatMethods = Table(ValueKind.Float);
            Add(floatMethods, "toString", 1, 1, (vm, a) => Value.Str(Value.FormatDouble(a[0].AsDouble())), "Decimal representation.");
            Add(floatMethods, "toInt", 1, 1, (vm, a) => new Value((long)a[0].AsDouble()), "Truncates toward zero.");
            Add(floatMethods, "abs", 1, 1, (vm, a) => new Value(Math.Abs(a[0].AsDouble())), "Absolute value.");
            Add(floatMethods, "round", 1, 2, (vm, a) => RoundFloat(a), "Rounds to the nearest integer, with optional digits.");
            Add(floatMethods, "clamp", 3, 3, (vm, a) =>
            {
                var value = a[0].AsDouble();
                var low = a[1].AsDouble();
                var high = a[2].AsDouble();
                return new Value(value < low ? low : value > high ? high : value);
            }, "Constrains the value to [low, high].");

            var rangeMethods = Table(ValueKind.Range);
            Add(rangeMethods, "toList", 1, 1, (vm, a) => Value.Of(a[0].AsRange().ToList()), "Materialises the range as a list.");
            Add(rangeMethods, "contains", 2, 2, (vm, a) =>
                Value.Bool(a[1].Kind == ValueKind.Int && a[0].AsRange().Contains(a[1].AsInt())), "True when the number is inside the range.");
            Add(rangeMethods, "toString", 1, 1, (vm, a) => Value.Str(a[0].AsRange().ToString()), "Range notation such as 0..<5.");
            Add(rangeMethods, "length", 1, 1, (vm, a) => new Value(a[0].AsRange().Length), "Number of values in the range.");
        }

        private static Dictionary<string, TernNative> Table(ValueKind kind)
        {
            var table = new Dictionary<string, TernNative>(StringComparer.Ordinal);
            Tables[kind] = table;
            return table;
        }

        private static void Add(Dictionary<string, TernNative> table, string name, int min, int max, NativeFn function,
            string? documentation = null) =>
            table[name] = new TernNative(name, function, min, max, documentation);

        /// <summary>Every built-in method, for documentation and tooling.</summary>
        public static IEnumerable<(string Kind, string Name, int Min, int Max, string? Documentation)> Describe()
        {
            foreach (var (kind, table) in Tables)
            {
                foreach (var (name, native) in table)
                {
                    yield return (KindName(kind), name, native.MinArguments, native.MaxArguments, native.Documentation);
                }
            }
        }

        /// <summary>Script visible name for a value kind, matching <c>type()</c>.</summary>
        public static string KindName(ValueKind kind) => kind switch
        {
            ValueKind.Str => "string",
            ValueKind.List => "list",
            ValueKind.Map => "map",
            ValueKind.Int => "int",
            ValueKind.Float => "float",
            ValueKind.Range => "range",
            _ => kind.ToString().ToLowerInvariant(),
        };

        public static bool TryResolve(ValueKind kind, string name, out TernNative native)
        {
            native = null!;
            return Tables.TryGetValue(kind, out var table) && table.TryGetValue(name, out native!);
        }

        /// <summary>Properties that read like fields but are computed (list.length, range.start, ...).</summary>
        public static bool TryGetProperty(in Value target, string name, out Value value)
        {
            value = Value.Nil;

            switch (target.Kind)
            {
                case ValueKind.List:
                    if (name is "length" or "count" or "size")
                    {
                        value = new Value(target.AsList().Count);
                        return true;
                    }

                    break;

                case ValueKind.Str:
                    if (name is "length" or "count" or "size")
                    {
                        value = new Value(target.AsString().Length);
                        return true;
                    }

                    break;

                case ValueKind.Map:
                    if (name is "length" or "count" or "size")
                    {
                        value = new Value(target.AsMap().Count);
                        return true;
                    }

                    break;

                case ValueKind.Range:
                    if (name is "start")
                    {
                        value = new Value(target.AsRange().Start);
                        return true;
                    }

                    if (name is "end")
                    {
                        value = new Value(target.AsRange().End);
                        return true;
                    }

                    if (name is "length" or "count")
                    {
                        value = new Value(target.AsRange().Length);
                        return true;
                    }

                    break;

                case ValueKind.Instance:
                    break;
            }

            return false;
        }

        // ------------------------------------------------------------ shared helpers

        private static string[] SplitChars(string text)
        {
            var parts = new string[text.Length];
            for (var i = 0; i < text.Length; i++)
            {
                parts[i] = text[i].ToString();
            }

            return parts;
        }

        private static string ReplaceFirst(string text, string search, string replacement)
        {
            var index = text.IndexOf(search, StringComparison.Ordinal);
            if (index < 0)
            {
                return text;
            }

            return text[..index] + replacement + text[(index + search.Length)..];
        }

        private static string Pad(string text, int width, string fill, bool start)
        {
            if (width <= text.Length || fill.Length == 0)
            {
                return text;
            }

            var builder = new StringBuilder(width);
            var padding = width - text.Length;
            while (builder.Length < padding)
            {
                builder.Append(fill);
            }

            var pad = builder.ToString(0, padding);
            return start ? pad + text : text + pad;
        }

        private static Value Slice(ReadOnlySpan<Value> a)
        {
            var text = a[0].AsString();
            var start = a.Length > 1 ? (int)RequireInt(a[1], "slice", 1) : 0;
            start = start < 0 ? start + text.Length : start;
            start = Math.Max(0, Math.Min(start, text.Length));
            var end = a.Length > 2 ? (int)RequireInt(a[2], "slice", 2) : text.Length;
            end = end < 0 ? end + text.Length : end;
            end = Math.Max(start, Math.Min(end, text.Length));
            return Value.Str(text.Substring(start, end - start));
        }

        internal static long RequireInt(in Value value, string function, int index)
        {
            if (value.Kind == ValueKind.Int)
            {
                return value.AsInt();
            }

            if (value.Kind == ValueKind.Float)
            {
                var number = value.AsDouble();
                if (Math.Floor(number) == number && Math.Abs(number) < 9.2e18)
                {
                    return (long)number;
                }
            }

            throw new TernError("TypeError",
                $"{function}() argument {index + 1} must be an integer, got {value.ToDebugString()}");
        }

        private static Value ParseInteger(VM vm, ReadOnlySpan<Value> a, string name)
        {
            var text = a[0].AsString().Trim();
            if (long.TryParse(text, NumberStyles.Integer, Invariant, out var integer))
            {
                return new Value(integer);
            }

            if (a.Length > 1)
            {
                return a[1];
            }

            throw new TernError("ArgumentError", $"cannot parse '{text}' as an integer");
        }

        private static Value ParseFloat(VM vm, ReadOnlySpan<Value> a, string name)
        {
            var text = a[0].AsString().Trim();
            if (double.TryParse(text, NumberStyles.Float, Invariant, out var number))
            {
                return new Value(number);
            }

            if (a.Length > 1)
            {
                return a[1];
            }

            throw new TernError("ArgumentError", $"cannot parse '{text}' as a number");
        }

        private static Value RoundFloat(ReadOnlySpan<Value> a)
        {
            var value = a[0].AsDouble();
            var digits = a.Length > 1 ? (int)RequireInt(a[1], "round", 1) : 0;
            if (digits <= 0)
            {
                return new Value(Math.Round(value, MidpointRounding.AwayFromZero));
            }

            var factor = Math.Pow(10, digits);
            return new Value(Math.Round(value * factor, MidpointRounding.AwayFromZero) / factor);
        }

        private static Value Extreme(VM vm, TernList list, bool smallest)
        {
            if (list.Count == 0)
            {
                return Value.Nil;
            }

            var best = list[0];
            for (var i = 1; i < list.Count; i++)
            {
                var comparison = vm.CompareValues(list[i], best);
                if (smallest ? comparison < 0 : comparison > 0)
                {
                    best = list[i];
                }
            }

            return best;
        }

        private static void Flatten(TernList source, TernList target, long depth)
        {
            for (var i = 0; i < source.Count; i++)
            {
                var item = source[i];
                if (item.Kind == ValueKind.List && depth > 0)
                {
                    Flatten(item.AsList(), target, depth - 1);
                }
                else
                {
                    target.Add(item);
                }
            }
        }

        internal static Value Sort(VM vm, ReadOnlySpan<Value> a)
        {
            var list = a[0].AsList();
            var count = list.Count;
            if (count <= 1)
            {
                return a[0];
            }

            var items = new Value[count];
            for (var i = 0; i < count; i++)
            {
                items[i] = list[i];
            }

            if (a.Length > 1)
            {
                var callback = a[1];
                var arguments = new Value[2];
                Array.Sort(items, (left, right) =>
                {
                    arguments[0] = left;
                    arguments[1] = right;
                    var result = vm.CallValue(callback, arguments.AsSpan(0, 2));
                    return result.Kind == ValueKind.Int
                        ? (int)Math.Sign(result.AsInt())
                        : (int)Math.Sign(result.AsDouble());
                });
            }
            else
            {
                Array.Sort(items, (left, right) => vm.CompareValues(left, right));
            }

            for (var i = 0; i < count; i++)
            {
                list[i] = items[i];
            }

            return a[0];
        }
    }
}
