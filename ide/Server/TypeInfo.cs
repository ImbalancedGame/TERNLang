using System;
using System.Collections.Generic;
using TERN.Syntax;

namespace TERN.Ide
{
    /// <summary>
    /// The type of a receiver, as far as the server can tell from the source.
    ///
    /// Two answers matter to completion: a built-in kind ("string", "list",
    /// "map", ...), which selects one method table, and a struct declared in
    /// this file, whose fields and methods are the only members it has. Anything
    /// else is <see cref="Unknown"/>, and a language server that does not know
    /// should say so rather than offer members the value cannot have — a list
    /// with `toUpper` on it teaches nothing good.
    /// </summary>
    public readonly struct ReceiverType
    {
        private readonly string? _name;
        private readonly bool _isStruct;

        private ReceiverType(string? name, bool isStruct)
        {
            _name = name;
            _isStruct = isStruct;
        }

        public static readonly ReceiverType Unknown = default;

        /// <summary>A value kind, named as <c>type()</c> reports it.</summary>
        public static ReceiverType Builtin(string name) => new(name, false);

        /// <summary>A struct declared in this file.</summary>
        public static ReceiverType OfStruct(string name) => new(name, true);

        /// <summary>The script-visible name: <c>list</c>, <c>string</c>, <c>Ticket</c>.</summary>
        public string? Name => _name;

        public bool IsKnown => _name is not null;

        public bool IsStruct => _isStruct;

        public override string ToString() => _name ?? "unknown";
    }

    /// <summary>
    /// What an expression's value is, worked out from the source alone.
    ///
    /// The rules are deliberately literal, because an editor that guesses is
    /// worse than one that stays quiet: a `let` of a literal, of a list or map
    /// literal, of a range, of a struct constructor or of a call whose page
    /// names its return type is understood, and everything else is unknown.
    /// Nothing here runs the program or looks outside the file.
    /// </summary>
    public static class TypeInference
    {
        /// <summary>How far a chain of `let a = b; let b = c;` is followed.</summary>
        public const int MaxDepth = 8;

        public static ReceiverType Of(Expr? expression, SymbolIndex index, HashSet<string> namespaces,
            int offset = -1, int depth = 0)
        {
            if (expression is null || depth > MaxDepth)
            {
                return ReceiverType.Unknown;
            }

            switch (expression)
            {
                case LiteralExpr literal:
                    return FromValue(literal.Value);

                case InterpolatedStringExpr:
                    return ReceiverType.Builtin("string");

                case IdentifierExpr identifier:
                    return index.TypeOf(identifier.Name, namespaces, offset, depth + 1);

                case ThisExpr:
                    // `this` is the struct whose method the caret is inside;
                    // outside one it is a compile error, so nothing is claimed.
                    return index.ThisAt(offset) is { } owner
                        ? ReceiverType.OfStruct(owner)
                        : ReceiverType.Unknown;

                // `...xs` has the type of xs, and nothing else here knows what
                // that is: the element type is whatever the list holds.
                case SpreadExpr:
                    return ReceiverType.Unknown;

                case ListComprehensionExpr:
                    return ReceiverType.Builtin("list");

                case MapComprehensionExpr:
                    return ReceiverType.Builtin("map");

                case ListExpr:
                    return ReceiverType.Builtin("list");

                case MapExpr:
                    return ReceiverType.Builtin("map");

                case RangeExpr:
                    return ReceiverType.Builtin("range");

                case CallExpr call:
                    return OfCall(call, index, namespaces, offset, depth);

                case MemberExpr member:
                    return OfMember(member, index, namespaces, offset, depth);

                case IndexExpr indexed:
                    return OfIndex(indexed, index, namespaces, offset, depth);

                case SliceExpr slice:
                {
                    var target = Of(slice.Target, index, namespaces, offset, depth + 1);
                    return target.Name switch
                    {
                        "string" => ReceiverType.Builtin("string"),
                        "list" => ReceiverType.Builtin("list"),
                        _ => ReceiverType.Unknown,
                    };
                }

                case BinaryExpr binary:
                    return OfBinary(binary, index, namespaces, offset, depth);

                case UnaryExpr unary:
                    return OfUnary(unary, index, namespaces, offset, depth);

                case TernaryExpr ternary:
                    return Unify(Of(ternary.WhenTrue, index, namespaces, offset, depth + 1),
                        Of(ternary.WhenFalse, index, namespaces, offset, depth + 1));

                case IfExpr conditional:
                    return Unify(Of(StatementValue(conditional.Then), index, namespaces, offset, depth + 1),
                        Of(StatementValue(conditional.Else), index, namespaces, offset, depth + 1));

                case MatchExpr match:
                {
                    var result = ReceiverType.Unknown;
                    foreach (var arm in match.Arms)
                    {
                        var armType = Of(arm.ExpressionBody ?? StatementValue(arm.Body), index, namespaces, offset, depth + 1);
                        result = result.IsKnown ? Unify(result, armType) : armType;
                    }

                    return result;
                }

                case AssignExpr assign:
                    return Of(assign.Value, index, namespaces, offset, depth + 1);

                case IncrementExpr:
                    return ReceiverType.Builtin("int");

                default:
                    return ReceiverType.Unknown;
            }
        }

        /// <summary>The value a statement (as used by `if` and `match` expressions) produces.</summary>
        private static Expr? StatementValue(Stmt? statement) => statement switch
        {
            ExpressionStmt expression => expression.Expression,
            ReturnStmt { Value: { } value } => value,
            _ => null,
        };

        private static ReceiverType FromValue(object? value) => value switch
        {
            string => ReceiverType.Builtin("string"),
            bool => ReceiverType.Builtin("bool"),
            long or int => ReceiverType.Builtin("int"),
            double or float => ReceiverType.Builtin("float"),
            null => ReceiverType.Builtin("nil"),
            _ => ReceiverType.Unknown,
        };

        private static ReceiverType OfCall(CallExpr call, SymbolIndex index, HashSet<string> namespaces, int offset,
            int depth)
        {
            switch (call.Callee)
            {
                case IdentifierExpr callee:
                    if (index.IsStruct(callee.Name))
                    {
                        return ReceiverType.OfStruct(callee.Name);
                    }

                    return FromKind(Function("global." + callee.Name));

                case MemberExpr member:
                {
                    // `math.sqrt(2)` — the namespace page names the return type.
                    if (member.Target is IdentifierExpr target && namespaces.Contains(target.Name))
                    {
                        return FromKind(Function($"{target.Name}.{member.Name}"));
                    }

                    var receiver = Of(member.Target, index, namespaces, offset, depth + 1);
                    if (!receiver.IsKnown || receiver.IsStruct)
                    {
                        // A struct method's page is the user's own code, which
                        // does not declare a return type.
                        return ReceiverType.Unknown;
                    }

                    return FromKind(BuiltinReturns.Methods.TryGetValue($"{receiver.Name}.{member.Name}", out var kind)
                        ? kind
                        : null);
                }

                default:
                    return ReceiverType.Unknown;
            }
        }

        private static ReceiverType OfMember(MemberExpr member, SymbolIndex index, HashSet<string> namespaces,
            int offset, int depth)
        {
            if (member.Target is IdentifierExpr target && namespaces.Contains(target.Name))
            {
                return FromKind(Function($"{target.Name}.{member.Name}"));
            }

            var receiver = Of(member.Target, index, namespaces, offset, depth + 1);
            if (receiver.IsStruct)
            {
                return ReceiverType.Unknown;
            }

            // Computed properties: `xs.length` and friends are integers.
            return receiver.Name switch
            {
                "list" or "string" or "map" when member.Name is "length" or "count" or "size"
                    => ReceiverType.Builtin("int"),
                "range" when member.Name is "start" or "end" or "length" or "count"
                    => ReceiverType.Builtin("int"),
                _ => ReceiverType.Unknown,
            };
        }

        private static ReceiverType OfIndex(IndexExpr indexed, SymbolIndex index, HashSet<string> namespaces,
            int offset, int depth)
        {
            var target = Of(indexed.Target, index, namespaces, offset, depth + 1);
            if (target.Name == "string")
            {
                // Indexing text yields text; indexing a list yields a value of
                // no declared type, so nothing is claimed about it.
                return ReceiverType.Builtin("string");
            }

            return ReceiverType.Unknown;
        }

        private static ReceiverType OfBinary(BinaryExpr binary, SymbolIndex index, HashSet<string> namespaces,
            int offset, int depth)
        {
            switch (binary.Operator)
            {
                case TokenKind.EqualEqual:
                case TokenKind.BangEqual:
                case TokenKind.Less:
                case TokenKind.LessEqual:
                case TokenKind.Greater:
                case TokenKind.GreaterEqual:
                case TokenKind.AmpAmp:
                case TokenKind.PipePipe:
                case TokenKind.And:
                case TokenKind.Or:
                case TokenKind.In:
                    return ReceiverType.Builtin("bool");
            }

            var left = Of(binary.Left, index, namespaces, offset, depth + 1);
            var right = Of(binary.Right, index, namespaces, offset, depth + 1);

            if (binary.Operator == TokenKind.QuestionQuestion)
            {
                return Unify(left, right);
            }

            switch (binary.Operator)
            {
                case TokenKind.Plus:
                    if (left.Name == "string" || right.Name == "string")
                    {
                        return ReceiverType.Builtin("string");
                    }

                    break;

                case TokenKind.Amp:
                case TokenKind.Pipe:
                case TokenKind.Caret:
                case TokenKind.ShiftLeft:
                case TokenKind.ShiftRight:
                    if (left.Name == "int" && right.Name == "int")
                    {
                        return ReceiverType.Builtin("int");
                    }

                    return ReceiverType.Unknown;
            }

            if (!IsNumber(left) || !IsNumber(right))
            {
                return ReceiverType.Unknown;
            }

            // Division is the one operator that is always a float, because
            // `1 / 2` is 0.5 in this language.
            return binary.Operator == TokenKind.Slash || left.Name == "float" || right.Name == "float"
                ? ReceiverType.Builtin("float")
                : ReceiverType.Builtin("int");
        }

        private static ReceiverType OfUnary(UnaryExpr unary, SymbolIndex index, HashSet<string> namespaces,
            int offset, int depth)
        {
            switch (unary.Operator)
            {
                case TokenKind.Not:
                    return ReceiverType.Builtin("bool");
                case TokenKind.TypeOf:
                    return ReceiverType.Builtin("string");
                case TokenKind.Tilde:
                    return ReceiverType.Builtin("int");
            }

            var operand = Of(unary.Operand, index, namespaces, offset, depth + 1);
            return IsNumber(operand) ? operand : ReceiverType.Unknown;
        }

        private static string? Function(string qualified) =>
            BuiltinReturns.Functions.TryGetValue(qualified, out var kind) ? kind : null;

        private static ReceiverType FromKind(string? kind) =>
            kind is null ? ReceiverType.Unknown : ReceiverType.Builtin(kind);

        private static bool IsNumber(ReceiverType type) => type.Name is "int" or "float";

        /// <summary>The type both branches agree on, or nothing when they disagree.</summary>
        public static ReceiverType Unify(ReceiverType left, ReceiverType right)
        {
            if (!left.IsKnown || !right.IsKnown)
            {
                return ReceiverType.Unknown;
            }

            if (left.Name == right.Name && left.IsStruct == right.IsStruct)
            {
                return left;
            }

            if (IsNumber(left) && IsNumber(right))
            {
                return ReceiverType.Builtin("float");
            }

            return ReceiverType.Unknown;
        }
    }
}
