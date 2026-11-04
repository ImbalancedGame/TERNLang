using System;
using System.Collections.Generic;
using System.Linq;
using TERN.Syntax;

namespace TERN.Ide
{
    public enum SymbolKind
    {
        Function,
        Struct,
        Variable,
        Constant,
        Parameter,
        Field,
        Method,
        Import,
    }

    /// <summary>A declaration the editor can navigate to and show in the structure view.</summary>
    public sealed class Symbol
    {
        public required string Name { get; init; }
        public required SymbolKind Kind { get; init; }
        public required int Offset { get; init; }
        public required int Line { get; init; }
        public required string Detail { get; init; }

        /// <summary>Nesting depth, for the structure view's tree.</summary>
        public int Depth { get; init; }

        /// <summary>Parameter names, in order, for signature help.</summary>
        public string[] Parameters { get; init; } = Array.Empty<string>();

        /// <summary>The struct a field or method belongs to, if it is one.</summary>
        public string? Owner { get; init; }

        public string KindName => Kind switch
        {
            SymbolKind.Function => "function",
            SymbolKind.Struct => "struct",
            SymbolKind.Constant => "constant",
            SymbolKind.Parameter => "parameter",
            SymbolKind.Field => "field",
            SymbolKind.Method => "method",
            SymbolKind.Import => "import",
            _ => "variable",
        };

        public int LspKind => Kind switch
        {
            SymbolKind.Function => 12,
            SymbolKind.Method => 6,
            SymbolKind.Struct => 23,
            SymbolKind.Constant => 14,
            SymbolKind.Parameter => 13,
            SymbolKind.Field => 8,
            SymbolKind.Import => 2,
            _ => 13,
        };
    }

    /// <summary>
    /// The declarations in one file, and enough type knowledge to complete a
    /// member access.
    ///
    /// The full compiler does not expose a symbol table, and a language server
    /// does not need one: it needs to answer "what is declared here" and "what
    /// could follow this dot", quickly, on a source buffer that is mid-edit. So
    /// this walks the AST that the parser already produced and, for a receiver,
    /// records the struct or namespace an identifier was initialised from.
    ///
    /// The type knowledge comes from the initializer: a literal, a list or map
    /// literal, a range, a struct constructor and a call whose reference page
    /// names its return type are all understood, and a name assigned a second
    /// type elsewhere is not. A wrong answer is worse than no answer for
    /// completion, so anything uncertain returns <see cref="ReceiverType.Unknown"/>
    /// and the editor falls back to the general list.
    /// </summary>
    public sealed class SymbolIndex
    {
        private readonly Dictionary<string, Symbol> _declarations = new(StringComparer.Ordinal);

        /// <summary>The expression each `let`/`const` was initialised with.</summary>
        private readonly Dictionary<string, Expr> _initializers = new(StringComparer.Ordinal);

        /// <summary>Bindings whose type comes from the loop rather than an initializer.</summary>
        private readonly Dictionary<string, ReceiverType> _directTypes = new(StringComparer.Ordinal);

        /// <summary>Values assigned to a name after its declaration, which may change its type.</summary>
        private readonly Dictionary<string, List<Expr>> _assignments = new(StringComparer.Ordinal);

        public List<Symbol> Symbols { get; } = new();

        /// <summary>The declaration with this name, innermost first, if any.</summary>
        public Symbol? Find(string name)
        {
            for (var index = Symbols.Count - 1; index >= 0; index--)
            {
                if (string.Equals(Symbols[index].Name, name, StringComparison.Ordinal))
                {
                    return Symbols[index];
                }
            }

            return null;
        }

        public HashSet<string> Structs { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Parameter annotations met during the walk, by parameter name. A struct
        /// may be declared after the function that names it, so these wait until
        /// the walk has seen the whole file before becoming types.
        /// </summary>
        private readonly Dictionary<string, HashSet<string>> _annotations = new(StringComparer.Ordinal);

        public HashSet<string> Imports { get; } = new(StringComparer.Ordinal);

        public static SymbolIndex Build(Document document)
        {
            var index = new SymbolIndex();
            if (document.Program is not null)
            {
                index.Walk(document.Program, 0);
            }

            index.ResolveAnnotations();
            return index;
        }

        /// <summary>The declaration of `name`, preferring one before `offset`.</summary>
        public Symbol? Resolve(string name, int offset)
        {
            if (!_declarations.TryGetValue(name, out var symbol))
            {
                return null;
            }

            return symbol;
        }

        /// <summary>
        /// The struct whose method contains <paramref name="offset"/>, if any.
        ///
        /// The index has no ranges, only declaration offsets, so "inside" is
        /// taken to mean "after the method's declaration and before the next
        /// member's": locals are not members, and a member's body cannot
        /// outlive the next member's header.
        /// </summary>
        public string? ThisAt(int offset)
        {
            Symbol? nearest = null;
            foreach (var symbol in Symbols)
            {
                if (symbol.Kind is SymbolKind.Method or SymbolKind.Field or SymbolKind.Struct or SymbolKind.Function)
                {
                    if (symbol.Offset <= offset && (nearest is null || symbol.Offset > nearest.Offset))
                    {
                        nearest = symbol;
                    }
                }
            }

            return nearest?.Owner;
        }

        /// <summary>True when <paramref name="name"/> is a struct declared in this file.</summary>
        public bool IsStruct(string name) => Structs.Contains(name);

        /// <summary>
        /// The type of a binding, from its declaration and the calls it was given.
        /// Unknown when the declaration says nothing, or when a later assignment
        /// gives it a different type: `let x = 1; x = "text";` is a string and an
        /// integer at once, and completion has to offer neither's members.
        /// </summary>
        public ReceiverType TypeOf(string name, HashSet<string> namespaces, int offset = -1, int depth = 0)
        {
            if (depth > TypeInference.MaxDepth)
            {
                return ReceiverType.Unknown;
            }

            if (_directTypes.TryGetValue(name, out var direct))
            {
                return direct;
            }

            // Completion reaches a receiver by name, so `this.` arrives here
            // rather than as a ThisExpr.
            if (name == "this")
            {
                return ThisAt(offset) is { } owner ? ReceiverType.OfStruct(owner) : ReceiverType.Unknown;
            }

            var type = _initializers.TryGetValue(name, out var initializer)
                ? TypeInference.Of(initializer, this, namespaces, offset, depth + 1)
                : ReceiverType.Unknown;

            if (_assignments.TryGetValue(name, out var values))
            {
                foreach (var value in values)
                {
                    var assigned = TypeInference.Of(value, this, namespaces, offset, depth + 1);
                    if (!assigned.IsKnown)
                    {
                        continue;
                    }

                    type = type.IsKnown ? TypeInference.Unify(type, assigned) : assigned;
                }
            }

            return type;
        }

        private void Declare(Symbol symbol)
        {
            Symbols.Add(symbol);
            // First declaration wins for navigation: the top-level one is the
            // most useful target, and later shadowing is rare in a script.
            _declarations.TryAdd(symbol.Name, symbol);
        }

        private void Walk(Stmt statement, int depth)
        {
            switch (statement)
            {
                case BlockStmt block:
                    foreach (var child in block.Statements)
                    {
                        Walk(child, depth);
                    }

                    break;

                case FunctionDeclStmt declared when declared.Function.Name is { } name:
                    Declare(new Symbol
                    {
                        Name = name,
                        Kind = SymbolKind.Function,
                        Offset = declared.Function.Offset,
                        Line = declared.Function.Line,
                        Detail = Signature(declared.Function),
                        Depth = depth,
                        Parameters = ParameterNames(declared.Function),
                    });
                    DeclareParameters(declared.Function, depth + 1);
                    if (declared.Function.Body is not null)
                    {
                        Walk(declared.Function.Body, depth + 1);
                    }

                    break;

                case StructDeclStmt declared:
                    Structs.Add(declared.Name);
                    Declare(new Symbol
                    {
                        Name = declared.Name,
                        Kind = SymbolKind.Struct,
                        Offset = declared.Offset,
                        Line = declared.Line,
                        Detail = $"struct {declared.Name}",
                        Depth = depth,
                        Parameters = declared.Fields.Select(field => field.Name).ToArray(),
                    });
                    foreach (var field in declared.Fields)
                    {
                        Declare(new Symbol
                        {
                            Name = field.Name,
                            Kind = SymbolKind.Field,
                            Offset = field.Offset,
                            Line = LineOf(document: null, field.Offset),
                            Detail = $"{declared.Name}.{field.Name}",
                            Depth = depth + 1,
                            Owner = declared.Name,
                        });
                    }

                    foreach (var method in declared.Methods)
                    {
                        if (method.Name is not { } methodName)
                        {
                            continue;
                        }

                        Declare(new Symbol
                        {
                            Name = methodName,
                            Kind = SymbolKind.Method,
                            Offset = method.Offset,
                            Line = method.Line,
                            Detail = Signature(method),
                            Depth = depth + 1,
                            Parameters = ParameterNames(method),
                            Owner = declared.Name,
                        });
                        DeclareParameters(method, depth + 2);
                        if (method.Body is not null)
                        {
                            Walk(method.Body, depth + 2);
                        }
                    }

                    break;

                case LetStmt let:
                    foreach (var name in let.Names)
                    {
                        Declare(new Symbol
                        {
                            Name = name,
                            Kind = let.IsConst ? SymbolKind.Constant : SymbolKind.Variable,
                            Offset = let.Offset,
                            Line = let.Line,
                            Detail = (let.IsConst ? "const " : "let ") + name,
                            Depth = depth,
                        });
                    }

                    if (let.Name is { } simple && let.Initializer is { } initializer)
                    {
                        _initializers.TryAdd(simple, initializer);
                    }

                    break;

                case ExpressionStmt { Expression: AssignExpr { Target: IdentifierExpr target } assignment }:
                    NoteAssignment(target.Name, assignment.Value);
                    break;

                case IfStmt conditional:
                    Walk(conditional.Then, depth);
                    if (conditional.Else is not null)
                    {
                        Walk(conditional.Else, depth);
                    }

                    break;

                case WhileStmt loop:
                    Walk(loop.Body, depth);
                    break;

                case DoWhileStmt loop:
                    Walk(loop.Body, depth);
                    break;

                case LoopStmt loop:
                    Walk(loop.Body, depth);
                    break;

                case ForStmt counted:
                    if (counted.Initializer is not null)
                    {
                        Walk(counted.Initializer, depth);
                    }

                    Walk(counted.Body, depth);
                    break;

                case ForInStmt each:
                    if (each.Names.Count == 1)
                    {
                        // A loop over text yields text; a loop over a range
                        // yields integers. A list or map yields values of no
                        // declared type, so nothing is claimed about them.
                        var element = ElementOf(each.Iterable) is { } kind
                            ? ReceiverType.Builtin(kind)
                            : (ReceiverType?)null;
                        if (element is { } type && type.IsKnown)
                        {
                            _directTypes[each.Names[0]] = type;
                        }
                    }

                    foreach (var name in each.Names)
                    {
                        Declare(new Symbol
                        {
                            Name = name,
                            Kind = SymbolKind.Variable,
                            Offset = each.Offset,
                            Line = each.Line,
                            Detail = "loop variable",
                            Depth = depth,
                        });
                    }

                    Walk(each.Body, depth);
                    break;

                case TryStmt guarded:
                    Walk(guarded.Body, depth);
                    if (guarded.CatchName is { } caught)
                    {
                        Declare(new Symbol
                        {
                            Name = caught,
                            Kind = SymbolKind.Variable,
                            Offset = guarded.Offset,
                            Line = guarded.Line,
                            Detail = "catch binding",
                            Depth = depth,
                        });
                    }

                    if (guarded.Catch is not null)
                    {
                        Walk(guarded.Catch, depth);
                    }

                    if (guarded.Finally is not null)
                    {
                        Walk(guarded.Finally, depth);
                    }

                    break;

                case ImportStmt import:
                    Imports.Add(import.Alias ?? import.Path);
                    Declare(new Symbol
                    {
                        Name = import.Alias ?? import.Path,
                        Kind = SymbolKind.Import,
                        Offset = import.Offset,
                        Line = import.Line,
                        Detail = $"import \"{import.Path}\"",
                        Depth = depth,
                    });
                    break;
            }
        }

        private static int LineOf(Document? document, int offset) => document?.PositionAt(offset).Line ?? 0;

        private void DeclareParameters(FunctionLiteral function, int depth)
        {
            foreach (var parameter in function.Parameters)
            {
                Declare(new Symbol
                {
                    Name = parameter.Name,
                    Kind = SymbolKind.Parameter,
                    Offset = parameter.Offset,
                    Line = function.Line,
                    Detail = (parameter.IsRest ? "..." : string.Empty) + parameter.Name +
                             (parameter.TypeSpec is null ? string.Empty : ": " + parameter.TypeSpec),
                    Depth = depth,
                });

                if (parameter.TypeSpec is { } spec && !parameter.IsRest)
                {
                    if (!_annotations.TryGetValue(parameter.Name, out var seen))
                    {
                        seen = new HashSet<string>(StringComparer.Ordinal);
                        _annotations[parameter.Name] = seen;
                    }

                    seen.Add(spec);
                }
            }
        }

        /// <summary>
        /// Turns annotations into receiver types, once. `x: list` makes `x.` offer
        /// list methods, the same as `let x = []` would.
        ///
        /// Two conditions keep the editor quiet rather than wrong: a name that is
        /// also a module-scope binding is left alone, because the parameter only
        /// shadows it inside one body, and a name annotated with two different
        /// types in two functions is left unknown for the same reason.
        /// </summary>
        private void ResolveAnnotations()
        {
            // Parameters are symbols too, so "is there a binding of this name"
            // means a declaration that is not itself a parameter.
            var bound = new HashSet<string>(
                Symbols.Where(symbol => symbol.Kind is not SymbolKind.Parameter)
                       .Select(symbol => symbol.Name),
                StringComparer.Ordinal);

            foreach (var (name, specs) in _annotations)
            {
                if (specs.Count != 1 || bound.Contains(name))
                {
                    continue;
                }

                var spec = specs.First();
                var type = AnnotatedType(spec.EndsWith("?", StringComparison.Ordinal)
                    ? spec.Substring(0, spec.Length - 1)
                    : spec);
                if (type.IsKnown)
                {
                    _directTypes[name] = type;
                }
            }
        }

        private ReceiverType AnnotatedType(string name) => name switch
        {
            "int" => ReceiverType.Builtin("int"),
            "float" => ReceiverType.Builtin("float"),
            "string" or "text" => ReceiverType.Builtin("string"),
            "bool" => ReceiverType.Builtin("bool"),
            "list" => ReceiverType.Builtin("list"),
            "map" => ReceiverType.Builtin("map"),
            "range" => ReceiverType.Builtin("range"),
            _ => IsStruct(name) ? ReceiverType.OfStruct(name) : ReceiverType.Unknown,
        };

        /// <summary>The kind a `for ... in` loop hands out, when the iterable says so.</summary>
        private static string? ElementOf(Expr iterable) => iterable switch
        {
            LiteralExpr { Value: string } => "string",
            InterpolatedStringExpr => "string",
            RangeExpr => "int",
            _ => null,
        };

        private void NoteAssignment(string name, Expr value)
        {
            if (!_assignments.TryGetValue(name, out var values))
            {
                values = new List<Expr>();
                _assignments[name] = values;
            }

            values.Add(value);
        }

        private static string[] ParameterNames(FunctionLiteral function) =>
            function.Parameters.Select(parameter =>
                (parameter.IsRest ? "..." : string.Empty) + parameter.Name).ToArray();

        private static string Signature(FunctionLiteral function)
        {
            var parameters = function.Parameters.Select(parameter =>
                (parameter.IsRest ? "..." : string.Empty) + parameter.Name +
                (parameter.TypeSpec is null ? string.Empty : ": " + parameter.TypeSpec) +
                (parameter.Default is null ? string.Empty : "?"));
            return $"fn {function.Name ?? "lambda"}({string.Join(", ", parameters)})";
        }
    }
}
