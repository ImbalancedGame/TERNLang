// TERN's debugger: a small debug adapter, one JSON object per line.
//
// The protocol is deliberately not DAP. DAP is a good protocol and a large one;
// the only client here is Studio, and a line-oriented request/response/event
// stream with six commands is something a reader can hold in their head. It
// looks like this:
//
//     -> {"id":1,"command":"launch","program":"script.tern","breakpoints":[{"line":7}]}
//     <- {"type":"event","event":"started"}
//     <- {"type":"event","event":"stopped","reason":"breakpoint","line":7,"frames":[...]}
//     -> {"id":2,"command":"variables"}
//     <- {"id":2,"ok":true,"variables":[{"name":"total","value":"42","type":"int"}]}
//     -> {"id":3,"command":"continue"}
//     <- {"type":"event","event":"stopped","reason":"exited","output":"..."}
//
// Execution runs on a worker thread; the reader thread answers commands. When a
// breakpoint fires, the worker parks inside the VM's break hook while the reader
// inspects the (now stationary) stack, then releases it. Program output is
// forwarded as events as it is written.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using TERN;
using TERN.Compiler;
using TERN.Runtime;

namespace TERN.Debug
{
    internal sealed class Breakpoint
    {
        public string File = string.Empty;
        public int Line;
    }

    internal sealed class Program
    {
        private readonly object _gate = new();
        private readonly TextWriter _out;

        private TernEngine? _engine;
        private VM? _vm;
        private TernModule? _module;
        private string _program = string.Empty;

        private readonly List<Breakpoint> _breakpoints = new();
        private readonly Dictionary<FunctionProto, Dictionary<int, Instr>> _patched = new();
        private readonly Dictionary<FunctionProto, Dictionary<int, Instr>> _trampolines = new();
        private readonly List<(FunctionProto Proto, int Index)> _stepTargets = new();

        private Thread? _worker;
        private ManualResetEventSlim _resume = new(false);
        private volatile bool _stopped;
        private string _stopReason = "breakpoint";
        private int _stopLine;
        private bool _finished;
        private Exception? _failure;

        // Step state, applied when execution resumes.
        private enum StepMode { None, Into, Over, Out }
        private StepMode _step = StepMode.None;
        private int _stepFrame = -1;

        private readonly StringBuilder _bufferedOutput = new();

        public Program(TextWriter output)
        {
            _out = output;
        }

        public static int Main(string[] args)
        {
            var detached = args.Length > 0 && args[0] == "--detached";

            var writer = Console.Out;
            Console.SetOut(new OutputTrap(writer));

            var program = new Program(writer);
            if (detached)
            {
                // Used by the test suite: run to completion, no debugger.
                var path = args.Length > 1 ? args[1] : string.Empty;
                return program.RunDetached(path);
            }

            return program.Serve();
        }

        // ------------------------------------------------------------------ io

        private int Serve()
        {
            var input = Console.In;
            var output = _out;

            string? line;
            while ((line = input.ReadLine()) is not null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    Write(output, new Dictionary<string, object?>
                    {
                        ["type"] = "error",
                        ["message"] = "malformed request",
                    });
                    continue;
                }

                using (document)
                {
                    Handle(document.RootElement, output);
                }
            }

            Stop();
            return 0;
        }

        private void Handle(JsonElement request, TextWriter output)
        {
            var id = request.TryGetProperty("id", out var idElement) ? idElement.GetInt32() : 0;
            var command = request.TryGetProperty("command", out var commandElement)
                ? commandElement.GetString() ?? string.Empty
                : string.Empty;

            try
            {
                switch (command)
                {
                    case "launch":
                        Launch(request, output);
                        Reply(output, id, new Dictionary<string, object?> { ["ok"] = true });
                        break;

                    case "breakpoints":
                        SetBreakpoints(request, output);
                        Reply(output, id, new Dictionary<string, object?>
                        {
                            ["ok"] = true,
                            ["breakpoints"] = _breakpoints.Count,
                        });
                        break;

                    case "continue":
                        Resume(StepMode.None, output, id);
                        break;

                    case "next":
                        Resume(StepMode.Over, output, id);
                        break;

                    case "stepIn":
                        Resume(StepMode.Into, output, id);
                        break;

                    case "stepOut":
                        Resume(StepMode.Out, output, id);
                        break;

                    case "stack":
                        Reply(output, id, new Dictionary<string, object?>
                        {
                            ["ok"] = true,
                            ["frames"] = Frames(),
                        });
                        break;

                    case "variables":
                        var frame = request.TryGetProperty("frame", out var frameElement)
                            ? frameElement.GetInt32()
                            : _vm is null ? 0 : _vm.FrameCount - 1;
                        Reply(output, id, new Dictionary<string, object?>
                        {
                            ["ok"] = true,
                            ["variables"] = Variables(frame),
                        });
                        break;

                    case "evaluate":
                        var expression = request.TryGetProperty("expression", out var expressionElement)
                            ? expressionElement.GetString() ?? string.Empty
                            : string.Empty;
                        Reply(output, id, new Dictionary<string, object?>
                        {
                            ["ok"] = true,
                            ["value"] = Evaluate(expression),
                        });
                        break;

                    case "terminate":
                        Stop();
                        Reply(output, id, new Dictionary<string, object?> { ["ok"] = true });
                        break;

                    default:
                        Reply(output, id, new Dictionary<string, object?>
                        {
                            ["ok"] = false,
                            ["message"] = $"unknown command '{command}'",
                        });
                        break;
                }
            }
            catch (Exception error)
            {
                Reply(output, id, new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["message"] = error.Message,
                });
            }
        }

        private static void Write(TextWriter output, object payload)
        {
            output.WriteLine(JsonSerializer.Serialize(payload));
            output.Flush();
        }

        private static void Reply(TextWriter output, int id, Dictionary<string, object?> body)
        {
            body["id"] = id;
            body["type"] = "response";
            Write(output, body);
        }

        private static void Event(TextWriter output, string name, Dictionary<string, object?> body)
        {
            body["type"] = "event";
            body["event"] = name;
            Write(output, body);
        }

        // -------------------------------------------------------------- launch

        private void Launch(JsonElement request, TextWriter output)
        {
            _program = request.GetProperty("program").GetString() ?? string.Empty;

            _breakpoints.Clear();
            if (request.TryGetProperty("breakpoints", out var breakpoints)
                && breakpoints.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in breakpoints.EnumerateArray())
                {
                    _breakpoints.Add(new Breakpoint
                    {
                        File = _program,
                        Line = entry.GetProperty("line").GetInt32(),
                    });
                }
            }

            _engine = new TernEngine
            {
                // Output arrives as events, so the client can show it live.
                Out = new EventWriter(this, output),
                Error = new EventWriter(this, output),
            };

            _vm = _engine.CreateVm();
            _vm.OnFunctionEnter = InstallBreakpoints;
            _vm.OnBreak = OnBreak;

            _module = _engine.LoadFile(_program);

            // Compilation happens now, so a syntax error is reported before
            // anything runs rather than as a mysterious exit.
            PluginBreakpointsIn(_module.Entry, null);

            _finished = false;
            _failure = null;

            _worker = new Thread(() =>
            {
                try
                {
                    _engine.Execute(_module, _vm);
                }
                catch (TernError error)
                {
                    _failure = error;
                }
                catch (Exception error)
                {
                    _failure = error;
                }
                finally
                {
                    _finished = true;
                    FlushBuffered(output);
                    Event(output, "exited", new Dictionary<string, object?>
                    {
                        ["ok"] = _failure is null,
                        ["message"] = _failure?.Message ?? "exited",
                        ["type"] = _failure is TernError ternError ? ternError.ErrorType : null,
                    });
                }
            })
            {
                IsBackground = true,
                Name = "tern-debuggee",
            };

            _worker.Start();
            Event(output, "started", new Dictionary<string, object?>());
        }

        private int RunDetached(string path)
        {
            var engine = new TernEngine();
            try
            {
                var module = engine.LoadFile(path);
                engine.Execute(module);
                return 0;
            }
            catch (TernError error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        // --------------------------------------------------------- breakpoints

        private void SetBreakpoints(JsonElement request, TextWriter output)
        {
            _breakpoints.Clear();
            if (request.TryGetProperty("breakpoints", out var breakpoints)
                && breakpoints.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in breakpoints.EnumerateArray())
                {
                    _breakpoints.Add(new Breakpoint
                    {
                        File = _program,
                        Line = entry.GetProperty("line").GetInt32(),
                    });
                }
            }

            // Functions that have not run yet pick these up on entry.
            RestorePatched();
            if (_module is not null)
            {
                PluginBreakpointsIn(_module.Entry, null);
            }
        }

        private bool WantsLine(int line)
        {
            foreach (var breakpoint in _breakpoints)
            {
                if (breakpoint.Line == line)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Replaces the first instruction of each breakpoint line with Op.Break.
        /// Called when a function is entered, so code compiled after a breakpoint
        /// was set is covered, and recursively so nested functions are too.
        /// </summary>
        private void PluginBreakpointsIn(FunctionProto proto, HashSet<FunctionProto>? seen,
            FunctionProto? skipProto = null, int skipIndex = -1)
        {
            seen ??= new HashSet<FunctionProto>();

            if (!seen.Add(proto))
            {
                return;
            }

            if (!_patched.TryGetValue(proto, out var replacements))
            {
                replacements = new Dictionary<int, Instr>();
                _patched[proto] = replacements;
            }

            for (var index = 0; index < proto.Instructions.Length; index++)
            {
                var line = proto.Lines[index];
                if (!WantsLine(line) || replacements.ContainsKey(index))
                {
                    continue;
                }

                // The instruction being resumed from keeps running: patching it
                // here would stop on it again before it had executed.
                if (ReferenceEquals(proto, skipProto) && index == skipIndex)
                {
                    continue;
                }

                // Only the first instruction of the line becomes the hook, so a
                // breakpoint stops where the line starts.
                if (index > 0 && proto.Lines[index - 1] == line)
                {
                    continue;
                }

                replacements[index] = proto.Instructions[index];
                proto.Instructions[index] = new Instr(Op.Break);
            }

            foreach (var nested in proto.Prototypes)
            {
                if (nested is not null)
                {
                    PluginBreakpointsIn(nested, seen, skipProto, skipIndex);
                }
            }
        }

        private void InstallBreakpoints(FunctionProto proto)
        {
            lock (_gate)
            {
                if (_breakpoints.Count > 0)
                {
                    PluginBreakpointsIn(proto, null);
                }

                // A step that has just entered a function ends on its first line.
                if (_step == StepMode.Into)
                {
                    ArmFirstLine(proto);
                }
            }
        }

        private void RestorePatched()
        {
            lock (_gate)
            {
                foreach (var pair in _patched)
                {
                    foreach (var replacement in pair.Value)
                    {
                        pair.Key.Instructions[replacement.Key] = replacement.Value;
                    }

                    pair.Value.Clear();
                }

                foreach (var pair in _trampolines)
                {
                    foreach (var replacement in pair.Value)
                    {
                        pair.Key.Instructions[replacement.Key] = replacement.Value;
                    }

                    pair.Value.Clear();
                }
            }
        }

        /// <summary>Patches one instruction, remembering what it was.</summary>
        private bool Patch(FunctionProto proto, int index, Dictionary<FunctionProto, Dictionary<int, Instr>> table)
        {
            if (index < 0 || index >= proto.Instructions.Length)
            {
                return false;
            }

            if (proto.Instructions[index].Op == Op.Break)
            {
                return false;
            }

            if (!table.TryGetValue(proto, out var replacements))
            {
                replacements = new Dictionary<int, Instr>();
                table[proto] = replacements;
            }

            replacements[index] = proto.Instructions[index];
            proto.Instructions[index] = new Instr(Op.Break);
            return true;
        }

        /// <summary>The first index after `index` that runs a different line.</summary>
        private static int NextLineIndex(FunctionProto proto, int index)
        {
            var line = proto.LineAt(index);
            for (var next = index + 1; next < proto.Instructions.Length; next++)
            {
                if (proto.LineAt(next) != line)
                {
                    return next;
                }
            }

            return -1;
        }

        private void ArmFirstLine(FunctionProto proto)
        {
            for (var index = 0; index < proto.Instructions.Length; index++)
            {
                if (proto.LineAt(index) > 0)
                {
                    if (Patch(proto, index, _patched))
                    {
                        _stepTargets.Add((proto, index));
                    }

                    return;
                }
            }
        }

        /// <summary>Puts the temporary breakpoint that ends the current step.</summary>
        private void ArmStep(FunctionProto proto, int index)
        {
            switch (_step)
            {
                case StepMode.Into:
                case StepMode.Over:
                {
                    // Into stops anywhere, including deeper frames (whose entry
                    // hook arms them); Over stops only when the current frame's
                    // next line arrives, and a call that has since returned still
                    // lands here.
                    var next = NextLineIndex(proto, index);
                    if (next >= 0 && Patch(proto, next, _patched))
                    {
                        _stepTargets.Add((proto, next));
                        break;
                    }

                    // No further line in this function: the step ends where the
                    // function was called from, which is what a debugger does
                    // with "next" on a function's last line.
                    ArmReturn(proto, index);
                    break;
                }

                case StepMode.Out:
                {
                    ArmReturn(proto, index);
                    break;
                }
            }
        }

        /// <summary>
        /// Ends a step when control returns to the caller: the caller's saved
        /// instruction pointer is where it will resume, so patching that
        /// instruction stops the step the moment the call has unwound.
        /// </summary>
        private void ArmReturn(FunctionProto proto, int index)
        {
            if (_vm is null)
            {
                return;
            }

            var frame = _vm.FrameCount - 1;
            if (frame < 1)
            {
                return;
            }

            var (caller, callerIp) = _vm.FrameAt(frame - 1);
            if (caller is not null && Patch(caller, callerIp, _patched))
            {
                _stepTargets.Add((caller, callerIp));
            }
        }

        // -------------------------------------------------------------- stops

        private int OnBreak(FunctionProto proto, int index)
        {
            // A trampoline is the "re-arm me once I have moved past" marker left
            // by a resume; it is never a stop.
            if (_trampolines.TryGetValue(proto, out var trampolines)
                && trampolines.TryGetValue(index, out var displaced))
            {
                lock (_gate)
                {
                    proto.Instructions[index] = displaced;
                    trampolines.Remove(index);
                }

                Rearm();
                return index;
            }

            var line = proto.LineAt(index);
            if (Environment.GetEnvironmentVariable("TERN_DEBUG_TRACE") is not null)
            {
                Console.Error.WriteLine($"[trace] break ip={index} line={line} step={_step} target={_stepTargets.Count}");
            }
            var isBreakpoint = WantsLine(line);
            var isStep = _step != StepMode.None && _stepTargets.Contains((proto, index));

            if (!isBreakpoint && !isStep)
            {
                // A patch whose breakpoint has since been removed: run the
                // instruction it replaced.
                return index;
            }

            lock (_gate)
            {
                // Put the original back so the VM can execute it on resume.
                if (_patched.TryGetValue(proto, out var replacements)
                    && replacements.TryGetValue(index, out var original))
                {
                    proto.Instructions[index] = original;
                    replacements.Remove(index);
                }

                _stepTargets.Remove((proto, index));
            }

            _stopLine = line;
            _stopReason = isBreakpoint ? "breakpoint" : "step";
            _stopped = true;
            FlushBuffered(_out);

            Event(_out, "stopped", new Dictionary<string, object?>
            {
                ["reason"] = _stopReason,
                ["file"] = _program,
                ["line"] = line,
                ["frames"] = Frames(),
            });

            // Park until the client says continue, step or stop. The VM is not
            // running, so its stack and locals are safe to read.
            _resume.Wait();
            _resume.Reset();
            _stopped = false;

            lock (_gate)
            {
                // Everything that was patched is stale now; re-patch from the
                // new position, and leave a trampoline so this very instruction
                // can be armed again once it has run.
                RestorePatched();
                _stepTargets.Clear();

                if (_step == StepMode.None)
                {
                    if (_breakpoints.Count > 0)
                    {
                        PluginBreakpointsIn(proto, null, proto, index);
                        if (WantsLine(line))
                        {
                            ArmTrampoline(proto, index + 1, index);
                        }
                    }
                }
                else
                {
                    ArmStep(proto, index);
                }
            }

            return index;
        }

        /// <summary>Marks an instruction to re-arm the breakpoint once passed.</summary>
        private void ArmTrampoline(FunctionProto proto, int index, int rearmIndex)
        {
            if (index < 0 || index >= proto.Instructions.Length)
            {
                return;
            }

            if (!_trampolines.TryGetValue(proto, out var table))
            {
                table = new Dictionary<int, Instr>();
                _trampolines[proto] = table;
            }

            // Both the instruction after the stop (so the breakpoint can be armed
            // again the next time round a loop) and, when there is room after it,
            // one further on.
            if (!table.ContainsKey(index) && proto.Instructions[index].Op != Op.Break)
            {
                table[index] = proto.Instructions[index];
                proto.Instructions[index] = new Instr(Op.Break);
            }

            var next = NextLineIndex(proto, index);
            if (next >= 0 && !table.ContainsKey(next) && proto.Instructions[next].Op != Op.Break)
            {
                table[next] = proto.Instructions[next];
                proto.Instructions[next] = new Instr(Op.Break);
            }
        }

        /// <summary>
        /// Re-patches every breakpoint in a function that is about to run from
        /// the start. Called from the trampoline path, which cannot know which
        /// function is next, so it walks the module's prototypes.
        /// </summary>
        private void Rearm()
        {
            if (_breakpoints.Count == 0 || _module is null)
            {
                return;
            }

            lock (_gate)
            {
                PluginBreakpointsIn(_module.Entry, null);
            }
        }

        private void Resume(StepMode mode, TextWriter output, int id)
        {
            if (_worker is null || _finished)
            {
                Reply(output, id, new Dictionary<string, object?> { ["ok"] = false, ["message"] = "not running" });
                return;
            }

            if (!_stopped)
            {
                Reply(output, id, new Dictionary<string, object?> { ["ok"] = false, ["message"] = "not stopped" });
                return;
            }

            lock (_gate)
            {
                _step = mode;
                _stepFrame = _vm is null ? 0 : _vm.FrameCount - 1;
            }

            Reply(output, id, new Dictionary<string, object?> { ["ok"] = true });
            _resume.Set();
        }

        private void Stop()
        {
            // The worker is either parked on the resume event or running; the
            // process exits either way, and the VM has no interrupt flag worth
            // the complexity for a single-script debugger.
            _resume.Set();
        }

        // ------------------------------------------------------------ inspection

        private List<Dictionary<string, object?>> Frames()
        {
            var frames = new List<Dictionary<string, object?>>();
            if (_vm is null)
            {
                return frames;
            }

            var count = _vm.FrameCount;
            for (var index = count - 1; index >= 0; index--)
            {
                var (proto, ip) = _vm.FrameAt(index);
                if (proto is null)
                {
                    continue;
                }

                var line = proto.LineAt(Math.Max(0, ip - 1));
                frames.Add(new Dictionary<string, object?>
                {
                    ["index"] = index,
                    ["name"] = string.IsNullOrEmpty(proto.Name) ? "<module>" : proto.Name,
                    ["file"] = _program,
                    ["line"] = line,
                });
            }

            return frames;
        }

        private List<Dictionary<string, object?>> Variables(int frameIndex)
        {
            var variables = new List<Dictionary<string, object?>>();
            if (_vm is null)
            {
                return variables;
            }

            if (frameIndex < 0 || frameIndex >= _vm.FrameCount)
            {
                frameIndex = _vm.FrameCount - 1;
            }

            var (proto, _) = _vm.FrameAt(frameIndex);
            if (proto is null)
            {
                return variables;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var slot = 0; slot < proto.LocalNames.Length; slot++)
            {
                var name = proto.LocalNames[slot];
                if (string.IsNullOrEmpty(name) || !seen.Add(name!))
                {
                    continue;
                }

                var value = _vm.LocalAt(frameIndex, slot);
                variables.Add(new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["value"] = Describe(value),
                    ["type"] = TypeOf(value),
                });
            }

            // Module globals are readable too, and are where a script's
            // top-level state lives. The standard library's own bindings are
            // skipped: a hundred natives and namespace maps in the list would
            // bury the two variables the script declared.
            if (frameIndex == 0 && _module is not null)
            {
                var slots = _module.Scope.Slots;
                for (var slot = 0; slot < slots.Count && slot < _module.Globals.Length; slot++)
                {
                    var binding = slots[slot];
                    if (binding.IsHost || !seen.Add(binding.Name))
                    {
                        continue;
                    }

                    var value = _module.Globals[slot];
                    variables.Add(new Dictionary<string, object?>
                    {
                        ["name"] = binding.Name,
                        ["value"] = Truncate(Describe(value)),
                        ["type"] = TypeOf(value),
                    });
                }
            }

            return variables;
        }

        private string Evaluate(string expression)
        {
            if (_vm is null || string.IsNullOrWhiteSpace(expression))
            {
                return string.Empty;
            }

            var frameIndex = _vm.FrameCount - 1;
            var (proto, _) = _vm.FrameAt(frameIndex);
            var name = expression.Trim();

            if (proto is not null)
            {
                for (var slot = 0; slot < proto.LocalNames.Length; slot++)
                {
                    if (string.Equals(proto.LocalNames[slot], name, StringComparison.Ordinal))
                    {
                        return Describe(_vm.LocalAt(frameIndex, slot));
                    }
                }
            }

            if (_module is not null)
            {
                foreach (var pair in _module.NamedGlobals())
                {
                    if (string.Equals(pair.Key, name, StringComparison.Ordinal))
                    {
                        return Describe(pair.Value);
                    }
                }
            }

            return $"'{name}' is not in scope";
        }

        /// <summary>Long values are cut, so one map cannot fill the panel.</summary>
        private static string Truncate(string text) =>
            text.Length <= 200 ? text : text[..197] + "...";

        private static string Describe(Value value)
        {
            try
            {
                return value.Kind == ValueKind.Str ? value.AsString() : value.ToString() ?? "nil";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string TypeOf(Value value) => value.Kind switch
        {
            ValueKind.Nil => "nil",
            ValueKind.Bool => "bool",
            ValueKind.Int => "int",
            ValueKind.Float => "float",
            ValueKind.Str => "string",
            ValueKind.List => "list",
            ValueKind.Map => "map",
            _ => value.Kind.ToString().ToLower(CultureInfo.InvariantCulture),
        };

        private void FlushBuffered(TextWriter output)
        {
            lock (_bufferedOutput)
            {
                if (_bufferedOutput.Length == 0)
                {
                    return;
                }

                Event(output, "output", new Dictionary<string, object?>
                {
                    ["text"] = _bufferedOutput.ToString(),
                });
                _bufferedOutput.Clear();
            }
        }

        /// <summary>Forwards everything a program prints as an event.</summary>
        private sealed class EventWriter : TextWriter
        {
            private readonly Program _program;
            private readonly TextWriter _output;

            public EventWriter(Program program, TextWriter output)
            {
                _program = program;
                _output = output;
            }

            public override Encoding Encoding => Encoding.UTF8;

            public override void Write(char value) => _program.Emit(value.ToString(), _output);

            public override void Write(string? value) => _program.Emit(value ?? string.Empty, _output);

            public override void WriteLine(string? value) => _program.Emit((value ?? string.Empty) + "\n", _output);
        }

        private void Emit(string text, TextWriter output)
        {
            if (text.Length == 0)
            {
                return;
            }

            // Nothing is sent while the program is stopped, and nothing while a
            // response is being serialised, so events cannot interleave with them.
            lock (_bufferedOutput)
            {
                _bufferedOutput.Append(text);
            }

            if (!_stopped)
            {
                FlushBuffered(output);
            }
        }

        /// <summary>
        /// Keeps anything written to Console.Out by a library from corrupting the
        /// protocol stream.
        /// </summary>
        private sealed class OutputTrap : TextWriter
        {
            private readonly TextWriter _inner;

            public OutputTrap(TextWriter inner)
            {
                _inner = inner;
            }

            public override Encoding Encoding => _inner.Encoding;

            public override void Write(char value) => Console.Error.Write(value);

            public override void Write(string? value) => Console.Error.Write(value);

            public override void WriteLine(string? value) => Console.Error.WriteLine(value);
        }
    }
}
