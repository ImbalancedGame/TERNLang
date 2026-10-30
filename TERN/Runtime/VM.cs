using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using TERN.Builtins;
using TERN.Compiler;

namespace TERN.Runtime
{
    /// <summary>Limits that keep a script from taking the host down.</summary>
    public sealed class VmOptions
    {
        public long MaxInstructions { get; set; } = 2_000_000_000;
        // Matches the native backend's limit, so a script that recurses to a
        // given depth behaves the same in the VM and in a compiled binary.
        public int MaxCallDepth { get; set; } = 10000;
        public int MaxCollectionSize { get; set; } = 8_000_000;
        public long MaxStringLength { get; set; } = 16_000_000;
    }

    /// <summary>
    /// Stack based bytecode interpreter.
    ///
    /// Design notes (all aimed at the hot path):
    ///  * values are 16 byte structs, so int/float math never allocates;
    ///  * locals live in one flat <see cref="Value"/> array indexed by frame base,
    ///    which the compiler resolves to constant slots, so variable access is a
    ///    single array read;
    ///  * instructions are fixed size and the switch below is dense, which the JIT
    ///    turns into a jump table;
    ///  * the frequent patterns (local + const, compare + branch, local increment)
    ///    have dedicated opcodes, cutting dispatch count in loops;
    ///  * frames, handlers and iteration state are pooled arrays rather than
    ///    per call heap objects.
    /// </summary>
    public sealed class VM
    {
        private struct NativeLookup
        {
            public ValueKind Kind;
            public string? Name;
            public TernNative? Native;
        }

        private sealed class Frame
        {
            public TernClosure? Closure;
            public TernModule? Module;
            public Instr[] Code = Array.Empty<Instr>();
            public Value[] Constants = Array.Empty<Value>();
            public DivMagic[] Divisions = Array.Empty<DivMagic>();
            public int Ip;
            public int LocalsBase;
            public int StackBase;
            public int Argc;
            public bool IsMethod;
            public List<Upvalue>? OpenUpvalues;
        }

        private readonly struct Handler
        {
            public readonly int FrameIndex;
            public readonly int Ip;
            public readonly int StackDepth;

            public Handler(int frameIndex, int ip, int stackDepth)
            {
                FrameIndex = frameIndex;
                Ip = ip;
                StackDepth = stackDepth;
            }
        }

        private readonly int _maxCallDepth;
        private readonly long _maxInstructions;

        private Value[] _stack = CreateValues(256);
        private int _sp;

        /// <summary>
        /// Direct-mapped cache for builtin method lookups. Scripts call the same
        /// handful of methods on the same handful of receiver kinds, so a single
        /// probe replaces the two dictionary lookups in <c>BuiltinMethods.Tables</c>.
        /// </summary>
        private readonly NativeLookup[] _nativeCache = new NativeLookup[256];

        /// <summary>Reusable argument buffers, indexed by native-call nesting depth.</summary>
        private Value[][] _nativeArgs = new Value[4][];

        private int _nativeDepth;
        private Value[] _locals = CreateValues(256);
        private int _localsTop;
        private Frame[] _frames = CreateFrames(64);
        private int _frameCount;
        private readonly List<Handler> _handlers = new();
        private long _steps;

        private static readonly Frame EmptyFrame = new();

        public VM(VmOptions? options = null)
        {
            Options = options ?? new VmOptions();
            _maxCallDepth = Options.MaxCallDepth;
            _maxInstructions = Options.MaxInstructions;
        }

        public VmOptions Options { get; }

        public TernEngine? Engine { get; internal set; }

        public TextWriter Output { get; set; } = Console.Out;

        public TextReader Input { get; set; } = Console.In;

        public TextWriter ErrorOutput { get; set; } = Console.Error;

        // ------------------------------------------------------------- execution

        /// <summary>Modules this VM has run, so struct types can be found by name.</summary>
        private readonly List<TernModule> _executedModules = new();

        /// <summary>
        /// Finds a struct type by name among the globals of the modules this VM has
        /// run. Used when decoding a value whose sender named the struct.
        /// </summary>
        public TernType? FindType(string name)
        {
            for (var i = _executedModules.Count - 1; i >= 0; i--)
            {
                var module = _executedModules[i];
                if (!module.Scope.TryResolve(name, out var slot) ||
                    slot >= module.Globals.Length)
                {
                    continue;
                }

                var value = module.Globals[slot];
                if (value.Kind == ValueKind.Type)
                {
                    return value.AsType();
                }
            }

            return null;
        }

        /// <summary>Runs a module body once.</summary>
        public Value ExecuteModule(TernModule module)
        {
            if (!_executedModules.Contains(module))
            {
                _executedModules.Add(module);
            }

            if (module.Initialized)
            {
                return Value.Nil;
            }

            if (module.Initializing)
            {
                throw Error("ImportError", $"circular import of module '{module.Name}'");
            }

            module.Initializing = true;

            try
            {
                var closure = new TernClosure(module.Entry, Array.Empty<Upvalue>()) { Module = module };
                var result = CallValue(Value.Of(closure), ReadOnlySpan<Value>.Empty);
                module.Initialized = true;
                return result;
            }
            finally
            {
                module.Initializing = false;
            }
        }

        /// <summary>Runs a bare prototype (used for lazy struct field defaults).</summary>
        public Value CallProto(FunctionProto prototype)
        {
            var closure = new TernClosure(prototype, Array.Empty<Upvalue>());
            return CallValue(Value.Of(closure), ReadOnlySpan<Value>.Empty);
        }

        /// <summary>Calls a TERN value from host code (or from a native function).</summary>
        public Value CallValue(in Value callee, ReadOnlySpan<Value> arguments)
        {
            var floor = _frameCount;
            Push(callee);
            var argsBase = _sp;
            foreach (var argument in arguments)
            {
                Push(argument);
            }

            if (!TryEnterCall(callee, arguments.Length, argsBase, argsBase - 1))
            {
                throw Error("TypeError", $"{TypeName(callee)} is not callable");
            }

            return Run(floor);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private Value Run(int frameFloor)
        {
            if (_frameCount <= frameFloor)
            {
                return Value.Nil;
            }

            // The instruction budget is counted in a local and only published to the
            // field at frame boundaries: incrementing a heap field on every loop back
            // edge costs a load/store pair per iteration.
            var steps = _steps;
            var budget = _maxInstructions;

            while (_frameCount > frameFloor)
            {
                var frame = _frames[_frameCount - 1];
                var code = frame.Code;
                var constants = frame.Constants;
                var divisions = frame.Divisions;
                var ip = frame.Ip;
                var frameBase = frame.LocalsBase;
                var locals = _locals;
                var module = frame.Module;

                try
                {
                while (true)
                {
                    var instruction = code[ip++];

                    switch (instruction.Op)
                    {
                        case Op.Nop:
                            break;

                        case Op.Pop:
                            _sp--;
                            break;

                        case Op.Dup:
                            Push(_stack[_sp - 1]);
                            break;

                        case Op.Swap:
                        {
                            var top = _stack[_sp - 1];
                            _stack[_sp - 1] = _stack[_sp - 2];
                            _stack[_sp - 2] = top;
                            break;
                        }

                        case Op.LoadNil:
                            Push(Value.Nil);
                            break;

                        case Op.LoadTrue:
                            Push(Value.True);
                            break;

                        case Op.LoadFalse:
                            Push(Value.False);
                            break;

                        case Op.LoadInt:
                            Push(new Value(instruction.A));
                            break;

                        case Op.LoadConst:
                            Push(constants[instruction.A]);
                            break;

                        case Op.LoadLocal:
                            Push(_locals[frameBase + instruction.A]);
                            break;

                        case Op.StoreLocal:
                            _locals[frameBase + instruction.A] = _stack[--_sp];
                            break;

                        case Op.LoadGlobal when module is not null:
                            Push(module.Globals[instruction.A]);
                            break;

                        case Op.StoreGlobal when module is not null:
                            module.Globals[instruction.A] = _stack[--_sp];
                            break;

                        case Op.LoadUpvalue:
                            Push(frame.Closure!.Upvalues[instruction.A].Get());
                            break;

                        case Op.StoreUpvalue:
                            frame.Closure!.Upvalues[instruction.A].Set(_stack[--_sp]);
                            break;

                        case Op.AddStoreLocal:
                        case Op.SubStoreLocal:
                        case Op.MulStoreLocal:
                        case Op.ModStoreLocal:
                        {
                            var right = _stack[--_sp];
                            var left = _stack[--_sp];
                            var op = instruction.Op switch
                            {
                                Op.AddStoreLocal => Op.Add,
                                Op.SubStoreLocal => Op.Sub,
                                Op.MulStoreLocal => Op.Mul,
                                _ => Op.Mod,
                            };

                            var slot = frameBase + instruction.A;

                            if (left.Kind == ValueKind.Int && right.Kind == ValueKind.Int)
                            {
                                var a = left.AsInt();
                                var b = right.AsInt();
                                if (op == Op.Add && TryAdd(a, b, out var sum))
                                {
                                    _locals[slot] = new Value(sum);
                                    break;
                                }

                                if (op == Op.Sub && TrySubtract(a, b, out var difference))
                                {
                                    _locals[slot] = new Value(difference);
                                    break;
                                }

                                if (op == Op.Mul && TryMultiply(a, b, out var product))
                                {
                                    _locals[slot] = new Value(product);
                                    break;
                                }

                                if (op == Op.Mod)
                                {
                                    _locals[slot] = new Value(a % b);
                                    break;
                                }
                            }

                            _locals[slot] = BinaryOp(op, left, right);
                            break;
                        }

                        case Op.DivStoreLocal:
                        {
                            var right = _stack[--_sp];
                            var left = _stack[--_sp];
                            _locals[frameBase + instruction.A] = BinaryOp(Op.Div, left, right);
                            break;
                        }

                        // `x = x <op> y` fused with the store: no stack traffic at all.
                        case Op.AddLocalConstStore:
                        case Op.SubLocalConstStore:
                        {
                            ref var cell = ref _locals[frameBase + instruction.A];
                            var current = cell;
                            var delta = instruction.B;

                            if (current.Kind == ValueKind.Int)
                            {
                                var a = current.AsInt();
                                if (instruction.Op == Op.AddLocalConstStore && TryAdd(a, delta, out var sum))
                                {
                                    cell = new Value(sum);
                                    break;
                                }

                                if (instruction.Op == Op.SubLocalConstStore && TrySubtract(a, delta, out var difference))
                                {
                                    cell = new Value(difference);
                                    break;
                                }
                            }

                            // Add, not ArithmeticOp: `s = s + "x"` concatenates.
                            cell = Combine(
                                instruction.Op == Op.AddLocalConstStore ? Op.Add : Op.Sub,
                                current,
                                new Value((long)delta));
                            break;
                        }

                        case Op.AddLocalLocalStore:
                        case Op.SubLocalLocalStore:
                        {
                            ref var cell = ref _locals[frameBase + instruction.A];
                            var current = cell;
                            var other = _locals[frameBase + instruction.B];

                            if (current.Kind == ValueKind.Int && other.Kind == ValueKind.Int)
                            {
                                var a = current.AsInt();
                                var b = other.AsInt();
                                if (instruction.Op == Op.AddLocalLocalStore && TryAdd(a, b, out var sum))
                                {
                                    cell = new Value(sum);
                                    break;
                                }

                                if (instruction.Op == Op.SubLocalLocalStore && TrySubtract(a, b, out var difference))
                                {
                                    cell = new Value(difference);
                                    break;
                                }
                            }

                            cell = Combine(
                                instruction.Op == Op.AddLocalLocalStore ? Op.Add : Op.Sub,
                                current,
                                other);
                            break;
                        }

                        case Op.AddLocalStack:
                        case Op.SubLocalStack:
                        {
                            var right = _stack[--_sp];
                            ref var cell = ref _locals[frameBase + instruction.A];
                            var current = cell;

                            if (current.Kind == ValueKind.Int && right.Kind == ValueKind.Int)
                            {
                                var a = current.AsInt();
                                var b = right.AsInt();
                                if (instruction.Op == Op.AddLocalStack && TryAdd(a, b, out var sum))
                                {
                                    cell = new Value(sum);
                                    break;
                                }

                                if (instruction.Op == Op.SubLocalStack && TrySubtract(a, b, out var difference))
                                {
                                    cell = new Value(difference);
                                    break;
                                }
                            }

                            cell = Combine(
                                instruction.Op == Op.AddLocalStack ? Op.Add : Op.Sub,
                                current,
                                right);
                            break;
                        }

                        // Counted loops: one dispatch per iteration for `for (let i = a; i < n; i++)`.
                        case Op.ForPrepLt:
                        case Op.ForPrepLe:
                        case Op.ForPrepGt:
                        case Op.ForPrepGe:
                        {
                            var counter = _locals[frameBase + instruction.A];
                            if (counter.Kind != ValueKind.Int)
                            {
                                throw Error("TypeError",
                                    $"cannot compare {TypeName(counter)} with an integer loop bound");
                            }

                            var value = counter.AsInt();
                            var limit = (long)instruction.B;
                            var run = instruction.Op switch
                            {
                                Op.ForPrepLt => value < limit,
                                Op.ForPrepLe => value <= limit,
                                Op.ForPrepGt => value > limit,
                                _ => value >= limit,
                            };

                            if (!run)
                            {
                                ip = instruction.C;
                            }

                            break;
                        }

                        case Op.ForNextLt:
                        {
                            ref var counter = ref _locals[frameBase + instruction.A];
                            if (counter.Kind != ValueKind.Int)
                            {
                                LoopCounterNotInt(counter);
                            }

                            var value = counter.AsInt() + 1;
                            counter = new Value(value);

                            if (value < (long)instruction.B)
                            {
                                if (++steps > budget)
                                {
                                    _steps = steps;
                                    CheckBudget(frame, ip);
                                }

                                ip = instruction.C;
                            }

                            break;
                        }

                        case Op.ForNextLe:
                        {
                            ref var counter = ref _locals[frameBase + instruction.A];
                            if (counter.Kind != ValueKind.Int)
                            {
                                LoopCounterNotInt(counter);
                            }

                            var value = counter.AsInt() + 1;
                            counter = new Value(value);

                            if (value <= (long)instruction.B)
                            {
                                if (++steps > budget)
                                {
                                    _steps = steps;
                                    CheckBudget(frame, ip);
                                }

                                ip = instruction.C;
                            }

                            break;
                        }

                        case Op.ForNextGt:
                        {
                            ref var counter = ref _locals[frameBase + instruction.A];
                            if (counter.Kind != ValueKind.Int)
                            {
                                LoopCounterNotInt(counter);
                            }

                            var value = counter.AsInt() - 1;
                            counter = new Value(value);

                            if (value > (long)instruction.B)
                            {
                                if (++steps > budget)
                                {
                                    _steps = steps;
                                    CheckBudget(frame, ip);
                                }

                                ip = instruction.C;
                            }

                            break;
                        }

                        case Op.ForNextGe:
                        {
                            ref var counter = ref _locals[frameBase + instruction.A];
                            if (counter.Kind != ValueKind.Int)
                            {
                                LoopCounterNotInt(counter);
                            }

                            var value = counter.AsInt() - 1;
                            counter = new Value(value);

                            if (value >= (long)instruction.B)
                            {
                                if (++steps > budget)
                                {
                                    _steps = steps;
                                    CheckBudget(frame, ip);
                                }

                                ip = instruction.C;
                            }

                            break;
                        }

                        case Op.IncLocalJump:
                        {
                            ref var cell = ref _locals[frameBase + instruction.A];
                            var current = cell;
                            cell = current.Kind switch
                            {
                                ValueKind.Int => new Value(unchecked(current.AsInt() + instruction.B)),
                                ValueKind.Float => new Value(current.AsDouble() + instruction.B),
                                _ => throw Error("TypeError", $"cannot increment {TypeName(current)}"),
                            };

                            if (++steps > budget)
                            {
                                _steps = steps;
                                CheckBudget(frame, ip);
                            }

                            ip = instruction.C;
                            break;
                        }

                        case Op.IncLocal:
                        {
                            ref var cell = ref _locals[frameBase + instruction.A];
                            var current = cell;
                            if (current.Kind == ValueKind.Int)
                            {
                                cell = new Value(unchecked(current.AsInt() + instruction.B));
                                break;
                            }

                            if (current.Kind == ValueKind.Float)
                            {
                                cell = new Value(current.AsDouble() + instruction.B);
                                break;
                            }

                            throw Error("TypeError", $"cannot increment {TypeName(current)}");
                        }

                        case Op.ArgOrJump:
                        {
                            if (_locals[frameBase + instruction.A].Kind != ValueKind.Missing)
                            {
                                ip = instruction.B;
                            }

                            break;
                        }

                        case Op.CloseUpvalues:
                        {
                            var open = frame.OpenUpvalues;
                            if (open is not null)
                            {
                                for (var i = open.Count - 1; i >= 0; i--)
                                {
                                    if (open[i].Index >= frameBase + instruction.A)
                                    {
                                        open[i].Close();
                                        open.RemoveAt(i);
                                    }
                                }
                            }

                            break;
                        }

                        case Op.LoadField:
                        {
                            var target = _stack[--_sp];
                            Push(LoadMember(target, constants[instruction.A].AsString()));
                            break;
                        }

                        case Op.Length:
                        {
                            // `x.length` for the three container kinds, without the
                            // general member lookup (which calls into the builtins).
                            ref var target = ref _stack[_sp - 1];
                            switch (target.Kind)
                            {
                                case ValueKind.Str:
                                    target = new Value(target.AsString().Length);
                                    break;
                                case ValueKind.List:
                                    target = new Value(target.AsList().Count);
                                    break;
                                case ValueKind.Map:
                                {
                                    var map = target.AsMap();
                                    var name = constants[instruction.A].AsString();
                                    if (map.ContainsKey(Value.Str(name)))
                                    {
                                        target = LoadMember(target, name);
                                        break;
                                    }

                                    target = new Value(map.Count);
                                    break;
                                }
                                default:
                                    target = LoadMember(target, constants[instruction.A].AsString());
                                    break;
                            }

                            break;
                        }

                        case Op.LoadIndex:
                        {
                            var index = _stack[--_sp];
                            var target = _stack[--_sp];
                            Push(LoadIndexed(target, index));
                            break;
                        }

                        case Op.StoreIndex:
                        {
                            var value = _stack[--_sp];
                            var index = _stack[--_sp];
                            var target = _stack[--_sp];
                            StoreIndexed(target, index, value);
                            Push(value);
                            break;
                        }

                        case Op.StoreIndexDiscard:
                        {
                            var value = _stack[--_sp];
                            var index = _stack[--_sp];
                            var target = _stack[--_sp];
                            StoreIndexed(target, index, value);
                            break;
                        }

                        case Op.StoreFieldDiscard:
                        {
                            var value = _stack[--_sp];
                            var target = _stack[--_sp];
                            StoreMember(target, constants[instruction.A].AsString(), value);
                            break;
                        }

                        case Op.StoreField:
                        {
                            var value = _stack[--_sp];
                            var target = _stack[--_sp];
                            StoreMember(target, constants[instruction.A].AsString(), value);
                            Push(value);
                            break;
                        }

                        case Op.In:
                        {
                            var container = _stack[--_sp];
                            var item = _stack[--_sp];

                            if (container.Kind == ValueKind.List)
                            {
                                var list = container.AsList();
                                var found = false;
                                for (var i = 0; i < list.Count; i++)
                                {
                                    if (list[i].Equals(item))
                                    {
                                        found = true;
                                        break;
                                    }
                                }

                                Push(found ? Value.True : Value.False);
                                break;
                            }

                            Push(Contains(item, container) ? Value.True : Value.False);
                            break;
                        }

                        case Op.Slice:
                        {
                            var end = _stack[--_sp];
                            var start = _stack[--_sp];
                            var target = _stack[--_sp];
                            Push(Slice(target, start, end, instruction.A != 0));
                            break;
                        }

                        case Op.NewRange:
                        {
                            var end = _stack[--_sp];
                            var start = _stack[--_sp];
                            if (start.Kind != ValueKind.Int || end.Kind != ValueKind.Int)
                            {
                                throw Error("TypeError", "range bounds must be integers");
                            }

                            Push(Value.Of(new TernRange(start.AsInt(), end.AsInt(), instruction.A != 0)));
                            break;
                        }

                        case Op.LoadModule:
                        {
                            var target = constants[instruction.A].AsModule();
                            ExecuteModule(target);
                            Push(Value.Of(target));
                            break;
                        }

                        case Op.NewList:
                        {
                            var count = instruction.A;
                            var list = new TernList(count);
                            var start = _sp - count;
                            for (var i = 0; i < count; i++)
                            {
                                list.Add(_stack[start + i]);
                            }

                            _sp = start;
                            Push(Value.Of(list));
                            break;
                        }

                        case Op.NewMap:
                        {
                            var pairs = instruction.A;
                            var map = new TernMap(Math.Max(4, pairs));
                            var start = _sp - pairs * 2;
                            for (var i = 0; i < pairs; i++)
                            {
                                map.Set(_stack[start + i * 2], _stack[start + i * 2 + 1]);
                            }

                            _sp = start;
                            Push(Value.Of(map));
                            break;
                        }

                        case Op.NewInstance:
                        {
                            var argc = instruction.B;
                            var typeValue = constants[instruction.A];
                            var type = (TernType)typeValue.AsObject()!;
                            var instance = type.Create(this);

                            if (argc > type.FieldCount)
                            {
                                throw Error("TypeError",
                                    $"{type.Name}() takes at most {type.FieldCount} field arguments but {argc} were given");
                            }

                            var start = _sp - argc;
                            for (var i = 0; i < argc; i++)
                            {
                                instance.Fields[i] = _stack[start + i];
                            }

                            _sp = start;
                            Push(Value.Of(instance));
                            break;
                        }

                        case Op.Concat:
                        {
                            var count = instruction.A;
                            var start = _sp - count;
                            if (count == 1)
                            {
                                ref var only = ref _stack[start];
                                only = Value.Str(only.ToDisplayString());
                                _sp = start + 1;
                                break;
                            }

                            _sp = start;
                            Push(ConcatValues(start, count));
                            break;
                        }

                        case Op.Add:
                        case Op.Sub:
                        case Op.Mul:
                        case Op.Div:
                        case Op.Mod:
                        case Op.Pow:
                        case Op.BAnd:
                        case Op.BOr:
                        case Op.BXor:
                        case Op.Shl:
                        case Op.Shr:
                        case Op.Eq:
                        case Op.Ne:
                        case Op.Lt:
                        case Op.Le:
                        case Op.Gt:
                        case Op.Ge:
                        {
                            // Operands are read through refs: copying a Value into a
                            // local costs a struct-copy helper call on .NET.
                            _sp -= 2;
                            ref var left = ref _stack[_sp];
                            ref var right = ref _stack[_sp + 1];

                            // Integers dominate real scripts, so they skip the
                            // generic dispatch in BinaryOp entirely.
                            if (left.Kind == ValueKind.Int && right.Kind == ValueKind.Int)
                            {
                                var a = left.AsInt();
                                var b = right.AsInt();

                                switch (instruction.Op)
                                {
                                    // Overflow promotes to a float, exactly like
                                    // Add and ArithmeticOp: wrapping here would make
                                    // a compiled binary disagree with the interpreter.
                                    case Op.Add when TryAdd(a, b, out var sum):
                                        Push(new Value(sum));
                                        goto binaryDone;
                                    case Op.Sub when TrySubtract(a, b, out var difference):
                                        Push(new Value(difference));
                                        goto binaryDone;
                                    case Op.Mul when TryMultiply(a, b, out var product):
                                        Push(new Value(product));
                                        goto binaryDone;
                                    case Op.Mod when b != 0:
                                        Push(new Value(a % b));
                                        goto binaryDone;
                                    case Op.BAnd:
                                        Push(new Value(a & b));
                                        goto binaryDone;
                                    case Op.BOr:
                                        Push(new Value(a | b));
                                        goto binaryDone;
                                    case Op.BXor:
                                        Push(new Value(a ^ b));
                                        goto binaryDone;
                                    case Op.Shl:
                                        Push(new Value(a << (int)(b & 63)));
                                        goto binaryDone;
                                    case Op.Shr:
                                        Push(new Value(a >> (int)(b & 63)));
                                        goto binaryDone;
                                    case Op.Lt:
                                        Push(a < b ? Value.True : Value.False);
                                        goto binaryDone;
                                    case Op.Le:
                                        Push(a <= b ? Value.True : Value.False);
                                        goto binaryDone;
                                    case Op.Gt:
                                        Push(a > b ? Value.True : Value.False);
                                        goto binaryDone;
                                    case Op.Ge:
                                        Push(a >= b ? Value.True : Value.False);
                                        goto binaryDone;
                                    case Op.Eq:
                                        Push(a == b ? Value.True : Value.False);
                                        goto binaryDone;
                                    case Op.Ne:
                                        Push(a != b ? Value.True : Value.False);
                                        goto binaryDone;
                                }
                            }

                            Push(BinaryOp(instruction.Op, left, right));

                        binaryDone:
                            break;
                        }

                        case Op.AddLocalLocal:
                        case Op.SubLocalLocal:
                        case Op.MulLocalLocal:
                        case Op.DivLocalLocal:
                        case Op.ModLocalLocal:
                        {
                            var left = _locals[frameBase + instruction.A];
                            var right = _locals[frameBase + instruction.B];

                            if (left.Kind == ValueKind.Int && right.Kind == ValueKind.Int)
                            {
                                var a = left.AsInt();
                                var b = right.AsInt();
                                Push(instruction.Op switch
                                {
                                    Op.AddLocalLocal when TryAdd(a, b, out var sum) => new Value(sum),
                                    Op.SubLocalLocal when TrySubtract(a, b, out var difference) => new Value(difference),
                                    Op.MulLocalLocal when TryMultiply(a, b, out var product) => new Value(product),
                                    Op.ModLocalLocal when b != 0 => new Value(a % b),
                                    _ => Combine(instruction.Op, left, right),
                                });

                                break;
                            }

                            Push(Combine(instruction.Op, left, right));
                            break;
                        }

                        case Op.AddLocalConst:
                        case Op.SubLocalConst:
                        case Op.MulLocalConst:
                        case Op.DivLocalConst:
                        case Op.ModLocalConst:
                        {
                            var left = _locals[frameBase + instruction.A];
                            var right = constants[instruction.B];

                            if (left.Kind == ValueKind.Int && right.Kind == ValueKind.Int)
                            {
                                var a = left.AsInt();
                                var b = right.AsInt();
                                Push(instruction.Op switch
                                {
                                    Op.AddLocalConst when TryAdd(a, b, out var sum) => new Value(sum),
                                    Op.SubLocalConst when TrySubtract(a, b, out var difference) => new Value(difference),
                                    Op.MulLocalConst when TryMultiply(a, b, out var product) => new Value(product),
                                    Op.ModLocalConst when b != 0 => new Value(a % b),
                                    _ => Combine(instruction.Op, left, right),
                                });

                                break;
                            }

                            Push(Combine(instruction.Op, left, right));
                            break;
                        }

                        case Op.ModLocalConstMagic:
                        {
                            var cell = _locals[frameBase + instruction.A];
                            if (cell.Kind == ValueKind.Int)
                            {
                                var x = cell.AsInt();
                                ref readonly var magic = ref divisions[instruction.B];
                                var divisor = magic.Divisor;

                                // Unsigned reciprocal of |x|, then an exact range check.
                                var magnitude = x < 0 ? (ulong)(-(x + 1)) + 1 : (ulong)x;
                                var quotient = Math.BigMul(magnitude, magic.Magic, out _) >> magic.Shift;
                                var remainder = (long)(magnitude - quotient * (ulong)divisor);

                                if ((ulong)remainder >= (ulong)divisor)
                                {
                                    // Reciprocal was off by one for this value: divide for real.
                                    remainder = x % divisor;
                                    Push(new Value(remainder));
                                    break;
                                }

                                Push(new Value(x < 0 ? -remainder : remainder));
                                break;
                            }

                            Push(ArithmeticOp(Op.Mod, cell, new Value((long)divisions[instruction.B].Divisor)));
                            break;
                        }

                        case Op.Neg:
                        {
                            var value = _stack[_sp - 1];
                            if (value.Kind == ValueKind.Int)
                            {
                                _stack[_sp - 1] = new Value(unchecked(-value.AsInt()));
                                break;
                            }

                            if (value.Kind == ValueKind.Float)
                            {
                                _stack[_sp - 1] = new Value(-value.AsDouble());
                                break;
                            }

                            throw Error("TypeError", $"cannot negate {TypeName(value)}");
                        }

                        case Op.Not:
                            _stack[_sp - 1] = Value.Bool(!_stack[_sp - 1].Truthy);
                            break;

                        case Op.BNot:
                        {
                            var value = _stack[_sp - 1];
                            if (value.Kind != ValueKind.Int)
                            {
                                throw Error("TypeError", "bitwise not requires an integer");
                            }

                            _stack[_sp - 1] = new Value(~value.AsInt());
                            break;
                        }

                        case Op.Jump:
                            // Every loop back edge is an unconditional jump, so counting
                            // steps here is enough to stop runaway scripts while keeping
                            // the per-instruction dispatch free of bookkeeping.
                            if (++steps > budget)
                            {
                                _steps = steps;
                                CheckBudget(frame, ip);
                            }

                            ip = instruction.A;
                            break;

                        case Op.JumpIfFalse:
                            if (!_stack[--_sp].Truthy)
                            {
                                ip = instruction.A;
                            }

                            break;

                        case Op.JumpIfTrue:
                            if (_stack[--_sp].Truthy)
                            {
                                ip = instruction.A;
                            }

                            break;

                        case Op.JumpIfFalseKeep:
                            if (!_stack[_sp - 1].Truthy)
                            {
                                ip = instruction.A;
                            }

                            break;

                        case Op.JumpIfTrueKeep:
                            if (_stack[_sp - 1].Truthy)
                            {
                                ip = instruction.A;
                            }

                            break;

                        case Op.JumpIfNotNullKeep:
                            if (!_stack[_sp - 1].IsNil)
                            {
                                ip = instruction.A;
                            }

                            break;

                        case Op.JumpIfFalseLocal:
                        {
                            locals = _locals;
                            if (!locals[frameBase + instruction.A].Truthy)
                            {
                                ip = instruction.B;
                            }

                            break;
                        }

                        case Op.JumpIfTrueLocal:
                        {
                            locals = _locals;
                            if (locals[frameBase + instruction.A].Truthy)
                            {
                                ip = instruction.B;
                            }

                            break;
                        }

                        case Op.JumpIfNotList:
                        {
                            locals = _locals;
                            if (locals[frameBase + instruction.A].Kind != ValueKind.List)
                            {
                                ip = instruction.B;
                            }

                            break;
                        }

                        case Op.JumpIfNotMap:
                        {
                            locals = _locals;
                            if (locals[frameBase + instruction.A].Kind != ValueKind.Map)
                            {
                                ip = instruction.B;
                            }

                            break;
                        }

                        // Range patterns only match numbers: any other subject simply
                        // fails the arm instead of raising a comparison error.
                        case Op.JumpIfNotNumber:
                        {
                            locals = _locals;
                            var kind = locals[frameBase + instruction.A].Kind;
                            if (kind != ValueKind.Int && kind != ValueKind.Float)
                            {
                                ip = instruction.B;
                            }

                            break;
                        }

                        // Fused numeric compare and branch: one dispatch instead of three.
                        case Op.JumpLtLocalInt:
                        case Op.JumpLeLocalInt:
                        case Op.JumpGtLocalInt:
                        case Op.JumpGeLocalInt:
                        case Op.JumpEqLocalInt:
                        case Op.JumpNeLocalInt:
                        {
                            var cell = _locals[frameBase + instruction.A];

                            if (cell.Kind == ValueKind.Int)
                            {
                                var left = cell.AsInt();
                                var right = instruction.B;
                                var take = instruction.Op switch
                                {
                                    Op.JumpLtLocalInt => left < right,
                                    Op.JumpLeLocalInt => left <= right,
                                    Op.JumpGtLocalInt => left > right,
                                    Op.JumpGeLocalInt => left >= right,
                                    Op.JumpEqLocalInt => left == right,
                                    _ => left != right,
                                };

                                if (!take)
                                {
                                    ip = instruction.C;
                                }

                                break;
                            }

                            // Slow path: floats, strings, and other comparable values.
                            var slowResult = CompareLocalToInt(cell, instruction.B);
                            var slowTake = instruction.Op switch
                            {
                                Op.JumpLtLocalInt => slowResult < 0,
                                Op.JumpLeLocalInt => slowResult <= 0,
                                Op.JumpGtLocalInt => slowResult > 0,
                                Op.JumpGeLocalInt => slowResult >= 0,
                                Op.JumpEqLocalInt => slowResult == 0,
                                _ => slowResult != 0,
                            };

                            if (!slowTake)
                            {
                                ip = instruction.C;
                            }

                            break;
                        }

                        case Op.JumpLtLocalLocal:
                        case Op.JumpLeLocalLocal:
                        case Op.JumpGtLocalLocal:
                        case Op.JumpGeLocalLocal:
                        case Op.JumpEqLocalLocal:
                        case Op.JumpNeLocalLocal:
                        {
                            var leftCell = _locals[frameBase + instruction.A];
                            var rightCell = _locals[frameBase + instruction.B];

                            if (leftCell.Kind == ValueKind.Int && rightCell.Kind == ValueKind.Int)
                            {
                                var left = leftCell.AsInt();
                                var right = rightCell.AsInt();
                                var take = instruction.Op switch
                                {
                                    Op.JumpLtLocalLocal => left < right,
                                    Op.JumpLeLocalLocal => left <= right,
                                    Op.JumpGtLocalLocal => left > right,
                                    Op.JumpGeLocalLocal => left >= right,
                                    Op.JumpEqLocalLocal => left == right,
                                    _ => left != right,
                                };

                                if (!take)
                                {
                                    ip = instruction.C;
                                }

                                break;
                            }

                            var result = CompareOrThrow(leftCell, rightCell);
                            var slowTake = instruction.Op switch
                            {
                                Op.JumpLtLocalLocal => result < 0,
                                Op.JumpLeLocalLocal => result <= 0,
                                Op.JumpGtLocalLocal => result > 0,
                                Op.JumpGeLocalLocal => result >= 0,
                                Op.JumpEqLocalLocal => result == 0,
                                _ => result != 0,
                            };

                            if (!slowTake)
                            {
                                ip = instruction.C;
                            }

                            break;
                        }

                        case Op.JumpCmp:
                        {
                            var right = _stack[--_sp];
                            var left = _stack[--_sp];
                            bool condition;
                            switch ((CompareOp)instruction.A)
                            {
                                case CompareOp.Lt:
                                    condition = CompareOrThrow(left, right) < 0;
                                    break;
                                case CompareOp.Le:
                                    condition = CompareOrThrow(left, right) <= 0;
                                    break;
                                case CompareOp.Gt:
                                    condition = CompareOrThrow(left, right) > 0;
                                    break;
                                case CompareOp.Ge:
                                    condition = CompareOrThrow(left, right) >= 0;
                                    break;
                                case CompareOp.Eq:
                                    condition = left.Equals(right);
                                    break;
                                default:
                                    condition = !left.Equals(right);
                                    break;
                            }

                            if (condition == (instruction.C != 0))
                            {
                                ip = instruction.B;
                            }

                            break;
                        }

                        case Op.Call:
                        {
                            var argc = instruction.A;
                            var argsBase = _sp - argc;
                            var callee = _stack[argsBase - 1];
                            frame.Ip = ip;
                            if (!TryEnterCall(callee, argc, argsBase, argsBase - 1))
                            {
                                throw Error("TypeError", $"{TypeName(callee)} is not callable", frame, ip);
                            }

                            goto nextFrame;
                        }

                        case Op.CallLocal:
                        {
                            var argc = instruction.A;
                            var slot = frameBase + instruction.B;
                            var callee = _locals[slot];
                            var argsBase = _sp - argc;
                            frame.Ip = ip;
                            if (!TryEnterCall(callee, argc, argsBase, argsBase))
                            {
                                throw Error("TypeError", $"{TypeName(callee)} is not callable", frame, ip);
                            }

                            goto nextFrame;
                        }

                        case Op.CallGlobal when module is not null:
                        {
                            var argc = instruction.A;
                            var callee = module.Globals[instruction.B];
                            var argsBase = _sp - argc;
                            frame.Ip = ip;
                            if (!TryEnterCall(callee, argc, argsBase, argsBase))
                            {
                                throw Error("TypeError", $"{TypeName(callee)} is not callable", frame, ip);
                            }

                            goto nextFrame;
                        }

                        case Op.Invoke:
                        {
                            var argc = instruction.B;
                            var receiverIndex = _sp - argc - 1;
                            var receiver = _stack[receiverIndex];
                            frame.Ip = ip;
                            var depth = _frameCount;

                            if (!TryInvoke(receiver, constants[instruction.A].AsString(), argc, receiverIndex))
                            {
                                throw Error("TypeError",
                                    $"{TypeName(receiver)} has no method '{constants[instruction.A].AsString()}'", frame, ip);
                            }

                            if (_frameCount == depth)
                            {
                                break;
                            }

                            goto nextFrame;
                        }

                        case Op.MakeClosure:
                        {
                            var prototype = frame.Closure!.Proto.Prototypes[instruction.A]!;
                            var upvalueCount = prototype.Upvalues.Length;
                            var upvalues = upvalueCount == 0 ? Array.Empty<Upvalue>() : new Upvalue[upvalueCount];

                            for (var i = 0; i < upvalueCount; i++)
                            {
                                var descriptor = prototype.Upvalues[i];
                                if (descriptor.FromLocal)
                                {
                                    upvalues[i] = CaptureUpvalue(frame, frameBase + descriptor.Index);
                                }
                                else
                                {
                                    upvalues[i] = frame.Closure.Upvalues[descriptor.Index];
                                }
                            }

                            Push(Value.Of(new TernClosure(prototype, upvalues) { Module = module }));
                            break;
                        }

                        case Op.Return:
                        {
                            var result = _stack[--_sp];

                            // Inline fast path: no open upvalues and no active handlers.
                            if (frame.OpenUpvalues is null && _handlers.Count == 0)
                            {
                                _frameCount--;
                                _sp = frame.StackBase;
                                _localsTop = frame.LocalsBase;

                                if (_frameCount <= frameFloor)
                                {
                                    _steps = steps;
                                    return result;
                                }

                                Push(result);
                                goto nextFrame;
                            }

                            frame.Ip = ip;

                            if (ReturnFrame(result, frameFloor))
                            {
                                return result;
                            }

                            goto nextFrame;
                        }

                        case Op.ReturnLocal:
                        {
                            var result = _locals[frameBase + instruction.A];

                            if (frame.OpenUpvalues is null && _handlers.Count == 0)
                            {
                                _frameCount--;
                                _sp = frame.StackBase;
                                _localsTop = frame.LocalsBase;

                                if (_frameCount <= frameFloor)
                                {
                                    _steps = steps;
                                    return result;
                                }

                                Push(result);
                                goto nextFrame;
                            }

                            frame.Ip = ip;

                            if (ReturnFrame(result, frameFloor))
                            {
                                return result;
                            }

                            goto nextFrame;
                        }

                        case Op.ReturnNil:
                        {
                            frame.Ip = ip;
                            if (ReturnFrame(Value.Nil, frameFloor))
                            {
                                _steps = steps;
                                return Value.Nil;
                            }

                            goto nextFrame;
                        }

                        case Op.IterNew:
                        {
                            var iterable = _stack[_sp - 1];
                            _stack[_sp - 1] = Value.Of(CreateIterator(iterable));
                            break;
                        }

                        case Op.IterNext:
                        {
                            // The iterator is *consumed*: the compiler records
                            // pop: 1 / push: bindings, so leaving the (already
                            // advanced) iterator on the stack would leak one slot
                            // per iteration - unbounded growth for `for` loops.
                            var iterator = _stack[--_sp].AsIterator();
                            var bindings = instruction.B;

                            if (!iterator.Next(out var first, out var second))
                            {
                                ip = instruction.A;
                                break;
                            }

                            for (var i = 0; i < bindings; i++)
                            {
                                Push(i == 0 ? first : second);
                            }

                            break;
                        }

                        case Op.EnterTry:
                            _handlers.Add(new Handler(_frameCount - 1, instruction.A, _sp));
                            break;

                        case Op.ExitTry:
                            if (_handlers.Count > 0)
                            {
                                _handlers.RemoveAt(_handlers.Count - 1);
                            }

                            break;

                        case Op.Throw:
                        {
                            var value = _stack[--_sp];
                            frame.Ip = ip;

                            if (!DispatchThrow(value, frame, ip))
                            {
                                throw BuildError(value);
                            }

                            goto nextFrame;
                        }

                        case Op.ToString:
                            _stack[_sp - 1] = Value.Str(_stack[_sp - 1].ToDisplayString());
                            break;

                        case Op.TypeOf:
                            Push(Value.Str(TypeName(_stack[--_sp])));
                            break;

                        default:
                            throw Error("InternalError", $"unknown opcode {instruction.Op}", frame, ip);
                    }
                }

                }
                catch (TernError error)
                {
                    // Publish the counted steps: an escaping error aborts the loop, and
                    // the budget must not silently reset when execution resumes later.
                    _steps = steps;

                    // Runtime errors use the same handler stack as `throw`, so scripts
                    // can catch arity/index/type failures alongside their own errors.
                    frame.Ip = ip;
                    LocateTopFrame(error, frame, ip);
                    if (!DispatchThrow(error.Payload, frame, ip))
                    {
                        throw;
                    }
                }

                nextFrame:
                _steps = steps;
                steps = _steps;

                if (_frameCount <= frameFloor)
                {
                    return Value.Nil;
                }
            }

            return Value.Nil;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CheckBudget(Frame frame, int ip)
        {
            if (_steps > Options.MaxInstructions)
            {
                throw Error("ResourceError", $"script exceeded the instruction budget of {Options.MaxInstructions}", frame, ip);
            }
        }

        // ------------------------------------------------------------------ stack

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Push(in Value value)
        {
            // Frames reserve `StackSize` slots when they are entered, so this is
            // a de-facto never-taken guard that keeps the JIT from eliding safety.
            if (_sp >= _stack.Length)
            {
                GrowStack(_sp + 1);
            }

            _stack[_sp++] = value;
        }

        /// <summary>
        /// A <see cref="Value"/> array zero-filled as nil rather than as integer 0
        /// (a default <c>Value</c> is the integer 0).
        /// </summary>
        internal static Value[] CreateValues(int length)
        {
            var values = new Value[length];
            Array.Fill(values, Value.Nil);
            return values;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrowStack(int needed)
        {
            var size = _stack.Length;
            while (size < needed)
            {
                size *= 2;
            }

            var old = _stack.Length;
            Array.Resize(ref _stack, size);
            Array.Fill(_stack, Value.Nil, old, size - old);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Value Pop() => _stack[--_sp];

        private void EnsureLocals(int needed)
        {
            if (needed <= _locals.Length)
            {
                return;
            }

            var size = _locals.Length;
            while (size < needed)
            {
                size *= 2;
            }

            var replacement = CreateValues(size);
            Array.Copy(_locals, replacement, _localsTop);

            // Rebind open upvalues; they point into the old array.
            for (var f = 0; f < _frameCount; f++)
            {
                var open = _frames[f].OpenUpvalues;
                if (open is null)
                {
                    continue;
                }

                foreach (var upvalue in open)
                {
                    upvalue.Array = replacement;
                }
            }

            _locals = replacement;
        }

        private Upvalue CaptureUpvalue(Frame frame, int slot)
        {
            frame.OpenUpvalues ??= new List<Upvalue>(4);

            foreach (var existing in frame.OpenUpvalues)
            {
                if (existing.Index == slot)
                {
                    return existing;
                }
            }

            var upvalue = new Upvalue { Array = _locals, Index = slot };
            frame.OpenUpvalues.Add(upvalue);
            return upvalue;
        }

        // ------------------------------------------------------------------ frames

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureLocalsFast(int needed)
        {
            if (needed > _locals.Length)
            {
                GrowLocals(needed);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrowLocals(int needed)
        {
            var size = _locals.Length;
            while (size < needed)
            {
                size *= 2;
            }

            var old = _locals.Length;
            Array.Resize(ref _locals, size);
            Array.Fill(_locals, Value.Nil, old, size - old);
        }

        private static Frame[] CreateFrames(int count)
        {
            var frames = new Frame[count];
            for (var i = 0; i < count; i++)
            {
                frames[i] = new Frame();
            }

            return frames;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrowFrames()
        {
            var old = _frames.Length;
            var frames = new Frame[old * 2];
            Array.Copy(_frames, frames, old);
            for (var i = old; i < frames.Length; i++)
            {
                frames[i] = new Frame();
            }

            _frames = frames;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PushFrame(TernClosure closure, int argc, int stackBase, bool isMethod)
        {
            var proto = closure.Proto;

            if (_frameCount >= _maxCallDepth)
            {
                ThrowDepthExceeded();
            }

            var localCount = proto.LocalCount > 0 ? proto.LocalCount : 1;
            var needed = _localsTop + localCount;
            if (needed > _locals.Length)
            {
                GrowLocals(needed);
            }

            if (_frameCount == _frames.Length)
            {
                GrowFrames();
            }

            var frame = _frames[_frameCount++];

            // Reserve the callee's whole expression stack up front so the hot
            // opcode handlers never have to grow it mid-frame.
            var stackNeed = stackBase + proto.MaxStack;
            if (stackNeed > _stack.Length)
            {
                GrowStack(stackNeed);
            }

            frame.Closure = closure;
            frame.Module = closure.Module;
            frame.Code = proto.Instructions;
            frame.Constants = proto.Constants;
            frame.Divisions = proto.Divisions;
            frame.Ip = 0;
            frame.LocalsBase = _localsTop;
            frame.StackBase = stackBase;
            frame.Argc = argc;
            frame.IsMethod = isMethod;
            frame.OpenUpvalues?.Clear();

            _localsTop += localCount;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void LoopCounterNotInt(in Value counter) =>
            throw Error("TypeError", $"loop counter became {TypeName(counter)}; it must stay an integer");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ThrowDepthExceeded() =>
            throw Error("StackOverflow", $"call depth exceeded {_maxCallDepth}");

        /// <summary>
        /// Sets up a call. Closures, natives, bound methods and struct types are
        /// handled here; natives are invoked immediately and never allocate a frame.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryEnterCall(in Value callee, int argc, int argsBase, int resultSlot)
        {
            if (callee.Kind == ValueKind.Closure)
            {
                EnterClosure(callee.AsClosure(), argc, argsBase, resultSlot);
                return true;
            }

            return TryEnterNonClosure(callee, argc, argsBase, resultSlot);
        }

        /// <summary>
        /// Enters a TERN function. Kept small and branch-predictable: the exact arity
        /// case (no defaults, no rest) is the one every recursive benchmark hits.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnterClosure(TernClosure closure, int argc, int argsBase, int resultSlot)
        {
            var proto = closure.Proto;

            // Fast path: exactly the declared number of arguments.
            if (argc == proto.ParamCount && !proto.HasRest)
            {
                PushFrame(closure, argc, resultSlot, proto.IsMethod);

                var baseSlot = _localsTop - (proto.LocalCount > 0 ? proto.LocalCount : 1);
                var target = baseSlot + (proto.IsMethod ? 1 : 0);

                // Small arities dominate; an unrolled copy beats Array.Copy there.
                switch (argc)
                {
                    case 0:
                        break;
                    case 1:
                        _locals[target + 0] = _stack[argsBase + 0];
                        break;
                    case 2:
                        _locals[target + 0] = _stack[argsBase + 0];
                        _locals[target + 1] = _stack[argsBase + 1];
                        break;
                    case 3:
                        _locals[target + 0] = _stack[argsBase + 0];
                        _locals[target + 1] = _stack[argsBase + 1];
                        _locals[target + 2] = _stack[argsBase + 2];
                        break;
                    case 4:
                        _locals[target + 0] = _stack[argsBase + 0];
                        _locals[target + 1] = _stack[argsBase + 1];
                        _locals[target + 2] = _stack[argsBase + 2];
                        _locals[target + 3] = _stack[argsBase + 3];
                        break;
                    case 5:
                        _locals[target + 0] = _stack[argsBase + 0];
                        _locals[target + 1] = _stack[argsBase + 1];
                        _locals[target + 2] = _stack[argsBase + 2];
                        _locals[target + 3] = _stack[argsBase + 3];
                        _locals[target + 4] = _stack[argsBase + 4];
                        break;
                    case 6:
                        _locals[target + 0] = _stack[argsBase + 0];
                        _locals[target + 1] = _stack[argsBase + 1];
                        _locals[target + 2] = _stack[argsBase + 2];
                        _locals[target + 3] = _stack[argsBase + 3];
                        _locals[target + 4] = _stack[argsBase + 4];
                        _locals[target + 5] = _stack[argsBase + 5];
                        break;
                    case 7:
                        _locals[target + 0] = _stack[argsBase + 0];
                        _locals[target + 1] = _stack[argsBase + 1];
                        _locals[target + 2] = _stack[argsBase + 2];
                        _locals[target + 3] = _stack[argsBase + 3];
                        _locals[target + 4] = _stack[argsBase + 4];
                        _locals[target + 5] = _stack[argsBase + 5];
                        _locals[target + 6] = _stack[argsBase + 6];
                        break;
                    case 8:
                        _locals[target + 0] = _stack[argsBase + 0];
                        _locals[target + 1] = _stack[argsBase + 1];
                        _locals[target + 2] = _stack[argsBase + 2];
                        _locals[target + 3] = _stack[argsBase + 3];
                        _locals[target + 4] = _stack[argsBase + 4];
                        _locals[target + 5] = _stack[argsBase + 5];
                        _locals[target + 6] = _stack[argsBase + 6];
                        _locals[target + 7] = _stack[argsBase + 7];
                        break;
                    default:
                        Array.Copy(_stack, argsBase, _locals, target, argc);
                        break;
                }
                if (proto.IsMethod)
                {
                    _locals[baseSlot] = Value.Nil;
                }

                _sp = resultSlot;
                return;
            }

            EnterClosureSlow(closure, argc, argsBase, resultSlot);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EnterClosureSlow(TernClosure closure, int argc, int argsBase, int resultSlot)
        {
            var proto = closure.Proto;

            if (argc < proto.RequiredParams)
            {
                throw Error("ArgumentError",
                    $"{proto.Name ?? "function"}() expects at least {proto.RequiredParams} argument(s) but got {argc}");
            }

            if (!proto.HasRest && argc > proto.ParamCount)
            {
                throw Error("ArgumentError",
                    $"{proto.Name ?? "function"}() expects at most {proto.ParamCount} argument(s) but got {argc}");
            }

            PushFrame(closure, argc, resultSlot, proto.IsMethod);

            var frame = _frames[_frameCount - 1];
            var baseSlot = frame.LocalsBase;
            var offset = proto.IsMethod ? 1 : 0;

            if (proto.IsMethod)
            {
                // Called without a receiver: `this` is nil, fields are unavailable.
                _locals[baseSlot] = Value.Nil;
            }

            for (var i = 0; i < proto.ParamCount; i++)
            {
                _locals[baseSlot + offset + i] = i < argc ? _stack[argsBase + i] : Value.Missing;
            }

            if (proto.HasRest)
            {
                var rest = new TernList(Math.Max(0, argc - proto.ParamCount));
                for (var i = proto.ParamCount; i < argc; i++)
                {
                    rest.Add(_stack[argsBase + i]);
                }

                _locals[baseSlot + offset + proto.ParamCount] = Value.Of(rest);
            }

            _sp = resultSlot;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool TryEnterNonClosure(in Value callee, int argc, int argsBase, int resultSlot)
        {
            switch (callee.Kind)
            {
                case ValueKind.Native:
                {
                    var native = callee.AsNative();
                    var slice = new ReadOnlySpan<Value>(_stack, argsBase, argc);
                    var result = InvokeNative(native, argc, slice);
                    _sp = resultSlot;
                    Push(result);
                    return true;
                }

                case ValueKind.Bound:
                {
                    var bound = callee.AsBound();
                    return TryEnterBound(bound, argc, argsBase, resultSlot);
                }

                case ValueKind.Instance:
                {
                    var instance = callee.AsInstance();
                    var result = ConstructInstance(instance.Type, argc, argsBase);
                    _sp = resultSlot;
                    Push(result);
                    return true;
                }

                case ValueKind.Type:
                {
                    var result = ConstructInstance(callee.AsType(), argc, argsBase);
                    _sp = resultSlot;
                    Push(result);
                    return true;
                }

                default:
                    return false;
            }
        }

        private bool TryEnterBound(TernBoundMethod bound, int argc, int argsBase, int resultSlot)
        {
            var target = bound.Callee;

            if (target.Kind == ValueKind.Native)
            {
                var native = target.AsNative();
                if (argc + 1 < native.MinArguments || (native.MaxArguments >= 0 && argc + 1 > native.MaxArguments))
                {
                    throw Error("ArgumentError",
                        $"{native.Name}() expects {(native.MaxArguments < 0 ? $"at least {native.MinArguments - 1}" : $"{native.MinArguments - 1} to {native.MaxArguments - 1}")} argument(s) but got {argc}");
                }

                var scratch = new Value[argc + 1];
                scratch[0] = bound.Receiver;
                for (var i = 0; i < argc; i++)
                {
                    scratch[i + 1] = _stack[argsBase + i];
                }

                var result = native.Function(this, scratch);
                _sp = resultSlot;
                Push(result);
                return true;
            }

            if (target.Kind == ValueKind.Closure)
            {
                var closure = target.AsClosure();
                var proto = closure.Proto;

                if (argc + 1 < proto.RequiredParams)
                {
                    throw Error("ArgumentError",
                        $"{proto.Name ?? "method"}() expects at least {proto.RequiredParams - 1} argument(s) but got {argc}");
                }

                var isMethod = proto.IsMethod;
                if (closure.Module is null)
                {
                    closure.Module = _frameCount > 0 ? _frames[_frameCount - 1].Module : null;
                }

                PushFrame(closure, argc, resultSlot, isMethod);
                var frame = _frames[_frameCount - 1];
                var baseSlot = frame.LocalsBase;
                var offset = isMethod ? 1 : 0;

                if (isMethod)
                {
                    _locals[baseSlot] = bound.Receiver;
                }

                for (var i = 0; i < proto.ParamCount; i++)
                {
                    _locals[baseSlot + offset + i] = i < argc ? _stack[argsBase + i] : Value.Missing;
                }

                if (proto.HasRest)
                {
                    var rest = new TernList(Math.Max(0, argc - proto.ParamCount));
                    for (var i = proto.ParamCount; i < argc; i++)
                    {
                        rest.Add(_stack[argsBase + i]);
                    }

                    _locals[baseSlot + offset + proto.ParamCount] = Value.Of(rest);
                }

                _sp = resultSlot;
                return true;
            }

            throw Error("TypeError", $"{TypeName(bound.Receiver)} method is not callable");
        }

        private Value ConstructInstance(TernType type, int argc, int argsBase)
        {
            if (argc > type.FieldCount)
            {
                throw Error("TypeError",
                    $"{type.Name}() takes at most {type.FieldCount} field arguments but got {argc}");
            }

            var instance = type.Create(this);
            for (var i = 0; i < argc; i++)
            {
                instance.Fields[i] = _stack[argsBase + i];
            }

            return Value.Of(instance);
        }

        private Value InvokeNative(TernNative native, int argc, ReadOnlySpan<Value> args)
        {
            if (argc < native.MinArguments || (native.MaxArguments >= 0 && argc > native.MaxArguments))
            {
                var expected = native.MaxArguments < 0
                    ? $"at least {native.MinArguments}"
                    : native.MinArguments == native.MaxArguments
                        ? $"{native.MinArguments}"
                        : $"{native.MinArguments} to {native.MaxArguments}";
                throw Error("ArgumentError", $"{native.Name}() expects {expected} argument(s) but got {argc}");
            }

            try
            {
                return native.Function(this, args);
            }
            catch (Exception error) when (error is InvalidCastException or NullReferenceException)
            {
                // A builtin that assumed the wrong argument type must surface as a
                // catchable TypeError: an unhandled cast or dereference here would
                // abort the whole process instead.
                throw Error("TypeError", $"{native.Name}(): argument is not of the expected type");
            }
        }

        private bool ReturnFrame(in Value result, int frameFloor)
        {
            var frame = _frames[_frameCount - 1];
            CloseFrame(frame);

            _frameCount--;
            _sp = frame.StackBase;
            _localsTop = frame.LocalsBase;

            if (_frameCount <= frameFloor)
            {
                return true;
            }

            Push(result);
            return false;
        }

        private void CloseFrame(Frame frame)
        {
            var open = frame.OpenUpvalues;
            if (open is not null)
            {
                foreach (var upvalue in open)
                {
                    upvalue.Close();
                }

                open.Clear();
            }

            // Drop try handlers that belong to this frame.
            for (var i = _handlers.Count - 1; i >= 0; i--)
            {
                if (_handlers[i].FrameIndex >= _frameCount - 1)
                {
                    _handlers.RemoveAt(i);
                }
            }
        }

        // -------------------------------------------------------------- exceptions

        private bool DispatchThrow(in Value value, Frame frame, int ip)
        {
            while (_handlers.Count > 0)
            {
                var handler = _handlers[^1];
                _handlers.RemoveAt(_handlers.Count - 1);

                if (handler.FrameIndex >= _frameCount)
                {
                    continue;
                }

                // Unwind frames until the handler's frame is on top.
                while (_frameCount - 1 > handler.FrameIndex)
                {
                    var dying = _frames[_frameCount - 1];
                    CloseFrame(dying);
                    _frameCount--;
                    _sp = dying.StackBase;
                    _localsTop = dying.LocalsBase;
                }

                var target = _frames[_frameCount - 1];
                target.Ip = handler.Ip;
                _sp = handler.StackDepth;
                Push(value);
                return true;
            }

            return false;
        }

        private TernError BuildError(in Value value)
        {
            var trace = CaptureTrace();
            if (value.Kind == ValueKind.Instance)
            {
                var instance = value.AsInstance();
                var message = instance.Fields.Length > 0 ? instance.Fields[0].ToDisplayString() : instance.ToString();
                return new TernError(instance.Type.Name, message, trace, value);
            }

            return new TernError("Error", value.ToDisplayString(), trace, value);
        }

        /// <summary>
        /// Pins the innermost trace entry of <paramref name="error"/> to the
        /// instruction that raised it. <see cref="CaptureTrace"/> runs inside the
        /// raising operation, where the frame's <c>Ip</c> still points at the last
        /// call it made, so without this the first entry would name a stale line.
        /// </summary>
        private void LocateTopFrame(TernError error, Frame frame, int ip)
        {
            if (error.TraceLocated)
            {
                return;
            }

            var proto = frame.Closure?.Proto;
            if (proto is null)
            {
                return;
            }

            if (error.Trace.Count == 0)
            {
                // Builtins and the runtime's own value types construct TernError
                // without a trace; the frame is still live here, so build one.
                error.SetTrace(CaptureTrace());
            }

            var index = Math.Max(0, ip - 1);
            var line = index < proto.Lines.Length ? proto.Lines[index] : 0;
            error.TraceLocated = true;
            error.LocateTopFrame($"{proto.Name ?? "<anonymous>"} ({proto.SourceName ?? "<script>"}:{line})");
        }

        public List<string> CaptureTrace()
        {
            var trace = new List<string>(Math.Min(_frameCount, 32));

            for (var i = _frameCount - 1; i >= 0 && trace.Count < 32; i--)
            {
                var frame = _frames[i];
                var proto = frame.Closure?.Proto;
                if (proto is null)
                {
                    continue;
                }

                var line = 0;
                var instructionIndex = Math.Max(0, frame.Ip - 1);
                if (instructionIndex < proto.Lines.Length)
                {
                    line = proto.Lines[instructionIndex];
                }

                var source = proto.SourceName;
                trace.Add($"{proto.Name ?? "<anonymous>"} ({source ?? "<script>"}:{line})");
            }

            return trace;
        }

        // ------------------------------------------------------------- operations

        public TernError Error(string type, string message) => new(type, message, CaptureTrace());

        private TernError Error(string type, string message, Frame frame, int ip)
        {
            var trace = CaptureTrace();
            return new TernError(type, message, trace);
        }

        private enum CompareOp
        {
            Lt = 0,
            Le = 1,
            Gt = 2,
            Ge = 3,
            Eq = 4,
            Ne = 5,
        }

        /// <summary>
        /// Concatenates the <paramref name="count"/> values starting at
        /// <paramref name="start"/>. Interpolations of ints, bools and strings make
        /// up the overwhelming majority, and those are written straight into a
        /// stack buffer: one allocation and no <see cref="StringBuilder"/>.
        /// Anything else falls back to the general path.
        /// </summary>
        private Value ConcatValues(int start, int count)
        {
            var size = 0;
            for (var i = 0; i < count; i++)
            {
                var part = _stack[start + i];
                switch (part.Kind)
                {
                    case ValueKind.Str:
                        size += part.AsString().Length;
                        break;
                    case ValueKind.Int:
                        size += MaxIntLength;
                        break;
                    case ValueKind.Bool:
                        size += MaxBoolLength;
                        break;
                    default:
                        return ConcatValuesSlow(start, count);
                }

                if (size > ConcatBufferSize)
                {
                    return ConcatValuesSlow(start, count);
                }
            }

            Span<char> buffer = stackalloc char[ConcatBufferSize];
            var written = 0;

            for (var i = 0; i < count; i++)
            {
                if (!TryFormatPart(_stack[start + i], buffer[written..], out var length))
                {
                    return ConcatValuesSlow(start, count);
                }

                written += length;
            }

            return Value.Str(new string(buffer[..written]));
        }

        private const int ConcatBufferSize = 64;
        private const int MaxIntLength = 20;
        private const int MaxBoolLength = 5;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private Value ConcatValuesSlow(int start, int count)
        {
            var builder = new StringBuilder(32);
            for (var i = 0; i < count; i++)
            {
                builder.Append(_stack[start + i].ToDisplayString());
            }

            return Value.Str(builder.ToString());
        }

        /// <summary>
        /// Writes a value in its display form into <paramref name="destination"/>.
        /// Returns false for kinds that need a heap string (containers, doubles, ...).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryFormatPart(in Value value, Span<char> destination, out int written)
        {
            switch (value.Kind)
            {
                case ValueKind.Str:
                {
                    var text = value.AsString();
                    if (text.Length > destination.Length)
                    {
                        written = 0;
                        return false;
                    }

                    text.AsSpan().CopyTo(destination);
                    written = text.Length;
                    return true;
                }

                case ValueKind.Int:
                    return value.AsInt().TryFormat(destination, out written, default, CultureInfo.InvariantCulture);

                case ValueKind.Bool:
                {
                    var word = value.AsBool() ? "true" : "false";
                    if (word.Length > destination.Length)
                    {
                        written = 0;
                        return false;
                    }

                    word.AsSpan().CopyTo(destination);
                    written = word.Length;
                    return true;
                }

                default:
                    written = 0;
                    return false;
            }
        }

        private Value BinaryOp(Op op, in Value left, in Value right)
        {
            switch (op)
            {
                case Op.Add:
                    return Add(left, right);
                case Op.Sub:
                case Op.Mul:
                case Op.Div:
                case Op.Mod:
                case Op.Pow:
                    return ArithmeticOp(op, left, right);
                case Op.BAnd:
                    return IntegerOp(op, left, right);
                case Op.BOr:
                    return IntegerOp(op, left, right);
                case Op.BXor:
                    return IntegerOp(op, left, right);
                case Op.Shl:
                    return IntegerOp(op, left, right);
                case Op.Shr:
                    return IntegerOp(op, left, right);
                case Op.Eq:
                    return left.Equals(right) ? Value.True : Value.False;
                case Op.Ne:
                    return !left.Equals(right) ? Value.True : Value.False;
                case Op.Lt:
                    return CompareOrThrow(left, right) < 0 ? Value.True : Value.False;
                case Op.Le:
                    return CompareOrThrow(left, right) <= 0 ? Value.True : Value.False;
                case Op.Gt:
                    return CompareOrThrow(left, right) > 0 ? Value.True : Value.False;
                case Op.Ge:
                    return CompareOrThrow(left, right) >= 0 ? Value.True : Value.False;
                default:
                    throw Error("InternalError", $"unexpected binary opcode {op}");
            }
        }

        /// <summary>
        /// `+` and `-` for the fused store opcodes. Addition goes through
        /// <see cref="Add"/> so strings and lists concatenate; everything else
        /// is plain arithmetic.
        /// </summary>
        private Value Combine(Op op, in Value left, in Value right) =>
            BaseArithmeticOp(op) == Op.Add ? Add(left, right) : ArithmeticOp(op, left, right);

        private Value ArithmeticOp(Op op, in Value left, in Value right)
        {
            op = BaseArithmeticOp(op);

            if (left.Kind == ValueKind.Int && right.Kind == ValueKind.Int)
            {
                var a = left.AsInt();
                var b = right.AsInt();

                switch (op)
                {
                    case Op.Add:
                        return TryAdd(a, b, out var sum)
                            ? new Value(sum)
                            : new Value((double)a + b);
                    case Op.Sub:
                        return TrySubtract(a, b, out var difference)
                            ? new Value(difference)
                            : new Value((double)a - b);
                    case Op.Mul:
                        return TryMultiply(a, b, out var product)
                            ? new Value(product)
                            : new Value((double)a * b);
                    case Op.Div:
                        if (b == 0)
                        {
                            throw Error("DivisionByZero", "division by zero");
                        }

                        // long.MinValue / -1 overflows the integer range; fall back to a float.
                        if (b == -1)
                        {
                            return a == long.MinValue ? new Value(-(double)a) : new Value(-a);
                        }

                        // Exact integer division stays an int; otherwise promote to float.
                        return a % b == 0 ? new Value(a / b) : new Value((double)a / b);
                    case Op.Mod:
                        if (b == 0)
                        {
                            throw Error("DivisionByZero", "modulo by zero");
                        }

                        // long.MinValue % -1 overflows the hardware divide; the result is 0.
                        return new Value(b == -1 ? 0 : a % b);
                    case Op.Pow:
                        if (b >= 0 && b <= 62)
                        {
                            long result = 1;
                            var baseValue = a;
                            var exponent = b;
                            while (exponent > 0)
                            {
                                if ((exponent & 1) != 0)
                                {
                                    result = unchecked(result * baseValue);
                                }

                                baseValue = unchecked(baseValue * baseValue);
                                exponent >>= 1;
                            }

                            return new Value(result);
                        }

                        return new Value(Math.Pow(a, b));
                    case Op.AddLocalConst:
                    case Op.SubLocalConst:
                    case Op.MulLocalConst:
                    case Op.DivLocalConst:
                    case Op.ModLocalConst:
                        break;
                }
            }

            if (left.IsNumber && right.IsNumber)
            {
                var a = left.AsDouble();
                var b = right.AsDouble();

                switch (op)
                {
                    case Op.Add:
                    case Op.AddLocalLocal:
                    case Op.AddLocalConst:
                        return new Value(a + b);
                    case Op.Sub:
                    case Op.SubLocalLocal:
                    case Op.SubLocalConst:
                        return new Value(a - b);
                    case Op.Mul:
                    case Op.MulLocalLocal:
                    case Op.MulLocalConst:
                        return new Value(a * b);
                    case Op.Div:
                    case Op.DivLocalLocal:
                    case Op.DivLocalConst:
                        if (b == 0)
                        {
                            throw Error("DivisionByZero", "division by zero");
                        }

                        return new Value(a / b);
                    case Op.Mod:
                    case Op.ModLocalLocal:
                    case Op.ModLocalConst:
                        if (b == 0)
                        {
                            throw Error("DivisionByZero", "modulo by zero");
                        }

                        return new Value(a - b * Math.Floor(a / b));
                    case Op.Pow:
                        return new Value(Math.Pow(a, b));
                }
            }

            if (IsLocalArithmeticOp(op))
            {
                throw Error("TypeError",
                    $"operator '{OpSymbol(op)}' is not defined for {TypeName(left)} and {TypeName(right)}");
            }

            throw Error("TypeError",
                $"operator '{OpSymbol(op)}' is not defined for {TypeName(left)} and {TypeName(right)}");
        }

        /// <summary>Maps the fused local opcodes onto their base arithmetic operation.</summary>
        private static Op BaseArithmeticOp(Op op) => op switch
        {
            Op.AddLocalLocal or Op.AddLocalConst => Op.Add,
            Op.SubLocalLocal or Op.SubLocalConst => Op.Sub,
            Op.MulLocalLocal or Op.MulLocalConst => Op.Mul,
            Op.DivLocalLocal or Op.DivLocalConst => Op.Div,
            Op.ModLocalLocal or Op.ModLocalConst => Op.Mod,
            _ => op,
        };

        private static bool IsLocalArithmeticOp(Op op) => op is
            Op.AddLocalLocal or Op.SubLocalLocal or Op.MulLocalLocal or Op.DivLocalLocal or Op.ModLocalLocal or
            Op.AddLocalConst or Op.SubLocalConst or Op.MulLocalConst or Op.DivLocalConst or Op.ModLocalConst;

        private static string OpSymbol(Op op) => op switch
        {
            Op.Add or Op.AddLocalLocal or Op.AddLocalConst => "+",
            Op.Sub or Op.SubLocalLocal or Op.SubLocalConst => "-",
            Op.Mul or Op.MulLocalLocal or Op.MulLocalConst => "*",
            Op.Div or Op.DivLocalLocal or Op.DivLocalConst => "/",
            Op.Mod or Op.ModLocalLocal or Op.ModLocalConst => "%",
            Op.Pow => "**",
            Op.BAnd => "&",
            Op.BOr => "|",
            Op.BXor => "^",
            Op.Shl => "<<",
            Op.Shr => ">>",
            _ => op.ToString(),
        };

        /// <summary>`string + value` without the intermediate <c>ToString</c> allocations.</summary>
        private static Value ConcatPair(in Value left, in Value right)
        {
            if (left.Kind == ValueKind.Str && right.Kind == ValueKind.Str)
            {
                return Value.Str(string.Concat(left.AsString(), right.AsString()));
            }

            Span<char> buffer = stackalloc char[64];
            if (TryFormatPart(left, buffer, out var first) &&
                TryFormatPart(right, buffer[first..], out var second))
            {
                return Value.Str(new string(buffer[..(first + second)]));
            }

            return Value.Str(left.ToDisplayString() + right.ToDisplayString());
        }

        /// <summary>
        /// Integer addition that reports whether the result fits in a long. The
        /// sign test is the classic overflow check: two operands with the same
        /// sign cannot overflow, and if they do, the sum's sign disagrees with
        /// both.
        /// </summary>
        private static bool TryAdd(long a, long b, out long result)
        {
            result = unchecked(a + b);
            return ((a ^ result) & (b ^ result)) >= 0;
        }

        /// <summary>Integer subtraction; see <see cref="TryAdd"/>.</summary>
        private static bool TrySubtract(long a, long b, out long result)
        {
            result = unchecked(a - b);
            return ((a ^ b) & (a ^ result)) >= 0;
        }

        /// <summary>Integer multiplication; see <see cref="TryAdd"/>.</summary>
        private static bool TryMultiply(long a, long b, out long result)
        {
            result = unchecked(a * b);
            if (a == 0 || b == 0)
            {
                return true;
            }

            // long.MinValue / -1 traps in hardware, so that pair is checked first.
            return !(a == -1 && b == long.MinValue) && result / a == b;
        }

        private Value Add(in Value left, in Value right)
        {
            if (left.Kind == ValueKind.Int && right.Kind == ValueKind.Int)
            {
                var a = left.AsInt();
                var b = right.AsInt();
                var sum = unchecked(a + b);
                // Overflow promotes to float instead of silently wrapping.
                if (((a ^ sum) & (b ^ sum)) < 0)
                {
                    return new Value((double)a + b);
                }

                return new Value(sum);
            }

            if (left.Kind == ValueKind.Float && right.Kind == ValueKind.Float)
            {
                return new Value(left.AsDouble() + right.AsDouble());
            }

            if (left.IsNumber && right.IsNumber)
            {
                return new Value(left.AsDouble() + right.AsDouble());
            }

            if (left.Kind == ValueKind.Str || right.Kind == ValueKind.Str)
            {
                return ConcatPair(left, right);
            }

            if (left.Kind == ValueKind.List && right.Kind == ValueKind.List)
            {
                var a = left.AsList();
                var b = right.AsList();
                var combined = new TernList(a.Count + b.Count);
                for (var i = 0; i < a.Count; i++)
                {
                    combined.Add(a[i]);
                }

                for (var i = 0; i < b.Count; i++)
                {
                    combined.Add(b[i]);
                }

                return Value.Of(combined);
            }

            if (left.Kind == ValueKind.Map && right.Kind == ValueKind.Map)
            {
                var merged = left.AsMap().Clone();
                var source = right.AsMap();
                var slots = source.SlotCount;
                for (var i = 0; i < slots; i++)
                {
                    if (source.TryGetEntry(i, out var key, out var value))
                    {
                        merged.Set(key, value);
                    }
                }

                return Value.Of(merged);
            }

            throw Error("TypeError",
                $"operator '+' is not defined for {TypeName(left)} and {TypeName(right)}");
        }

        private Value IntegerOp(Op op, in Value left, in Value right)
        {
            var a = ToInteger(left, OpSymbol(op));
            var b = ToInteger(right, OpSymbol(op));

            return op switch
            {
                Op.BAnd => new Value(a & b),
                Op.BOr => new Value(a | b),
                Op.BXor => new Value(a ^ b),
                Op.Shl => new Value(a << (int)(b & 63)),
                Op.Shr => new Value(a >> (int)(b & 63)),
                _ => throw Error("InternalError", $"unexpected integer opcode {op}"),
            };
        }

        private long ToInteger(in Value value, string symbol)
        {
            if (value.Kind == ValueKind.Int)
            {
                return value.AsInt();
            }

            if (value.Kind == ValueKind.Float)
            {
                var number = value.AsDouble();
                if (Math.Floor(number) == number && Math.Abs(number) < 9.22e18)
                {
                    return (long)number;
                }
            }

            throw Error("TypeError", $"operator '{symbol}' requires integers but got {TypeName(value)}");
        }

        private int CompareOrThrow(in Value left, in Value right)
        {
            if (Value.TryCompare(left, right, out var result))
            {
                return result;
            }

            if (left.Kind == ValueKind.Str && right.Kind == ValueKind.Str)
            {
                return string.CompareOrdinal(left.AsString(), right.AsString());
            }

            if (left.Kind == ValueKind.List && right.Kind == ValueKind.List)
            {
                var a = left.AsList();
                var b = right.AsList();
                var shared = Math.Min(a.Count, b.Count);
                for (var i = 0; i < shared; i++)
                {
                    var comparison = CompareOrThrow(a[i], b[i]);
                    if (comparison != 0)
                    {
                        return comparison;
                    }
                }

                return a.Count.CompareTo(b.Count);
            }

            throw Error("TypeError", $"cannot compare {TypeName(left)} with {TypeName(right)}");
        }

        public string TypeName(in Value value) => value.Kind switch
        {
            ValueKind.Nil => "nil",
            ValueKind.Bool => "bool",
            ValueKind.Int => "int",
            ValueKind.Float => "float",
            ValueKind.Str => "string",
            ValueKind.List => "list",
            ValueKind.Map => "map",
            ValueKind.Instance => value.AsInstance().Type.Name,
            ValueKind.Closure => "function",
            ValueKind.Native => "native",
            ValueKind.Bound => "method",
            ValueKind.Range => "range",
            ValueKind.Module => "module",
            ValueKind.Type => "struct",
            _ => "nil",
        };

        // -------------------------------------------------------- member access

        /// <summary>Used by native helpers (sum, min/max, ...) to reuse interpreter semantics.</summary>
        public Value AddValues(in Value left, in Value right) => Add(left, right);

        public int CompareValues(in Value left, in Value right) => CompareOrThrow(left, right);

        public Value LoadMember(in Value target, string name)
        {
            switch (target.Kind)
            {
                case ValueKind.Instance:
                {
                    var instance = target.AsInstance();
                    if (instance.Type.TryGetField(name, out var index))
                    {
                        return instance.Fields[index];
                    }

                    if (instance.Type.Methods.TryGetValue(name, out var method))
                    {
                        return Value.Of(new TernBoundMethod(target, Value.Of(method)));
                    }

                    throw Error("MemberError", $"{instance.Type.Name} has no member '{name}'");
                }

                case ValueKind.Map:
                {
                    var map = target.AsMap();
                    if (map.TryGet(Value.Str(name), out var value))
                    {
                        return value;
                    }

                    if (BuiltinMethods.TryGetProperty(target, name, out var property))
                    {
                        return property;
                    }

                    if (BuiltinMethods.TryResolve(ValueKind.Map, name, out var member))
                    {
                        return Value.Of(new TernBoundMethod(target, Value.Of(member)));
                    }

                    throw Error("MemberError", $"map has no member '{name}'");
                }

                case ValueKind.Module:
                {
                    var module = target.AsModule();
                    if (module.Scope.TryResolve(name, out var slot))
                    {
                        return module.Globals[slot];
                    }

                    throw Error("MemberError", $"module '{module.Name}' has no export '{name}'");
                }

                default:
                {
                    if (BuiltinMethods.TryGetProperty(target, name, out var property))
                    {
                        return property;
                    }

                    if (BuiltinMethods.TryResolve(target.Kind, name, out var native))
                    {
                        return Value.Of(new TernBoundMethod(target, Value.Of(native)));
                    }

                    if (target.Kind is ValueKind.Nil)
                    {
                        throw Error("TypeError", $"cannot read member '{name}' of nil");
                    }

                    throw Error("MemberError", $"{TypeName(target)} has no member '{name}'");
                }
            }
        }

        /// <summary>Method dispatch used by the Invoke opcode; avoids allocating bound methods.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryResolveNative(ValueKind kind, string name, out TernNative native)
        {
            var slot = (int)(((uint)name.GetHashCode() * 31u + (uint)kind) & 0xFFu);
            ref var entry = ref _nativeCache[slot];
            if (entry.Kind == kind && ReferenceEquals(entry.Name, name))
            {
                native = entry.Native!;
                return native is not null;
            }

            if (!BuiltinMethods.TryResolve(kind, name, out native!))
            {
                entry = default;
                return false;
            }

            entry.Kind = kind;
            entry.Name = name;
            entry.Native = native;
            return true;
        }

        private Value[] BorrowNativeArguments(int depth, int length)
        {
            if (depth >= _nativeArgs.Length)
            {
                Array.Resize(ref _nativeArgs, Math.Max(depth + 1, _nativeArgs.Length * 2));
            }

            var buffer = _nativeArgs[depth];
            if (buffer is null || buffer.Length < length)
            {
                buffer = new Value[Math.Max(length, 8)];
                _nativeArgs[depth] = buffer;
            }

            return buffer;
        }

        private bool TryInvoke(in Value receiver, string name, int argc, int receiverIndex)
        {
            switch (receiver.Kind)
            {
                case ValueKind.Instance:
                {
                    var instance = receiver.AsInstance();
                    if (instance.Type.TryGetField(name, out var fieldIndex))
                    {
                        var field = instance.Fields[fieldIndex];
                        if (field.Kind is ValueKind.Closure or ValueKind.Native or ValueKind.Bound)
                        {
                            return TryEnterCall(field, argc, receiverIndex + 1, receiverIndex);
                        }

                        throw Error("TypeError", $"{instance.Type.Name}.{name} is {TypeName(field)}, not callable");
                    }

                    if (instance.Type.Methods.TryGetValue(name, out var method))
                    {
                        return TryEnterCallWithReceiver(Value.Of(method), receiver, argc, receiverIndex + 1, receiverIndex);
                    }

                    break;
                }

                case ValueKind.Map:
                {
                    var map = receiver.AsMap();
                    if (map.TryGet(Value.Str(name), out var value))
                    {
                        return TryEnterCallWithReceiver(value, Value.Nil, argc, receiverIndex + 1, receiverIndex);
                    }

                    break;
                }

                case ValueKind.Module:
                {
                    var module = receiver.AsModule();
                    if (module.Scope.TryResolve(name, out var slot))
                    {
                        return TryEnterCallWithReceiver(module.Globals[slot], Value.Nil, argc, receiverIndex + 1, receiverIndex);
                    }

                    break;
                }
            }

            if (!TryResolveNative(receiver.Kind, name, out var native))
            {
                return false;
            }

            if (argc + 1 < native.MinArguments || (native.MaxArguments >= 0 && argc + 1 > native.MaxArguments))
            {
                var expected = native.MaxArguments < 0
                    ? $"at least {native.MinArguments - 1}"
                    : $"{native.MinArguments - 1} to {native.MaxArguments - 1}";
                throw Error("ArgumentError", $"{name}() expects {expected} argument(s) but got {argc}");
            }

            // The buffer is reused (and re-entrancy safe because each nesting level
            // gets its own): a fresh Value[] per native call is pure garbage.
            var depth = _nativeDepth++;
            var args = BorrowNativeArguments(depth, argc + 1);
            args[0] = receiver;
            for (var i = 0; i < argc; i++)
            {
                args[i + 1] = _stack[receiverIndex + 1 + i];
            }

            Value result;
            try
            {
                result = native.Function(this, args.AsSpan(0, argc + 1));
            }
            finally
            {
                _nativeDepth = depth;
                if (argc + 1 <= 16)
                {
                    args.AsSpan(0, argc + 1).Clear();
                }
            }

            _sp = receiverIndex;
            Push(result);
            return true;
        }

        private bool TryEnterCallWithReceiver(in Value callee, in Value receiver, int argc, int argsBase, int resultSlot)
        {
            if (receiver.IsNil)
            {
                return TryEnterCall(callee, argc, argsBase, resultSlot);
            }

            return TryEnterBound(new TernBoundMethod(receiver, callee), argc, argsBase, resultSlot);
        }

        public void StoreMember(in Value target, string name, in Value value)
        {
            switch (target.Kind)
            {
                case ValueKind.Instance:
                {
                    var instance = target.AsInstance();
                    if (instance.Type.TryGetField(name, out var index))
                    {
                        instance.Fields[index] = value;
                        return;
                    }

                    throw Error("MemberError", $"{instance.Type.Name} has no field '{name}'");
                }

                case ValueKind.Map:
                    target.AsMap().Set(Value.Str(name), value);
                    return;

                case ValueKind.Module:
                {
                    var module = target.AsModule();
                    if (module.Scope.TryResolve(name, out var slot))
                    {
                        if (module.Scope.IsConst(slot))
                        {
                            throw Error("TypeError", $"cannot assign to constant '{name}'");
                        }

                        module.Globals[slot] = value;
                        return;
                    }

                    throw Error("MemberError", $"module '{module.Name}' has no export '{name}'");
                }

                default:
                    throw Error("TypeError", $"cannot set member '{name}' on {TypeName(target)}");
            }
        }

        /// <summary>The `in` operator: membership of `item` inside `container`.</summary>
        public bool Contains(in Value item, in Value container)
        {
            switch (container.Kind)
            {
                case ValueKind.Str:
                    return container.AsString().Contains(
                        item.Kind == ValueKind.Str ? item.AsString() : item.ToDisplayString(),
                        StringComparison.Ordinal);

                case ValueKind.List:
                {
                    var list = container.AsList();
                    for (var i = 0; i < list.Count; i++)
                    {
                        if (list[i].Equals(item))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                case ValueKind.Map:
                    return container.AsMap().ContainsKey(item);

                case ValueKind.Range:
                {
                    if (item.Kind != ValueKind.Int)
                    {
                        return false;
                    }

                    var range = container.AsRange();
                    var end = range.Inclusive ? range.End : range.End - 1;
                    return item.AsInt() >= range.Start && item.AsInt() <= end;
                }

                case ValueKind.Instance:
                    return container.AsInstance().Type.TryGetField(
                        item.Kind == ValueKind.Str ? item.AsString() : item.ToDisplayString(), out _);

                default:
                    throw Error("TypeError", $"cannot use 'in' on a {TypeName(container)}");
            }
        }

        /// <summary>Slice with optional bounds: <c>xs[1..3]</c>, <c>xs[..2]</c>, <c>xs[2..]</c>.</summary>
        public Value Slice(in Value target, in Value start, in Value end, bool inclusive)
        {
            if (target.Kind is not (ValueKind.List or ValueKind.Str))
            {
                throw Error("TypeError", $"cannot slice a {TypeName(target)}");
            }

            var length = target.Kind == ValueKind.List ? target.AsList().Count : target.AsString().Length;

            var from = start.Kind == ValueKind.Nil ? 0 : RequireInteger(start, "slice start");
            var to = end.Kind == ValueKind.Nil ? length : RequireInteger(end, "slice end") + (inclusive ? 1 : 0);

            from = from < 0 ? from + length : from;
            to = to < 0 ? to + length : to;
            from = Math.Max(0, Math.Min(from, length));
            to = Math.Max(from, Math.Min(to, length));

            return target.Kind == ValueKind.List
                ? Value.Of(target.AsList().Slice((int)from, (int)to))
                : Value.Str(target.AsString().Substring((int)from, (int)(to - from)));
        }

        /// <summary>Compares a local against an integer constant, supporting floats and strings.</summary>
        private int CompareLocalToInt(in Value cell, int constant)
        {
            if (cell.Kind == ValueKind.Float)
            {
                return cell.AsDouble().CompareTo((double)constant);
            }

            return CompareOrThrow(cell, new Value((long)constant));
        }

        private long RequireInteger(in Value value, string what) => value.Kind switch
        {
            ValueKind.Int => value.AsInt(),
            ValueKind.Float => (long)value.AsDouble(),
            _ => throw Error("TypeError", $"{what} must be an integer, not {TypeName(value)}"),
        };

        public Value LoadIndexed(in Value target, in Value index)
        {
            switch (target.Kind)
            {
                case ValueKind.List:
                {
                    var list = target.AsList();

                    if (index.Kind == ValueKind.Range)
                    {
                        var range = index.AsRange();
                        return Value.Of(list.Slice(range.Start, range.Inclusive ? range.End + 1 : range.End));
                    }

                    if (index.Kind != ValueKind.Int)
                    {
                        throw Error("TypeError", $"list index must be an integer, not {TypeName(index)}");
                    }

                    if (!list.TryGet(index.AsInt(), out var value))
                    {
                        throw Error("IndexError", $"index {index.AsInt()} out of range for list of length {list.Count}");
                    }

                    return value;
                }

                case ValueKind.Map:
                    return target.AsMap().GetOrNil(index);

                case ValueKind.Str:
                {
                    var text = target.AsString();

                    if (index.Kind == ValueKind.Range)
                    {
                        var range = index.AsRange();
                        var from = (int)range.Start;
                        var to = (int)(range.Inclusive ? range.End + 1 : range.End);
                        from = from < 0 ? from + text.Length : from;
                        to = to < 0 ? to + text.Length : to;
                        from = Math.Max(0, Math.Min(from, text.Length));
                        to = Math.Max(from, Math.Min(to, text.Length));
                        return Value.Str(text.Substring(from, to - from));
                    }

                    if (index.Kind != ValueKind.Int)
                    {
                        throw Error("TypeError", $"string index must be an integer, not {TypeName(index)}");
                    }

                    var position = index.AsInt() < 0 ? index.AsInt() + text.Length : index.AsInt();
                    if (position < 0 || position >= text.Length)
                    {
                        throw Error("IndexError", $"index {index.AsInt()} out of range for string of length {text.Length}");
                    }

                    return Value.Str(text[(int)position].ToString());
                }

                case ValueKind.Instance:
                {
                    var instance = target.AsInstance();
                    if (index.Kind != ValueKind.Str)
                    {
                        throw Error("TypeError", $"{instance.Type.Name} index must be a string");
                    }

                    var name = index.AsString();
                    if (instance.Type.TryGetField(name, out var field))
                    {
                        return instance.Fields[field];
                    }

                    throw Error("MemberError", $"{instance.Type.Name} has no field '{name}'");
                }

                case ValueKind.Range:
                {
                    if (index.Kind != ValueKind.Int)
                    {
                        throw Error("TypeError", $"range index must be an integer, not {TypeName(index)}");
                    }

                    var range = target.AsRange();
                    var offset = index.AsInt();
                    var length = range.Length;
                    if (offset < 0)
                    {
                        offset += length;
                    }

                    if (offset < 0 || offset >= length)
                    {
                        throw Error("IndexError", $"index {index.AsInt()} out of range for range of length {length}");
                    }

                    return new Value(range.Start + offset * range.Step);
                }

                case ValueKind.Nil:
                    throw Error("TypeError", "cannot index nil");

                default:
                    throw Error("TypeError", $"{TypeName(target)} cannot be indexed");
            }
        }

        public void StoreIndexed(in Value target, in Value index, in Value value)
        {
            switch (target.Kind)
            {
                case ValueKind.List:
                {
                    var list = target.AsList();
                    if (index.Kind != ValueKind.Int)
                    {
                        throw Error("TypeError", $"list index must be an integer, not {TypeName(index)}");
                    }

                    var position = index.AsInt() < 0 ? index.AsInt() + list.Count : index.AsInt();
                    if (position < 0 || position >= list.Count)
                    {
                        throw Error("IndexError", $"index {index.AsInt()} out of range for list of length {list.Count}");
                    }

                    list[(int)position] = value;
                    return;
                }

                case ValueKind.Map:
                    target.AsMap().Set(index, value);
                    return;

                case ValueKind.Instance:
                {
                    var instance = target.AsInstance();
                    if (index.Kind != ValueKind.Str || !instance.Type.TryGetField(index.AsString(), out var field))
                    {
                        throw Error("MemberError", $"{instance.Type.Name} has no such field");
                    }

                    instance.Fields[field] = value;
                    return;
                }

                default:
                    throw Error("TypeError", $"{TypeName(target)} does not support indexed assignment");
            }
        }

        public TernIterator CreateIterator(in Value value)
        {
            switch (value.Kind)
            {
                case ValueKind.List:
                    return TernIterator.ForList(value.AsList());
                case ValueKind.Map:
                    return TernIterator.ForMap(value.AsMap());
                case ValueKind.Str:
                    return TernIterator.ForString(value.AsString());
                case ValueKind.Range:
                    return TernIterator.ForRange(value.AsRange());
                case ValueKind.Nil:
                    throw Error("TypeError", "cannot iterate nil");
                default:
                    throw Error("TypeError", $"{TypeName(value)} is not iterable");
            }
        }
    }
}
