using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
  tern check <script.tern>           Parse and compile only (no execution)
  tern disasm <script.tern>          Print the compiled bytecode
  tern repl                          Start an interactive session
  tern version                       Print the version
  tern help                          Show this help

Options for `run`:
  --no-fs        Disable file system access (sandbox mode)
  --time         Print execution time to stderr
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

            var engine = new TernEngine
            {
                AllowFileSystem = !options.Contains("--no-fs"),
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory(),
            };

            for (var i = 1; i < positional.Count; i++)
            {
                engine.ScriptArguments.Add(positional[i]);
            }

            var module = engine.LoadFile(Path.GetFullPath(path));

            if (options.Contains("--dump"))
            {
                Disassembler.Dump(module.Entry, Console.Out);
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
            var source = File.ReadAllText(path);
            var diagnostics = new DiagnosticBag();

            var engine = new TernEngine { WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(path))! };

            try
            {
                engine.CompileScript(source, Path.GetFileName(path), path: Path.GetFullPath(path));
            }
            catch (TernCompileException exception)
            {
                foreach (var diagnostic in exception.Diagnostics)
                {
                    Console.Error.WriteLine(diagnostic.Format());
                }

                return 2;
            }

            var count = 0;
            foreach (var diagnostic in diagnostics.Items)
            {
                Console.Error.WriteLine(diagnostic.Format());
                count++;
            }

            Console.WriteLine($"ok: {Path.GetFileName(path)} compiled");
            return count == 0 ? 0 : 2;
        }

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

        private static int Repl()
        {
            var engine = new TernEngine();
            var scope = engine.CreateScope("<repl>");
            var module = engine.CompileScript(string.Empty, "<repl>", scope);
            var vm = engine.CreateVm();

            Console.WriteLine($"TERN {Version} interactive mode. Type `exit` to quit.");
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

            foreach (var c in source)
            {
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
