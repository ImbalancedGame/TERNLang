using System;
using System.Collections.Generic;
using System.IO;
using TERN.Builtins;
using TERN.Compiler;
using TERN.Runtime;
using TERN.Syntax;

namespace TERN
{
    /// <summary>One entry in the standard library catalogue.</summary>
    public sealed record BuiltinDoc(
        string Group,
        string Name,
        int MinArguments,
        int MaxArguments,
        string? Documentation,
        bool IsConstant);

    /// <summary>
    /// Host facing entry point. An engine owns the standard library, the module
    /// cache and the sandbox policy; a fresh <see cref="VM"/> is used per execution
    /// so scripts cannot leak state into each other.
    /// </summary>
    public sealed class TernEngine : ICompilationHost
    {
        private readonly List<(string Name, Value Value)> _hostBindings = new();
        private readonly Dictionary<string, StdModule> _stdModules = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TernModule> _moduleCache = new(StringComparer.Ordinal);

        public TernEngine(bool includeStandardLibrary = true)
        {
            if (includeStandardLibrary)
            {
                StandardLibrary.Install(this);
            }
        }

        public VmOptions Options { get; } = new();

        public TextWriter Out { get; set; } = Console.Out;

        public TextReader In { get; set; } = Console.In;

        public TextWriter Error { get; set; } = Console.Error;

        /// <summary>When false, every <c>fs</c> call raises a PermissionError.</summary>
        public bool AllowFileSystem { get; set; } = true;

        public string WorkingDirectory { get; set; } = Directory.GetCurrentDirectory();

        public List<string> ScriptArguments { get; } = new();

        public Random Random { get; private set; } = new();

        public void SeedRandom(long seed) => Random = new Random(unchecked((int)seed));

        public IReadOnlyList<(string Name, Value Value)> HostBindings => _hostBindings;

        // --------------------------------------------------------------- registry

        /// <summary>Exposes a constant value to scripts as a global.</summary>
        public void RegisterValue(string name, in Value value)
        {
            _hostBindings.Add((name, value));
        }

        /// <summary>Exposes a native function to scripts as a global.</summary>
        public void RegisterFunction(string name, NativeFn function, int minArguments = 0, int maxArguments = -1, string? documentation = null)
        {
            RegisterValue(name, Value.Of(new TernNative(name, function, minArguments, maxArguments, documentation)));
        }

        /// <summary>Registers a `std:` module that scripts can import by name.</summary>
        public void RegisterStdModule(StdModule module)
        {
            _stdModules[module.Name] = module;
        }

        public bool TryGetStdModule(string name, out StdModule module) => _stdModules.TryGetValue(name, out module!);

        /// <summary>Names of the `std:` modules available to scripts.</summary>
        public IEnumerable<string> StandardModuleNames
        {
            get
            {
                var names = new List<string>(_stdModules.Keys);
                names.Sort(StringComparer.Ordinal);
                return names;
            }
        }

        /// <summary>
        /// Catalogue of everything the engine exposes to scripts: globals,
        /// namespaces (math, json, ...) and their members. Used by tooling and
        /// documentation generators so the reference cannot drift from the code.
        /// </summary>
        public IEnumerable<BuiltinDoc> DescribeStandardLibrary()
        {
            var result = new List<BuiltinDoc>();

            foreach (var (name, value) in _hostBindings)
            {
                if (value.Kind == ValueKind.Map)
                {
                    var members = value.AsMap();
                    var keys = members.Keys();

                    for (var i = 0; i < keys.Count; i++)
                    {
                        var key = keys[i];
                        if (key.Kind != ValueKind.Str)
                        {
                            continue;
                        }

                        var member = members.GetOrNil(key);
                        if (member.Kind == ValueKind.Native)
                        {
                            var native = member.AsNative();
                            result.Add(new BuiltinDoc(name, key.AsString(), native.MinArguments, native.MaxArguments,
                                native.Documentation, IsConstant: false));
                        }
                        else
                        {
                            result.Add(new BuiltinDoc(name, key.AsString(), 0, 0, null, IsConstant: true));
                        }
                    }

                    continue;
                }

                if (value.Kind == ValueKind.Native)
                {
                    var native = value.AsNative();
                    result.Add(new BuiltinDoc("global", name, native.MinArguments, native.MaxArguments,
                        native.Documentation, IsConstant: false));
                }
                else
                {
                    result.Add(new BuiltinDoc("global", name, 0, 0, null, IsConstant: true));
                }
            }

            result.Sort((a, b) =>
            {
                var byGroup = string.CompareOrdinal(a.Group, b.Group);
                return byGroup != 0 ? byGroup : string.CompareOrdinal(a.Name, b.Name);
            });

            return result;
        }

        // -------------------------------------------------------------- compiling

        public ModuleScope CreateScope(string name)
        {
            var scope = new ModuleScope(name);

            foreach (var (bindingName, value) in _hostBindings)
            {
                scope.DeclareHost(bindingName, value);
            }

            return scope;
        }

        /// <summary>Parses and compiles a script. Throws <see cref="TernCompileException"/> on errors.</summary>
        public TernModule CompileScript(string source, string? sourceName = null, ModuleScope? scope = null, string? path = null)
        {
            scope ??= CreateScope(sourceName ?? "<script>");

            var diagnostics = new DiagnosticBag();
            var program = Parser.ParseProgram(source, diagnostics, sourceName);

            if (diagnostics.HasErrors)
            {
                throw new TernCompileException(diagnostics.Items);
            }

            var compiler = new ModuleCompiler(scope, this, diagnostics, sourceName, path);
            var entry = compiler.CompileModule(program);

            if (diagnostics.HasErrors)
            {
                throw new TernCompileException(diagnostics.Items);
            }

            return new TernModule(sourceName ?? "<script>", path, entry, scope);
        }

        /// <summary>Compiles into an existing module so a REPL can keep its globals.</summary>
        public void CompileInto(TernModule module, string source, string? sourceName = null, bool returnLastValue = false)
        {
            var diagnostics = new DiagnosticBag();
            var program = Parser.ParseProgram(source, diagnostics, sourceName);

            if (diagnostics.HasErrors)
            {
                throw new TernCompileException(diagnostics.Items);
            }

            var compiler = new ModuleCompiler(module.Scope, this, diagnostics, sourceName, module.Path);
            var entry = compiler.CompileModule(program, returnLastValue);

            if (diagnostics.HasErrors)
            {
                throw new TernCompileException(diagnostics.Items);
            }

            module.ReplaceEntry(entry);
        }

        /// <summary>Parses without compiling; used by `tern check` and editor tooling.</summary>
        public BlockStmt Parse(string source, DiagnosticBag diagnostics, string? sourceName = null) =>
            Parser.ParseProgram(source, diagnostics, sourceName);

        public TernModule? LoadFileModule(string path, string? importerPath)
        {
            var baseDirectory = WorkingDirectory;

            if (!string.IsNullOrEmpty(importerPath))
            {
                var directory = System.IO.Path.GetDirectoryName(importerPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    baseDirectory = directory;
                }
            }

            var candidate = path;
            if (!System.IO.Path.IsPathRooted(candidate))
            {
                candidate = System.IO.Path.Combine(baseDirectory, candidate);
            }

            if (!candidate.EndsWith(".tern", StringComparison.OrdinalIgnoreCase) && !File.Exists(candidate))
            {
                candidate += ".tern";
            }

            string full;
            try
            {
                full = System.IO.Path.GetFullPath(candidate);
            }
            catch (Exception)
            {
                return null;
            }

            if (_moduleCache.TryGetValue(full, out var cached))
            {
                return cached;
            }

            if (!File.Exists(full))
            {
                return null;
            }

            string source;
            try
            {
                source = File.ReadAllText(full);
            }
            catch (Exception)
            {
                return null;
            }

            var name = System.IO.Path.GetFileNameWithoutExtension(full);
            var scope = CreateScope(name);
            var module = CompileScript(source, name, scope, full);
            _moduleCache[full] = module;
            return module;
        }

        public TernModule LoadFile(string path)
        {
            var module = LoadFileModule(path, null) ??
                         throw new TernError("ImportError", $"cannot load '{path}'");
            return module;
        }

        // -------------------------------------------------------------- executing

        public VM CreateVm()
        {
            var vm = new VM(Options)
            {
                Engine = this,
                Output = Out,
                Input = In,
                ErrorOutput = Error,
            };

            return vm;
        }

        public Value Execute(TernModule module, VM? vm = null)
        {
            vm ??= CreateVm();
            vm.Output = Out;
            vm.Input = In;
            vm.ErrorOutput = Error;
            return vm.ExecuteModule(module);
        }

        /// <summary>Compiles and runs a script in one call.</summary>
        public Value RunScript(string source, string? sourceName = null, string? path = null)
        {
            var module = CompileScript(source, sourceName, path: path);
            return Execute(module);
        }

        public Value RunFile(string path)
        {
            var module = LoadFile(path);
            return Execute(module);
        }
    }
}
