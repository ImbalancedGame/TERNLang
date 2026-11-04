using System;
using System.Collections.Generic;
using TERN.Runtime;
using TERN.Syntax;

namespace TERN.Compiler
{
    /// <summary>A built in module such as <c>std:math</c>.</summary>
    public sealed class StdModule
    {
        public required string Name { get; init; }
        public required TernMap Members { get; init; }
        public required string[] FlatNames { get; init; }

        public Value Value => Runtime.Value.Of(Members);
    }

    /// <summary>Services the compiler needs from the engine (module resolution).</summary>
    public interface ICompilationHost
    {
        bool TryGetStdModule(string name, out StdModule module);

        /// <summary>
        /// The `std:` module names this host can import. Only a diagnostic uses
        /// it: "cannot load module" is unhelpful on its own, because a `std:`
        /// name is compiled in rather than read from disk, so the interesting
        /// fact is which names this build actually has.
        /// </summary>
        IEnumerable<string> StandardModuleNames { get; }

        /// <summary>Compiles (or returns a cached) file module. Returns null when it cannot be loaded.</summary>
        TernModule? LoadFileModule(string path, string? importerPath);
    }

    /// <summary>Compiles a parsed TERN module into bytecode.</summary>
    public sealed class ModuleCompiler
    {
        private readonly ModuleScope _scope;
        private readonly ICompilationHost _host;
        private readonly DiagnosticBag _diagnostics;
        private readonly string? _sourceName;
        private readonly string? _path;

        public ModuleCompiler(ModuleScope scope, ICompilationHost host, DiagnosticBag diagnostics, string? sourceName, string? path)
        {
            _scope = scope;
            _host = host;
            _diagnostics = diagnostics;
            _sourceName = sourceName;
            _path = path;
        }

        public FunctionProto CompileModule(BlockStmt program, bool returnLastValue = false)
        {
            var function = new FunctionCompiler(this, null, "<module>", _scope, _sourceName, _path, isMethod: false, isTopLevel: true);
            function.CompileTopLevel(program, returnLastValue);
            return function.Build();
        }

        // ------------------------------------------------------------------ state

        internal DiagnosticBag Diagnostics => _diagnostics;
        internal ModuleScope Scope => _scope;
        internal ICompilationHost Host => _host;
        internal string? SourceName => _sourceName;
        internal string? Path => _path;

        internal void Error(string code, string message, Node node) =>
            _diagnostics.Error(code, message, new SourcePosition(node.Offset, node.Line, 0), _sourceName);

        // -------------------------------------------------------- function compile

        private sealed class Local
        {
            public required string Name { get; init; }
            public required int Slot { get; init; }
            public required int Depth { get; init; }
            public bool IsConst { get; init; }
            public bool Captured { get; set; }
        }

        private sealed class LoopContext
        {
            public required int ContinueTarget { get; init; }
            public required List<int> BreakJumps { get; init; }

            /// <summary>The name from <c>outer: for ...</c>, or null.</summary>
            public string? Label { get; init; }
            public required int FinallyDepth { get; init; }
            public required int ScopeDepth { get; init; }
            public List<int>? ContinueJumps { get; init; }
        }

        private sealed class FinallyContext
        {
            public required Stmt Block { get; init; }
            public required int LoopDepth { get; init; }
        }

        private enum ConstKind
        {
            None,
            Int,
            Const,
        }

        private sealed class FunctionCompiler
        {
            private readonly ModuleCompiler _owner;
            private readonly FunctionCompiler? _parent;
            private readonly string _name;
            private readonly ModuleScope _scope;
            private readonly string? _sourceName;
            private readonly string? _path;
            private readonly bool _isMethod;
            private readonly bool _isTopLevel;

            private readonly List<Instr> _code = new();
            private readonly List<Value> _constants = new();
            // Constants are distinct unless they are the *same* value: Value's
            // own equality is numeric on purpose (`1 == 1.0` at run time), which
            // would otherwise make `3000000000.0` and `3000000000` share a pool
            // entry and `-0.0` share one with `0.0`.
            private readonly Dictionary<Value, int> _constantIndex = new(ConstantComparer.Instance);
            private readonly List<FunctionProto> _prototypes = new();
            private readonly List<DivMagic> _divisions = new();
            private readonly Dictionary<long, int> _divisionIndex = new();
            private readonly List<int> _lines = new();
            private readonly List<Local> _locals = new();
            private readonly List<int> _scopeMarks = new();
            private readonly List<UpvalueDescriptor> _upvalues = new();
            private readonly Dictionary<(bool, int), int> _upvalueIndex = new();
            private readonly List<LoopContext> _loops = new();
            private readonly List<FinallyContext> _finally = new();

            private int _nextSlot;
            private readonly List<string?> _slotNames = new();
            private int _maxSlots;
            private int _depth;
            private int _maxDepth;
            private int _scopeDepth;
            private int _paramCount;
            private int _requiredParams;
            private bool _hasRest;
            private bool _hasThis;

            public FunctionCompiler(ModuleCompiler owner, FunctionCompiler? parent, string name, ModuleScope scope,
                string? sourceName, string? path, bool isMethod, bool isTopLevel)
            {
                _owner = owner;
                _parent = parent;
                _name = name;
                _scope = scope;
                _sourceName = sourceName;
                _path = path;
                _isMethod = isMethod;
                _isTopLevel = isTopLevel;
                _scopeMarks.Add(0);
            }

            private ModuleCompiler Module => _owner;

            private DiagnosticBag Diagnostics => _owner.Diagnostics;

            // ---------------------------------------------------------- lifecycle

            public void DeclareParameters(List<Parameter> parameters, bool declareThis)
            {
                if (declareThis)
                {
                    DeclareLocal("this", isConst: false);
                    _hasThis = true;
                }

                for (var i = 0; i < parameters.Count; i++)
                {
                    var parameter = parameters[i];

                    if (parameter.IsRest)
                    {
                        DeclareLocal(parameter.Name, isConst: false);
                        _hasRest = true;
                        continue;
                    }

                    DeclareLocal(parameter.Name, isConst: false);
                    _paramCount++;

                    if (parameter.Default is null)
                    {
                        _requiredParams++;
                    }
                }
            }

            public void EmitParameterPrologue(List<Parameter> parameters, bool declaresThis)
            {
                // Rest arguments are packed by the VM before the body starts.
                for (var i = 0; i < parameters.Count; i++)
                {
                    var parameter = parameters[i];
                    if (parameter.IsRest || parameter.Default is null)
                    {
                        continue;
                    }

                    var slot = FindLocalSlot(parameter.Name);
                    var jumpIndex = Emit(Op.ArgOrJump, slot, 0, 0, pop: 0, push: 0);
                    CompileExpression(parameter.Default);
                    Emit(Op.StoreLocal, slot, 0, 0, pop: 1, push: 0);
                    PatchTo(jumpIndex, _code.Count);
                }
            }

            public FunctionProto Build()
            {
                // Implicit "return nil" at the end of every function.
                if (ShouldEmitImplicitReturn())
                {
                    Emit(Op.ReturnNil, 0, 0, 0, pop: 0, push: 0);
                }

                return new FunctionProto
                {
                    Name = _name,
                    Instructions = _code.ToArray(),
                    Constants = _constants.ToArray(),
                    Upvalues = _upvalues.ToArray(),
                    Prototypes = _prototypes.ToArray(),
                    ParamCount = _paramCount,
                    RequiredParams = _requiredParams,
                    HasRest = _hasRest,
                    IsMethod = _isMethod && _hasThis,
                    LocalCount = Math.Max(_maxSlots, _nextSlot),
                    LocalNames = _slotNames.ToArray(),
                    MaxStack = Math.Max(_maxDepth, 8),
                    Lines = _lines.ToArray(),
                    Divisions = _divisions.ToArray(),
                    SourceName = _sourceName,
                    Scope = _scope,
                };
            }

            private bool ShouldEmitImplicitReturn()
            {
                if (_code.Count == 0)
                {
                    return true;
                }

                var last = _code[^1].Op;
                return last is not (Op.Return or Op.ReturnNil);
            }

            // ------------------------------------------------------------- emission

            /// <summary>
            /// States the operand-stack depth at a point the linear walk cannot infer:
            /// a handler, which is entered with a value pushed by the VM rather than by
            /// an instruction. The maximum still has to be recorded, because the frame
            /// is sized from it.
            /// </summary>
            private void NoteDepth(int depth)
            {
                _depth = depth;
                if (depth > _maxDepth)
                {
                    _maxDepth = depth;
                }
            }

            private int Emit(Op op, int a = 0, int b = 0, int c = 0, int pop = 0, int push = 0)
            {
                _code.Add(new Instr(op, a, b, c));
                _lines.Add(_currentLine);

                _depth -= pop;
                _depth += push;

                if (_depth > _maxDepth)
                {
                    _maxDepth = _depth;
                }

                return _code.Count - 1;
            }

            private int _currentLine = 1;

            private void SetLine(Node node)
            {
                if (node.Line > 0)
                {
                    _currentLine = node.Line;
                }
            }

            /// <summary>Points a jump at a target, respecting which operand holds the address.</summary>
            private void PatchTo(int index, int target)
            {
                // -1 means "there is no jump to patch": a condition that is
                // always true emits none, and every caller patches the exit it
                // was given whether or not one was emitted.
                if (index < 0)
                {
                    return;
                }

                var instruction = _code[index];
                _code[index] = instruction.Op switch
                {
                    // target lives in A
                    Op.Jump or Op.JumpIfFalse or Op.JumpIfTrue or Op.JumpIfFalseKeep or Op.JumpIfTrueKeep
                        or Op.JumpIfNotNullKeep or Op.IterNext or Op.EnterTry =>
                        new Instr(instruction.Op, target, instruction.B, instruction.C),

                    // target lives in C: fused compare-and-branch instructions
                    Op.JumpLtLocalInt or Op.JumpLeLocalInt or Op.JumpGtLocalInt
                        or Op.JumpGeLocalInt or Op.JumpEqLocalInt or Op.JumpNeLocalInt
                        or Op.JumpLtLocalLocal or Op.JumpLeLocalLocal or Op.JumpGtLocalLocal
                        or Op.JumpGeLocalLocal or Op.JumpEqLocalLocal or Op.JumpNeLocalLocal
                        or Op.ForPrepLt or Op.ForPrepLe or Op.ForPrepGt or Op.ForPrepGe =>
                        new Instr(instruction.Op, instruction.A, instruction.B, target),

                    // target lives in B
                    _ => new Instr(instruction.Op, instruction.A, target, instruction.C),
                };
            }

            private void PatchA(int index, int value)
            {
                var instruction = _code[index];
                _code[index] = new Instr(instruction.Op, value, instruction.B, instruction.C);
            }

            private int AddDivision(long divisor)
            {
                if (_divisionIndex.TryGetValue(divisor, out var index))
                {
                    return index;
                }

                index = _divisions.Count;
                _divisions.Add(DivisionMagic.TryFor(divisor, out var magic) ? magic : default);
                _divisionIndex[divisor] = index;
                return index;
            }

            /// <summary>
            /// Pools constants by identity: kind plus the exact bits for numbers,
            /// ordinal text for strings, reference for everything else.
            /// </summary>
            private sealed class ConstantComparer : IEqualityComparer<Value>
            {
                public static readonly ConstantComparer Instance = new();

                public bool Equals(Value left, Value right)
                {
                    if (left.Kind != right.Kind)
                    {
                        return false;
                    }

                    return left.Kind switch
                    {
                        ValueKind.Nil or ValueKind.Missing => true,
                        ValueKind.Bool => left.AsBool() == right.AsBool(),
                        ValueKind.Int => left.AsInt() == right.AsInt(),
                        ValueKind.Float => BitConverter.DoubleToInt64Bits(left.AsDouble())
                                           == BitConverter.DoubleToInt64Bits(right.AsDouble()),
                        ValueKind.Str => string.Equals(left.AsString(), right.AsString(), StringComparison.Ordinal),
                        _ => ReferenceEquals(left.AsObject(), right.AsObject()),
                    };
                }

                public int GetHashCode(Value value) => value.Kind switch
                {
                    ValueKind.Nil or ValueKind.Missing => 0,
                    ValueKind.Bool => value.AsBool() ? 1 : 2,
                    ValueKind.Int => value.AsInt().GetHashCode(),
                    // Deliberately unlike Int's hash: 1 and 1.0 are different
                    // constants even though they compare equal at run time.
                    ValueKind.Float => BitConverter.DoubleToInt64Bits(value.AsDouble()).GetHashCode(),
                    ValueKind.Str => StringComparer.Ordinal.GetHashCode(value.AsString()),
                    _ => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.AsObject()!),
                };
            }

            private int AddConstant(in Value value)
            {
                if (_constantIndex.TryGetValue(value, out var index))
                {
                    return index;
                }

                index = _constants.Count;
                _constants.Add(value);
                _constantIndex[value] = index;
                return index;
            }

            private int AddStringConstant(string text) => AddConstant(Value.Str(text));

            private void EmitLoadConstant(in Value value)
            {
                switch (value.Kind)
                {
                    case ValueKind.Nil:
                        Emit(Op.LoadNil, 0, 0, 0, pop: 0, push: 1);
                        return;
                    case ValueKind.Bool when value.AsBool():
                        Emit(Op.LoadTrue, 0, 0, 0, pop: 0, push: 1);
                        return;
                    case ValueKind.Bool:
                        Emit(Op.LoadFalse, 0, 0, 0, pop: 0, push: 1);
                        return;
                    case ValueKind.Int when value.AsInt() >= int.MinValue && value.AsInt() <= int.MaxValue:
                        Emit(Op.LoadInt, (int)value.AsInt(), 0, 0, pop: 0, push: 1);
                        return;
                    default:
                        Emit(Op.LoadConst, AddConstant(value), 0, 0, pop: 0, push: 1);
                        return;
                }
            }

            private void EmitLoadLocal(int slot) =>
                Emit(Op.LoadLocal, slot, 0, 0, pop: 0, push: 1);

            /// <summary>
            /// Emits a store, fusing it with the arithmetic instruction that produced the
            /// value. `<arith>; store` becomes one instruction that leaves nothing on the
            /// stack, which is the shape of every `total = total + x` in a loop.
            /// </summary>
            private void EmitStoreLocal(int slot)
            {
                if (_code.Count > 0)
                {
                    var previous = _code[^1];
                    var fused = previous.Op switch
                    {
                        Op.Add => Op.AddStoreLocal,
                        Op.Sub => Op.SubStoreLocal,
                        Op.Mul => Op.MulStoreLocal,
                        Op.Div => Op.DivStoreLocal,
                        Op.Mod => Op.ModStoreLocal,
                        _ => (Op?)null,
                    };

                    if (fused is not null)
                    {
                        _code.RemoveAt(_code.Count - 1);
                        _lines.RemoveAt(_lines.Count - 1);

                        // The arithmetic instruction took two operands off the stack
                        // and pushed their result, so undoing it gives those two
                        // operands back - and this is the depth that the fused
                        // instruction then pops, leaving nothing. Undoing only its
                        // push instead (a `_depth--`) lost two slots from the count
                        // for the rest of the function, which made MaxStack two too
                        // small: a long list literal after `let x = a * b` then wrote
                        // past the end of the C backend's `Value st[MaxStack]`.
                        _depth++;

                        Emit(fused.Value, slot, 0, 0, pop: 2, push: 0);
                        return;
                    }
                }

                Emit(Op.StoreLocal, slot, 0, 0, pop: 1, push: 0);
            }

            private void EmitStoreLocalPlain(int slot) =>
                Emit(Op.StoreLocal, slot, 0, 0, pop: 1, push: 0);

            // ------------------------------------------------------- scopes & slots

            private void PushScope()
            {
                _scopeDepth++;
                _scopeMarks.Add(_locals.Count);
            }

            private void PopScope()
            {
                for (var i = _locals.Count - 1; i >= _scopeMarks[^1]; i--)
                {
                    if (_locals[i].Captured)
                    {
                        Emit(Op.CloseUpvalues, _locals[i].Slot, 0, 0, pop: 0, push: 0);
                    }
                }

                _locals.RemoveRange(_scopeMarks[^1], _locals.Count - _scopeMarks[^1]);
                _scopeMarks.RemoveAt(_scopeMarks.Count - 1);
                if (_scopeDepth > 0)
                {
                    _scopeDepth--;
                }
            }

            /// <summary>Reserves an anonymous slot for compiler temporaries.</summary>
            private int ReserveSlot()
            {
                var slot = _nextSlot++;
                if (_nextSlot > _maxSlots)
                {
                    _maxSlots = _nextSlot;
                }

                return slot;
            }

            /// <summary>
            /// Gives back a slot taken by <see cref="ReserveSlot"/> once the value in
            /// it is dead. Only the most recent reservation can be reclaimed, which is
            /// all a pattern test needs: it fills a temporary, tests it, and is done.
            /// </summary>
            private void ReleaseSlot(int slot)
            {
                if (_nextSlot == slot + 1)
                {
                    _nextSlot = slot;
                }
            }

            private int DeclareLocal(string name, bool isConst)
            {
                var slot = _nextSlot++;
                if (_nextSlot > _maxSlots)
                {
                    _maxSlots = _nextSlot;
                }

                NameSlot(slot, name);
                _locals.Add(new Local { Name = name, Slot = slot, Depth = _scopeDepth, IsConst = isConst });
                return slot;
            }

            /// <summary>
            /// Records the name in a slot for the debugger. A slot may be reused
            /// by a later block; the last name that claimed it wins, which is the
            /// one worth showing.
            /// </summary>
            private void NameSlot(int slot, string name)
            {
                while (_slotNames.Count <= slot)
                {
                    _slotNames.Add(null);
                }

                _slotNames[slot] = name;
            }

            private bool TryResolveLocal(string name, out Local local)
            {
                for (var i = _locals.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(_locals[i].Name, name, StringComparison.Ordinal))
                    {
                        local = _locals[i];
                        return true;
                    }
                }

                local = null!;
                return false;
            }

            private int FindLocalSlot(string name) => TryResolveLocal(name, out var local) ? local.Slot : -1;

            private int AddUpvalue(UpvalueDescriptor descriptor)
            {
                var key = (descriptor.FromLocal, descriptor.Index);
                if (_upvalueIndex.TryGetValue(key, out var existing))
                {
                    return existing;
                }

                var index = _upvalues.Count;
                _upvalues.Add(descriptor);
                _upvalueIndex[key] = index;
                return index;
            }

            private int ResolveUpvalue(string name)
            {
                if (_parent is null)
                {
                    return -1;
                }

                if (_parent.TryResolveLocal(name, out var local))
                {
                    local.Captured = true;
                    return AddUpvalue(new UpvalueDescriptor(true, local.Slot));
                }

                var parentUpvalue = _parent.ResolveUpvalue(name);
                if (parentUpvalue >= 0)
                {
                    return AddUpvalue(new UpvalueDescriptor(false, parentUpvalue));
                }

                return -1;
            }

            // ------------------------------------------------------------ top level

            public void CompileTopLevel(BlockStmt program, bool returnLastValue)
            {
                var statements = program.Statements;

                for (var i = 0; i < statements.Count; i++)
                {
                    // In REPL mode the final expression is the module's result, so a
                    // typed expression echoes its value like in any other REPL.
                    if (returnLastValue && i == statements.Count - 1 && statements[i] is ExpressionStmt last)
                    {
                        CompileExpression(last.Expression);
                        Emit(Op.Return, 0, 0, 0, pop: 1, push: 0);
                        return;
                    }

                    CompileTopLevelStatement(statements[i]);
                }
            }

            private void CompileTopLevelStatement(Stmt statement)
            {
                switch (statement)
                {
                    case LetStmt { Pattern: not null } patterned:
                        CompilePatternDeclaration(patterned, globals: true);
                        return;

                    case LetStmt let:
                    {
                        var slot = _scope.Declare(let.Name!, let.IsConst, exported: true);
                        if (let.Initializer is null)
                        {
                            EmitLoadConstant(Value.Nil);
                        }
                        else
                        {
                            CompileExpression(let.Initializer);
                        }

                        Emit(Op.StoreGlobal, slot, 0, 0, pop: 1, push: 0);
                        return;
                    }

                    case FunctionDeclStmt declaration:
                    {
                        var slot = _scope.Declare(declaration.Function.Name!, isConst: true, exported: true);
                        CompileFunctionLiteral(declaration.Function, out _);
                        Emit(Op.StoreGlobal, slot, 0, 0, pop: 1, push: 0);
                        return;
                    }

                    case StructDeclStmt structDeclaration:
                        CompileStructDeclaration(structDeclaration, topLevel: true);
                        return;

                    default:
                        CompileStatement(statement);
                        return;
                }
            }

            private void CompileStructDeclaration(StructDeclStmt declaration, bool topLevel)
            {
                var fieldNames = new List<string>(declaration.Fields.Count);
                foreach (var field in declaration.Fields)
                {
                    fieldNames.Add(field.Name);
                }

                // Evaluate defaults: constants are baked into the type, anything else
                // becomes a tiny prototype that runs once per instance.
                var defaultsRelative = new List<Value>(declaration.Fields.Count);
                var defaultPrototypes = new FunctionProto?[declaration.Fields.Count];

                for (var i = 0; i < declaration.Fields.Count; i++)
                {
                    var field = declaration.Fields[i];

                    if (field.Default is null)
                    {
                        defaultsRelative.Add(Value.Nil);
                        continue;
                    }

                    if (TryEvaluateConstant(field.Default, out var constant))
                    {
                        defaultsRelative.Add(constant);
                        continue;
                    }

                    defaultsRelative.Add(Value.Nil);
                    defaultPrototypes[i] = CompileDefaultExpression(field.Default);
                }

                // The name has to exist before the methods are compiled so they can
                // construct their own struct type recursively.
                var slot = topLevel
                    ? _scope.Declare(declaration.Name, isConst: true, exported: true)
                    : DeclareLocal(declaration.Name, isConst: true);

                var typeConstant = AddConstant(BuildTypeConstant(declaration, fieldNames, defaultsRelative, defaultPrototypes));

                Emit(Op.LoadConst, typeConstant, 0, 0, pop: 0, push: 1);

                if (topLevel)
                {
                    Emit(Op.StoreGlobal, slot, 0, 0, pop: 1, push: 0);
                }
                else
                {
                    Emit(Op.StoreLocal, slot, 0, 0, pop: 1, push: 0);
                }
            }

            /// <summary>Compiles `field default` into a zero argument prototype.</summary>
            private FunctionProto CompileDefaultExpression(Expr expression)
            {
                var child = new FunctionCompiler(Module, this, "<default>", _scope, _sourceName, _path,
                    isMethod: false, isTopLevel: false);

                child.PushScope();
                child.CompileExpression(expression);
                child.Emit(Op.Return, 0, 0, 0, pop: 1, push: 0);
                child.PopScope();

                return child.Build();
            }

            private Value BuildTypeConstant(StructDeclStmt declaration, List<string> fieldNames, List<Value> defaults,
                FunctionProto?[] defaultPrototypes)
            {
                var methods = new Dictionary<string, TernClosure>(StringComparer.Ordinal);

                foreach (var method in declaration.Methods)
                {
                    CompileFunctionLiteral(method, out var prototype, isMethod: true);
                    methods[method.Name!] = new TernClosure(prototype, Array.Empty<Upvalue>());
                }

                var type = new TernType(declaration.Name, fieldNames.ToArray(), defaults.ToArray(), methods, defaultPrototypes);

                // Methods that capture upvalues need a closure over the current frame;
                // those are created at instantiation time by cloning the template.
                foreach (var method in declaration.Methods)
                {
                    var closure = methods[method.Name!];
                    if (closure.Proto.Upvalues.Length > 0)
                    {
                        Module.Error("TRN0221",
                            $"method '{method.Name}' cannot capture outer variables; pass them as fields", (Node?)method.Body ?? declaration);
                    }
                }

                return Runtime.Value.Of(type);
            }

            // ----------------------------------------------------------- statements

            /// <summary>
            /// Compiles one statement and checks that it left the operand stack where
            /// it found it. Every statement does, by construction - an expression's
            /// value is either stored, returned or popped - so a mismatch means the
            /// depth bookkeeping inside the compiler has drifted, and MaxStack (which
            /// both back ends size a frame from) is no longer trustworthy. This is the
            /// check that a fumbled `_depth` in the arithmetic-plus-store fusion went
            /// without: the drift made a native frame two slots too small, and a long
            /// list literal after `let x = a * b` wrote past the end of it.
            /// </summary>
            public void CompileStatement(Stmt statement)
            {
                var entry = _depth;
                CompileStatementCore(statement);
                if (_depth != entry)
                {
                    Module.Error("TRN0219", StackDepthMessage(entry, _depth), statement);
                    _depth = entry;
                }
            }

            private string StackDepthMessage(int entry, int exit) =>
                $"internal: this statement {DescribeDepthChange(entry, exit)} on the operand stack, " +
                "so the frame this function is given would be the wrong size - please report this";

            private static string DescribeDepthChange(int entry, int exit) => exit switch
            {
                _ when exit > entry => $"left {exit - entry} extra value(s)",
                _ when exit < entry => $"consumed {entry - exit} value(s) too many",
                _ => "moved the wrong number of values",
            };

            private void CompileStatementCore(Stmt statement)
            {
                SetLine(statement);

                switch (statement)
                {
                    case EmptyStmt:
                        return;

                    case BlockStmt block:
                        PushScope();
                        foreach (var child in block.Statements)
                        {
                            CompileStatement(child);
                        }

                        PopScope();
                        return;

                    case LetStmt let:
                        CompileLetStatement(let);
                        return;

                    case ExpressionStmt expression:
                        CompileExpressionStatement(expression.Expression);
                        return;

                    case FunctionDeclStmt declaration:
                    {
                        var slot = DeclareLocal(declaration.Function.Name!, isConst: true);
                        CompileFunctionLiteral(declaration.Function, out _);
                        EmitStoreLocal(slot);
                        return;
                    }

                    case StructDeclStmt structDeclaration:
                        CompileStructDeclaration(structDeclaration, topLevel: false);
                        return;

                    case IfStmt ifStatement:
                        CompileIf(ifStatement);
                        return;

                    case WhileStmt whileStatement:
                        CompileWhile(whileStatement);
                        return;

                    case LoopStmt loop:
                        CompileLoop(loop);
                        return;

                    case DoWhileStmt doWhile:
                        CompileDoWhile(doWhile);
                        return;

                    case ForStmt forStatement:
                        CompileFor(forStatement);
                        return;

                    case ForInStmt forIn:
                        CompileForIn(forIn);
                        return;

                    case BreakStmt breakStatement:
                        CompileBreak(breakStatement);
                        return;

                    case ContinueStmt continueStatement:
                        CompileContinue(continueStatement);
                        return;

                    case ReturnStmt returnStatement:
                        CompileReturn(returnStatement);
                        return;

                    case ThrowStmt throwStatement:
                        CompileExpression(throwStatement.Value);
                        Emit(Op.Throw, 0, 0, 0, pop: 1, push: 0);
                        return;

                    case TryStmt tryStatement:
                        CompileTry(tryStatement);
                        return;

                    case ImportStmt import:
                        CompileImport(import);
                        return;

                    default:
                        Module.Error("TRN0201", "unsupported statement", statement);
                        return;
                }
            }

            private void CompileLetStatement(LetStmt let)
            {
                if (let.Pattern is not null)
                {
                    CompilePatternDeclaration(let, globals: false);
                    return;
                }

                var slot = DeclareLocal(let.Name!, let.IsConst);

                if (let.Initializer is null)
                {
                    EmitLoadConstant(Value.Nil);
                }
                else
                {
                    CompileExpression(let.Initializer);
                }

                EmitStoreLocal(slot);
            }

            /// <summary>
            /// <c>let [a, b] = ...</c> and <c>for [k, v] in ...</c>: evaluate the
            /// subject once, test its shape, then bind the names.
            ///
            /// The test is the same one <c>match</c> runs, so the two agree about
            /// what a list pattern means. It is not silent when it fails: a
            /// declaration whose subject does not have the shape it names raises a
            /// TypeError that shows both sides, because the alternative is a
            /// program that carries on with nil.
            /// </summary>
            private void CompilePatternDeclaration(LetStmt let, bool globals)
            {
                var pattern = let.Pattern!;

                // One slot for the subject: the test reads it once per element, and
                // the initializer may have a side effect that must happen once.
                var subject = ReserveSlot();
                if (let.Initializer is null)
                {
                    EmitLoadConstant(Value.Nil);
                }
                else
                {
                    CompileExpression(let.Initializer);
                }

                EmitStoreLocalPlain(subject);

                // A `const` pattern binds constants; a `let` pattern binds lets.
                var failJumps = new List<int>();
                EmitPatternTest(pattern, subject, failJumps);
                BindPatternVariables(pattern, subject, globals, isConst: let.IsConst);
                var done = Emit(Op.Jump, 0, 0, 0, pop: 0, push: 0);

                foreach (var jump in failJumps)
                {
                    PatchTo(jump, _code.Count);
                }

                // `<pattern> does not match <value>`: the two halves of the mistake,
                // in the order that reads. `repr` rather than `str`, so a string
                // subject shows its quotes and "12" is not mistaken for 12.
                EmitConstantString(Patterns.Describe(pattern) + " does not match ");
                CompileNameLoad("repr", let);
                EmitLoadLocal(subject);
                // Call takes the callee as well as the argument: `repr` is on the
                // stack underneath it.
                Emit(Op.Call, 1, 0, 0, pop: 2, push: 1);
                Emit(Op.Concat, 2, 0, 0, pop: 2, push: 1);
                Emit(Op.ThrowType, AddStringConstant("TypeError"), 0, 0, pop: 1, push: 0);

                PatchTo(done, _code.Count);
                ReleaseSlot(subject);
            }

            private void EmitConstantString(string text)
            {
                Emit(Op.LoadConst, AddStringConstant(text), 0, 0, pop: 0, push: 1);
            }

            /// <summary>Compiles an `if` statement in value position.</summary>
            private void CompileIfValue(IfStmt statement, Node node)
            {
                var empty = new BlockStmt
                {
                    Statements = new List<Stmt>(),
                    Offset = statement.Offset,
                    Line = statement.Line,
                };

                CompileConditionalExpression(statement.Condition, statement.Then, statement.Else ?? empty, node);
            }

            private void CompileIf(IfStmt statement)
            {
                if (TryEvaluateConstant(statement.Condition, out var constant))
                {
                    if (constant.Truthy)
                    {
                        CompileStatement(statement.Then);
                    }
                    else if (statement.Else is not null)
                    {
                        CompileStatement(statement.Else);
                    }

                    return;
                }

                var jumpToElse = EmitConditionJump(statement.Condition, jumpWhenFalse: true);

                PushScope();
                CompileStatement(statement.Then);
                PopScope();

                if (statement.Else is null)
                {
                    PatchTo(jumpToElse, _code.Count);
                    return;
                }

                var jumpToEnd = Emit(Op.Jump, 0, 0, 0, pop: 0, push: 0);
                PatchTo(jumpToElse, _code.Count);

                PushScope();
                CompileStatement(statement.Else);
                PopScope();

                PatchTo(jumpToEnd, _code.Count);
            }

            private void CompileWhile(WhileStmt statement)
            {
                if (TryEvaluateConstant(statement.Condition, out var constant) && !constant.Truthy)
                {
                    return;
                }

                var start = _code.Count;
                var loop = new LoopContext
                {
                    ContinueTarget = start,
                    BreakJumps = new List<int>(),
                    FinallyDepth = _finally.Count,
                    ScopeDepth = _scopeDepth,
                    Label = statement.Label,
                };
                _loops.Add(loop);

                var exit = EmitConditionJump(statement.Condition, jumpWhenFalse: true);

                PushScope();
                CompileStatement(statement.Body);
                PopScope();

                Emit(Op.Jump, start, 0, 0, pop: 0, push: 0);
                PatchTo(exit, _code.Count);
                foreach (var jump in loop.BreakJumps)
                {
                    PatchTo(jump, _code.Count);
                }

                _loops.RemoveAt(_loops.Count - 1);
            }

            private void CompileLoop(LoopStmt statement)
            {
                var start = _code.Count;
                var loop = new LoopContext
                {
                    ContinueTarget = start,
                    BreakJumps = new List<int>(),
                    FinallyDepth = _finally.Count,
                    ScopeDepth = _scopeDepth,
                    Label = statement.Label,
                };
                _loops.Add(loop);

                PushScope();
                CompileStatement(statement.Body);
                PopScope();

                Emit(Op.Jump, start, 0, 0, pop: 0, push: 0);
                foreach (var jump in loop.BreakJumps)
                {
                    PatchTo(jump, _code.Count);
                }

                _loops.RemoveAt(_loops.Count - 1);
            }

            private void CompileDoWhile(DoWhileStmt statement)
            {
                var start = _code.Count;
                var loop = new LoopContext
                {
                    ContinueTarget = -1,
                    BreakJumps = new List<int>(),
                    FinallyDepth = _finally.Count,
                    ScopeDepth = _scopeDepth,
                    Label = statement.Label,
                    ContinueJumps = new List<int>(),
                };
                _loops.Add(loop);

                PushScope();
                CompileStatement(statement.Body);
                PopScope();

                var conditionTarget = _code.Count;
                var exit = EmitConditionJump(statement.Condition, jumpWhenFalse: true);
                Emit(Op.Jump, start, 0, 0, pop: 0, push: 0);
                PatchTo(exit, _code.Count);

                foreach (var jump in loop.BreakJumps)
                {
                    PatchTo(jump, _code.Count);
                }

                foreach (var jump in loop.ContinueJumps!)
                {
                    PatchTo(jump, conditionTarget);
                }

                _loops.RemoveAt(_loops.Count - 1);
            }

            /// <summary>
            /// Compiles `for (let i = a; i &lt; n; i++)` into a counted loop: the bound is a
            /// constant, so the whole loop needs one dispatch per iteration instead of three.
            /// </summary>
            private bool TryCompileCountedLoop(ForStmt statement)
            {
                if (statement.Initializer is not LetStmt { Name: { } name, Initializer: { } initializer } ||
                    statement.Increment is not IncrementExpr
                    {
                        Target: IdentifierExpr counter,
                        Prefix: false,
                    } increment ||
                    counter.Name != name ||
                    statement.Condition is not BinaryExpr condition)
                {
                    return false;
                }

                // Accept `i < n`, `i <= n`, `i > n`, `i >= n` and the mirrored forms.
                var code = condition.Operator switch
                {
                    TokenKind.Less => CompareCode.Lt,
                    TokenKind.LessEqual => CompareCode.Le,
                    TokenKind.Greater => CompareCode.Gt,
                    TokenKind.GreaterEqual => CompareCode.Ge,
                    _ => (CompareCode?)null,
                };

                if (code is null)
                {
                    return false;
                }

                Expr bound;
                if (condition.Left is IdentifierExpr left && left.Name == name)
                {
                    bound = condition.Right;
                }
                else if (condition.Right is IdentifierExpr right && right.Name == name)
                {
                    bound = condition.Left;
                    code = Mirror(code.Value);
                }
                else
                {
                    return false;
                }

                if (bound is not LiteralExpr { Value: long limit } || limit is < int.MinValue or > int.MaxValue)
                {
                    return false;
                }

                var step = increment.Delta;
                if (code == CompareCode.Lt || code == CompareCode.Le)
                {
                    if (step < 0)
                    {
                        return false;
                    }
                }
                else if (step > 0)
                {
                    return false;
                }

                // The initialiser must be a plain counter declaration for this shape.
                if (TryEvaluateConstant(initializer, out _))
                {
                    // falls through: constants are ideal, anything else is rejected below
                }

                CompileExpression(initializer);
                var slot = DeclareLocal(name, isConst: false);
                EmitStoreLocalPlain(slot);

                var (prep, next) = code switch
                {
                    CompareCode.Lt => (Op.ForPrepLt, Op.ForNextLt),
                    CompareCode.Le => (Op.ForPrepLe, Op.ForNextLe),
                    CompareCode.Gt => (Op.ForPrepGt, Op.ForNextGt),
                    _ => (Op.ForPrepGe, Op.ForNextGe),
                };

                var loop = new LoopContext
                {
                    ContinueTarget = -1,
                    BreakJumps = new List<int>(),
                    FinallyDepth = _finally.Count,
                    ScopeDepth = _scopeDepth,
                    Label = statement.Label,
                    ContinueJumps = new List<int>(),
                };
                _loops.Add(loop);

                var exit = Emit(prep, slot, (int)limit, 0, pop: 0, push: 0);

                var bodyStart = _code.Count;
                PushScope();
                CompileStatement(statement.Body);
                PopScope();

                // `continue` re-enters at the step, which is where ForNext sits.
                var nextIndex = _code.Count;
                foreach (var jump in loop.ContinueJumps)
                {
                    PatchTo(jump, nextIndex);
                }

                Emit(next, slot, (int)limit, bodyStart, pop: 0, push: 0);

                PatchTo(exit, _code.Count);
                foreach (var jump in loop.BreakJumps)
                {
                    PatchTo(jump, _code.Count);
                }

                _loops.RemoveAt(_loops.Count - 1);
                return true;
            }

            private void CompileFor(ForStmt statement)
            {
                PushScope();

                if (statement.Initializer is not null)
                {
                    CompileStatement(statement.Initializer);
                }

                if (TryCompileCountedLoop(statement))
                {
                    PopScope();
                    return;
                }

                var start = _code.Count;
                var loop = new LoopContext
                {
                    ContinueTarget = -1,
                    BreakJumps = new List<int>(),
                    FinallyDepth = _finally.Count,
                    ScopeDepth = _scopeDepth,
                    Label = statement.Label,
                    ContinueJumps = new List<int>(),
                };
                _loops.Add(loop);

                int? exit = null;
                if (statement.Condition is not null &&
                    !(TryEvaluateConstant(statement.Condition, out var constant) && constant.Truthy))
                {
                    exit = EmitConditionJump(statement.Condition, jumpWhenFalse: true);
                }

                PushScope();
                CompileStatement(statement.Body);
                PopScope();

                var incrementTarget = _code.Count;
                if (statement.Increment is not null)
                {
                    // The increment's value is always discarded here, so `i++` becomes
                    // a single IncLocal instead of load/increment/pop.
                    CompileExpressionStatement(statement.Increment);
                }

                // Fuse the increment with the back edge: one dispatch per iteration
                // instead of two for the classic `for (...; i++)` loop.
                if (statement.Increment is IncrementExpr
                    {
                        Target: IdentifierExpr counter,
                    } increment && TryResolveLocal(counter.Name, out var counterLocal) &&
                    _code.Count > 0 && _code[^1].Op == Op.IncLocal && _code[^1].A == counterLocal.Slot)
                {
                    _code.RemoveAt(_code.Count - 1);
                    _lines.RemoveAt(_lines.Count - 1);
                    Emit(Op.IncLocalJump, counterLocal.Slot, increment.Delta, start, pop: 0, push: 0);
                }
                else
                {
                    Emit(Op.Jump, start, 0, 0, pop: 0, push: 0);
                }

                if (exit is not null)
                {
                    PatchTo(exit.Value, _code.Count);
                }

                foreach (var jump in loop.BreakJumps)
                {
                    PatchTo(jump, _code.Count);
                }

                foreach (var jump in loop.ContinueJumps!)
                {
                    PatchTo(jump, incrementTarget);
                }

                _loops.RemoveAt(_loops.Count - 1);
                PopScope();
            }

            private void CompileForIn(ForInStmt statement)
            {
                PushScope();

                // Two names is the most a loop can bind - an index and a value -
                // and a third used to be declared and then never assigned, which
                // reads as nil for the whole loop rather than as a mistake.
                if (statement.Names.Count > 2)
                {
                    Module.Error("TRN0214",
                        $"a for loop binds at most two names (a, b); this one has {statement.Names.Count}",
                        statement);
                }

                CompileExpression(statement.Iterable);
                Emit(Op.IterNew, 0, 0, 0, pop: 1, push: 1);

                var iteratorSlot = DeclareLocal("$iter", isConst: true);
                EmitStoreLocal(iteratorSlot);

                var start = _code.Count;
                var loop = new LoopContext
                {
                    ContinueTarget = start,
                    BreakJumps = new List<int>(),
                    FinallyDepth = _finally.Count,
                    ScopeDepth = _scopeDepth,
                    Label = statement.Label,
                    ContinueJumps = new List<int>(),
                };
                _loops.Add(loop);

                PushScope();
                var bindings = statement.Pattern is not null ? 1 : Math.Min(statement.Names.Count, 2);
                foreach (var name in statement.Names)
                {
                    DeclareLocal(name, isConst: true);
                }

                EmitLoadLocal(iteratorSlot);
                var next = Emit(Op.IterNext, 0, bindings, 0, pop: 1, push: bindings);

                if (statement.Pattern is not null)
                {
                    // The element goes to a slot and the pattern binds from it, which
                    // costs one store per iteration and keeps the loop a plain
                    // IterNext rather than a special case in the runtime.
                    var element = DeclareLocal("$element", isConst: true);
                    EmitStoreLocalPlain(element);
                    BindPatternVariables(statement.Pattern, element, globals: false);
                }
                else
                {
                    // IterNext leaves the bindings on the stack; park them in their slots.
                    for (var i = bindings - 1; i >= 0; i--)
                    {
                        var slot = FindLocalSlot(statement.Names[i]);
                        Emit(Op.StoreLocal, slot, 0, 0, pop: 1, push: 0);
                    }
                }

                CompileStatement(statement.Body);
                PopScope();

                Emit(Op.Jump, start, 0, 0, pop: 0, push: 0);

                PatchA(next, _code.Count);
                foreach (var jump in loop.BreakJumps)
                {
                    PatchTo(jump, _code.Count);
                }

                foreach (var jump in loop.ContinueJumps!)
                {
                    PatchTo(jump, start);
                }

                _loops.RemoveAt(_loops.Count - 1);
                PopScope();
            }

            private void CompileBreak(BreakStmt statement)
            {
                if (_loops.Count == 0)
                {
                    Module.Error("TRN0202", "'break' outside of a loop", statement);
                    return;
                }

                var index = TargetLoop(statement.Label, "break", statement);
                if (index < 0)
                {
                    return;
                }

                var loop = _loops[index];
                LeaveLoopBody(loop, index);

                loop.BreakJumps.Add(Emit(Op.Jump, 0, 0, 0, pop: 0, push: 0));

                // Unreachable code after break is discarded by the depth bookkeeping only;
                // the jump target is patched once the loop body ends.
            }

            private void CompileContinue(ContinueStmt statement)
            {
                if (_loops.Count == 0)
                {
                    Module.Error("TRN0203", "'continue' outside of a loop", statement);
                    return;
                }

                var index = TargetLoop(statement.Label, "continue", statement);
                if (index < 0)
                {
                    return;
                }

                var loop = _loops[index];
                LeaveLoopBody(loop, index);

                if (loop.ContinueJumps is not null)
                {
                    loop.ContinueJumps.Add(Emit(Op.Jump, 0, 0, 0, pop: 0, push: 0));
                }
                else
                {
                    Emit(Op.Jump, loop.ContinueTarget, 0, 0, pop: 0, push: 0);
                }
            }

            /// <summary>
            /// Which loop a <c>break</c> or <c>continue</c> leaves: the named one,
            /// or the innermost. Returns -1, having reported the problem, when the
            /// name matches no enclosing loop.
            /// </summary>
            private int TargetLoop(string? label, string what, Node at)
            {
                if (label is null)
                {
                    return _loops.Count - 1;
                }

                for (var i = _loops.Count - 1; i >= 0; i--)
                {
                    if (_loops[i].Label == label)
                    {
                        return i;
                    }
                }

                Module.Error("TRN0220",
                    $"there is no loop labelled '{label}' for this '{what}' to leave", at);
                return -1;
            }

            /// <summary>
            /// Everything that has to happen on the way out of the loops between
            /// here and <paramref name="target"/>: the <c>finally</c> blocks this
            /// jump crosses run, and the locals of the scopes it leaves are closed
            /// so a closure made in one of them keeps the value it was given.
            /// </summary>
            private void LeaveLoopBody(LoopContext target, int targetIndex)
            {
                EmitPendingFinallyForTransfer(targetIndex);
                CloseUpvaluesOfScopesDeeperThan(target.ScopeDepth);
            }

            /// <summary>
            /// <c>CloseUpvalues</c> for the innermost local that a closure could
            /// hold on to at or above <paramref name="scopeDepth"/>. One is enough:
            /// the instruction closes every open upvalue from that slot upwards,
            /// which is every scope this jump is leaving.
            /// </summary>
            private void CloseUpvaluesOfScopesDeeperThan(int scopeDepth)
            {
                if (scopeDepth < 0 || scopeDepth >= _scopeMarks.Count)
                {
                    return;
                }

                for (var i = _scopeMarks[scopeDepth]; i < _locals.Count; i++)
                {
                    if (_locals[i].Captured)
                    {
                        Emit(Op.CloseUpvalues, _locals[i].Slot, 0, 0, pop: 0, push: 0);
                        return;
                    }
                }
            }

            private void CompileReturn(ReturnStmt statement)
            {
                EmitPendingFinallyForTransfer();

                if (statement.Value is null)
                {
                    Emit(Op.ReturnNil, 0, 0, 0, pop: 0, push: 0);
                    return;
                }

                // `return x;` where x is a local skips the stack entirely.
                if (statement.Value is IdentifierExpr name && TryResolveLocal(name.Name, out var local) &&
                    !local.IsConst)
                {
                    Emit(Op.ReturnLocal, local.Slot, 0, 0, pop: 0, push: 0);
                    return;
                }

                CompileExpression(statement.Value);
                Emit(Op.Return, 0, 0, 0, pop: 1, push: 0);
            }

            private void CompileTry(TryStmt statement)
            {
                var hasCatch = statement.Catch is not null;
                var hasFinally = statement.Finally is not null;

                // Where the stack is when the statement begins. The handler is
                // entered from wherever the throw happened, with one value more on
                // the stack than that - the raised value - so the depth has to be
                // set from this rather than inherited from the fall-through path.
                var entry = _depth;

                // try { body } catch (e) { handler } finally { cleanup }
                var enterTry = Emit(Op.EnterTry, 0, 0, 0, pop: 0, push: 0);

                if (hasFinally)
                {
                    _finally.Add(new FinallyContext { Block = statement.Finally!, LoopDepth = _loops.Count });
                }

                PushScope();
                CompileStatement(statement.Body);
                PopScope();

                Emit(Op.ExitTry, 0, 0, 0, pop: 0, push: 0);

                if (hasFinally)
                {
                    var saved = _finally[^1];
                    _finally.RemoveAt(_finally.Count - 1);
                    CompileStatement(saved.Block);
                    _finally.Add(saved);
                }

                var jumpToEnd = Emit(Op.Jump, 0, 0, 0, pop: 0, push: 0);
                PatchTo(enterTry, _code.Count);

                // Handler entry: the raised value has been pushed by the VM. Both
                // paths consume it - the catch stores it as the error name, and a
                // try/finally without a catch rethrows it - so the count starts one
                // above the statement's entry depth.
                NoteDepth(entry + 1);

                if (hasCatch)
                {
                    PushScope();

                    int? catchSlot = null;
                    if (statement.CatchName is not null)
                    {
                        catchSlot = DeclareLocal(statement.CatchName, isConst: true);
                        EmitStoreLocal(catchSlot.Value);
                    }
                    else
                    {
                        Emit(Op.Pop, 0, 0, 0, pop: 1, push: 0);
                    }

                    var innerTry = -1;
                    var innerFinallyDepth = -1;

                    if (hasFinally)
                    {
                        innerFinallyDepth = _finally.Count;
                        _finally.Add(new FinallyContext { Block = statement.Finally!, LoopDepth = _loops.Count });
                        innerTry = Emit(Op.EnterTry, 0, 0, 0, pop: 0, push: 0);
                    }

                    if (statement.Catch is not null)
                    {
                        CompileStatement(statement.Catch);
                    }

                    PopScope();

                    if (hasFinally)
                    {
                        Emit(Op.ExitTry, 0, 0, 0, pop: 0, push: 0);
                        var saved = _finally[^1];
                        _finally.RemoveAt(_finally.Count - 1);
                        CompileStatement(saved.Block);
                        _finally.Add(saved);
                        _finally.RemoveRange(innerFinallyDepth, _finally.Count - innerFinallyDepth);

                        var afterCatch = Emit(Op.Jump, 0, 0, 0, pop: 0, push: 0);
                        PatchTo(innerTry, _code.Count);
                        NoteDepth(entry + 1);   // this handler is entered with the raised value too
                        CompileStatement(statement.Finally!);
                        Emit(Op.Throw, 0, 0, 0, pop: 1, push: 0);
                        PatchTo(afterCatch, _code.Count);
                    }
                }
                else
                {
                    // try/finally without catch: run cleanup, then rethrow.
                    var saved = _finally[^1];
                    _finally.RemoveAt(_finally.Count - 1);
                    CompileStatement(saved.Block);
                    _finally.Add(saved);
                    Emit(Op.Throw, 0, 0, 0, pop: 1, push: 0);
                }

                if (hasFinally)
                {
                    _finally.RemoveAt(_finally.Count - 1);
                }

                PatchTo(jumpToEnd, _code.Count);
            }

            /// <summary>Inlines pending <c>finally</c> blocks before a control transfer.</summary>
            private void EmitPendingFinallyForTransfer(int loopIndex = -1)
            {
                if (_finally.Count == 0)
                {
                    return;
                }

                // A finally belongs to the loops that were open when its `try`
                // started, so a jump to the loop at `loopIndex` runs the ones
                // entered inside it and leaves the rest for their own exits.
                var first = 0;
                while (first < _finally.Count && _finally[first].LoopDepth <= loopIndex)
                {
                    first++;
                }

                if (first == _finally.Count)
                {
                    return;
                }

                var saved = new List<FinallyContext>(_finally);
                _finally.RemoveRange(first, _finally.Count - first);

                try
                {
                    for (var i = saved.Count - 1; i >= first; i--)
                    {
                        CompileStatement(saved[i].Block);
                    }
                }
                finally
                {
                    _finally.Clear();
                    _finally.AddRange(saved);
                }
            }

            /// <summary>
            /// The TRN0218 message: the name that is missing, the names the build
            /// does have, and why a rebuild is the answer.
            /// </summary>
            private string MissingStdModule(string path)
            {
                var available = new List<string>();
                foreach (var name in Module.Host.StandardModuleNames)
                {
                    available.Add(name.StartsWith("std:", StringComparison.Ordinal) ? name.Substring(4) : name);
                }

                available.Sort(StringComparer.Ordinal);
                var listed = available.Count == 0 ? "none" : string.Join(", ", available);

                return $"cannot load module '{path}': this build has no standard-library module " +
                       $"called '{path.Substring(4)}'. It has: {listed}. A 'std:' module is compiled " +
                       "into the runner rather than read from a file, so a runner older than the " +
                       "script cannot import a newer module - rebuild it.";
            }

            private void CompileImport(ImportStmt import)
            {
                if (Module.Host.TryGetStdModule(import.Path, out var stdModule))
                {
                    if (import.Alias is not null)
                    {
                        var slot = _scope.Declare(import.Alias, isConst: true, exported: true);
                        EmitLoadConstant(stdModule.Value);
                        Emit(Op.StoreGlobal, slot, 0, 0, pop: 1, push: 0);
                        return;
                    }

                    var mapConstant = AddConstant(stdModule.Value);
                    foreach (var name in stdModule.FlatNames)
                    {
                        if (_scope.TryResolve(name, out _))
                        {
                            continue;
                        }

                        var slot = _scope.Declare(name, isConst: true, exported: false);
                        Emit(Op.LoadConst, mapConstant, 0, 0, pop: 0, push: 1);
                        Emit(Op.LoadField, AddStringConstant(name), 0, 0, pop: 1, push: 1);
                        Emit(Op.StoreGlobal, slot, 0, 0, pop: 1, push: 0);
                    }

                    return;
                }

                if (import.Path.StartsWith("std:", StringComparison.Ordinal))
                {
                    // A std: name is compiled into the runner, not read from a
                    // file, so this is a build that is older (or thinner) than
                    // the script rather than a bad path. Say which names it does
                    // have, and declare the alias anyway so the failure produces
                    // one error instead of one per use of the module.
                    Module.Error("TRN0218", MissingStdModule(import.Path), import);

                    // Declare the name the script was about to use anyway. The
                    // import has failed and the program will not run, and without
                    // this every `cli.parse(...)` below reports its own
                    // undefined-variable error for the same one cause.
                    var binding = import.Alias ?? import.Path.Substring(4);
                    if (!_scope.TryResolve(binding, out _))
                    {
                        _scope.Declare(binding, isConst: true, exported: true);
                    }

                    return;
                }

                var module = Module.Host.LoadFileModule(import.Path, Module.Path);
                if (module is null)
                {
                    Module.Error("TRN0204", $"cannot load module '{import.Path}'", import);
                    return;
                }

                if (import.Alias is null)
                {
                    // Import every exported binding directly into this module.
                    for (var i = 0; i < module.Scope.Slots.Count; i++)
                    {
                        var source = module.Scope.Slots[i];
                        if (source.IsHost || !source.Exported)
                        {
                            continue;
                        }

                        if (_scope.TryResolve(source.Name, out _))
                        {
                            continue;
                        }

                        var slot = _scope.Declare(source.Name, isConst: true);
                        var moduleConstant = AddConstant(Runtime.Value.Of(module));
                        Emit(Op.LoadModule, moduleConstant, 0, 0, pop: 0, push: 1);
                        Emit(Op.LoadField, AddStringConstant(source.Name), 0, 0, pop: 1, push: 1);
                        Emit(Op.StoreGlobal, slot, 0, 0, pop: 1, push: 0);
                    }

                    return;
                }

                var aliasSlot = _scope.Declare(import.Alias, isConst: true, exported: true);
                Emit(Op.LoadModule, AddConstant(Runtime.Value.Of(module)), 0, 0, pop: 0, push: 1);
                Emit(Op.StoreGlobal, aliasSlot, 0, 0, pop: 1, push: 0);
            }

            // ---------------------------------------------------------- cond jumps

            /// <summary>
            /// Emits code that jumps to a (later patched) target based on a condition,
            /// using the fused compare-and-branch opcodes where possible.
            /// </summary>
            private int EmitConditionJump(Expr condition, bool jumpWhenFalse)
            {
                SetLine(condition);

                if (condition is UnaryExpr { Operator: TokenKind.Bang or TokenKind.Not } negated)
                {
                    return EmitConditionJump(negated.Operand, !jumpWhenFalse);
                }

                if (TryEvaluateConstant(condition, out var constant))
                {
                    var takeJump = jumpWhenFalse ? !constant.Truthy : constant.Truthy;
                    if (takeJump)
                    {
                        return Emit(Op.Jump, 0, 0, 0, pop: 0, push: 0);
                    }

                    // The condition always passes, so there is no jump to make.
                    // This used to emit one and patch it to the next instruction
                    // as a stand-in for "nothing" — and the caller then patched
                    // that same instruction to the end of the body, so
                    // `while true { ... }` was a loop whose first instruction
                    // jumped over itself. -1 is the honest answer.
                    return -1;
                }

                if (TryGetComparison(condition) is { } comparison)
                {
                    if (jumpWhenFalse && TryEmitFusedCompare(comparison))
                    {
                        return _lastFusedJump;
                    }

                    CompileExpression(comparison.Left);
                    CompileExpression(comparison.Right);
                    return Emit(Op.JumpCmp, (int)comparison.Code, 0, jumpWhenFalse ? 0 : 1, pop: 2, push: 0);
                }

                if (condition is IdentifierExpr identifier && TryResolveLocal(identifier.Name, out var local))
                {
                    return jumpWhenFalse
                        ? Emit(Op.JumpIfFalseLocal, local.Slot, 0, 0, pop: 0, push: 0)
                        : Emit(Op.JumpIfTrueLocal, local.Slot, 0, 0, pop: 0, push: 0);
                }

                CompileExpression(condition);
                return jumpWhenFalse
                    ? Emit(Op.JumpIfFalse, 0, 0, 0, pop: 1, push: 0)
                    : Emit(Op.JumpIfTrue, 0, 0, 0, pop: 1, push: 0);
            }

            private int _lastFusedJump = -1;

            /// <summary>
            /// Emits a single compare-and-branch instruction when both operands live in
            /// registers (or one is a small integer). This removes two dispatches from
            /// every loop iteration and every `if` in a hot path.
            /// </summary>
            private bool TryEmitFusedCompare((Expr Left, Expr Right, CompareCode Code) comparison)
            {
                var (left, right, code) = comparison;

                if (left is IdentifierExpr leftName && TryResolveLocal(leftName.Name, out var leftLocal) &&
                    TryGetSmallInt(right, out var constant))
                {
                    _lastFusedJump = Emit(FusedLocalIntOpcode(code), leftLocal.Slot, constant, 0, pop: 0, push: 0);
                    return true;
                }

                if (right is IdentifierExpr rightName && TryResolveLocal(rightName.Name, out var rightLocal) &&
                    TryGetSmallInt(left, out var value))
                {
                    // `2 <= n` is the same test as `n >= 2`.
                    _lastFusedJump = Emit(FusedLocalIntOpcode(Mirror(code)), rightLocal.Slot, value, 0, pop: 0, push: 0);
                    return true;
                }

                if (left is IdentifierExpr a && right is IdentifierExpr b &&
                    TryResolveLocal(a.Name, out var slotA) && TryResolveLocal(b.Name, out var slotB))
                {
                    _lastFusedJump = Emit(FusedLocalLocalOpcode(code), slotA.Slot, slotB.Slot, 0, pop: 0, push: 0);
                    return true;
                }

                return false;
            }

            private static CompareCode Mirror(CompareCode code) => code switch
            {
                CompareCode.Lt => CompareCode.Gt,
                CompareCode.Le => CompareCode.Ge,
                CompareCode.Gt => CompareCode.Lt,
                CompareCode.Ge => CompareCode.Le,
                _ => code,
            };

            private static bool TryGetSmallInt(Expr expression, out int value)
            {
                if (expression is LiteralExpr { Value: long number } && number is >= int.MinValue and <= int.MaxValue)
                {
                    value = (int)number;
                    return true;
                }

                value = 0;
                return false;
            }

            private static Op FusedLocalIntOpcode(CompareCode code) => code switch
            {
                CompareCode.Lt => Op.JumpLtLocalInt,
                CompareCode.Le => Op.JumpLeLocalInt,
                CompareCode.Gt => Op.JumpGtLocalInt,
                CompareCode.Ge => Op.JumpGeLocalInt,
                CompareCode.Eq => Op.JumpEqLocalInt,
                _ => Op.JumpNeLocalInt,
            };

            private static Op FusedLocalLocalOpcode(CompareCode code) => code switch
            {
                CompareCode.Lt => Op.JumpLtLocalLocal,
                CompareCode.Le => Op.JumpLeLocalLocal,
                CompareCode.Gt => Op.JumpGtLocalLocal,
                CompareCode.Ge => Op.JumpGeLocalLocal,
                CompareCode.Eq => Op.JumpEqLocalLocal,
                _ => Op.JumpNeLocalLocal,
            };

            private enum CompareCode
            {
                Lt = 0,
                Le = 1,
                Gt = 2,
                Ge = 3,
                Eq = 4,
                Ne = 5,
            }

            private (Expr Left, Expr Right, CompareCode Code)? TryGetComparison(Expr expression)
            {
                if (expression is not BinaryExpr binary)
                {
                    return null;
                }

                var code = binary.Operator switch
                {
                    TokenKind.Less => CompareCode.Lt,
                    TokenKind.LessEqual => CompareCode.Le,
                    TokenKind.Greater => CompareCode.Gt,
                    TokenKind.GreaterEqual => CompareCode.Ge,
                    TokenKind.EqualEqual => CompareCode.Eq,
                    TokenKind.BangEqual => CompareCode.Ne,
                    _ => (CompareCode?)null,
                };

                return code is null ? null : (binary.Left, binary.Right, code.Value);
            }

            // --------------------------------------------------------- expressions

            public void CompileExpression(Expr expression)
            {
                SetLine(expression);

                switch (expression)
                {
                    case LiteralExpr literal:
                        EmitLoadConstant(ToValue(literal.Value));
                        return;

                    case InterpolatedStringExpr interpolated:
                        CompileInterpolatedString(interpolated);
                        return;

                    case IdentifierExpr identifier:
                        CompileIdentifierLoad(identifier);
                        return;

                    case ThisExpr thisExpression:
                        CompileNameLoad("this", thisExpression);
                        return;

                    case UnaryExpr unary:
                        CompileUnary(unary);
                        return;

                    case BinaryExpr binary:
                        CompileBinary(binary);
                        return;

                    case TernaryExpr ternary:
                        CompileConditionalExpression(ternary.Condition, AsBranch(ternary.WhenTrue), AsBranch(ternary.WhenFalse), expression);
                        return;

                    case IfExpr ifExpression:
                        CompileConditionalExpression(ifExpression.Condition, ifExpression.Then, ifExpression.Else, expression);
                        return;

                    case CallExpr call:
                        CompileCall(call);
                        return;

                    case MemberExpr member:
                        CompileMember(member);
                        return;

                    case IndexExpr index:
                        CompileExpression(index.Target);
                        CompileExpression(index.Index);
                        Emit(Op.LoadIndex, 0, 0, 0, pop: 2, push: 1);
                        return;

                    case RangeExpr range:
                        CompileRange(range);
                        return;

                    case SliceExpr slice:
                        CompileSlice(slice);
                        return;

                    case AssignExpr assign:
                        CompileAssign(assign);
                        return;

                    case IncrementExpr increment:
                        CompileIncrement(increment);
                        return;

                    case LambdaExpr lambda:
                        CompileFunctionLiteral(lambda.Function, out _);
                        return;

                    case ListExpr list:
                    {
                        foreach (var item in list.Items)
                        {
                            CompileExpression(item);
                        }

                        Emit(Op.NewList, list.Items.Count, 0, 0, pop: list.Items.Count, push: 1);
                        return;
                    }

                    case MapExpr map:
                    {
                        for (var i = 0; i < map.Keys.Count; i++)
                        {
                            CompileExpression(map.Keys[i]);
                            CompileExpression(map.Values[i]);
                        }

                        Emit(Op.NewMap, map.Keys.Count, 0, 0, pop: map.Keys.Count * 2, push: 1);
                        return;
                    }

                    case MatchExpr match:
                        CompileMatch(match);
                        return;

                    default:
                        Module.Error("TRN0205", "unsupported expression", expression);
                        EmitLoadConstant(Value.Nil);
                        return;
                }
            }

            private void CompileIdentifierLoad(IdentifierExpr identifier) => CompileNameLoad(identifier.Name, identifier);

            private void CompileNameLoad(string name, Node node)
            {
                if (TryResolveLocal(name, out var local))
                {
                    EmitLoadLocal(local.Slot);
                    return;
                }

                var upvalue = ResolveUpvalue(name);
                if (upvalue >= 0)
                {
                    Emit(Op.LoadUpvalue, upvalue, 0, 0, pop: 0, push: 1);
                    return;
                }

                if (_scope.TryResolve(name, out var slot))
                {
                    Emit(Op.LoadGlobal, slot, 0, 0, pop: 0, push: 1);
                    return;
                }

                Module.Error("TRN0206", $"undefined variable '{name}'", node);
                EmitLoadConstant(Value.Nil);
            }

            private void CompileInterpolatedString(InterpolatedStringExpr interpolated)
            {
                var parts = 0;

                for (var i = 0; i < interpolated.Parts.Count; i++)
                {
                    var part = interpolated.Parts[i];

                    if (!part.IsExpression)
                    {
                        EmitLoadConstant(Value.Str(part.Text));
                        parts++;
                        continue;
                    }

                    var expression = interpolated.Expressions[i];
                    if (expression is null)
                    {
                        EmitLoadConstant(Value.Str(string.Empty));
                        parts++;
                        continue;
                    }

                    CompileExpression(expression);
                    parts++;
                }

                if (parts == 1)
                {
                    Emit(Op.ToString, 0, 0, 0, pop: 1, push: 1);
                    return;
                }

                Emit(Op.Concat, parts, 0, 0, pop: parts, push: 1);
            }

            private void CompileUnary(UnaryExpr unary)
            {
                switch (unary.Operator)
                {
                    case TokenKind.TypeOf:
                        CompileExpression(unary.Operand);
                        Emit(Op.TypeOf, 0, 0, 0, pop: 1, push: 1);
                        return;
                }

                if (TryEvaluateConstant(unary, out var folded))
                {
                    EmitLoadConstant(folded);
                    return;
                }

                CompileExpression(unary.Operand);

                switch (unary.Operator)
                {
                    case TokenKind.Minus:
                        Emit(Op.Neg, 0, 0, 0, pop: 1, push: 1);
                        return;
                    case TokenKind.Bang:
                    case TokenKind.Not:
                        Emit(Op.Not, 0, 0, 0, pop: 1, push: 1);
                        return;
                    case TokenKind.Tilde:
                        Emit(Op.BNot, 0, 0, 0, pop: 1, push: 1);
                        return;
                    case TokenKind.Plus:
                        return; // unary plus is a no-op
                    default:
                        Module.Error("TRN0207", $"unsupported unary operator '{unary.Operator}'", unary);
                        return;
                }
            }

            private void CompileBinary(BinaryExpr binary)
            {
                switch (binary.Operator)
                {
                    case TokenKind.AmpAmp:
                    case TokenKind.And:
                        CompileLogical(binary, isAnd: true);
                        return;
                    case TokenKind.PipePipe:
                    case TokenKind.Or:
                        CompileLogical(binary, isAnd: false);
                        return;
                    case TokenKind.QuestionQuestion:
                        CompileCoalesce(binary);
                        return;
                    case TokenKind.In:
                        if (TryEvaluateConstant(binary, out var membership))
                        {
                            EmitLoadConstant(membership);
                            return;
                        }

                        CompileExpression(binary.Left);
                        CompileExpression(binary.Right);
                        Emit(Op.In, 0, 0, 0, pop: 2, push: 1);
                        return;
                }

                if (TryEvaluateConstant(binary, out var folded))
                {
                    EmitLoadConstant(folded);
                    return;
                }

                var op = BinaryOpcode(binary.Operator);
                if (op is null)
                {
                    Module.Error("TRN0208", $"unsupported operator '{binary.Operator}'", binary);
                    EmitLoadConstant(Value.Nil);
                    return;
                }

                // Fused local arithmetic: `a + b`, `a + 1`, ...
                if (op is Op.Add or Op.Sub or Op.Mul or Op.Div or Op.Mod)
                {
                    if (TryEmitLocalArithmetic(binary, op.Value))
                    {
                        return;
                    }
                }

                CompileExpression(binary.Left);
                CompileExpression(binary.Right);
                Emit(op.Value, 0, 0, 0, pop: 2, push: 1);
            }

            private bool TryEmitLocalArithmetic(BinaryExpr binary, Op op)
            {
                if (binary.Left is not IdentifierExpr leftIdentifier)
                {
                    return false;
                }

                if (!TryResolveLocal(leftIdentifier.Name, out var local))
                {
                    return false;
                }

                if (TryEvaluateConstant(binary.Right, out var constant) &&
                    IsConstantArithmeticOperand(constant))
                {
                    // `x % k` for a positive constant k uses a precomputed reciprocal
                    // instead of the (very slow) 64-bit hardware divide.
                    if (op == Op.Mod && constant.Kind == ValueKind.Int &&
                        DivisionMagic.TryFor(constant.AsInt(), out _))
                    {
                        Emit(Op.ModLocalConstMagic, local.Slot, AddDivision(constant.AsInt()), 0, pop: 0, push: 1);
                        return true;
                    }

                    Emit(OpFusedLocalConst(op), local.Slot, AddConstant(constant), 0, pop: 0, push: 1);
                    return true;
                }

                if (binary.Right is IdentifierExpr rightIdentifier &&
                    TryResolveLocal(rightIdentifier.Name, out var rightLocal))
                {
                    Emit(OpFusedLocalLocal(op), local.Slot, rightLocal.Slot, 0, pop: 0, push: 1);
                    return true;
                }

                return false;
            }

            private static bool IsConstantArithmeticOperand(in Value value) =>
                value.Kind is ValueKind.Int or ValueKind.Float;

            private static Op OpFusedLocalConst(Op op) => op switch
            {
                Op.Add => Op.AddLocalConst,
                Op.Sub => Op.SubLocalConst,
                Op.Mul => Op.MulLocalConst,
                Op.Div => Op.DivLocalConst,
                Op.Mod => Op.ModLocalConst,
                _ => op,
            };

            private static Op OpFusedLocalLocal(Op op) => op switch
            {
                Op.Add => Op.AddLocalLocal,
                Op.Sub => Op.SubLocalLocal,
                Op.Mul => Op.MulLocalLocal,
                Op.Div => Op.DivLocalLocal,
                Op.Mod => Op.ModLocalLocal,
                _ => op,
            };

            /// <summary>Compile time fold for `x in y` when both sides are literals.</summary>
            private static bool TryFoldMembership(in Value item, in Value container, out bool contained)
            {
                contained = false;

                switch (container.Kind)
                {
                    case ValueKind.Str when item.Kind == ValueKind.Str:
                        contained = container.AsString().Contains(item.AsString(), StringComparison.Ordinal);
                        return true;

                    case ValueKind.List:
                    {
                        var list = container.AsList();
                        for (var i = 0; i < list.Count; i++)
                        {
                            if (list[i].Equals(item))
                            {
                                contained = true;
                                break;
                            }
                        }

                        return true;
                    }

                    case ValueKind.Map:
                        contained = container.AsMap().ContainsKey(item);
                        return true;

                    case ValueKind.Range:
                    {
                        var range = container.AsRange();
                        if (item.Kind != ValueKind.Int)
                        {
                            return false;
                        }

                        var end = range.Inclusive ? range.End : range.End - 1;
                        contained = item.AsInt() >= range.Start && item.AsInt() <= end;
                        return true;
                    }

                    default:
                        return false;
                }
            }

            private static Op? BinaryOpcode(TokenKind kind) => kind switch
            {
                TokenKind.Plus => Op.Add,
                TokenKind.Minus => Op.Sub,
                TokenKind.Star => Op.Mul,
                TokenKind.Slash => Op.Div,
                TokenKind.Percent => Op.Mod,
                TokenKind.StarStar => Op.Pow,
                TokenKind.Amp => Op.BAnd,
                TokenKind.Pipe => Op.BOr,
                TokenKind.Caret => Op.BXor,
                TokenKind.ShiftLeft => Op.Shl,
                TokenKind.ShiftRight => Op.Shr,
                TokenKind.EqualEqual => Op.Eq,
                TokenKind.BangEqual => Op.Ne,
                TokenKind.Less => Op.Lt,
                TokenKind.LessEqual => Op.Le,
                TokenKind.Greater => Op.Gt,
                TokenKind.GreaterEqual => Op.Ge,
                _ => null,
            };

            private void CompileLogical(BinaryExpr binary, bool isAnd)
            {
                CompileExpression(binary.Left);
                var jump = isAnd
                    ? Emit(Op.JumpIfFalseKeep, 0, 0, 0, pop: 0, push: 0)
                    : Emit(Op.JumpIfTrueKeep, 0, 0, 0, pop: 0, push: 0);

                Emit(Op.Pop, 0, 0, 0, pop: 1, push: 0);
                CompileExpression(binary.Right);
                PatchTo(jump, _code.Count);
            }

            private void CompileCoalesce(BinaryExpr binary)
            {
                CompileExpression(binary.Left);
                var jump = Emit(Op.JumpIfNotNullKeep, 0, 0, 0, pop: 0, push: 0);
                Emit(Op.Pop, 0, 0, 0, pop: 1, push: 0);
                CompileExpression(binary.Right);
                PatchTo(jump, _code.Count);
            }

            private static Stmt AsBranch(Expr expression) => new ExpressionStmt
            {
                Expression = expression,
                Offset = expression.Offset,
                Line = expression.Line,
            };

            private void CompileConditionalExpression(Expr condition, Stmt thenBranch, Stmt elseBranch, Node node)
            {
                if (TryEvaluateConstant(condition, out var constant))
                {
                    CompileBranchValue(constant.Truthy ? thenBranch : elseBranch, node);
                    return;
                }

                // Where the branches start, and what each of them leaves behind.
                // Both are needed at the labels: the then branch ends with its value
                // on the stack, and the else branch is jumped to from before that, so
                // the depth at the label is the one going in, not the one coming out.
                var entry = _depth;

                var jumpToElse = EmitConditionJump(condition, jumpWhenFalse: true);

                PushScope();
                CompileBranchValue(thenBranch, node);
                PopScope();

                var jumpToEnd = Emit(Op.Jump, 0, 0, 0, pop: 0, push: 0);
                PatchTo(jumpToElse, _code.Count);
                NoteDepth(entry);

                PushScope();
                CompileBranchValue(elseBranch, node);
                PopScope();

                PatchTo(jumpToEnd, _code.Count);
                NoteDepth(entry + 1);   // whichever branch ran, a value is what is here
            }

            private void CompileBranchValue(Stmt branch, Node node)
            {
                if (branch is BlockStmt block)
                {
                    CompileBlockValue(block);
                    return;
                }

                if (branch is ExpressionStmt expression)
                {
                    CompileExpression(expression.Expression);
                    return;
                }

                if (branch is IfStmt ifStatement)
                {
                    CompileIfValue(ifStatement, ifStatement);
                    return;
                }

                CompileStatement(branch);
                EmitLoadConstant(Value.Nil);
            }

            private void CompileBlockValue(BlockStmt block)
            {
                if (block.Statements.Count == 0)
                {
                    EmitLoadConstant(Value.Nil);
                    return;
                }

                var last = block.Statements[^1];
                for (var i = 0; i < block.Statements.Count - 1; i++)
                {
                    CompileStatement(block.Statements[i]);
                }

                if (last is ExpressionStmt expression)
                {
                    CompileExpression(expression.Expression);
                }
                else if (last is IfStmt ifStatement)
                {
                    CompileIfValue(ifStatement, ifStatement);
                }
                else
                {
                    CompileStatement(last);
                    EmitLoadConstant(Value.Nil);
                }
            }

            private void CompileCall(CallExpr call)
            {
                // Direct call to a global (e.g. print(...)) or a local function.
                if (call.Callee is IdentifierExpr identifier)
                {
                    if (TryResolveLocal(identifier.Name, out var local) && !MentionsName(call.Arguments, identifier.Name))
                    {
                        CompileArguments(call.Arguments);
                        Emit(Op.CallLocal, call.Arguments.Count, local.Slot, 0, pop: call.Arguments.Count, push: 1);
                        return;
                    }

                    if (!TryResolveLocal(identifier.Name, out _) &&
                        ResolveUpvalue(identifier.Name) < 0 &&
                        _scope.TryResolve(identifier.Name, out var globalSlot) &&
                        !MentionsName(call.Arguments, identifier.Name))
                    {
                        CompileArguments(call.Arguments);
                        Emit(Op.CallGlobal, call.Arguments.Count, globalSlot, 0, pop: call.Arguments.Count, push: 1);
                        return;
                    }
                }

                // Method style call `receiver.name(args)`.
                if (call.Callee is MemberExpr member)
                {
                    CompileExpression(member.Target);
                    CompileArguments(call.Arguments);
                    // The receiver goes with the arguments: Invoke leaves the result
                    // in the receiver's slot. Counting only the arguments left the
                    // depth one too low per call for the rest of the function, and a
                    // chain like `text.lower().words()` is two of them.
                    Emit(Op.Invoke, AddStringConstant(member.Name), call.Arguments.Count, 0,
                        pop: call.Arguments.Count + 1, push: 1);
                    return;
                }

                CompileExpression(call.Callee);
                CompileArguments(call.Arguments);
                Emit(Op.Call, call.Arguments.Count, 0, 0, pop: call.Arguments.Count + 1, push: 1);
            }

            private void CompileArguments(List<Expr> arguments)
            {
                foreach (var argument in arguments)
                {
                    CompileExpression(argument);
                }
            }

            /// <summary>Conservative check: does any argument assign the given name?</summary>
            private static bool MentionsName(List<Expr> arguments, string name)
            {
                foreach (var argument in arguments)
                {
                    if (WritesName(argument, name))
                    {
                        return true;
                    }
                }

                return false;
            }

            private static bool WritesName(Expr expression, string name)
            {
                switch (expression)
                {
                    case AssignExpr assign:
                        return (assign.Target is IdentifierExpr target && target.Name == name) ||
                               WritesName(assign.Target, name) || WritesName(assign.Value, name);
                    case IncrementExpr increment:
                        return increment.Target is IdentifierExpr incTarget && incTarget.Name == name ||
                               WritesName(increment.Target, name);
                    case BinaryExpr binary:
                        return WritesName(binary.Left, name) || WritesName(binary.Right, name);
                    case UnaryExpr unary:
                        return WritesName(unary.Operand, name);
                    case TernaryExpr ternary:
                        return WritesName(ternary.Condition, name) || WritesName(ternary.WhenTrue, name) ||
                               WritesName(ternary.WhenFalse, name);
                    case CallExpr call:
                    {
                        foreach (var argument in call.Arguments)
                        {
                            if (WritesName(argument, name))
                            {
                                return true;
                            }
                        }

                        return WritesName(call.Callee, name);
                    }
                    case IndexExpr index:
                        return WritesName(index.Target, name) || WritesName(index.Index, name);
                    case MemberExpr member:
                        return WritesName(member.Target, name);
                    case ListExpr list:
                    {
                        foreach (var item in list.Items)
                        {
                            if (WritesName(item, name))
                            {
                                return true;
                            }
                        }

                        return false;
                    }
                    case MapExpr map:
                    {
                        for (var i = 0; i < map.Keys.Count; i++)
                        {
                            if (WritesName(map.Keys[i], name) || WritesName(map.Values[i], name))
                            {
                                return true;
                            }
                        }

                        return false;
                    }
                    case InterpolatedStringExpr interpolated:
                    {
                        foreach (var part in interpolated.Expressions)
                        {
                            if (part is not null && WritesName(part, name))
                            {
                                return true;
                            }
                        }

                        return false;
                    }
                    case LambdaExpr:
                        return false;   // separate scope
                    default:
                        return false;
                }
            }

            private void CompileRange(RangeExpr range)
            {
                if (range.End is null)
                {
                    Module.Error("TRN0213", "a range with no upper bound is only valid in a slice", range);
                    EmitLoadConstant(Value.Nil);
                    return;
                }

                CompileExpression(range.Start);
                CompileExpression(range.End);
                Emit(Op.NewRange, range.Inclusive ? 1 : 0, 0, 0, pop: 2, push: 1);
            }

            /// <summary>
            /// Emits a member read. <c>length</c>, <c>count</c> and <c>size</c> become a
            /// dedicated opcode so strings, lists and maps never enter the general
            /// member resolution path.
            /// </summary>
            private void EmitLoadField(string name)
            {
                if (name is "length" or "count" or "size")
                {
                    Emit(Op.Length, AddStringConstant(name), 0, 0, pop: 1, push: 1);
                    return;
                }

                Emit(Op.LoadField, AddStringConstant(name), 0, 0, pop: 1, push: 1);
            }

            /// <summary>Loads a member, short-circuiting to nil for the optional form <c>a?.b</c>.</summary>
            private void CompileMember(MemberExpr member)
            {
                CompileExpression(member.Target);

                if (!member.Safe)
                {
                    EmitLoadField(member.Name);
                    return;
                }

                var present = Emit(Op.JumpIfNotNullKeep, 0, 0, 0, pop: 0, push: 0);
                Emit(Op.Pop, 0, 0, 0, pop: 1, push: 0);
                EmitLoadConstant(Value.Nil);
                var done = Emit(Op.Jump, 0, 0, 0, pop: 0, push: 0);
                PatchTo(present, _code.Count);
                EmitLoadField(member.Name);
                PatchTo(done, _code.Count);
            }

            private void CompileSlice(SliceExpr slice)
            {
                CompileExpression(slice.Target);

                if (slice.Start is null)
                {
                    EmitLoadConstant(Value.Nil);
                }
                else
                {
                    CompileExpression(slice.Start);
                }

                if (slice.End is null)
                {
                    EmitLoadConstant(Value.Nil);
                }
                else
                {
                    CompileExpression(slice.End);
                }

                Emit(Op.Slice, slice.Inclusive ? 1 : 0, 0, 0, pop: 3, push: 1);
            }

            /// <summary>
            /// Compiles an expression whose value is thrown away. Assignments and
            /// increments to variables skip the trailing reload, which is what keeps
            /// `total = total + 1;` and `i++` at two dispatches instead of four.
            /// </summary>
            private void CompileExpressionStatement(Expr expression)
            {
                switch (expression)
                {
                    case AssignExpr assign when assign.Target is IdentifierExpr:
                        CompileAssign(assign, keepValue: false);
                        return;

                    case AssignExpr { Operator: TokenKind.Equal } assign
                        when assign.Target is MemberExpr or IndexExpr && IsJumpFree(assign.Value):
                        // `obj.field = v;` / `m[k] = v;` as a statement: the value is
                        // not needed, so the store opcode does not push it back.
                        CompileDiscardingStore(assign);
                        return;

                    case IncrementExpr increment when increment.Target is IdentifierExpr:
                        CompileIncrement(increment, keepValue: false);
                        return;

                    case IfExpr or MatchExpr:
                        // Values produced by block-shaped expressions are dropped by the
                        // branches themselves; compiling them as statements avoids a Pop.
                        CompileExpression(expression);
                        Emit(Op.Pop, 0, 0, 0, pop: 1, push: 0);
                        return;

                    default:
                        CompileExpression(expression);
                        Emit(Op.Pop, 0, 0, 0, pop: 1, push: 0);
                        return;
                }
            }

            /// <summary>
            /// Recognises `x = x + 1`, `x = x - y` and `x = x + (pure expression)` on a
            /// local. These are one dispatch instead of three, and they cannot change
            /// evaluation order because the right hand side is free of side effects.
            /// </summary>
            private bool TryCompileFusedSelfAssignment(AssignExpr assign, int slot, bool keepValue)
            {
                if (assign.Value is not BinaryExpr binary)
                {
                    return false;
                }

                var (other, mirrored) = binary.Left is IdentifierExpr left &&
                                        TryResolveLocal(left.Name, out var leftLocal) && leftLocal.Slot == slot
                    ? (binary.Right, false)
                    : binary.Right is IdentifierExpr right &&
                      TryResolveLocal(right.Name, out var rightLocal) && rightLocal.Slot == slot
                        ? (binary.Left, true)
                        : (null, false);

                if (other is null)
                {
                    return false;
                }

                // x = y + x is the same as x = x + y; subtraction and division are not.
                var op = binary.Operator;
                if (mirrored && op != TokenKind.Plus && op != TokenKind.Star)
                {
                    return false;
                }

                var fusedStore = op switch
                {
                    TokenKind.Plus => (Op.AddLocalConstStore, Op.AddLocalLocalStore, Op.AddLocalStack),
                    TokenKind.Minus => (Op.SubLocalConstStore, Op.SubLocalLocalStore, Op.SubLocalStack),
                    _ => ((Op, Op, Op)?)null,
                };

                // Simple constant or local operands need no stack at all.
                if (fusedStore is { } store)
                {
                    if (other is LiteralExpr { Value: long number } && number is >= int.MinValue and <= int.MaxValue)
                    {
                        Emit(store.Item1, slot, (int)number, 0, pop: 0, push: 0);
                        EmitReloadAfterFusedStore(slot, keepValue);
                        return true;
                    }

                    if (other is IdentifierExpr name && TryResolveLocal(name.Name, out var source))
                    {
                        Emit(store.Item2, slot, source.Slot, 0, pop: 0, push: 0);
                        EmitReloadAfterFusedStore(slot, keepValue);
                        return true;
                    }
                }

                // Otherwise fall back to a stack based fusion when the right side is pure.
                if (fusedStore is { } stackStore && IsPure(other))
                {
                    CompileExpression(other);
                    Emit(stackStore.Item3, slot, 0, 0, pop: 1, push: 0);
                    EmitReloadAfterFusedStore(slot, keepValue);
                    return true;
                }

                return false;
            }

            private void EmitReloadAfterFusedStore(int slot, bool keepValue)
            {
                if (keepValue)
                {
                    EmitLoadLocal(slot);
                }
            }

            /// <summary>True when evaluating the expression cannot call user code or mutate state.</summary>
            private bool IsPure(Expr expression) => expression switch
            {
                LiteralExpr or IdentifierExpr or ThisExpr => true,
                UnaryExpr unary => IsPure(unary.Operand),
                BinaryExpr binary => IsPure(binary.Left) && IsPure(binary.Right),
                TernaryExpr ternary => IsPure(ternary.Condition) && IsPure(ternary.WhenTrue) && IsPure(ternary.WhenFalse),
                RangeExpr range => IsPure(range.Start) && (range.End is null || IsPure(range.End)),
                ListExpr list => list.Items.TrueForAll(IsPure),
                _ => false,
            };

            /// <summary>
            /// True for expressions that compile to straight-line code: no jumps, so
            /// they can be evaluated while another value (an assignment receiver or
            /// index) is still sitting on the value stack. That is what lets
            /// <c>m[k] = v</c> skip spilling the receiver and the index to locals.
            /// </summary>
            private static bool IsJumpFree(Expr expression) => expression switch
            {
                LiteralExpr or IdentifierExpr or ThisExpr or LambdaExpr => true,
                UnaryExpr unary => IsJumpFree(unary.Operand),
                BinaryExpr binary => binary.Operator is not (TokenKind.AmpAmp or TokenKind.PipePipe or
                    TokenKind.QuestionQuestion) &&
                    IsJumpFree(binary.Left) && IsJumpFree(binary.Right),
                MemberExpr member => !member.Safe && IsJumpFree(member.Target),
                IndexExpr index => IsJumpFree(index.Target) && IsJumpFree(index.Index),
                CallExpr call => IsJumpFree(call.Callee) && call.Arguments.TrueForAll(IsJumpFree),
                InterpolatedStringExpr interpolated => interpolated.Expressions.TrueForAll(
                    part => part is null || IsJumpFree(part)),
                RangeExpr range => IsJumpFree(range.Start) && (range.End is null || IsJumpFree(range.End)),
                SliceExpr slice => IsJumpFree(slice.Target) &&
                    (slice.Start is null || IsJumpFree(slice.Start)) &&
                    (slice.End is null || IsJumpFree(slice.End)),
                _ => false,
            };

            /// <summary>
            /// Statement form of <c>x.f = v</c> / <c>x[i] = v</c>: no receiver spill,
            /// no value retained, so the whole statement is a single opcode.
            /// </summary>
            private void CompileDiscardingStore(AssignExpr assign)
            {
                if (assign.Target is MemberExpr member)
                {
                    var name = AddStringConstant(member.Name);
                    CompileExpression(member.Target);
                    CompileExpression(assign.Value);
                    Emit(Op.StoreFieldDiscard, name, 0, 0, pop: 2, push: 0);
                    return;
                }

                var index = (IndexExpr)assign.Target;
                CompileExpression(index.Target);
                CompileExpression(index.Index);
                CompileExpression(assign.Value);
                Emit(Op.StoreIndexDiscard, 0, 0, 0, pop: 3, push: 0);
            }

            private void CompileAssign(AssignExpr assign, bool keepValue = true)
            {
                if (IsShortCircuitAssign(assign.Operator))
                {
                    CompileShortCircuitAssign(assign, keepValue);
                    return;
                }

                var simple = assign.Operator == TokenKind.Equal;

                Op? compoundOp = simple ? null : CompoundOpcode(assign.Operator);
                if (!simple && compoundOp is null)
                {
                    Module.Error("TRN0212", $"unsupported assignment operator '{assign.Operator}'", assign);
                    EmitLoadConstant(Value.Nil);
                    return;
                }

                if (assign.Target is IdentifierExpr identifier)
                {
                    if (TryResolveLocal(identifier.Name, out var local))
                    {
                        if (local.IsConst)
                        {
                            Module.Error("TRN0209", $"cannot assign to constant '{identifier.Name}'", assign);
                        }

                        if (simple)
                        {
                            if (TryCompileFusedSelfAssignment(assign, local.Slot, keepValue))
                            {
                                return;
                            }

                            CompileExpression(assign.Value);
                        }
                        else
                        {
                            EmitLoadLocal(local.Slot);
                            CompileExpression(assign.Value);
                            Emit(compoundOp!.Value, 0, 0, 0, pop: 2, push: 1);
                        }

                        EmitStoreLocal(local.Slot);

                        if (keepValue)
                        {
                            EmitLoadLocal(local.Slot);
                        }

                        return;
                    }

                    var upvalue = ResolveUpvalue(identifier.Name);
                    if (upvalue >= 0)
                    {
                        if (simple)
                        {
                            CompileExpression(assign.Value);
                        }
                        else
                        {
                            Emit(Op.LoadUpvalue, upvalue, 0, 0, pop: 0, push: 1);
                            CompileExpression(assign.Value);
                            Emit(compoundOp!.Value, 0, 0, 0, pop: 2, push: 1);
                        }

                        Emit(Op.StoreUpvalue, upvalue, 0, 0, pop: 1, push: 0);

                        if (keepValue)
                        {
                            Emit(Op.LoadUpvalue, upvalue, 0, 0, pop: 0, push: 1);
                        }

                        return;
                    }

                    if (_scope.TryResolve(identifier.Name, out var globalSlot))
                    {
                        if (_scope.IsConst(globalSlot))
                        {
                            Module.Error("TRN0209", $"cannot assign to constant '{identifier.Name}'", assign);
                        }

                        if (simple)
                        {
                            CompileExpression(assign.Value);
                        }
                        else
                        {
                            Emit(Op.LoadGlobal, globalSlot, 0, 0, pop: 0, push: 1);
                            CompileExpression(assign.Value);
                            Emit(compoundOp!.Value, 0, 0, 0, pop: 2, push: 1);
                        }

                        Emit(Op.StoreGlobal, globalSlot, 0, 0, pop: 1, push: 0);

                        if (keepValue)
                        {
                            Emit(Op.LoadGlobal, globalSlot, 0, 0, pop: 0, push: 1);
                        }

                        return;
                    }

                    Module.Error("TRN0206", $"undefined variable '{identifier.Name}'", assign);
                    CompileExpression(assign.Value);
                    return;
                }

                if (assign.Target is MemberExpr member)
                {
                    if (simple && IsJumpFree(assign.Value))
                    {
                        // Both operands stay on the stack: no temporary slot, no
                        // reload, and the stored value is already the result.
                        CompileExpression(member.Target);
                        CompileExpression(assign.Value);
                        Emit(Op.StoreField, AddStringConstant(member.Name), 0, 0, pop: 2, push: 1);
                        return;
                    }

                    // receiver is evaluated once into a temporary slot
                    PushScope();
                    CompileExpression(member.Target);
                    var receiverSlot = DeclareLocal("$recv", isConst: true);
                    EmitStoreLocal(receiverSlot);

                    if (simple)
                    {
                        EmitLoadLocal(receiverSlot);
                        CompileExpression(assign.Value);
                    }
                    else
                    {
                        EmitLoadLocal(receiverSlot);
                        Emit(Op.LoadField, AddStringConstant(member.Name), 0, 0, pop: 1, push: 1);
                        CompileExpression(assign.Value);
                        Emit(compoundOp!.Value, 0, 0, 0, pop: 2, push: 1);
                        var valueSlot = DeclareLocal("$value", isConst: true);
                        EmitStoreLocal(valueSlot);
                        EmitLoadLocal(receiverSlot);
                        EmitLoadLocal(valueSlot);
                    }

                    Emit(Op.StoreField, AddStringConstant(member.Name), 0, 0, pop: 2, push: 1);
                    PopScope();
                    return;
                }

                if (assign.Target is IndexExpr index)
                {
                    if (simple && IsJumpFree(index.Target) && IsJumpFree(index.Index) &&
                        IsJumpFree(assign.Value))
                    {
                        CompileExpression(index.Target);
                        CompileExpression(index.Index);
                        CompileExpression(assign.Value);
                        Emit(Op.StoreIndex, 0, 0, 0, pop: 3, push: 1);
                        return;
                    }

                    PushScope();
                    CompileExpression(index.Target);
                    var targetSlot = DeclareLocal("$target", isConst: true);
                    EmitStoreLocal(targetSlot);

                    CompileExpression(index.Index);
                    var indexSlot = DeclareLocal("$index", isConst: true);
                    EmitStoreLocal(indexSlot);

                    if (simple)
                    {
                        EmitLoadLocal(targetSlot);
                        EmitLoadLocal(indexSlot);
                        CompileExpression(assign.Value);
                    }
                    else
                    {
                        EmitLoadLocal(targetSlot);
                        EmitLoadLocal(indexSlot);
                        Emit(Op.LoadIndex, 0, 0, 0, pop: 2, push: 1);
                        CompileExpression(assign.Value);
                        Emit(compoundOp!.Value, 0, 0, 0, pop: 2, push: 1);
                        var valueSlot = DeclareLocal("$value", isConst: true);
                        EmitStoreLocal(valueSlot);
                        EmitLoadLocal(targetSlot);
                        EmitLoadLocal(indexSlot);
                        EmitLoadLocal(valueSlot);
                    }

                    Emit(Op.StoreIndex, 0, 0, 0, pop: 3, push: 1);
                    PopScope();
                    return;
                }

                Module.Error("TRN0210", "invalid assignment target", assign);
                CompileExpression(assign.Value);
            }

            /// <summary>
            /// The three assignment operators that are a test first: <c>x ??= v</c>
            /// gives v to x only when x is nil, <c>||=</c> only when it is falsy, and
            /// <c>&&=</c> only when it is truthy. What makes them worth their own
            /// path is that the value is not evaluated at all when the test passes -
            /// <c>m[k] ??= expensive()</c> is one call, not one call per miss plus
            /// one - so they cannot be a load, an operator and a store.
            /// </summary>
            private static bool IsShortCircuitAssign(TokenKind kind) =>
                kind is TokenKind.QuestionQuestionEqual or TokenKind.PipePipeEqual or TokenKind.AmpAmpEqual;

            /// <summary>The test that decides whether the value is wanted.</summary>
            private static Op ShortCircuitTest(TokenKind kind) => kind switch
            {
                TokenKind.QuestionQuestionEqual => Op.JumpIfNotNullKeep,
                TokenKind.PipePipeEqual => Op.JumpIfTrueKeep,
                _ => Op.JumpIfFalseKeep,
            };

            /// <summary>
            /// <c>target op= value</c>, compiling to: read the target, jump past the
            /// value when the test says the value is not wanted, otherwise evaluate
            /// it and store it. Both paths end with the target's new value on the
            /// stack, because that is what the expression is worth - and the caller
            /// pops it when the assignment was a statement.
            /// </summary>
            private void CompileShortCircuitAssign(AssignExpr assign, bool keepValue)
            {
                var test = ShortCircuitTest(assign.Operator);

                void Finish()
                {
                    if (!keepValue)
                    {
                        Emit(Op.Pop, 0, 0, 0, pop: 1, push: 0);
                    }
                }

                if (assign.Target is IdentifierExpr identifier)
                {
                    if (TryResolveLocal(identifier.Name, out var local))
                    {
                        if (local.IsConst)
                        {
                            Module.Error("TRN0209", $"cannot assign to constant '{identifier.Name}'", assign);
                        }

                        EmitLoadLocal(local.Slot);
                        var done = Emit(test, 0, 0, 0, pop: 0, push: 0);
                        Emit(Op.Pop, 0, 0, 0, pop: 1, push: 0);
                        CompileExpression(assign.Value);
                        EmitStoreLocal(local.Slot);
                        EmitLoadLocal(local.Slot);
                        PatchTo(done, _code.Count);
                        Finish();
                        return;
                    }

                    var upvalue = ResolveUpvalue(identifier.Name);
                    if (upvalue >= 0)
                    {
                        Emit(Op.LoadUpvalue, upvalue, 0, 0, pop: 0, push: 1);
                        var done = Emit(test, 0, 0, 0, pop: 0, push: 0);
                        Emit(Op.Pop, 0, 0, 0, pop: 1, push: 0);
                        CompileExpression(assign.Value);
                        Emit(Op.StoreUpvalue, upvalue, 0, 0, pop: 1, push: 0);
                        Emit(Op.LoadUpvalue, upvalue, 0, 0, pop: 0, push: 1);
                        PatchTo(done, _code.Count);
                        Finish();
                        return;
                    }

                    if (_scope.TryResolve(identifier.Name, out var globalSlot))
                    {
                        if (_scope.IsConst(globalSlot))
                        {
                            Module.Error("TRN0209", $"cannot assign to constant '{identifier.Name}'", assign);
                        }

                        Emit(Op.LoadGlobal, globalSlot, 0, 0, pop: 0, push: 1);
                        var done = Emit(test, 0, 0, 0, pop: 0, push: 0);
                        Emit(Op.Pop, 0, 0, 0, pop: 1, push: 0);
                        CompileExpression(assign.Value);
                        Emit(Op.StoreGlobal, globalSlot, 0, 0, pop: 1, push: 0);
                        Emit(Op.LoadGlobal, globalSlot, 0, 0, pop: 0, push: 1);
                        PatchTo(done, _code.Count);
                        Finish();
                        return;
                    }

                    Module.Error("TRN0206", $"undefined variable '{identifier.Name}'", assign);
                    EmitLoadConstant(Value.Nil);
                    Finish();
                    return;
                }

                // A member or an index is evaluated once into temporaries: the
                // receiver must not run twice when the value is wanted, and must
                // still not run at all when it is not.
                PushScope();
                if (assign.Target is MemberExpr member)
                {
                    CompileExpression(member.Target);
                    var receiverSlot = DeclareLocal("$recv", isConst: true);
                    EmitStoreLocal(receiverSlot);

                    EmitLoadLocal(receiverSlot);
                    Emit(Op.LoadField, AddStringConstant(member.Name), 0, 0, pop: 1, push: 1);
                    var done = Emit(test, 0, 0, 0, pop: 0, push: 0);
                    Emit(Op.Pop, 0, 0, 0, pop: 1, push: 0);
                    EmitLoadLocal(receiverSlot);
                    CompileExpression(assign.Value);
                    // StoreField leaves the stored value on the stack, which is the
                    // value of the expression.
                    Emit(Op.StoreField, AddStringConstant(member.Name), 0, 0, pop: 2, push: 1);
                    PatchTo(done, _code.Count);
                    PopScope();
                    Finish();
                    return;
                }

                if (assign.Target is IndexExpr index)
                {
                    CompileExpression(index.Target);
                    var targetSlot = DeclareLocal("$target", isConst: true);
                    EmitStoreLocal(targetSlot);

                    CompileExpression(index.Index);
                    var indexSlot = DeclareLocal("$index", isConst: true);
                    EmitStoreLocal(indexSlot);

                    EmitLoadLocal(targetSlot);
                    EmitLoadLocal(indexSlot);
                    Emit(Op.LoadIndex, 0, 0, 0, pop: 2, push: 1);
                    var done = Emit(test, 0, 0, 0, pop: 0, push: 0);
                    Emit(Op.Pop, 0, 0, 0, pop: 1, push: 0);
                    EmitLoadLocal(targetSlot);
                    EmitLoadLocal(indexSlot);
                    CompileExpression(assign.Value);
                    Emit(Op.StoreIndex, 0, 0, 0, pop: 3, push: 1);
                    PatchTo(done, _code.Count);
                    PopScope();
                    Finish();
                    return;
                }

                PopScope();
                Module.Error("TRN0210", "invalid assignment target", assign);
                EmitLoadConstant(Value.Nil);
                Finish();
            }

            private static Op? CompoundOpcode(TokenKind kind) => kind switch
            {
                TokenKind.PlusEqual => Op.Add,
                TokenKind.MinusEqual => Op.Sub,
                TokenKind.StarEqual => Op.Mul,
                TokenKind.SlashEqual => Op.Div,
                TokenKind.PercentEqual => Op.Mod,
                TokenKind.StarStarEqual => Op.Pow,
                TokenKind.AmpEqual => Op.BAnd,
                TokenKind.PipeEqual => Op.BOr,
                TokenKind.CaretEqual => Op.BXor,
                TokenKind.ShiftLeftEqual => Op.Shl,
                TokenKind.ShiftRightEqual => Op.Shr,
                _ => null,
            };

            private void CompileIncrement(IncrementExpr increment, bool keepValue = true)
            {
                if (increment.Target is IdentifierExpr identifier)
                {
                    if (TryResolveLocal(identifier.Name, out var local))
                    {
                        if (!increment.Prefix && keepValue)
                        {
                            EmitLoadLocal(local.Slot);
                        }

                        Emit(Op.IncLocal, local.Slot, increment.Delta, 0, pop: 0, push: 0);

                        if (increment.Prefix && keepValue)
                        {
                            EmitLoadLocal(local.Slot);
                        }

                        return;
                    }

                    var upvalue = ResolveUpvalue(identifier.Name);
                    if (upvalue >= 0)
                    {
                        if (!increment.Prefix && keepValue)
                        {
                            Emit(Op.LoadUpvalue, upvalue, 0, 0, pop: 0, push: 1);
                        }

                        Emit(Op.LoadUpvalue, upvalue, 0, 0, pop: 0, push: 1);
                        Emit(Op.LoadInt, increment.Delta, 0, 0, pop: 0, push: 1);
                        Emit(Op.Add, 0, 0, 0, pop: 2, push: 1);
                        Emit(Op.StoreUpvalue, upvalue, 0, 0, pop: 1, push: 0);

                        if (increment.Prefix && keepValue)
                        {
                            Emit(Op.LoadUpvalue, upvalue, 0, 0, pop: 0, push: 1);
                        }

                        return;
                    }

                    if (_scope.TryResolve(identifier.Name, out var globalSlot))
                    {
                        if (!increment.Prefix && keepValue)
                        {
                            Emit(Op.LoadGlobal, globalSlot, 0, 0, pop: 0, push: 1);
                        }

                        Emit(Op.LoadGlobal, globalSlot, 0, 0, pop: 0, push: 1);
                        Emit(Op.LoadInt, increment.Delta, 0, 0, pop: 0, push: 1);
                        Emit(Op.Add, 0, 0, 0, pop: 2, push: 1);
                        Emit(Op.StoreGlobal, globalSlot, 0, 0, pop: 1, push: 0);

                        if (increment.Prefix && keepValue)
                        {
                            Emit(Op.LoadGlobal, globalSlot, 0, 0, pop: 0, push: 1);
                        }

                        return;
                    }
                }

                Module.Error("TRN0211", "increment requires a variable", increment);
                EmitLoadConstant(Value.Nil);
            }

            private void CompileFunctionLiteral(FunctionLiteral literal, out FunctionProto prototype, bool isMethod = false)
            {
                var child = new FunctionCompiler(Module, this, literal.Name ?? "<lambda>", _scope, _sourceName, _path,
                    isMethod, isTopLevel: false);

                if (literal.Parameters.Count > 0 && literal.Parameters[0].IsRest)
                {
                    // handled by DeclareParameters
                }

                child.DeclareParameters(literal.Parameters, declareThis: isMethod);

                child.PushScope();
                child.EmitParameterPrologue(literal.Parameters, declaresThis: isMethod);

                if (literal.ExpressionBody is not null)
                {
                    child.CompileExpression(literal.ExpressionBody);
                    child.Emit(Op.Return, 0, 0, 0, pop: 1, push: 0);
                }
                else if (literal.Body is not null)
                {
                    foreach (var statement in literal.Body.Statements)
                    {
                        child.CompileStatement(statement);
                    }
                }

                child.PopScope();
                prototype = child.Build();

                _prototypes.Add(prototype);
                var protoIndex = _prototypes.Count - 1;
                Emit(Op.MakeClosure, protoIndex, 0, 0, pop: 0, push: 1);
            }

            // --------------------------------------------------------------- match

            private void CompileMatch(MatchExpr match)
            {
                var entry = _depth;

                PushScope();
                CompileExpression(match.Subject);
                var subjectSlot = DeclareLocal("$match", isConst: true);
                EmitStoreLocal(subjectSlot);

                var endJumps = new List<int>();
                var exhaustive = false;

                foreach (var arm in match.Arms)
                {
                    if (exhaustive)
                    {
                        break;
                    }

                    var failJumps = new List<int>();
                    var catchAll = (arm.Pattern is WildcardPattern || arm.Pattern is BindingPattern) && arm.Guard is null;

                    EmitPatternTest(arm.Pattern, subjectSlot, failJumps);

                    PushScope();
                    BindPatternVariables(arm.Pattern, subjectSlot, globals: false);

                    if (arm.Guard is not null)
                    {
                        failJumps.Add(EmitConditionJump(arm.Guard, jumpWhenFalse: true));
                    }

                    CompileBranchValue(ResolveArmBody(arm), match);
                    // The arm's value stays where it is: a jump only moves the
                    // instruction pointer, so the value the arm produced is the
                    // value of the whole match at the label below.
                    endJumps.Add(Emit(Op.Jump, 0, 0, 0, pop: 0, push: 0));
                    PopScope();

                    foreach (var jump in failJumps)
                    {
                        PatchTo(jump, _code.Count);
                    }

                    if (catchAll)
                    {
                        exhaustive = true;
                    }
                }

                if (!exhaustive)
                {
                    EmitLoadConstant(Value.Nil);
                }

                foreach (var jump in endJumps)
                {
                    PatchTo(jump, _code.Count);
                }

                // Exhaustive or not, exactly one value is here now.
                NoteDepth(entry + 1);

                PopScope();
            }

            private static Stmt ResolveArmBody(MatchArm arm)
            {
                if (arm.Body is not null)
                {
                    return arm.Body;
                }

                if (arm.ExpressionBody is not null)
                {
                    return new ExpressionStmt
                    {
                        Expression = arm.ExpressionBody,
                        Offset = arm.ExpressionBody.Offset,
                        Line = arm.ExpressionBody.Line,
                    };
                }

                return new EmptyStmt { Offset = arm.Offset };
            }

            private void EmitPatternTest(Pattern pattern, int subjectSlot, List<int> failJumps)
            {
                switch (pattern)
                {
                    case WildcardPattern:
                        return;

                    case BindingPattern:
                        return;

                    case LiteralPattern literal:
                    {
                        EmitLoadLocal(subjectSlot);
                        EmitLoadConstant(ToValue(literal.Value));
                        Emit(Op.Eq, 0, 0, 0, pop: 2, push: 1);
                        failJumps.Add(Emit(Op.JumpIfFalse, 0, 0, 0, pop: 1, push: 0));
                        return;
                    }

                    case RangePattern range:
                    {
                        // A range arm is a numeric test, so a non-number subject must
                        // fail the arm rather than raise "cannot compare ...".
                        failJumps.Add(Emit(Op.JumpIfNotNumber, subjectSlot, 0, 0, pop: 0, push: 0));

                        EmitLoadLocal(subjectSlot);
                        CompileExpression(range.Start);
                        failJumps.Add(Emit(Op.JumpCmp, (int)CompareCode.Ge, 0, 0, pop: 2, push: 0));

                        EmitLoadLocal(subjectSlot);
                        CompileExpression(range.End);
                        failJumps.Add(Emit(Op.JumpCmp, range.Inclusive ? (int)CompareCode.Le : (int)CompareCode.Lt, 0, 0, pop: 2, push: 0));
                        return;
                    }

                    case OrPattern or:
                    {
                        var successJumps = new List<int>();

                        for (var i = 0; i < or.Alternatives.Count; i++)
                        {
                            var innerFails = new List<int>();
                            EmitPatternTest(or.Alternatives[i], subjectSlot, innerFails);

                            if (i == or.Alternatives.Count - 1)
                            {
                                // Last chance: a failure means the whole pattern failed.
                                failJumps.AddRange(innerFails);
                                break;
                            }

                            successJumps.Add(Emit(Op.Jump, 0, 0, 0, pop: 0, push: 0));
                            foreach (var jump in innerFails)
                            {
                                PatchTo(jump, _code.Count);
                            }
                        }

                        foreach (var jump in successJumps)
                        {
                            PatchTo(jump, _code.Count);
                        }

                        return;
                    }

                    case MapPattern mapPattern:
                    {
                        // Subset match: the subject must be a map, every named key must
                        // be present, and its value must match the entry's pattern.
                        failJumps.Add(Emit(Op.JumpIfNotMap, subjectSlot, 0, 0, pop: 0, push: 0));

                        // One temporary for the whole pattern: each entry is extracted,
                        // tested and dropped before the next one is read.
                        var valueSlot = ReserveSlot();

                        foreach (var entry in mapPattern.Entries)
                        {
                            var keyConstant = AddStringConstant(entry.Key);

                            // `key in map` pushes the item first, then the container.
                            Emit(Op.LoadConst, keyConstant, 0, 0, pop: 0, push: 1);
                            EmitLoadLocal(subjectSlot);
                            Emit(Op.In, 0, 0, 0, pop: 2, push: 1);
                            failJumps.Add(Emit(Op.JumpIfFalse, 0, 0, 0, pop: 1, push: 0));

                            EmitLoadLocal(subjectSlot);
                            Emit(Op.LoadConst, keyConstant, 0, 0, pop: 0, push: 1);
                            Emit(Op.LoadIndex, 0, 0, 0, pop: 2, push: 1);
                            EmitStoreLocalPlain(valueSlot);

                            EmitPatternTest(entry.Pattern, valueSlot, failJumps);
                        }

                        ReleaseSlot(valueSlot);
                        return;
                    }

                    case ListPattern listPattern:
                    {
                        // Subject must be a list of the right length, then each element is tested.
                        var fail = Emit(Op.JumpIfNotList, subjectSlot, 0, 0, pop: 0, push: 0);
                        failJumps.Add(fail);

                        if (listPattern.RestName is null)
                        {
                            var lengthConst = AddConstant(new Value(listPattern.Elements.Count));
                            EmitLoadLocal(subjectSlot);
                            Emit(Op.LoadField, AddStringConstant("length"), 0, 0, pop: 1, push: 1);
                            Emit(Op.LoadConst, lengthConst, 0, 0, pop: 0, push: 1);
                            Emit(Op.Ne, 0, 0, 0, pop: 2, push: 1);
                            failJumps.Add(Emit(Op.JumpIfTrue, 0, 0, 0, pop: 1, push: 0));
                        }

                        if (listPattern.Elements.Count > 0)
                        {
                            var tempSlot = ReserveSlot();

                            for (var i = 0; i < listPattern.Elements.Count; i++)
                            {
                                var element = listPattern.Elements[i];
                                EmitLoadLocal(subjectSlot);
                                Emit(Op.LoadInt, i, 0, 0, pop: 0, push: 1);
                                Emit(Op.LoadIndex, 0, 0, 0, pop: 2, push: 1);
                                EmitStoreLocalPlain(tempSlot);

                                var innerFails = new List<int>();
                                EmitPatternTest(element, tempSlot, innerFails);
                                failJumps.AddRange(innerFails);
                            }

                            ReleaseSlot(tempSlot);
                        }

                        return;
                    }
                }
            }

            /// <summary>
            /// Stores the subject into the slots a pattern names, in the order the
            /// pattern writes them. <paramref name="globals"/> decides where the names
            /// live: a top-level declaration binds module globals, like any other
            /// top-level <c>let</c>, so an importer and a later function see them.
            /// </summary>
            private void BindPatternVariables(Pattern pattern, int subjectSlot, bool globals, bool isConst = true)
            {
                switch (pattern)
                {
                    case BindingPattern binding:
                    {
                        var slot = DeclareBinding(binding.Name, globals, isConst);
                        EmitLoadLocal(subjectSlot);
                        EmitStoreInto(slot, globals);
                        return;
                    }

                    case OrPattern or:
                        foreach (var alternative in or.Alternatives)
                        {
                            BindPatternVariables(alternative, subjectSlot, globals);
                        }

                        return;

                    case MapPattern mapPattern:
                        foreach (var entry in mapPattern.Entries)
                        {
                            var keyConstant = AddStringConstant(entry.Key);

                            if (entry.Pattern is BindingPattern mapBinding)
                            {
                                var slot = DeclareBinding(mapBinding.Name, globals, isConst);
                                EmitLoadLocal(subjectSlot);
                                Emit(Op.LoadConst, keyConstant, 0, 0, pop: 0, push: 1);
                                Emit(Op.LoadIndex, 0, 0, 0, pop: 2, push: 1);
                                EmitStoreInto(slot, globals);
                                continue;
                            }

                            // Only a pattern that binds something is worth the read.
                            if (Patterns.Names(entry.Pattern).Count == 0)
                            {
                                continue;
                            }

                            var nested = ReserveSlot();
                            EmitLoadLocal(subjectSlot);
                            Emit(Op.LoadConst, keyConstant, 0, 0, pop: 0, push: 1);
                            Emit(Op.LoadIndex, 0, 0, 0, pop: 2, push: 1);
                            EmitStoreLocalPlain(nested);
                            BindPatternVariables(entry.Pattern, nested, globals, isConst);
                            ReleaseSlot(nested);
                        }

                        return;

                    case ListPattern listPattern:
                        for (var i = 0; i < listPattern.Elements.Count; i++)
                        {
                            var element = listPattern.Elements[i];
                            if (element is BindingPattern elementBinding)
                            {
                                var slot = DeclareBinding(elementBinding.Name, globals, isConst);
                                EmitLoadLocal(subjectSlot);
                                Emit(Op.LoadInt, i, 0, 0, pop: 0, push: 1);
                                Emit(Op.LoadIndex, 0, 0, 0, pop: 2, push: 1);
                                EmitStoreInto(slot, globals);
                                continue;
                            }

                            // Nested patterns bind too: pull the element into a
                            // temporary and recurse, so `[[a, b], [c, d]]` declares
                            // all four names instead of only matching structurally.
                            if (Patterns.Names(element).Count == 0)
                            {
                                continue;
                            }

                            var nested = ReserveSlot();
                            EmitLoadLocal(subjectSlot);
                            Emit(Op.LoadInt, i, 0, 0, pop: 0, push: 1);
                            Emit(Op.LoadIndex, 0, 0, 0, pop: 2, push: 1);
                            EmitStoreLocalPlain(nested);
                            BindPatternVariables(element, nested, globals, isConst);
                            ReleaseSlot(nested);
                        }

                        if (listPattern.RestName is not null)
                        {
                            // `...rest` is a slice from the first unbound index to the
                            // end: indexing needs a single range value, not two integers.
                            var slot = DeclareBinding(listPattern.RestName, globals, isConst);
                            EmitLoadLocal(subjectSlot);
                            Emit(Op.LoadInt, listPattern.Elements.Count, 0, 0, pop: 0, push: 1);
                            EmitLoadLocal(subjectSlot);
                            Emit(Op.Length, AddStringConstant("length"), 0, 0, pop: 1, push: 1);
                            Emit(Op.LoadInt, 1, 0, 0, pop: 0, push: 1);
                            Emit(Op.Sub, 0, 0, 0, pop: 2, push: 1);
                            Emit(Op.NewRange, 1, 0, 0, pop: 2, push: 1);
                            Emit(Op.LoadIndex, 0, 0, 0, pop: 2, push: 1);
                            EmitStoreInto(slot, globals);
                        }

                        return;
                }
            }

            private int DeclareBinding(string name, bool globals, bool isConst = true) =>
                globals ? _scope.Declare(name, isConst, exported: true) : DeclareLocal(name, isConst);

            private void EmitStoreInto(int slot, bool globals)
            {
                if (globals)
                {
                    Emit(Op.StoreGlobal, slot, 0, 0, pop: 1, push: 0);
                    return;
                }

                EmitStoreLocal(slot);
            }

            // ------------------------------------------------------ constant folding

            private static Value ToValue(object? literal)
            {
                return literal switch
                {
                    null => Value.Nil,
                    bool b => Value.Bool(b),
                    int i => new Value(i),
                    long l => new Value(l),
                    double d => new Value(d),
                    string s => Value.Str(s),
                    Value v => v,
                    _ => Value.Nil,
                };
            }

            private bool TryEvaluateConstant(Expr expression, out Value value)
            {
                switch (expression)
                {
                    case LiteralExpr literal:
                        value = ToValue(literal.Value);
                        return true;

                    case UnaryExpr { Operator: TokenKind.Minus } negate when TryEvaluateConstant(negate.Operand, out var inner):
                        value = inner.Kind switch
                        {
                            ValueKind.Int => new Value(-inner.AsInt()),
                            ValueKind.Float => new Value(-inner.AsDouble()),
                            _ => Value.Nil,
                        };
                        return inner.IsNumber;

                    case UnaryExpr { Operator: TokenKind.Bang or TokenKind.Not } not when TryEvaluateConstant(not.Operand, out var operand):
                        value = Value.Bool(!operand.Truthy);
                        return true;

                    case UnaryExpr { Operator: TokenKind.Tilde } complement when TryEvaluateConstant(complement.Operand, out var operandValue):
                        if (operandValue.Kind == ValueKind.Int)
                        {
                            value = new Value(~operandValue.AsInt());
                            return true;
                        }

                        break;

                    case BinaryExpr binary when TryEvaluateConstant(binary.Left, out var left) &&
                                                 TryEvaluateConstant(binary.Right, out var right):
                        if (TryFoldBinary(binary.Operator, left, right, out value))
                        {
                            return true;
                        }

                        break;
                }

                value = Value.Nil;
                return false;
            }

            private static bool TryFoldBinary(TokenKind op, in Value left, in Value right, out Value value)
            {
                value = Value.Nil;

                switch (op)
                {
                    case TokenKind.Plus when left.Kind == ValueKind.Str && right.Kind == ValueKind.Str:
                        value = Value.Str(left.AsString() + right.AsString());
                        return true;
                    // Folding must agree with the runtime, which promotes an
                    // out-of-range result to a float rather than wrapping.
                    case TokenKind.Plus when left.Kind == ValueKind.Int && right.Kind == ValueKind.Int:
                    {
                        var a = left.AsInt();
                        var b = right.AsInt();
                        var sum = unchecked(a + b);
                        value = ((a ^ sum) & (b ^ sum)) >= 0 ? new Value(sum) : new Value((double)a + b);
                        return true;
                    }
                    case TokenKind.Plus when left.IsNumber && right.IsNumber:
                        value = new Value(left.AsDouble() + right.AsDouble());
                        return true;
                    case TokenKind.Minus when left.Kind == ValueKind.Int && right.Kind == ValueKind.Int:
                    {
                        var a = left.AsInt();
                        var b = right.AsInt();
                        var difference = unchecked(a - b);
                        value = ((a ^ b) & (a ^ difference)) >= 0
                            ? new Value(difference)
                            : new Value((double)a - b);
                        return true;
                    }
                    case TokenKind.Minus when left.IsNumber && right.IsNumber:
                        value = new Value(left.AsDouble() - right.AsDouble());
                        return true;
                    case TokenKind.Star when left.Kind == ValueKind.Int && right.Kind == ValueKind.Int:
                    {
                        var a = left.AsInt();
                        var b = right.AsInt();
                        var product = unchecked(a * b);
                        var fits = a == 0 || b == 0 || (!(a == -1 && b == long.MinValue) && product / a == b);
                        value = fits ? new Value(product) : new Value((double)a * b);
                        return true;
                    }
                    case TokenKind.Star when left.IsNumber && right.IsNumber:
                        value = new Value(left.AsDouble() * right.AsDouble());
                        return true;
                    case TokenKind.Slash when left.Kind == ValueKind.Int && right.Kind == ValueKind.Int && right.AsInt() != 0:
                    {
                        var a = left.AsInt();
                        var b = right.AsInt();

                        // long.MinValue / -1 overflows the integer range, and the
                        // hardware divide traps: the runtime answers with a float,
                        // so folding must do the same.
                        if (b == -1)
                        {
                            value = a == long.MinValue ? new Value(-(double)a) : new Value(-a);
                            return true;
                        }

                        // Exact integer division stays an integer; otherwise promote to float.
                        value = a % b == 0 ? new Value(a / b) : new Value((double)a / b);
                        return true;
                    }
                    case TokenKind.Slash when left.IsNumber && right.IsNumber && right.AsDouble() != 0:
                        value = new Value(left.AsDouble() / right.AsDouble());
                        return true;
                    case TokenKind.Percent when left.Kind == ValueKind.Int && right.Kind == ValueKind.Int && right.AsInt() != 0:
                    {
                        var a = left.AsInt();
                        var b = right.AsInt();
                        // long.MinValue % -1 traps too; the result is 0.
                        value = new Value(b == -1 ? 0 : a % b);
                        return true;
                    }
                    case TokenKind.StarStar when left.Kind == ValueKind.Int && right.Kind == ValueKind.Int &&
                                                 right.AsInt() >= 0 && right.AsInt() <= 62:
                    {
                        long result = 1;
                        var baseValue = left.AsInt();
                        var exponent = right.AsInt();
                        while (exponent > 0)
                        {
                            if ((exponent & 1) != 0)
                            {
                                result = unchecked(result * baseValue);
                            }

                            baseValue = unchecked(baseValue * baseValue);
                            exponent >>= 1;
                        }

                        value = new Value(result);
                        return true;
                    }
                    case TokenKind.StarStar when left.IsNumber && right.IsNumber:
                        value = new Value(Math.Pow(left.AsDouble(), right.AsDouble()));
                        return true;
                    case TokenKind.Amp when left.Kind == ValueKind.Int && right.Kind == ValueKind.Int:
                        value = new Value(left.AsInt() & right.AsInt());
                        return true;
                    case TokenKind.Pipe when left.Kind == ValueKind.Int && right.Kind == ValueKind.Int:
                        value = new Value(left.AsInt() | right.AsInt());
                        return true;
                    case TokenKind.Caret when left.Kind == ValueKind.Int && right.Kind == ValueKind.Int:
                        value = new Value(left.AsInt() ^ right.AsInt());
                        return true;
                    case TokenKind.ShiftLeft when left.Kind == ValueKind.Int && right.Kind == ValueKind.Int:
                        value = new Value(left.AsInt() << (int)(right.AsInt() & 63));
                        return true;
                    case TokenKind.ShiftRight when left.Kind == ValueKind.Int && right.Kind == ValueKind.Int:
                        value = new Value(left.AsInt() >> (int)(right.AsInt() & 63));
                        return true;
                    case TokenKind.EqualEqual:
                        value = Value.Bool(left.Equals(right));
                        return true;
                    case TokenKind.BangEqual:
                        value = Value.Bool(!left.Equals(right));
                        return true;
                    case TokenKind.Less when Value.TryCompare(left, right, out var compare):
                        value = Value.Bool(compare < 0);
                        return true;
                    case TokenKind.LessEqual when Value.TryCompare(left, right, out var compare):
                        value = Value.Bool(compare <= 0);
                        return true;
                    case TokenKind.Greater when Value.TryCompare(left, right, out var compare):
                        value = Value.Bool(compare > 0);
                        return true;
                    case TokenKind.In when TryFoldMembership(left, right, out var contained):
                        value = Value.Bool(contained);
                        return true;
                    case TokenKind.GreaterEqual when Value.TryCompare(left, right, out var compare):
                        value = Value.Bool(compare >= 0);
                        return true;
                    case TokenKind.AmpAmp:
                    case TokenKind.And:
                        value = Value.Bool(left.Truthy && right.Truthy);
                        return true;
                    case TokenKind.PipePipe:
                    case TokenKind.Or:
                        value = Value.Bool(left.Truthy || right.Truthy);
                        return true;
                    case TokenKind.QuestionQuestion:
                        value = left.IsNil ? right : left;
                        return true;
                }

                return false;
            }
        }
    }
}
