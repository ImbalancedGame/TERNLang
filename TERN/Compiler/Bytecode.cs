using System;
using System.Collections.Generic;
using TERN.Runtime;

namespace TERN.Compiler
{
    /// <summary>
    /// Stack machine instruction set. Instructions are fixed size and carry up to
    /// four operands so the dispatch loop never has to decode variable length data.
    /// The most common patterns (local arithmetic, compare-and-branch) have fused
    /// opcodes to cut dispatch count in hot loops.
    /// </summary>
    public enum Op : byte
    {
        Nop = 0,
        Pop,
        Dup,
        Swap,

        // literals
        LoadNil,
        LoadTrue,
        LoadFalse,
        LoadInt,        // A = int32 immediate
        LoadConst,      // A = constant index

        // variables
        LoadLocal,      // A = slot
        StoreLocal,     // A = slot
        LoadGlobal,     // A = module global slot
        StoreGlobal,    // A = module global slot
        LoadUpvalue,    // A = upvalue index
        StoreUpvalue,   // A = upvalue index
        IncLocal,       // A = slot, B = delta
        ArgOrJump,      // A = slot, B = target (jump when argument was provided)

        // members
        LoadField,      // A = name constant
        Length,         // A = name constant; fast path for length/count/size reads
        StoreField,     // A = name constant
        LoadIndex,
        StoreIndex,

        // aggregates
        NewRange,
        NewList,        // A = item count
        NewMap,         // A = pair count
        NewInstance,    // A = type constant, B = argument count
        LoadModule,     // A = module constant
        Concat,         // A = part count

        // arithmetic
        Add,
        Sub,
        Mul,
        Div,
        Mod,
        Pow,
        Neg,
        Not,
        BNot,
        BAnd,
        BOr,
        BXor,
        Shl,
        Shr,

        // comparison
        Eq,
        Ne,
        Lt,
        Le,
        Gt,
        Ge,

        // fused arithmetic on operands already in locals
        AddLocalLocal, SubLocalLocal, MulLocalLocal, DivLocalLocal, ModLocalLocal,
        AddLocalConst, SubLocalConst, MulLocalConst, DivLocalConst, ModLocalConst,

        // `local % constant` with a precomputed reciprocal: A = slot, B = division index.
        ModLocalConstMagic,

        // control flow
        Jump,
        JumpIfFalse,      // pops
        JumpIfTrue,       // pops
        JumpIfFalseLocal, // A = slot, B = target (no pop)
        JumpIfTrueLocal,  // A = slot, B = target (no pop)
        JumpIfFalseKeep,  // peeks
        JumpIfTrueKeep,   // peeks
        JumpIfNotNullKeep,// peeks
        JumpIfNotList,    // A = slot, B = target (no pop)
        JumpCmp,          // A = comparison op code, B = target, C = 1 when jumping on true

        // calls
        Call,             // A = argument count
        CallLocal,        // A = argument count, B = slot
        CallGlobal,       // A = argument count, B = global slot
        Invoke,           // A = name constant, B = argument count
        Return,
        ReturnNil,
        ReturnLocal,      // A = slot; returns the local directly

        // fused arithmetic-into-local: pops two values, stores into A, pushes nothing
        AddStoreLocal, SubStoreLocal, MulStoreLocal, DivStoreLocal, ModStoreLocal,

        // loop back edge: increments A by B, then jumps to C
        IncLocalJump,

        // `x = x <op> y` where y is a constant or another local: one dispatch, no stack traffic
        AddLocalConstStore, SubLocalConstStore, AddLocalLocalStore, SubLocalLocalStore,

        // `x = x <op> <pure expression>`: the expression leaves its value on the stack
        AddLocalStack, SubLocalStack,

        // counted loops with a constant bound and a step of one.
        // ForPrepLt A=counter, B=limit, C=exit    (jump to exit when the loop cannot run)
        // ForNextLt A=counter, B=limit, C=body    (step, then loop while the bound holds)
        ForPrepLt, ForNextLt, ForPrepLe, ForNextLe, ForPrepGt, ForNextGt, ForPrepGe, ForNextGe,
        MakeClosure,      // A = prototype index
        CloseUpvalues,    // A = first slot to close

        // conversions
        ToString,
        TypeOf,

        // iteration
        IterNew,
        IterNext,         // A = exit target, B = binding count

        // exceptions
        EnterTry,         // A = handler target
        ExitTry,
        Throw,

        // fused compare and jump (numeric fast paths for loop conditions)
        // A = first local slot, B = second slot or integer constant, C = false target
        JumpLtLocalInt, JumpLeLocalInt, JumpGtLocalInt, JumpGeLocalInt, JumpEqLocalInt, JumpNeLocalInt,
        JumpLtLocalLocal, JumpLeLocalLocal, JumpGtLocalLocal, JumpGeLocalLocal, JumpEqLocalLocal, JumpNeLocalLocal,

        // operators and slicing
        In,               // pops item, container; pushes bool
        Slice,            // A = 1 when the end bound is inclusive; pops end, start, target
    }

    public readonly struct Instr
    {
        public readonly Op Op;
        public readonly int A;
        public readonly int B;
        public readonly int C;

        public Instr(Op op, int a = 0, int b = 0, int c = 0)
        {
            Op = op;
            A = a;
            B = b;
            C = c;
        }

        public override string ToString() => A == 0 && B == 0 && C == 0
            ? Op.ToString()
            : $"{Op} {A} {B} {C}".TrimEnd();
    }

    /// <summary>How a closure captures one of its upvalues.</summary>
    public readonly struct UpvalueDescriptor
    {
        public readonly bool FromLocal;
        public readonly int Index;

        public UpvalueDescriptor(bool fromLocal, int index)
        {
            FromLocal = fromLocal;
            Index = index;
        }
    }

    /// <summary>
    /// A precomputed reciprocal for `x % divisor` where divisor is a positive
    /// compile-time constant. The VM still range-checks the result against the
    /// divisor and falls back to the hardware divide when the reciprocal is
    /// imprecise, so this is an optimisation only - never a semantic change.
    /// </summary>
    public readonly struct DivMagic
    {
        public readonly long Divisor;
        public readonly ulong Magic;
        public readonly int Shift;

        public DivMagic(long divisor, ulong magic, int shift)
        {
            Divisor = divisor;
            Magic = magic;
            Shift = shift;
        }
    }

    /// <summary>Builds the reciprocals used by <see cref="Op.ModLocalConstMagic"/>.</summary>
    public static class DivisionMagic
    {
        /// <summary>
        /// Finds an unsigned 64-bit reciprocal for <paramref name="divisor"/> (which
        /// must be at least 2) such that <c>hi64(|x| * Magic) >> Shift</c> is the
        /// quotient <c>|x| / divisor</c> for every value the VM cares about.
        /// </summary>
        public static bool TryFor(long divisor, out DivMagic magic)
        {
            for (var shift = 0; shift < 64; shift++)
            {
                var pow = new UInt128(1UL << shift, 0);
                var d = (UInt128)(ulong)divisor;
                var quotient = (pow + d - 1) / d;

                if (quotient > ulong.MaxValue)
                {
                    continue;
                }

                var remainder = pow % d;
                if (remainder == 0 || remainder <= (UInt128)(1UL << shift))
                {
                    magic = new DivMagic(divisor, (ulong)quotient, shift);
                    return true;
                }
            }

            magic = default;
            return false;
        }
    }

    /// <summary>A compiled function or module body.</summary>
    public sealed class FunctionProto
    {
        public required string Name { get; init; }
        public required Instr[] Instructions { get; init; }
        public required Value[] Constants { get; init; }
        public required UpvalueDescriptor[] Upvalues { get; init; }
        public required FunctionProto?[] Prototypes { get; init; }
        public required int ParamCount { get; init; }
        public required int RequiredParams { get; init; }
        public required bool HasRest { get; init; }
        public required int LocalCount { get; init; }
        public required int MaxStack { get; init; }
        public required int[] Lines { get; init; }
        public DivMagic[] Divisions { get; init; } = Array.Empty<DivMagic>();
        public string? SourceName { get; init; }
        public bool IsMethod { get; init; }
        public ModuleScope? Scope { get; init; }

        public int Arity => RequiredParams;
    }

    /// <summary>One module-level variable slot. Host bindings are prefilled by the engine.</summary>
    public sealed class GlobalSlot
    {
        public required string Name { get; init; }
        public bool IsConst { get; init; }
        public Value HostValue { get; init; }
        public bool IsHost { get; init; }
        public bool Exported { get; set; }
    }

    /// <summary>Compile time view of a module's globals.</summary>
    public sealed class ModuleScope
    {
        private readonly List<GlobalSlot> _slots = new();
        private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);

        public ModuleScope(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public IReadOnlyList<GlobalSlot> Slots => _slots;

        public int Count => _slots.Count;

        public bool TryResolve(string name, out int slot) => _byName.TryGetValue(name, out slot);

        public int Declare(string name, bool isConst, bool exported = false)
        {
            if (_byName.TryGetValue(name, out var existing))
            {
                return existing;
            }

            var slot = _slots.Count;
            _slots.Add(new GlobalSlot { Name = name, IsConst = isConst, Exported = exported });
            _byName[name] = slot;
            return slot;
        }

        public int DeclareHost(string name, Value value)
        {
            if (_byName.TryGetValue(name, out var existing))
            {
                return existing;
            }

            var slot = _slots.Count;
            _slots.Add(new GlobalSlot { Name = name, IsConst = true, IsHost = true, HostValue = value });
            _byName[name] = slot;
            return slot;
        }

        public void MarkExported(string name)
        {
            if (_byName.TryGetValue(name, out var slot))
            {
                _slots[slot].Exported = true;
            }
        }

        public bool IsConst(int slot) => _slots[slot].IsConst;
    }

    /// <summary>A loaded module: its compiled body plus its runtime global array.</summary>
    public sealed class TernModule
    {
        /// <summary>Zero-filled as nil (a default <see cref="Value"/> is integer zero).</summary>
        private static Value[] CreateValues(int length)
        {
            var values = new Value[length];
            Array.Fill(values, Value.Nil);
            return values;
        }

        public TernModule(string name, string? path, FunctionProto entry, ModuleScope scope)
        {
            Name = name;
            Path = path;
            Entry = entry;
            Scope = scope;
            Globals = CreateValues(Math.Max(scope.Count, 8));
            SeedHostValues(0);
        }

        public string Name { get; }
        public string? FilePath => Path;
        public string? Path { get; }
        public FunctionProto Entry { get; private set; }
        public ModuleScope Scope { get; }
        public Value[] Globals { get; private set; }
        public bool Initialized { get; set; }

        /// <summary>Replaces the body (used by the REPL, which recompiles into one module).</summary>
        public void ReplaceEntry(FunctionProto entry)
        {
            Entry = entry;
            EnsureGlobals(Scope.Count);
            Initialized = false;
            _exports = null;
        }

        public void EnsureGlobals(int count)
        {
            if (Globals.Length < count)
            {
                var replacement = CreateValues(Math.Max(count, Globals.Length * 2));
                Array.Copy(Globals, replacement, Globals.Length);
                Globals = replacement;
            }

            SeedHostValues(count);
        }

        /// <summary>Fills slots that the host pre-registered (print, math, ...).</summary>
        private void SeedHostValues(int upTo)
        {
            var slots = Scope.Slots;
            var limit = Math.Min(upTo <= 0 ? slots.Count : upTo, Math.Min(slots.Count, Globals.Length));

            for (var i = 0; i < limit; i++)
            {
                if (slots[i].IsHost)
                {
                    Globals[i] = slots[i].HostValue;
                }
            }
        }
        public bool Initializing { get; set; }
        private TernMap? _exports;

        public TernMap Exports
        {
            get
            {
                if (_exports is not null)
                {
                    return _exports;
                }

                var map = new TernMap();
                var slots = Scope.Slots;
                for (var i = 0; i < slots.Count; i++)
                {
                    var slot = slots[i];
                    if (slot.IsHost || !slot.Exported)
                    {
                        continue;
                    }

                    map.Set(Value.Str(slot.Name), Globals[i]);
                }

                _exports = map;
                return map;
            }
        }
    }
}
