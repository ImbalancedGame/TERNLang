using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TERN.Compiler;
using TERN.Runtime;

namespace TERN.Builtins
{
    /// <summary>
    /// <c>std:cli</c> - command-line arguments read against a schema.
    ///
    /// The point of this module is that a program says what its options are
    /// once, as defaults, and gets the parsing, the types, the error messages
    /// and <c>--help</c> from it:
    ///
    /// <code>
    /// let options = cli.parse({
    ///     "out": "raytracer.png",   // a string option: --out PATH
    ///     "size": "320x240",        // a string option
    ///     "samples": 1,             // an integer option: --samples N
    ///     "ascii": false,           // a switch: --ascii
    /// });
    /// if (options == nil) { return; }        // --help was printed
    /// </code>
    ///
    /// That replaces the twenty-five-line <c>while</c> loop over
    /// <c>os.args()</c> that every example in this repository used to carry,
    /// four times over.
    ///
    /// The default's type decides how the option is read: a boolean is a switch
    /// that takes no value, an integer or float takes a number, and anything
    /// else takes the next argument as text. <c>--name=value</c> and
    /// <c>--name value</c> are both accepted, because people type both.
    /// </summary>
    internal static class Cli
    {
        /// <summary>
        /// The positional arguments left over by the last parse(). They live on
        /// the engine, next to <c>ScriptArguments</c>, because a process can run
        /// more than one script - the IDE does - and the leftovers of the last
        /// one must not be handed to the next. The static list is only for a VM
        /// with no engine behind it (a session that never had arguments).
        /// </summary>
        private static readonly List<string> Detached = new();

        public static void Install(TernEngine engine)
        {
            var map = new TernMap();
            StandardLibrary.AddPublic(map, "parse", 1, 2, (vm, a) => Parse(vm, a), "Reads the arguments against a schema of defaults; nil after printing --help.");
            StandardLibrary.AddPublic(map, "args", 0, 0, (vm, a) => PositionalList(vm), "The arguments that were not options, from the last parse.");
            StandardLibrary.AddPublic(map, "pair", 1, 2, (vm, a) => Pair(vm, a), "Splits \"320x240\" into [320, 240].");
            StandardLibrary.AddPublic(map, "has", 1, 1, (vm, a) => Value.Bool(HasFlag(vm, a[0])), "True when the argument was typed at all.");

            engine.RegisterValue("cli", Value.Of(map));
            engine.RegisterStdModule(new StdModule
            {
                Name = "std:cli",
                Members = map,
                FlatNames = Array.Empty<string>(),
            });
        }

        private static Value PositionalList(VM vm)
        {
            var positional = Positionals(vm);
            var list = new TernList(positional.Count);
            foreach (var argument in positional)
            {
                list.Add(Value.Str(argument));
            }

            return Value.Of(list);
        }

        private static bool HasFlag(VM vm, in Value name)
        {
            if (name.Kind != ValueKind.Str)
            {
                throw vm.Error("TypeError", $"has() expects an option name, not {vm.TypeName(name)}");
            }

            var wanted = name.AsString();
            foreach (var argument in Arguments(vm))
            {
                // `--` ends the options, so anything after it is positional even
                // when it starts with a dash: `-- --out` has no --out option.
                if (argument == "--")
                {
                    return false;
                }

                if (Matches(argument, wanted))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Matches(string argument, string name) =>
            argument == "--" + name || argument.StartsWith("--" + name + "=", StringComparison.Ordinal);

        private static IReadOnlyList<string> Arguments(VM vm) =>
            vm.Engine?.ScriptArguments ?? (IReadOnlyList<string>)Array.Empty<string>();

        private static List<string> Positionals(VM vm) => vm.Engine?.ScriptPositionals ?? Detached;

        private static Value Parse(VM vm, ReadOnlySpan<Value> a)
        {
            var schema = a[0];
            // The second argument is optional: a map of name to the one-line
            // description that --help prints beside it. Without it the help is
            // still correct, just terser.
            var notes = a.Length > 1 && a[1].Kind == ValueKind.Map ? a[1].AsMap() : null;
            if (schema.Kind != ValueKind.Map)
            {
                throw vm.Error("TypeError",
                    $"parse() expects a map of option names to defaults, not {vm.TypeName(schema)}");
            }

            var spec = schema.AsMap();
            var arguments = Arguments(vm);
            var results = new TernMap();
            var positional = Positionals(vm);
            positional.Clear();

            // The declared defaults, so an option that was not typed keeps its
            // default without the parser having to remember which keys were seen.
            var defaults = new List<(string Name, Value Default)>();
            var boolean = new HashSet<string>(StringComparer.Ordinal);
            for (var slot = 0; slot < spec.SlotCount; slot++)
            {
                if (!spec.TryGetEntry(slot, out var key, out var value) || key.Kind != ValueKind.Str)
                {
                    continue;
                }

                var name = key.AsString();
                defaults.Add((name, value));
                results.Set(Value.Str(name), value);
                if (value.Kind == ValueKind.Bool)
                {
                    boolean.Add(name);
                }
            }

            for (var i = 0; i < arguments.Count; i++)
            {
                var argument = arguments[i];
                if (argument == "--")
                {
                    // Everything after a bare `--` is positional, whatever it looks like.
                    for (var rest = i + 1; rest < arguments.Count; rest++)
                    {
                        positional.Add(arguments[rest]);
                    }

                    break;
                }

                if (argument == "--help" || argument == "-h")
                {
                    PrintUsage(vm, defaults, boolean, notes);
                    return Value.Nil;
                }

                if (!argument.StartsWith("-", StringComparison.Ordinal) || argument == "-")
                {
                    positional.Add(argument);
                    continue;
                }

                var name = argument.TrimStart('-');
                string? inline = null;
                var equals = name.IndexOf('=');
                if (equals >= 0)
                {
                    inline = name.Substring(equals + 1);
                    name = name.Substring(0, equals);
                }

                if (!Contains(defaults, name))
                {
                    throw vm.Error("UsageError", UnknownMessage(name, defaults));
                }

                if (boolean.Contains(name))
                {
                    // `--ascii` means true, `--ascii=false` means false, and
                    // `--ascii false` is left as a positional argument: a switch
                    // that sometimes eats the next word is worse than one that
                    // never does.
                    results.Set(Value.Str(name), Value.Bool(inline is null || Truthy(inline)));
                    continue;
                }

                if (inline is null)
                {
                    if (i + 1 >= arguments.Count)
                    {
                        throw vm.Error("UsageError",
                            $"--{name} needs a value: {usage_line(name, DefaultOf(defaults, name))}");
                    }

                    inline = arguments[++i];
                }

                results.Set(Value.Str(name), Convert(vm, name, inline, DefaultOf(defaults, name)));
            }

            return Value.Of(results);
        }

        private static Value DefaultOf(List<(string Name, Value Default)> defaults, string name)
        {
            foreach (var (key, value) in defaults)
            {
                if (key == name)
                {
                    return value;
                }
            }

            return Value.Nil;
        }

        private static bool Contains(List<(string Name, Value Default)> defaults, string name)
        {
            foreach (var (key, _) in defaults)
            {
                if (key == name)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A typed value, converted to whatever the default was. The conversion
        /// is strict: <c>--samples abc</c> is an error naming the option, not a
        /// zero that renders an empty picture.
        /// </summary>
        private static Value Convert(VM vm, string name, string text, in Value fallback)
        {
            switch (fallback.Kind)
            {
                case ValueKind.Int:
                {
                    if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    {
                        throw vm.Error("UsageError",
                            $"--{name} wants a whole number, not '{text}'; {usage_line(name, fallback)}");
                    }

                    return new Value(number);
                }

                case ValueKind.Float:
                {
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        throw vm.Error("UsageError",
                            $"--{name} wants a number, not '{text}'; {usage_line(name, fallback)}");
                    }

                    return new Value(number);
                }

                default:
                    return Value.Str(text);
            }
        }

        private static bool Truthy(string text) =>
            text is not ("false" or "0" or "no" or "off");

        private static string usage_line(string name, in Value fallback) =>
            fallback.Kind switch
            {
                ValueKind.Bool => $"--{name} takes no value",
                ValueKind.Int => $"--{name} N",
                ValueKind.Float => $"--{name} NUMBER",
                _ => $"--{name} TEXT",
            };

        private static void PrintUsage(VM vm, List<(string Name, Value Default)> defaults,
            HashSet<string> boolean, TernMap? notes)
        {
            vm.Output.WriteLine("usage: <script> [options]");
            vm.Output.WriteLine();
            vm.Output.WriteLine("options:");
            foreach (var (name, value) in defaults)
            {
                var shape = boolean.Contains(name)
                    ? $"--{name}"
                    : $"--{name} {value.Kind switch
                    {
                        ValueKind.Int => "N",
                        ValueKind.Float => "NUMBER",
                        _ => "TEXT",
                    }}";
                var fallback = value.Kind == ValueKind.Str ? $"\"{value.AsString()}\"" : value.ToDisplayString();
                var note = notes?.GetOrNil(Value.Str(name));
                var described = note is { Kind: ValueKind.Str } ? "  " + note.Value.AsString() : string.Empty;
                vm.Output.WriteLine($"  {shape,-20} default {fallback,-14}{described}");
            }

            vm.Output.WriteLine($"  {"--help",-20} this text");
            vm.Output.Flush();
        }

        /// <summary>
        /// The message for an option that does not exist, with the nearest name
        /// when there is one: <c>--framse</c> is a typo for <c>--frames</c>, and
        /// saying so costs four lines.
        /// </summary>
        private static string UnknownMessage(string name, List<(string Name, Value Default)> defaults)
        {
            var best = Nearest(name, defaults);
            var builder = new StringBuilder();
            builder.Append($"unknown option '--{name}'");
            builder.Append(best is not null ? $"; did you mean '--{best}'? The options are " : "; the options are ");
            for (var i = 0; i < defaults.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(i == defaults.Count - 1 ? " and " : ", ");
                }

                builder.Append($"--{defaults[i].Name}");
            }

            builder.Append('.');
            return builder.ToString();
        }

        /// <summary>The declared name within one edit of what was typed, if any.</summary>
        private static string? Nearest(string name, List<(string Name, Value Default)> defaults)
        {
            string? best = null;
            var bestDistance = int.MaxValue;
            foreach (var (candidate, _) in defaults)
            {
                var distance = Distance(name, candidate);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            // One edit covers a typo; two covers a transposition of two letters
            // in a short name, which is the other common slip.
            return best is not null && bestDistance <= (best.Length <= 4 ? 2 : 1) ? best : null;
        }

        /// <summary>
        /// Damerau-Levenshtein distance: insert, delete, substitute, and swap two
        /// neighbours. The swap matters, because `--samplse` for `--samples` is
        /// the most common typo there is and plain Levenshtein scores it as two
        /// edits, which would need the threshold raised until unrelated words
        /// started to match.
        /// </summary>
        private static int Distance(string a, string b)
        {
            var matrix = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++)
            {
                matrix[i, 0] = i;
            }

            for (var j = 0; j <= b.Length; j++)
            {
                matrix[0, j] = j;
            }

            for (var i = 1; i <= a.Length; i++)
            {
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    var best = Math.Min(
                        Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                        matrix[i - 1, j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    {
                        best = Math.Min(best, matrix[i - 2, j - 2] + 1);
                    }

                    matrix[i, j] = best;
                }
            }

            return matrix[a.Length, b.Length];
        }

        /// <summary>
        /// <c>cli.pair("320x240")</c> as <c>[320, 240]</c>. The size argument is
        /// the one option nearly every program with a picture in it has, and
        /// splitting it on "x" is four lines that are easy to get wrong
        /// (a negative number, a size with three parts, a height that is not a
        /// number at all).
        /// </summary>
        private static Value Pair(VM vm, ReadOnlySpan<Value> a)
        {
            if (a[0].Kind != ValueKind.Str)
            {
                throw vm.Error("TypeError", $"pair() expects text such as \"320x240\", not {vm.TypeName(a[0])}");
            }

            var text = a[0].AsString();
            // An empty name means "caller has none to give": saying "for " with
            // nothing after it reads like a bug in the message.
            var name = a.Length > 1 && a[1].Kind == ValueKind.Str ? a[1].AsString() : string.Empty;
            var what = name.Length > 0 ? $" for {name}" : string.Empty;
            var parts = text.Split('x', 'X', ',');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
                !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
            {
                throw vm.Error("UsageError",
                    $"'{text}'{what} is not a size: write WIDTHxHEIGHT, such as 320x240");
            }

            if (width < 1 || height < 1)
            {
                throw vm.Error("UsageError",
                    $"'{text}'{what} is not a size: both numbers have to be at least 1");
            }

            var list = new TernList(2);
            list.Add(new Value(width));
            list.Add(new Value(height));
            return Value.Of(list);
        }
    }
}
