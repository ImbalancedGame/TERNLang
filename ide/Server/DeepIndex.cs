using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TERN.Syntax;

namespace TERN.Ide
{
    /// <summary>
    /// Everything a file mentions, not just what it declares: every identifier
    /// reference, every member access, and every string literal, each with the
    /// offset its name starts at.
    ///
    /// The shallow <see cref="SymbolIndex"/> answers "where is this declared";
    /// this answers "where else does it appear", which is what find-usages and
    /// rename need. References are matched by name across the file, so a rename
    /// renames a shadowed variable too — the compiler's scoping is not exposed
    /// to the editor, and a wrong rename is worse than a refused one. Rename
    /// therefore only proceeds when the file declares the name once.
    /// </summary>
    public sealed class DeepIndex
    {
        public List<(string Name, int Offset, int Length)> References { get; } = new();

        public List<(string Name, int Offset, int Length)> Members { get; } = new();

        /// <summary>Text literals and their offsets, so a rename can reach a map key.</summary>
        public List<(string Text, int Offset, int Length)> Strings { get; } = new();

        public static DeepIndex Build(Document document, SymbolIndex symbols)
        {
            var index = new DeepIndex();
            if (document.Program is null)
            {
                return index;
            }

            foreach (var statement in document.Program.Statements)
            {
                index.WalkStatement(statement);
            }

            return index;
        }

        private void Add(string name, int offset, int length, bool member)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            (member ? Members : References).Add((name, offset, length));
        }

        private void WalkStatement(Stmt statement)
        {
            switch (statement)
            {
                case BlockStmt block:
                    foreach (var child in block.Statements)
                    {
                        WalkStatement(child);
                    }

                    break;

                case ExpressionStmt expression:
                    WalkExpression(expression.Expression);
                    break;

                case LetStmt let:
                    if (let.Initializer is not null)
                    {
                        WalkExpression(let.Initializer);
                    }

                    break;

                case FunctionDeclStmt declared:
                    WalkFunction(declared.Function);
                    break;

                case StructDeclStmt declared:
                    foreach (var field in declared.Fields)
                    {
                        if (field.Default is not null)
                        {
                            WalkExpression(field.Default);
                        }
                    }

                    foreach (var method in declared.Methods)
                    {
                        WalkFunction(method);
                    }

                    break;

                case IfStmt conditional:
                    WalkExpression(conditional.Condition);
                    WalkStatement(conditional.Then);
                    if (conditional.Else is not null)
                    {
                        WalkStatement(conditional.Else);
                    }

                    break;

                case WhileStmt loop:
                    WalkExpression(loop.Condition);
                    WalkStatement(loop.Body);
                    break;

                case DoWhileStmt loop:
                    WalkStatement(loop.Body);
                    WalkExpression(loop.Condition);
                    break;

                case LoopStmt loop:
                    WalkStatement(loop.Body);
                    break;

                case ForStmt counted:
                    if (counted.Initializer is not null)
                    {
                        WalkStatement(counted.Initializer);
                    }

                    if (counted.Condition is not null)
                    {
                        WalkExpression(counted.Condition);
                    }

                    if (counted.Increment is not null)
                    {
                        WalkExpression(counted.Increment);
                    }

                    WalkStatement(counted.Body);
                    break;

                case ForInStmt each:
                    WalkExpression(each.Iterable);
                    WalkStatement(each.Body);
                    break;

                case ReturnStmt returned when returned.Value is not null:
                    WalkExpression(returned.Value);
                    break;

                case ThrowStmt thrown:
                    WalkExpression(thrown.Value);
                    break;

                case TryStmt guarded:
                    WalkStatement(guarded.Body);
                    if (guarded.Catch is not null)
                    {
                        WalkStatement(guarded.Catch);
                    }

                    if (guarded.Finally is not null)
                    {
                        WalkStatement(guarded.Finally);
                    }

                    break;
            }
        }

        private void WalkFunction(FunctionLiteral function)
        {
            foreach (var parameter in function.Parameters)
            {
                if (parameter.Default is not null)
                {
                    WalkExpression(parameter.Default);
                }
            }

            if (function.Body is not null)
            {
                WalkStatement(function.Body);
            }

            if (function.ExpressionBody is not null)
            {
                WalkExpression(function.ExpressionBody);
            }
        }

        private void WalkExpression(Expr expression)
        {
            switch (expression)
            {
                case IdentifierExpr identifier:
                    Add(identifier.Name, identifier.Offset, identifier.Name.Length, member: false);
                    break;

                case MemberExpr member:
                    WalkExpression(member.Target);
                    Add(member.Name, member.Offset, member.Name.Length, member: true);
                    break;

                case CallExpr call:
                    WalkExpression(call.Callee);
                    foreach (var argument in call.Arguments)
                    {
                        WalkExpression(argument);
                    }

                    break;

                case BinaryExpr binary:
                    WalkExpression(binary.Left);
                    WalkExpression(binary.Right);
                    break;

                case UnaryExpr unary:
                    WalkExpression(unary.Operand);
                    break;

                case TernaryExpr ternary:
                    WalkExpression(ternary.Condition);
                    WalkExpression(ternary.WhenTrue);
                    WalkExpression(ternary.WhenFalse);
                    break;

                case IndexExpr indexed:
                    WalkExpression(indexed.Target);
                    WalkExpression(indexed.Index);
                    break;

                case SliceExpr slice:
                    WalkExpression(slice.Target);
                    if (slice.Start is not null) WalkExpression(slice.Start);
                    if (slice.End is not null) WalkExpression(slice.End);
                    break;

                case RangeExpr range:
                    WalkExpression(range.Start);
                    if (range.End is not null) WalkExpression(range.End);
                    break;

                case AssignExpr assign:
                    WalkExpression(assign.Target);
                    WalkExpression(assign.Value);
                    break;

                case IncrementExpr increment:
                    WalkExpression(increment.Target);
                    break;

                case ListExpr list:
                    foreach (var item in list.Items)
                    {
                        WalkExpression(item);
                    }

                    break;

                case MapExpr map:
                    for (var i = 0; i < map.Keys.Count; i++)
                    {
                        WalkExpression(map.Keys[i]);
                        if (i < map.Values.Count)
                        {
                            WalkExpression(map.Values[i]);
                        }
                    }

                    break;

                case LambdaExpr lambda:
                    WalkFunction(lambda.Function);
                    break;

                case IfExpr conditional:
                    WalkExpression(conditional.Condition);
                    WalkStatement(conditional.Then);
                    WalkStatement(conditional.Else);
                    break;

                case InterpolatedStringExpr interpolated:
                    foreach (var part in interpolated.Expressions)
                    {
                        if (part is not null)
                        {
                            WalkExpression(part);
                        }
                    }

                    break;

                case LiteralExpr literal when literal.Value is string text:
                    Strings.Add((text, literal.Offset, text.Length + 2));
                    break;

                case LiteralExpr withQuotes when withQuotes.Value is string:
                    break;
            }
        }

        /// <summary>Every reference to `name`, including the declaration and string keys of that name.</summary>
        public List<(int Offset, int Length)> Occurrences(string name, SymbolIndex symbols)
        {
            var found = new List<(int, int)>();
            foreach (var (candidate, offset, length) in References)
            {
                if (candidate == name)
                {
                    found.Add((offset, length));
                }
            }

            foreach (var (candidate, offset, length) in Members)
            {
                if (candidate == name)
                {
                    found.Add((offset, length));
                }
            }

            foreach (var symbol in symbols.Symbols)
            {
                if (symbol.Name == name)
                {
                    found.Add((symbol.Offset, name.Length));
                }
            }

            found.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return found;
        }
    }
}
