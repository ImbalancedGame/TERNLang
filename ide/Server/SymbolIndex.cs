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
    /// The type knowledge is deliberately shallow — `let t = Ticket(...)` is
    /// recognised, `let t = make()` is not. A wrong answer is worse than no
    /// answer for completion, so anything uncertain returns nothing and the
    /// editor falls back to the general list.
    /// </summary>
    public sealed class SymbolIndex
    {
        private readonly Dictionary<string, Symbol> _declarations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _receiverType = new(StringComparer.Ordinal);

        public List<Symbol> Symbols { get; } = new();

        public HashSet<string> Structs { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Imports { get; } = new(StringComparer.Ordinal);

        public static SymbolIndex Build(Document document)
        {
            var index = new SymbolIndex();
            if (document.Program is not null)
            {
                index.Walk(document.Program, 0);
            }

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

        /// <summary>The struct type an identifier was initialised from, if it is obvious.</summary>
        public string? TypeOf(string name) => _receiverType.TryGetValue(name, out var type) ? type : null;

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
                        });
                        DeclareParameters(method, depth + 2);
                        if (method.Body is not null)
                        {
                            Walk(method.Body, depth + 2);
                        }
                    }

                    break;

                case LetStmt let:
                    Declare(new Symbol
                    {
                        Name = let.Name,
                        Kind = let.IsConst ? SymbolKind.Constant : SymbolKind.Variable,
                        Offset = let.Offset,
                        Line = let.Line,
                        Detail = (let.IsConst ? "const " : "let ") + let.Name,
                        Depth = depth,
                    });
                    NoteInitializer(let.Name, let.Initializer);
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
                    Detail = parameter.IsRest ? $"...{parameter.Name}" : parameter.Name,
                    Depth = depth,
                });
            }
        }

        /// <summary>Remembers `let t = Ticket(...)` so `t.` can complete Ticket's members.</summary>
        private void NoteInitializer(string name, Expr? initializer)
        {
            if (initializer is CallExpr call && call.Callee is IdentifierExpr callee && Structs.Contains(callee.Name))
            {
                _receiverType[name] = callee.Name;
            }
        }

        private static string Signature(FunctionLiteral function)
        {
            var parameters = function.Parameters.Select(parameter =>
                (parameter.IsRest ? "..." : string.Empty) + parameter.Name + (parameter.Default is null ? string.Empty : "?"));
            return $"fn {function.Name ?? "lambda"}({string.Join(", ", parameters)})";
        }
    }
}
