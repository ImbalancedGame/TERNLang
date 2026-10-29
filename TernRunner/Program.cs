using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using TERN;
using TERN.Compiler;

namespace TernRunner
{
    /// <summary>Command line front end for the TERN language.</summary>
    public static class Program
    {
        private const string Version = "2.0.0";

        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintHelp();
                return 0;
            }

            try
            {
                return args[0] switch
                {
                    "run" => RunFile(args),
                    "check" => Check(args),
                    "repl" => Repl(),
                    "disasm" => Disassemble(args),
                    "emit" => EmitBytecode(args),
                    "docs" => PrintDocs(args),
                    "version" or "--version" or "-v" => PrintVersion(),
                    "help" or "--help" or "-h" => PrintHelp(),
                    _ => args[0].StartsWith("-", StringComparison.Ordinal) ? RunFile(args) : RunImplicit(args),
                };
            }
            catch (TernCompileException compile)
            {
                foreach (var diagnostic in compile.Diagnostics)
                {
                    if (diagnostic.Severity == DiagnosticSeverity.Error)
                    {
                        Console.Error.WriteLine(diagnostic.Format());
                    }
                }

                return 2;
            }
            catch (TernError error)
            {
                Console.Error.WriteLine(error.Describe());
                return 1;
            }
            catch (FileNotFoundException notFound)
            {
                Console.Error.WriteLine($"error: {notFound.Message}");
                return 2;
            }
        }

        private static int PrintVersion()
        {
            Console.WriteLine($"TERN {Version}");
            return 0;
        }

        private static int PrintHelp()
        {
            Console.WriteLine($"""
TERN {Version} - a small, sandboxed scripting language.

Usage:
  tern run <script.tern> [args...]   Run a script
  tern check <script.tern> [--quiet] Parse, lint and compile only (no execution)
  tern disasm <script.tern>          Print the compiled bytecode
  tern emit <script.tern> [-o file]  Export bytecode as JSON for the native backend
  tern docs [--markdown]             List the standard library (markdown by default)
  tern repl                          Start an interactive session
  tern version                       Print the version
  tern help                          Show this help

Options for `run`:
  --no-fs        Disable file system access (sandbox mode)
  --time         Print execution time to stderr
  --quiet        Suppress lint warnings
  --dump         Print the bytecode before running
""");
            return 0;
        }

        private static int RunImplicit(string[] args) => RunFile(args);

        private static int RunFile(string[] args)
        {
            var startIndex = args[0] == "run" ? 1 : 0;

            var options = new List<string>();
            var positional = new List<string>();

            for (var i = startIndex; i < args.Length; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    options.Add(args[i]);
                    continue;
                }

                positional.Add(args[i]);
            }

            if (positional.Count == 0)
            {
                Console.Error.WriteLine("error: no script given\n");
                PrintHelp();
                return 2;
            }

            var path = positional[0];
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"error: script '{path}' not found");
                return 2;
            }

            var quiet = options.Contains("--quiet");

            var engine = new TernEngine
            {
                AllowFileSystem = !options.Contains("--no-fs"),
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory(),
                LintOnCompile = !quiet,
            };

            // Lint warnings are advisory, so they go to stderr and never change
            // the exit code; errors still throw out of LoadFile.
            engine.Warnings = diagnostic => Console.Error.WriteLine(diagnostic.Format());

            for (var i = 1; i < positional.Count; i++)
            {
                engine.ScriptArguments.Add(positional[i]);
            }

            var module = engine.LoadFile(Path.GetFullPath(path));

            if (options.Contains("--dump"))
            {
                // Keep stdout clean for the script's own output.
                Disassembler.Dump(module.Entry, Console.Error);
            }

            var stopwatch = Stopwatch.StartNew();
            engine.Execute(module);
            stopwatch.Stop();

            if (options.Contains("--time"))
            {
                Console.Error.WriteLine($"ran {Path.GetFileName(path)} in {stopwatch.Elapsed.TotalMilliseconds:F2} ms");
            }

            return 0;
        }

        private static int Check(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("error: check needs a script path");
                return 2;
            }

            var path = args[1];
            var full = Path.GetFullPath(path);
            var source = File.ReadAllText(full);

            var engine = new TernEngine { WorkingDirectory = Path.GetDirectoryName(full)! };
            var diagnostics = engine.Check(source, Path.GetFileName(path), full);

            var errors = 0;
            var warnings = 0;
            var quiet = args.Contains("--quiet", StringComparer.Ordinal);

            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                {
                    errors++;
                }
                else
                {
                    warnings++;

                    if (quiet)
                    {
                        continue;
                    }
                }

                Console.Error.WriteLine(diagnostic.Format());
            }

            if (errors > 0)
            {
                Console.Error.WriteLine($"failed: {Path.GetFileName(path)} with {errors} error(s)");
                return 2;
            }

            var summary = warnings == 0
                ? $"ok: {Path.GetFileName(path)} compiled"
                : $"ok: {Path.GetFileName(path)} compiled with {warnings} warning(s)";

            Console.WriteLine(summary);
            return 0;
        }

        private static int PrintDocs(string[] args)
        {
            var engine = new TernEngine();
            var plain = args.Contains("--plain", StringComparer.Ordinal);
            var catalogue = engine.DescribeStandardLibrary().ToList();

            if (plain)
            {
                foreach (var entry in catalogue)
                {
                    Console.WriteLine($"{entry.Group,-10} {Signature(entry),-26} {entry.Documentation}");
                }

                return 0;
            }

            var builder = new System.Text.StringBuilder();
            builder.AppendLine("# Standard library reference");
            builder.AppendLine();
            builder.AppendLine("<!-- Generated by `tern docs`. Do not edit by hand: run `tools/gen-docs.sh`. -->");
            builder.AppendLine();
            builder.AppendLine("Arity notation: `push(value)` takes one argument, `sort([comparator])` takes");
            builder.AppendLine("an optional one, `println(...)` is variadic. Method signatures omit the");
            builder.AppendLine("receiver, so `list.push(value)` is written `push(value)` under `list`.");
            builder.AppendLine();

            // Globals first, then the namespaces alphabetically.
            foreach (var group in catalogue
                         .GroupBy(entry => entry.Group)
                         .OrderBy(group => group.Key == "global" ? 0 : 1)
                         .ThenBy(group => group.Key, StringComparer.Ordinal))
            {
                var title = group.Key == "global" ? "Globals" : $"`{group.Key}`";
                builder.AppendLine($"## {title}");
                builder.AppendLine();
                builder.AppendLine("| Name | Signature | Notes |");
                builder.AppendLine("| ---- | --------- | ----- |");

                foreach (var entry in group)
                {
                    var notes = entry.IsConstant ? "constant" : entry.Documentation ?? string.Empty;
                    builder.AppendLine($"| `{entry.Name}` | `{Signature(entry)}` | {notes} |");
                }

                builder.AppendLine();
            }

            builder.AppendLine("## `std:` modules");
            builder.AppendLine();
            foreach (var name in engine.StandardModuleNames)
            {
                builder.AppendLine($"* `{name}`");
            }

            builder.AppendLine();
            builder.AppendLine("## Methods by type");
            builder.AppendLine();

            foreach (var group in TERN.Builtins.BuiltinMethods.Describe()
                         .GroupBy(entry => entry.Kind)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                builder.AppendLine($"### {group.Key}");
                builder.AppendLine();
                builder.AppendLine("| Name | Signature |");
                builder.AppendLine("| ---- | --------- |");

                foreach (var entry in group.OrderBy(e => e.Name, StringComparer.Ordinal))
                {
                    builder.AppendLine($"| `{entry.Name}` | `{Signature(entry.Name, entry.Min, entry.Max, false, hasReceiver: true)}` |");
                }

                builder.AppendLine();
            }

            Console.Write(builder.ToString());
            return 0;
        }

        /// <summary>
        /// Renders an arity. Native methods carry their receiver in the argument
        /// count, globals do not, so <paramref name="hasReceiver"/> decides whether
        /// the first slot is hidden.
        /// </summary>
        private static string Signature(string name, int min, int max, bool isConstant, bool hasReceiver = false)
        {
            if (isConstant)
            {
                return name;
            }

            var hidden = hasReceiver ? 1 : 0;
            var required = Math.Max(0, min - hidden);
            var total = max < 0 ? -1 : Math.Max(0, max - hidden);

            var parameters = new List<string>();
            for (var i = 0; i < required; i++)
            {
                parameters.Add("arg");
            }

            if (total < 0)
            {
                parameters.Add("...");
            }
            else
            {
                for (var i = required; i < total; i++)
                {
                    parameters.Add("[arg]");
                }
            }

            return $"{name}({string.Join(", ", parameters)})";
        }

        private static string Signature(BuiltinDoc entry) =>
            Signature(entry.Name, entry.MinArguments, entry.MaxArguments, entry.IsConstant);

        private static int Disassemble(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("error: disasm needs a script path");
                return 2;
            }

            var path = args[1];
            var engine = new TernEngine
            {
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory(),
            };

            var module = engine.LoadFile(Path.GetFullPath(path));
            Disassembler.Dump(module.Entry, Console.Out);
            return 0;
        }

        private static int EmitBytecode(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("error: emit needs a script path");
                return 2;
            }

            var path = Path.GetFullPath(args[1]);
            var engine = new TernEngine
            {
                WorkingDirectory = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory(),
            };

            var module = engine.LoadFile(path);
            var json = NativeExport.ToJson(module);

            var output = args.Length > 3 && args[2] == "-o" ? args[3] : null;
            if (output is null)
            {
                Console.Out.Write(json);
            }
            else
            {
                File.WriteAllText(output, json);
            }

            return 0;
        }

        private static int Repl()
        {
            var engine = new TernEngine();
            var scope = engine.CreateScope("<repl>");
            var module = engine.CompileScript(string.Empty, "<repl>", scope);
            var vm = engine.CreateVm();

            Console.WriteLine($"TERN {Version} interactive mode. Type `:help` for commands, `exit` to quit.");
            var buffer = new StringBuilder();

            while (true)
            {
                Console.Write(buffer.Length == 0 ? "tern> " : "  ... ");
                var line = Console.ReadLine();

                if (line is null)
                {
                    return 0;
                }

                if (buffer.Length == 0 && line.Trim() is "exit" or "quit" or ":q")
                {
                    return 0;
                }

                if (buffer.Length == 0 && line.TrimStart().StartsWith(":", StringComparison.Ordinal))
                {
                    switch (line.Trim())
                    {
                        case ":help":
                            Console.WriteLine("""
Commands:
  :help       show this help
  :globals    list the names defined so far
  :reset      forget every definition
  :quit       leave the session

Type an expression to evaluate it; blocks may span several lines.
""");
                            continue;

                        case ":globals":
                        {
                            var names = scope.Slots
                                .Where(slot => !slot.IsHost)
                                .Select(slot => slot.Name)
                                .OrderBy(name => name, StringComparer.Ordinal)
                                .ToList();

                            Console.WriteLine(names.Count == 0
                                ? "(nothing defined yet)"
                                : string.Join(", ", names));
                            continue;
                        }

                        case ":reset":
                            scope = engine.CreateScope("<repl>");
                            module = engine.CompileScript(string.Empty, "<repl>", scope);
                            vm = engine.CreateVm();
                            Console.WriteLine("(reset)");
                            continue;

                        default:
                            Console.Error.WriteLine($"unknown command '{line.Trim()}' (try :help)");
                            continue;
                    }
                }

                buffer.AppendLine(line);

                if (IsIncomplete(buffer.ToString()))
                {
                    continue;
                }

                var source = buffer.ToString();
                buffer.Clear();

                if (string.IsNullOrWhiteSpace(source))
                {
                    continue;
                }

                try
                {
                    engine.CompileInto(module, source, "<repl>", returnLastValue: true);
                    module.Initialized = false;
                    var result = vm.ExecuteModule(module);
                    module.Initialized = true;

                    if (!result.IsNil)
                    {
                        Console.WriteLine(result.ToDebugString());
                    }
                }
                catch (TernCompileException compile)
                {
                    foreach (var diagnostic in compile.Diagnostics)
                    {
                        if (diagnostic.Severity == DiagnosticSeverity.Error)
                        {
                            Console.Error.WriteLine(diagnostic.Format());
                        }
                    }
                }
                catch (TernError error)
                {
                    Console.Error.WriteLine(error.Describe());
                }
            }
        }

        /// <summary>Counts braces so multi-line blocks can be typed in the REPL.</summary>
        private static bool IsIncomplete(string source)
        {
            var depth = 0;
            var inString = false;
            var escaped = false;
            var inComment = false;

            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];

                if (inComment)
                {
                    if (c == '\n')
                    {
                        inComment = false;
                    }

                    continue;
                }

                if (!inString && c == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    inComment = true;
                    i++;
                    continue;
                }

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        break;
                    case '{':
                    case '(':
                    case '[':
                        depth++;
                        break;
                    case '}':
                    case ')':
                    case ']':
                        depth--;
                        break;
                }
            }

            return depth > 0 || inString;
        }
    }
}
