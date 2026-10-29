using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TERN.Compiler;
using TERN.Runtime;

namespace TERN.Builtins
{
    /// <summary>
    /// The standard library: global functions plus the `math`, `json`, `fs`, `os`,
    /// `time`, `random`, `text` and `lists` namespaces. Hosts can either use this as
    /// is, extend it, or skip it entirely and expose only what their scripts need.
    /// </summary>
    public static class StandardLibrary
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public static void Install(TernEngine engine)
        {
            InstallGlobals(engine);
            InstallMath(engine);
            InstallJson(engine);
            InstallFileSystem(engine);
            InstallOs(engine);
            InstallTime(engine);
            InstallRandom(engine);
            InstallText(engine);
            InstallLists(engine);
        }

        // ---------------------------------------------------------------- globals

        private static void InstallGlobals(TernEngine engine)
        {
            engine.RegisterFunction("print", (vm, a) =>
            {
                for (var i = 0; i < a.Length; i++)
                {
                    if (i > 0)
                    {
                        vm.Output.Write(' ');
                    }

                    vm.Output.Write(a[i].ToDisplayString());
                }

                return Value.Nil;
            }, 0, -1, "Writes its arguments to stdout without a trailing newline.");

            engine.RegisterFunction("println", (vm, a) =>
            {
                for (var i = 0; i < a.Length; i++)
                {
                    if (i > 0)
                    {
                        vm.Output.Write(' ');
                    }

                    vm.Output.Write(a[i].ToDisplayString());
                }

                vm.Output.WriteLine();
                return Value.Nil;
            }, 0, -1, "Writes its arguments followed by a newline.");

            engine.RegisterFunction("eprint", (vm, a) =>
            {
                for (var i = 0; i < a.Length; i++)
                {
                    if (i > 0)
                    {
                        vm.ErrorOutput.Write(' ');
                    }

                    vm.ErrorOutput.Write(a[i].ToDisplayString());
                }

                return Value.Nil;
            }, 0, -1, "Writes its arguments to stderr.");

            engine.RegisterFunction("input", (vm, a) =>
            {
                if (a.Length > 0)
                {
                    vm.Output.Write(a[0].ToDisplayString());
                }

                var line = vm.Input.ReadLine();
                return line is null ? Value.Nil : Value.Str(line);
            }, 0, 1, "Reads one line from stdin.");

            engine.RegisterFunction("len", (vm, a) => new Value(Length(a[0])), 1, 1,
                "Number of items in a string, list, map or range.");

            engine.RegisterFunction("type", (vm, a) => Value.Str(vm.TypeName(a[0])), 1, 1,
                "The type name of a value.");

            engine.RegisterFunction("str", (vm, a) => Value.Str(a[0].ToDisplayString()), 1, 1,
                "Converts a value to its string form.");

            engine.RegisterFunction("repr", (vm, a) => Value.Str(a[0].ToDebugString()), 1, 1,
                "Debug representation of a value (strings are quoted).");

            engine.RegisterFunction("int", (vm, a) => ToInt(vm, a), 1, 2, "Converts a value to an integer.");
            engine.RegisterFunction("float", (vm, a) => ToFloat(vm, a), 1, 1, "Converts a value to a floating point number.");
            engine.RegisterFunction("bool", (vm, a) => Value.Bool(a[0].Truthy), 1, 1, "Converts a value to a boolean.");

            engine.RegisterFunction("chr", (vm, a) => Value.Str(char.ConvertFromUtf32((int)BuiltinMethods.RequireInt(a[0], "chr", 0))), 1, 1,
                "Unicode code point to string.");
            engine.RegisterFunction("ord", (vm, a) =>
            {
                var text = a[0].AsString();
                if (text.Length == 0)
                {
                    throw vm.Error("ArgumentError", "ord() requires a non-empty string");
                }

                return new Value(char.ConvertToUtf32(text, 0));
            }, 1, 1, "First character of a string as a code point.");

            engine.RegisterFunction("range", (vm, a) =>
            {
                long start;
                long end;
                long step = 1;

                if (a.Length == 1)
                {
                    start = 0;
                    end = BuiltinMethods.RequireInt(a[0], "range", 0);
                }
                else
                {
                    start = BuiltinMethods.RequireInt(a[0], "range", 0);
                    end = BuiltinMethods.RequireInt(a[1], "range", 1);

                    if (a.Length > 2)
                    {
                        step = BuiltinMethods.RequireInt(a[2], "range", 2);
                    }
                }

                return Value.Of(new TernRange(start, end, inclusive: false, step));
            }, 1, 3, "Creates an exclusive integer range.");

            engine.RegisterFunction("keys", (vm, a) => Value.Of(a[0].AsMap().Keys()), 1, 1, "Keys of a map.");
            engine.RegisterFunction("values", (vm, a) => Value.Of(a[0].AsMap().Values()), 1, 1, "Values of a map.");
            engine.RegisterFunction("has", (vm, a) => Value.Bool(a[0].AsMap().ContainsKey(a[1])), 2, 2,
                "True when the map contains the key.");

            engine.RegisterFunction("assert", (vm, a) =>
            {
                if (!a[0].Truthy)
                {
                    var message = a.Length > 1 ? a[1].ToDisplayString() : "assertion failed";
                    throw new TernError("AssertionError", message);
                }

                return Value.Nil;
            }, 1, 2, "Throws unless the condition is truthy.");

            engine.RegisterFunction("error", (vm, a) => throw new TernError("Error", a[0].ToDisplayString()), 1, 1,
                "Raises an error.");

            engine.RegisterFunction("panic", (vm, a) => throw new TernError("Panic", a.Length > 0 ? a[0].ToDisplayString() : "panic"), 0, 1,
                "Aborts the script with a panic error.");

            engine.RegisterFunction("abs", (vm, a) => a[0].Kind == ValueKind.Int
                ? new Value(Math.Abs(a[0].AsInt()))
                : new Value(Math.Abs(a[0].AsDouble())), 1, 1, "Absolute value.");

            engine.RegisterFunction("min", (vm, a) => Extreme(vm, a, smallest: true), 1, -1, "Smallest argument.");
            engine.RegisterFunction("max", (vm, a) => Extreme(vm, a, smallest: false), 1, -1, "Largest argument.");

            engine.RegisterFunction("copy", (vm, a) => a[0].Kind switch
            {
                ValueKind.List => Value.Of(CopyList(a[0].AsList())),
                ValueKind.Map => Value.Of(a[0].AsMap().Clone()),
                _ => a[0],
            }, 1, 1, "Shallow copy of a list or map.");

            engine.RegisterFunction("sleep", (vm, a) =>
            {
                var milliseconds = BuiltinMethods.RequireInt(a[0], "sleep", 0);
                if (milliseconds > 0 && milliseconds < 60_000)
                {
                    System.Threading.Thread.Sleep((int)milliseconds);
                }

                return Value.Nil;
            }, 1, 1, "Blocks for the given number of milliseconds.");
        }

        private static TernList CopyList(TernList source)
        {
            var copy = new TernList(source.Count);
            for (var i = 0; i < source.Count; i++)
            {
                copy.Add(source[i]);
            }

            return copy;
        }

        private static long Length(in Value value) => value.Kind switch
        {
            ValueKind.Str => value.AsString().Length,
            ValueKind.List => value.AsList().Count,
            ValueKind.Map => value.AsMap().Count,
            ValueKind.Range => value.AsRange().Length,
            ValueKind.Instance => value.AsInstance().Fields.Length,
            _ => throw new TernError("TypeError", $"len() is not defined for {value.ToDebugString()}")
        };

        private static Value ToInt(VM vm, ReadOnlySpan<Value> a)
        {
            var source = a[0];
            switch (source.Kind)
            {
                case ValueKind.Int:
                    return source;
                case ValueKind.Float:
                    return new Value((long)source.AsDouble());
                case ValueKind.Bool:
                    return new Value(source.AsBool() ? 1 : 0);
                case ValueKind.Nil:
                    return new Value(0);
                case ValueKind.Str:
                {
                    var text = source.AsString().Trim();
                    var radix = a.Length > 1 ? (int)BuiltinMethods.RequireInt(a[1], "int", 1) : 10;

                    try
                    {
                        if (radix != 10)
                        {
                            return new Value(Convert.ToInt64(text, radix));
                        }

                        if (long.TryParse(text, NumberStyles.Integer, Invariant, out var integer))
                        {
                            return new Value(integer);
                        }

                        if (double.TryParse(text, NumberStyles.Float, Invariant, out var number))
                        {
                            return new Value((long)number);
                        }
                    }
                    catch (Exception)
                    {
                        // fall through to the error below
                    }

                    throw vm.Error("ArgumentError", $"cannot convert '{text}' to an integer");
                }
                default:
                    throw vm.Error("TypeError", $"cannot convert {vm.TypeName(source)} to an integer");
            }
        }

        private static Value ToFloat(VM vm, ReadOnlySpan<Value> a)
        {
            var source = a[0];
            switch (source.Kind)
            {
                case ValueKind.Float:
                    return source;
                case ValueKind.Int:
                    return new Value((double)source.AsInt());
                case ValueKind.Bool:
                    return new Value(source.AsBool() ? 1.0 : 0.0);
                case ValueKind.Nil:
                    return new Value(0.0);
                case ValueKind.Str:
                {
                    var text = source.AsString().Trim();
                    if (double.TryParse(text, NumberStyles.Float, Invariant, out var number))
                    {
                        return new Value(number);
                    }

                    throw vm.Error("ArgumentError", $"cannot convert '{text}' to a number");
                }
                default:
                    throw vm.Error("TypeError", $"cannot convert {vm.TypeName(source)} to a number");
            }
        }

        // ------------------------------------------------------------------- math

        private static void InstallMath(TernEngine engine)
        {
            var map = new TernMap();
            map.Set(Value.Str("pi"), new Value(Math.PI));
            map.Set(Value.Str("e"), new Value(Math.E));
            map.Set(Value.Str("tau"), new Value(Math.PI * 2));
            map.Set(Value.Str("infinity"), new Value(double.PositiveInfinity));
            map.Set(Value.Str("nan"), new Value(double.NaN));

            Add(map, "abs", 1, 1, (vm, a) => a[0].Kind == ValueKind.Int
                ? new Value(Math.Abs(a[0].AsInt()))
                : new Value(Math.Abs(a[0].AsDouble())), "Absolute value.");
            Add(map, "ceil", 1, 1, (vm, a) => new Value(Math.Ceiling(a[0].AsDouble())), "Rounds up.");
            Add(map, "floor", 1, 1, (vm, a) => new Value(Math.Floor(a[0].AsDouble())), "Rounds down.");
            Add(map, "round", 1, 2, (vm, a) => a.Length > 1
                ? new Value(Math.Round(a[0].AsDouble(), (int)BuiltinMethods.RequireInt(a[1], "round", 1), MidpointRounding.AwayFromZero))
                : new Value(Math.Round(a[0].AsDouble(), MidpointRounding.AwayFromZero)), "Rounds to nearest, optional digits.");
            Add(map, "trunc", 1, 1, (vm, a) => new Value(Math.Truncate(a[0].AsDouble())), "Drops the fractional part.");
            Add(map, "sqrt", 1, 1, (vm, a) => new Value(Math.Sqrt(a[0].AsDouble())), "Square root.");
            Add(map, "cbrt", 1, 1, (vm, a) => new Value(Math.Cbrt(a[0].AsDouble())), "Cube root.");
            Add(map, "pow", 2, 2, (vm, a) => new Value(Math.Pow(a[0].AsDouble(), a[1].AsDouble())), "base ** exponent.");
            Add(map, "exp", 1, 1, (vm, a) => new Value(Math.Exp(a[0].AsDouble())), "e raised to the power x.");
            Add(map, "log", 1, 2, (vm, a) => a.Length > 1
                ? new Value(Math.Log(a[0].AsDouble(), a[1].AsDouble()))
                : new Value(Math.Log(a[0].AsDouble())), "Natural logarithm (optional base).");
            Add(map, "log2", 1, 1, (vm, a) => new Value(Math.Log2(a[0].AsDouble())), "Base 2 logarithm.");
            Add(map, "log10", 1, 1, (vm, a) => new Value(Math.Log10(a[0].AsDouble())), "Base 10 logarithm.");
            Add(map, "sin", 1, 1, (vm, a) => new Value(Math.Sin(a[0].AsDouble())), "Sine of an angle in radians.");
            Add(map, "cos", 1, 1, (vm, a) => new Value(Math.Cos(a[0].AsDouble())), "Cosine of an angle in radians.");
            Add(map, "tan", 1, 1, (vm, a) => new Value(Math.Tan(a[0].AsDouble())), "Tangent of an angle in radians.");
            Add(map, "asin", 1, 1, (vm, a) => new Value(Math.Asin(a[0].AsDouble())), "Arc sine.");
            Add(map, "acos", 1, 1, (vm, a) => new Value(Math.Acos(a[0].AsDouble())), "Arc cosine.");
            Add(map, "atan", 1, 1, (vm, a) => new Value(Math.Atan(a[0].AsDouble())), "Arc tangent.");
            Add(map, "atan2", 2, 2, (vm, a) => new Value(Math.Atan2(a[0].AsDouble(), a[1].AsDouble())), "Angle of the vector (x, y).");
            Add(map, "min", 1, -1, (vm, a) => Extreme(vm, a, smallest: true), "Smallest argument.");
            Add(map, "max", 1, -1, (vm, a) => Extreme(vm, a, smallest: false), "Largest argument.");
            Add(map, "clamp", 3, 3, (vm, a) =>
            {
                var value = a[0].AsDouble();
                var low = a[1].AsDouble();
                var high = a[2].AsDouble();
                var clamped = value < low ? low : value > high ? high : value;
                return a[0].Kind == ValueKind.Int && a[1].Kind == ValueKind.Int && a[2].Kind == ValueKind.Int
                    ? new Value((long)clamped)
                    : new Value(clamped);
            }, "Constrains a value to [low, high].");
            Add(map, "sign", 1, 1, (vm, a) => new Value(Math.Sign(a[0].AsDouble())), "-1, 0 or 1.");
            Add(map, "hypot", 2, 2, (vm, a) => new Value(Math.Sqrt(a[0].AsDouble() * a[0].AsDouble() + a[1].AsDouble() * a[1].AsDouble())), "Length of the vector (x, y).");
            Add(map, "gcd", 2, 2, (vm, a) => new Value(Gcd(Math.Abs(BuiltinMethods.RequireInt(a[0], "gcd", 0)), Math.Abs(BuiltinMethods.RequireInt(a[1], "gcd", 1)))), "Greatest common divisor.");
            Add(map, "lcm", 2, 2, (vm, a) => new Value(Lcm(Math.Abs(BuiltinMethods.RequireInt(a[0], "lcm", 0)), Math.Abs(BuiltinMethods.RequireInt(a[1], "lcm", 1)))), "Least common multiple.");
            Add(map, "isInteger", 1, 1, (vm, a) => Value.Bool(a[0].Kind == ValueKind.Int ||
                                                              (a[0].Kind == ValueKind.Float && Math.Floor(a[0].AsDouble()) == a[0].AsDouble())), "True for whole numbers.");
            Add(map, "isNaN", 1, 1, (vm, a) => Value.Bool(a[0].Kind == ValueKind.Float && double.IsNaN(a[0].AsDouble())), "True for NaN.");
            Add(map, "toRadians", 1, 1, (vm, a) => new Value(a[0].AsDouble() * Math.PI / 180.0), "Degrees to radians.");
            Add(map, "toDegrees", 1, 1, (vm, a) => new Value(a[0].AsDouble() * 180.0 / Math.PI), "Radians to degrees.");

            engine.RegisterValue("math", Value.Of(map));
            engine.RegisterStdModule(new StdModule
            {
                Name = "std:math",
                Members = map,
                FlatNames = new[] { "abs", "ceil", "floor", "round", "sqrt", "pow", "log", "sin", "cos", "tan", "min", "max", "clamp", "sign" },
            });
        }

        private static long Gcd(long a, long b)
        {
            while (b != 0)
            {
                var t = b;
                b = a % b;
                a = t;
            }

            return a;
        }

        private static long Lcm(long a, long b) => a == 0 || b == 0 ? 0 : a / Gcd(a, b) * b;

        private static Value Extreme(VM vm, ReadOnlySpan<Value> a, bool smallest)
        {
            var best = a[0];
            for (var i = 1; i < a.Length; i++)
            {
                var comparison = vm.CompareValues(a[i], best);
                if (smallest ? comparison < 0 : comparison > 0)
                {
                    best = a[i];
                }
            }

            return best;
        }

        // ------------------------------------------------------------------- json

        private static void InstallJson(TernEngine engine)
        {
            var map = new TernMap();
            Add(map, "parse", 1, 1, (vm, a) =>
            {
                var text = a[0].AsString();
                var index = 0;
                var value = JsonParser.Parse(text, ref index, vm);
                JsonParser.SkipWhitespace(text, ref index);
                if (index < text.Length)
                {
                    throw vm.Error("JsonError", $"unexpected trailing content at offset {index}");
                }

                return value;
            }, "Parses JSON text into TERN values.");
            Add(map, "stringify", 1, 2, (vm, a) =>
            {
                var indent = a.Length > 1 && a[1].Truthy ? 2 : 0;
                var builder = new StringBuilder();
                JsonWriter.Write(builder, a[0], indent, 0);
                return Value.Str(builder.ToString());
            }, "Serialises a value as JSON, optional indent.");
            Add(map, "tryParse", 1, 2, (vm, a) =>
            {
                try
                {
                    var text = a[0].AsString();
                    var index = 0;
                    var value = JsonParser.Parse(text, ref index, vm);
                    return value;
                }
                catch (TernError)
                {
                    return a.Length > 1 ? a[1] : Value.Nil;
                }
            }, "Parses, returning nil instead of raising on bad input.");

            engine.RegisterValue("json", Value.Of(map));
            engine.RegisterStdModule(new StdModule
            {
                Name = "std:json",
                Members = map,
                FlatNames = new[] { "parse", "stringify", "tryParse" },
            });
        }

        // ------------------------------------------------------------- filesystem

        private static void InstallFileSystem(TernEngine engine)
        {
            var map = new TernMap();

            Add(map, "read", 1, 1, (vm, a) =>
            {
                var path = RequirePath(vm, a[0]);
                return Value.Str(File.ReadAllText(path));
            }, "Reads a whole file as text.");
            Add(map, "readBytes", 1, 1, (vm, a) =>
            {
                var path = RequirePath(vm, a[0]);
                var bytes = File.ReadAllBytes(path);
                var list = new TernList(bytes.Length);
                foreach (var b in bytes)
                {
                    list.Add(new Value(b));
                }

                return Value.Of(list);
            }, "Reads a file as a list of byte values.");
            Add(map, "write", 2, 2, (vm, a) =>
            {
                var path = RequirePath(vm, a[0]);
                EnsureDirectory(path);
                File.WriteAllText(path, a[1].ToDisplayString());
                return Value.Nil;
            }, "Writes text, replacing the file.");
            Add(map, "append", 2, 2, (vm, a) =>
            {
                var path = RequirePath(vm, a[0]);
                EnsureDirectory(path);
                File.AppendAllText(path, a[1].ToDisplayString());
                return Value.Nil;
            }, "Appends text to a file.");
            Add(map, "exists", 1, 1, (vm, a) => Value.Bool(File.Exists(RequirePath(vm, a[0])) || Directory.Exists(RequirePath(vm, a[0]))), "True when the path exists.");
            Add(map, "size", 1, 1, (vm, a) => new Value(new FileInfo(RequirePath(vm, a[0])).Length), "Size of a file in bytes.");
            Add(map, "remove", 1, 1, (vm, a) =>
            {
                var path = RequirePath(vm, a[0]);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }

                return Value.Nil;
            }, "Deletes a file.");
            Add(map, "list", 1, 1, (vm, a) =>
            {
                var path = RequirePath(vm, a[0]);
                var entries = Directory.GetFileSystemEntries(path);
                Array.Sort(entries, StringComparer.Ordinal);
                var list = new TernList(entries.Length);
                foreach (var entry in entries)
                {
                    list.Add(Value.Str(Path.GetFileName(entry)));
                }

                return Value.Of(list);
            }, "Directory entries as a list.");
            Add(map, "mkdir", 1, 1, (vm, a) =>
            {
                Directory.CreateDirectory(RequirePath(vm, a[0]));
                return Value.Nil;
            }, "Creates a directory.");
            Add(map, "isFile", 1, 1, (vm, a) => Value.Bool(File.Exists(RequirePath(vm, a[0]))), "True for a regular file.");
            Add(map, "isDir", 1, 1, (vm, a) => Value.Bool(Directory.Exists(RequirePath(vm, a[0]))), "True for a directory.");

            engine.RegisterValue("fs", Value.Of(map));
            engine.RegisterStdModule(new StdModule
            {
                Name = "std:fs",
                Members = map,
                FlatNames = new[] { "read", "write", "append", "exists", "remove", "list", "mkdir" },
            });
        }

        private static string RequirePath(VM vm, in Value value)
        {
            var engine = vm.Engine ?? throw vm.Error("InternalError", "no engine attached to this VM");

            if (!engine.AllowFileSystem)
            {
                throw vm.Error("PermissionError", "file system access is disabled for this script");
            }

            var path = value.AsString();
            if (Path.IsPathRooted(path))
            {
                return path;
            }

            return Path.GetFullPath(Path.Combine(engine.WorkingDirectory, path));
        }

        private static void EnsureDirectory(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        // --------------------------------------------------------------------- os

        private static void InstallOs(TernEngine engine)
        {
            var map = new TernMap();
            Add(map, "env", 1, 2, (vm, a) =>
            {
                var name = a[0].AsString();
                var value = Environment.GetEnvironmentVariable(name);
                if (value is not null)
                {
                    return Value.Str(value);
                }

                return a.Length > 1 ? a[1] : Value.Nil;
            }, "Environment variable value, or nil.");
            Add(map, "setEnv", 2, 2, (vm, a) =>
            {
                Environment.SetEnvironmentVariable(a[0].AsString(), a[1].ToDisplayString());
                return Value.Nil;
            }, "Sets an environment variable for this process.");
            Add(map, "cwd", 0, 0, (vm, a) => Value.Str(vm.Engine?.WorkingDirectory ?? Directory.GetCurrentDirectory()), "Current working directory.");
            Add(map, "platform", 0, 0, (vm, a) => Value.Str(OperatingSystem.IsWindows() ? "windows" :
                OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "unknown"), "Host platform name.");
            Add(map, "args", 0, 0, (vm, a) =>
            {
                var arguments = vm.Engine?.ScriptArguments;
                var list = new TernList(arguments?.Count ?? 0);
                if (arguments is not null)
                {
                    foreach (var argument in arguments)
                    {
                        list.Add(Value.Str(argument));
                    }
                }

                return Value.Of(list);
            }, "Script arguments as a list.");
            Add(map, "joinPath", 0, -1, (vm, a) =>
            {
                var parts = new string[a.Length];
                for (var i = 0; i < a.Length; i++)
                {
                    parts[i] = a[i].AsString();
                }

                return Value.Str(Path.Combine(parts));
            }, "Joins path segments for this platform.");
            Add(map, "lineSeparator", 0, 0, (vm, a) => Value.Str(Environment.NewLine), "The platform line separator.");

            engine.RegisterValue("os", Value.Of(map));
            engine.RegisterStdModule(new StdModule
            {
                Name = "std:os",
                Members = map,
                FlatNames = new[] { "env", "cwd", "platform", "args" },
            });
        }

        // ------------------------------------------------------------------- time

        private static void InstallTime(TernEngine engine)
        {
            var map = new TernMap();
            Add(map, "now", 0, 0, (vm, a) => new Value(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0), "Seconds since the Unix epoch as a float.");
            Add(map, "millis", 0, 0, (vm, a) => new Value(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), "Milliseconds since the Unix epoch.");
            Add(map, "sleep", 1, 1, (vm, a) =>
            {
                var milliseconds = BuiltinMethods.RequireInt(a[0], "sleep", 0);
                if (milliseconds > 0 && milliseconds < 60_000)
                {
                    System.Threading.Thread.Sleep((int)milliseconds);
                }

                return Value.Nil;
            }, "Suspends the script for a number of seconds.");
            Add(map, "iso", 1, 1, (vm, a) =>
            {
                var seconds = a[0].AsDouble();
                return Value.Str(DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).UtcDateTime
                    .ToString("yyyy-MM-ddTHH:mm:ssZ", Invariant));
            }, "Current time as an ISO 8601 string.");

            engine.RegisterValue("time", Value.Of(map));
            engine.RegisterStdModule(new StdModule
            {
                Name = "std:time",
                Members = map,
                FlatNames = new[] { "now", "millis", "sleep" },
            });
        }

        // ----------------------------------------------------------------- random

        private static void InstallRandom(TernEngine engine)
        {
            var map = new TernMap();
            Add(map, "seed", 1, 1, (vm, a) =>
            {
                var engineRef = vm.Engine;
                if (engineRef is not null)
                {
                    engineRef.SeedRandom(BuiltinMethods.RequireInt(a[0], "seed", 0));
                }

                return Value.Nil;
            }, "Seeds the generator for reproducible runs.");
            Add(map, "float", 0, 2, (vm, a) =>
            {
                var generator = vm.Engine?.Random ?? Random.Shared;
                var low = a.Length > 0 ? a[0].AsDouble() : 0.0;
                var high = a.Length > 1 ? a[1].AsDouble() : 1.0;
                return new Value(low + generator.NextDouble() * (high - low));
            }, "Random float in [low, high).");
            Add(map, "int", 1, 2, (vm, a) =>
            {
                var generator = vm.Engine?.Random ?? Random.Shared;
                var low = a.Length > 1 ? BuiltinMethods.RequireInt(a[0], "int", 0) : 0;
                var high = a.Length > 1 ? BuiltinMethods.RequireInt(a[1], "int", 1) : BuiltinMethods.RequireInt(a[0], "int", 0);

                if (high < low)
                {
                    (low, high) = (high, low);
                }

                return new Value(generator.NextInt64(low, high + 1));
            }, "Random integer in [low, high].");
            Add(map, "bool", 0, 0, (vm, a) => Value.Bool((vm.Engine?.Random ?? Random.Shared).Next(2) == 0), "Random boolean.");
            Add(map, "choice", 1, 1, (vm, a) =>
            {
                var list = a[0].AsList();
                if (list.Count == 0)
                {
                    return Value.Nil;
                }

                return list[(int)((vm.Engine?.Random ?? Random.Shared).NextInt64(list.Count))];
            }, "Random item from a list.");
            Add(map, "shuffle", 1, 1, (vm, a) =>
            {
                var list = a[0].AsList();
                var generator = vm.Engine?.Random ?? Random.Shared;
                for (var i = list.Count - 1; i > 0; i--)
                {
                    var j = (int)generator.NextInt64(i + 1);
                    (list[i], list[j]) = (list[j], list[i]);
                }

                return a[0];
            }, "Shuffles a list in place.");

            engine.RegisterValue("random", Value.Of(map));
            engine.RegisterStdModule(new StdModule
            {
                Name = "std:random",
                Members = map,
                FlatNames = new[] { "seed", "float", "int", "choice", "shuffle" },
            });
        }

        // ------------------------------------------------------------------- text

        private static void InstallText(TernEngine engine)
        {
            var map = new TernMap();
            Add(map, "format", 1, -1, (vm, a) =>
            {
                var template = a[0].AsString();
                var builder = new StringBuilder(template.Length + 16);
                var argument = 1;
                var index = 0;

                while (index < template.Length)
                {
                    var c = template[index];
                    if (c == '{')
                    {
                        if (index + 1 < template.Length && template[index + 1] == '{')
                        {
                            builder.Append('{');
                            index += 2;
                            continue;
                        }

                        var close = template.IndexOf('}', index);
                        if (close < 0)
                        {
                            throw vm.Error("FormatError", "unclosed '{' in format string");
                        }

                        var specifier = template.Substring(index + 1, close - index - 1).Trim();
                        Value value;
                        if (specifier.Length == 0)
                        {
                            if (argument >= a.Length)
                            {
                                throw vm.Error("FormatError", "not enough arguments for format string");
                            }

                            value = a[argument++];
                        }
                        else if (int.TryParse(specifier, out var position))
                        {
                            if (position + 1 >= a.Length)
                            {
                                throw vm.Error("FormatError", $"format argument {{{position}}} is missing");
                            }

                            value = a[position + 1];

                            // Keep positional placeholders in step with explicit ones:
                            // "text.format(\"{0} and {}\", \"a\", \"b\")" yields "a and b".
                            argument = Math.Max(argument, position + 2);
                        }
                        else
                        {
                            throw vm.Error("FormatError", $"unsupported format specifier '{{{specifier}}}'");
                        }

                        builder.Append(value.ToDisplayString());
                        index = close + 1;
                        continue;
                    }

                    if (c == '}' && index + 1 < template.Length && template[index + 1] == '}')
                    {
                        builder.Append('}');
                        index += 2;
                        continue;
                    }

                    builder.Append(c);
                    index++;
                }

                return Value.Str(builder.ToString());
            }, "Formats {} and {0} placeholders with arguments.");

            Add(map, "upper", 1, 1, (vm, a) => Value.Str(a[0].AsString().ToUpperInvariant()), "Uppercased copy.");
            Add(map, "lower", 1, 1, (vm, a) => Value.Str(a[0].AsString().ToLowerInvariant()), "Lowercased copy.");
            Add(map, "trim", 1, 1, (vm, a) => Value.Str(a[0].AsString().Trim()), "Removes surrounding whitespace.");
            Add(map, "split", 2, 2, (vm, a) =>
            {
                var pieces = a[0].AsString().Split(new[] { a[1].AsString() }, StringSplitOptions.None);
                var list = new TernList(pieces.Length);
                foreach (var piece in pieces)
                {
                    list.Add(Value.Str(piece));
                }

                return Value.Of(list);
            }, "Splits on a separator.");
            Add(map, "join", 2, 2, (vm, a) =>
            {
                var list = a[1].AsList();
                var separator = a[0].AsString();
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
            }, "Joins a list with a separator.");
            Add(map, "pad", 2, 3, (vm, a) =>
            {
                var text = a[0].AsString();
                var width = (int)BuiltinMethods.RequireInt(a[1], "pad", 1);
                var fill = a.Length > 2 ? a[2].AsString() : " ";
                if (text.Length >= width || fill.Length == 0)
                {
                    return Value.Str(text);
                }

                var builder = new StringBuilder();
                while (builder.Length < width - text.Length)
                {
                    builder.Append(fill);
                }

                return Value.Str(text + builder.ToString(0, width - text.Length));
            }, "Pads a string to a width on the right.");

            engine.RegisterValue("text", Value.Of(map));
            engine.RegisterStdModule(new StdModule
            {
                Name = "std:text",
                Members = map,
                FlatNames = new[] { "format", "join" },
            });
        }

        // ------------------------------------------------------------------ lists

        private static void InstallLists(TernEngine engine)
        {
            var map = new TernMap();
            Add(map, "range", 1, 3, (vm, a) =>
            {
                long start;
                long end;
                long step = 1;

                if (a.Length == 1)
                {
                    start = 0;
                    end = BuiltinMethods.RequireInt(a[0], "range", 0);
                }
                else
                {
                    start = BuiltinMethods.RequireInt(a[0], "range", 0);
                    end = BuiltinMethods.RequireInt(a[1], "range", 1);
                    if (a.Length > 2)
                    {
                        step = BuiltinMethods.RequireInt(a[2], "range", 2);
                    }
                }

                return Value.Of(new TernRange(start, end, inclusive: false, step).ToList());
            }, "List of integers from start to end.");
            Add(map, "repeat", 2, 2, (vm, a) =>
            {
                var count = (int)Math.Max(0, BuiltinMethods.RequireInt(a[1], "repeat", 1));
                var list = new TernList(count);
                for (var i = 0; i < count; i++)
                {
                    list.Add(a[0]);
                }

                return Value.Of(list);
            }, "List with a value repeated n times.");
            Add(map, "zip", 1, -1, (vm, a) =>
            {
                var shortest = int.MaxValue;
                for (var i = 0; i < a.Length; i++)
                {
                    shortest = Math.Min(shortest, a[i].AsList().Count);
                }

                var result = new TernList(shortest);
                for (var index = 0; index < shortest; index++)
                {
                    var pair = new TernList(a.Length);
                    for (var i = 0; i < a.Length; i++)
                    {
                        pair.Add(a[i].AsList()[index]);
                    }

                    result.Add(Value.Of(pair));
                }

                return Value.Of(result);
            }, "Pairs two lists into [a, b] entries.");
            Add(map, "enumerate", 1, 1, (vm, a) =>
            {
                var list = a[0].AsList();
                var result = new TernList(list.Count);
                for (var i = 0; i < list.Count; i++)
                {
                    var pair = new TernList(2);
                    pair.Add(new Value(i));
                    pair.Add(list[i]);
                    result.Add(Value.Of(pair));
                }

                return Value.Of(result);
            }, "Index/value pairs.");
            Add(map, "chunk", 2, 2, (vm, a) =>
            {
                var list = a[0].AsList();
                var size = (int)Math.Max(1, BuiltinMethods.RequireInt(a[1], "chunk", 1));
                var result = new TernList();
                for (var i = 0; i < list.Count; i += size)
                {
                    var chunk = new TernList(Math.Min(size, list.Count - i));
                    for (var j = i; j < Math.Min(i + size, list.Count); j++)
                    {
                        chunk.Add(list[j]);
                    }

                    result.Add(Value.Of(chunk));
                }

                return Value.Of(result);
            }, "Splits into lists of at most n items.");
            Add(map, "sort", 1, 2, (vm, a) => BuiltinMethods.Sort(vm, a), "Sorted copy of a list, optional comparator.");

            engine.RegisterValue("lists", Value.Of(map));
            engine.RegisterStdModule(new StdModule
            {
                Name = "std:lists",
                Members = map,
                FlatNames = Array.Empty<string>(),
            });
        }

        // ---------------------------------------------------------------- helpers

        private static void Add(TernMap map, string name, int min, int max, NativeFn function,
            string? documentation = null) =>
            map.Set(Value.Str(name), Value.Of(new TernNative(name, function, min, max, documentation)));
    }

    /// <summary>Minimal JSON reader producing TERN values (ints stay integers).</summary>
    internal static class JsonParser
    {
        public static Value Parse(string text, ref int index, VM vm)
        {
            SkipWhitespace(text, ref index);

            if (index >= text.Length)
            {
                throw vm.Error("JsonError", "unexpected end of input");
            }

            var c = text[index];

            switch (c)
            {
                case '{':
                {
                    index++;
                    var map = new TernMap();
                    SkipWhitespace(text, ref index);

                    if (index < text.Length && text[index] == '}')
                    {
                        index++;
                        return Value.Of(map);
                    }

                    while (true)
                    {
                        SkipWhitespace(text, ref index);
                        if (index >= text.Length || text[index] != '"')
                        {
                            throw vm.Error("JsonError", $"expected a string key at offset {index}");
                        }

                        var key = ParseString(text, ref index, vm);
                        SkipWhitespace(text, ref index);
                        if (index >= text.Length || text[index] != ':')
                        {
                            throw vm.Error("JsonError", $"expected ':' at offset {index}");
                        }

                        index++;
                        var value = Parse(text, ref index, vm);
                        map.Set(Value.Str(key), value);
                        SkipWhitespace(text, ref index);

                        if (index >= text.Length)
                        {
                            throw vm.Error("JsonError", "unexpected end of object");
                        }

                        if (text[index] == ',')
                        {
                            index++;
                            continue;
                        }

                        if (text[index] == '}')
                        {
                            index++;
                            return Value.Of(map);
                        }

                        throw vm.Error("JsonError", $"expected ',' or '}}' at offset {index}");
                    }
                }

                case '[':
                {
                    index++;
                    var list = new TernList();
                    SkipWhitespace(text, ref index);

                    if (index < text.Length && text[index] == ']')
                    {
                        index++;
                        return Value.Of(list);
                    }

                    while (true)
                    {
                        list.Add(Parse(text, ref index, vm));
                        SkipWhitespace(text, ref index);

                        if (index >= text.Length)
                        {
                            throw vm.Error("JsonError", "unexpected end of array");
                        }

                        if (text[index] == ',')
                        {
                            index++;
                            continue;
                        }

                        if (text[index] == ']')
                        {
                            index++;
                            return Value.Of(list);
                        }

                        throw vm.Error("JsonError", $"expected ',' or ']' at offset {index}");
                    }
                }

                case '"':
                    return Value.Str(ParseString(text, ref index, vm));

                case 't':
                    Expect(text, ref index, "true", vm);
                    return Value.True;

                case 'f':
                    Expect(text, ref index, "false", vm);
                    return Value.False;

                case 'n':
                    Expect(text, ref index, "null", vm);
                    return Value.Nil;

                default:
                {
                    var start = index;
                    if (index < text.Length && (text[index] == '-' || text[index] == '+'))
                    {
                        index++;
                    }

                    var isFloat = false;
                    while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.' ||
                                                   text[index] == 'e' || text[index] == 'E' ||
                                                   text[index] == '+' || text[index] == '-'))
                    {
                        if (text[index] is '.' or 'e' or 'E')
                        {
                            isFloat = true;
                        }

                        index++;
                    }

                    var slice = text.Substring(start, index - start);
                    if (slice.Length == 0)
                    {
                        throw vm.Error("JsonError", $"unexpected character '{c}' at offset {start}");
                    }

                    if (!isFloat && long.TryParse(slice, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    {
                        return new Value(integer);
                    }

                    if (double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        return new Value(number);
                    }

                    throw vm.Error("JsonError", $"invalid number '{slice}'");
                }
            }
        }

        private static void Expect(string text, ref int index, string literal, VM vm)
        {
            if (string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
            {
                throw vm.Error("JsonError", $"invalid literal at offset {index}");
            }

            index += literal.Length;
        }

        private static string ParseString(string text, ref int index, VM vm)
        {
            index++; // opening quote
            var builder = new StringBuilder();

            while (index < text.Length)
            {
                var c = text[index++];

                if (c == '"')
                {
                    return builder.ToString();
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (index >= text.Length)
                {
                    break;
                }

                var escape = text[index++];
                switch (escape)
                {
                    case 'n': builder.Append('\n'); break;
                    case 't': builder.Append('\t'); break;
                    case 'r': builder.Append('\r'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'u':
                        if (index + 4 <= text.Length &&
                            int.TryParse(text.AsSpan(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            builder.Append((char)code);
                            index += 4;
                            break;
                        }

                        throw vm.Error("JsonError", "invalid unicode escape");
                    default:
                        throw vm.Error("JsonError", $"invalid escape '\\{escape}'");
                }
            }

            throw vm.Error("JsonError", "unterminated string");
        }

        public static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }
        }
    }

    internal static class JsonWriter
    {
        public static void Write(StringBuilder builder, in Value value, int indent, int depth)
        {
            switch (value.Kind)
            {
                case ValueKind.Nil:
                    builder.Append("null");
                    break;
                case ValueKind.Bool:
                    builder.Append(value.AsBool() ? "true" : "false");
                    break;
                case ValueKind.Int:
                    builder.Append(value.AsInt().ToString(CultureInfo.InvariantCulture));
                    break;
                case ValueKind.Float:
                {
                    var number = value.AsDouble();
                    if (double.IsNaN(number) || double.IsInfinity(number))
                    {
                        builder.Append("null");
                        break;
                    }

                    builder.Append(number.ToString("R", CultureInfo.InvariantCulture));
                    break;
                }
                case ValueKind.Str:
                    WriteString(builder, value.AsString());
                    break;
                case ValueKind.List:
                {
                    var list = value.AsList();
                    builder.Append('[');
                    for (var i = 0; i < list.Count; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(',');
                        }

                        NewLine(builder, indent, depth + 1);
                        Write(builder, list[i], indent, depth + 1);
                    }

                    if (list.Count > 0)
                    {
                        NewLine(builder, indent, depth);
                    }

                    builder.Append(']');
                    break;
                }
                case ValueKind.Map:
                {
                    var map = value.AsMap();
                    builder.Append('{');
                    var first = true;
                    var slots = map.SlotCount;
                    for (var i = 0; i < slots; i++)
                    {
                        if (!map.TryGetEntry(i, out var key, out var entry))
                        {
                            continue;
                        }

                        if (!first)
                        {
                            builder.Append(',');
                        }

                        first = false;
                        NewLine(builder, indent, depth + 1);
                        WriteString(builder, key.Kind == ValueKind.Str ? key.AsString() : key.ToDisplayString());
                        builder.Append(':');
                        if (indent > 0)
                        {
                            builder.Append(' ');
                        }

                        Write(builder, entry, indent, depth + 1);
                    }

                    if (!first)
                    {
                        NewLine(builder, indent, depth);
                    }

                    builder.Append('}');
                    break;
                }
                default:
                    WriteString(builder, value.ToDisplayString());
                    break;
            }
        }

        private static void NewLine(StringBuilder builder, int indent, int depth)
        {
            if (indent <= 0)
            {
                return;
            }

            builder.Append('\n');
            builder.Append(' ', indent * depth);
        }

        private static void WriteString(StringBuilder builder, string text)
        {
            builder.Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            builder.Append('"');
        }
    }
}
