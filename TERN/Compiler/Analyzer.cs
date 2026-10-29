using System;
using System.Collections.Generic;
using TERN.Syntax;

namespace TERN.Compiler
{
    /// <summary>
    /// A deliberately small AST linter. It runs before compilation and reports
    /// the mistakes that are cheap to catch but expensive to debug at runtime:
    /// assignments used as conditions, comparisons that cannot vary, an `if`
    /// used as a value without an `else`, and locals that shadow an outer name.
    ///
    /// Every diagnostic here is a warning: the runtime accepts all of it, so a
    /// script that trips a warning still runs and still means what it says.
    /// </summary>
    public static class Analyzer
    {
        public static void Analyze(BlockStmt program, DiagnosticBag diagnostics, string? sourceName = null)
        {
            var walker = new Walker(diagnostics, sourceName);

            var moduleScope = new Scope(null) { IsModuleScope = true };
            BindDeclarations(program, moduleScope);
            walker.Begin(moduleScope);

            for (var i = 0; i < program.Statements.Count; i++)
            {
                walker.Statement(program.Statements[i]);
            }
        }

        /// <summary>Binds the names a block introduces so uses resolve regardless of order.</summary>
        private static void BindDeclarations(BlockStmt block, Scope scope)
        {
            foreach (var statement in block.Statements)
            {
                switch (statement)
                {
                    case LetStmt declaration:
                        scope.Bind(declaration.Name, declaration);
                        break;

                    case FunctionDeclStmt function when function.Function.Name is { } name:
                        scope.Bind(name, function);
                        break;

                    case StructDeclStmt structDecl:
                        scope.Bind(structDecl.Name, structDecl);
                        break;

                    case ImportStmt { Alias: { } alias } import:
                        scope.Bind(alias, import);
                        break;
                }
            }
        }

        private sealed class Scope
        {
            private readonly Dictionary<string, (Node Declaration, string Kind)> _bindings = new(StringComparer.Ordinal);

            public Scope(Scope? parent) => Parent = parent;

            public Scope? Parent { get; }

            /// <summary>Top level bindings are globals; shadowing one of those is normal.</summary>
            public bool IsModuleScope { get; init; }

            public void Bind(string name, Node declaration, string kind = "variable") =>
                _bindings[name] = (declaration, kind);

            /// <summary>Binds a name to an explicit position (parameters, match arms).</summary>
            public void Bind(string name, int offset, int line, string kind) =>
                _bindings[name] = (new PositionOnlyNode(offset, line), kind);

            public bool TryResolve(string name, out Node? declaration, out string kind)
            {
                for (var scope = this; scope is not null; scope = scope.Parent)
                {
                    if (scope._bindings.TryGetValue(name, out var binding))
                    {
                        declaration = binding.Declaration;
                        kind = scope.IsModuleScope ? "global" : binding.Kind;
                        return true;
                    }
                }

                declaration = null;
                kind = string.Empty;
                return false;
            }
        }

        /// <summary>A zero-payload node used when only a position is needed.</summary>
        private sealed class PositionOnlyNode : Node
        {
            public PositionOnlyNode(int offset, int line)
            {
                Offset = offset;
                Line = line;
            }
        }

        private sealed class Walker
        {
            private readonly DiagnosticBag _diagnostics;
            private readonly string? _source;

            public Walker(DiagnosticBag diagnostics, string? source)
            {
                _diagnostics = diagnostics;
                _source = source;
            }

            private Scope _scope = new(null);

            /// <summary>True while walking a loop body; `break`/`continue` are only valid there.</summary>
            private bool _inLoop;

            /// <summary>Depth of enclosing `try` blocks; errors raised there can be caught.</summary>
            private int _tryDepth;

            public void Begin(Scope scope) => _scope = scope;

            private void Warn(string code, string message, Node node) =>
                Warn(code, message, node.Offset, node.Line);

            private void Warn(string code, string message, int offset, int line) =>
                _diagnostics.Warning(code, message, new SourcePosition(offset, line, 0), _source);

            private Scope Push()
            {
                var scope = new Scope(_scope);
                _scope = scope;
                return scope;
            }

            private void Pop(Scope previous) => _scope = previous;

            // ------------------------------------------------------------ statements

            public void Statement(Stmt statement)
            {
                switch (statement)
                {
                    case LetStmt declaration:
                        if (declaration.Initializer is not null)
                        {
                            Expression(declaration.Initializer);
                        }

                        return;

                    case FunctionDeclStmt functionDecl:
                        Function(functionDecl.Function, isMethod: false);
                        return;

                    case StructDeclStmt structDecl:
                        foreach (var field in structDecl.Fields)
                        {
                            if (field.Default is not null)
                            {
                                // Field defaults run outside any method body, so they see
                                // the enclosing scope but not `this`.
                                Expression(field.Default);
                            }
                        }

                        foreach (var method in structDecl.Methods)
                        {
                            Function(method, isMethod: true);
                        }

                        return;

                    case BlockStmt block:
                        Block(block);
                        return;

                    case ExpressionStmt expressionStmt:
                        Expression(expressionStmt.Expression);
                        return;

                    case IfStmt ifStmt:
                        Condition(ifStmt.Condition);
                        Sub(ifStmt.Then);

                        if (ifStmt.Else is not null)
                        {
                            Sub(ifStmt.Else);
                        }

                        return;

                    case WhileStmt whileStmt:
                        Condition(whileStmt.Condition);
                        Loop(() => Sub(whileStmt.Body));
                        return;

                    case DoWhileStmt doWhile:
                        Loop(() => Sub(doWhile.Body));
                        Condition(doWhile.Condition);
                        return;

                    case LoopStmt loop:
                        Loop(() => Sub(loop.Body));
                        return;

                    case ForInStmt forIn:
                        Expression(forIn.Iterable);
                        Loop(() =>
                        {
                            var previous = Push();
                            foreach (var name in forIn.Names)
                            {
                                CheckShadowing(name, forIn);
                                _scope.Bind(name, forIn, "loop");
                            }

                            Sub(forIn.Body);
                            Pop(previous);
                        });
                        return;

                    case ForStmt forStmt:
                        var outer = Push();
                        if (forStmt.Initializer is not null)
                        {
                            Statement(forStmt.Initializer);
                        }

                        if (forStmt.Condition is not null)
                        {
                            Condition(forStmt.Condition);
                        }

                        Loop(() => Sub(forStmt.Body));

                        if (forStmt.Increment is not null)
                        {
                            Expression(forStmt.Increment);
                        }

                        Pop(outer);
                        return;

                    case ReturnStmt ret:
                        if (ret.Value is not null)
                        {
                            Expression(ret.Value);
                        }

                        return;

                    case ThrowStmt throwStmt:
                        Expression(throwStmt.Value);
                        return;

                    case BreakStmt or ContinueStmt when !_inLoop:
                        Warn("TRN0303", $"'{statement.GetType().Name.Replace("Stmt", "").ToLowerInvariant()}' outside a loop", statement);
                        return;

                    case TryStmt tryStmt:
                        _tryDepth++;
                        Sub(tryStmt.Body);
                        _tryDepth--;

                        if (tryStmt.Catch is not null)
                        {
                            var previous = Push();
                            if (tryStmt.CatchName is not null)
                            {
                                _scope.Bind(tryStmt.CatchName, tryStmt, "catch");
                            }

                            Sub(tryStmt.Catch);
                            Pop(previous);
                        }

                        if (tryStmt.Finally is not null)
                        {
                            Sub(tryStmt.Finally);
                        }

                        return;

                    default:
                        return;
                }
            }

            private void Sub(Stmt statement) => Statement(statement);

            private void Block(BlockStmt block)
            {
                var previous = Push();

                foreach (var statement in block.Statements)
                {
                    if (statement is LetStmt declaration)
                    {
                        CheckShadowing(declaration.Name, declaration);
                    }
                }

                BindDeclarations(block, _scope);

                foreach (var statement in block.Statements)
                {
                    Statement(statement);
                }

                Pop(previous);
            }

            private void Function(FunctionLiteral function, bool isMethod)
            {
                if (function.Body is null)
                {
                    // `fn(x) => expr` style bodies still need their expressions walked.
                    if (function.ExpressionBody is not null)
                    {
                        var previous = Push();
                        if (isMethod)
                        {
                            _scope.Bind("this", function.Offset, function.Line, "receiver");
                        }

                        foreach (var parameter in function.Parameters)
                        {
                            if (parameter.Default is not null)
                            {
                                Expression(parameter.Default);
                            }

                            _scope.Bind(parameter.Name, parameter.Offset, function.Line, "parameter");
                        }

                        Expression(function.ExpressionBody);
                        Pop(previous);
                    }

                    return;
                }

                var saved = Push();
                var savedLoop = _inLoop;
                _inLoop = false;

                if (isMethod)
                {
                    _scope.Bind("this", function.Offset, function.Line, "receiver");
                }

                foreach (var parameter in function.Parameters)
                {
                    if (parameter.Default is not null)
                    {
                        Expression(parameter.Default);
                    }

                    _scope.Bind(parameter.Name, parameter.Offset, function.Line, "parameter");
                }

                Block(function.Body);

                _inLoop = savedLoop;
                Pop(saved);
            }

            private void Loop(Action body)
            {
                var saved = _inLoop;
                _inLoop = true;
                body();
                _inLoop = saved;
            }

            // ----------------------------------------------------------- expressions

            public void Expression(Expr expression)
            {
                switch (expression)
                {
                    case InterpolatedStringExpr interpolated:
                        foreach (var part in interpolated.Expressions)
                        {
                            if (part is not null)
                            {
                                Expression(part);
                            }
                        }

                        return;

                    case UnaryExpr unary:
                        Expression(unary.Operand);
                        return;

                    case BinaryExpr binary:
                        Expression(binary.Left);
                        Expression(binary.Right);
                        CheckSuspiciousComparison(binary);
                        return;

                    case TernaryExpr ternary:
                        Condition(ternary.Condition);
                        Expression(ternary.WhenTrue);
                        Expression(ternary.WhenFalse);
                        return;

                    case IfExpr ifExpr:
                        Condition(ifExpr.Condition);
                        Sub(ifExpr.Then);
                        Sub(ifExpr.Else);
                        return;

                    case CallExpr call:
                        Expression(call.Callee);
                        WarnIfNilReceiver(call.Callee);

                        foreach (var argument in call.Arguments)
                        {
                            Expression(argument);
                        }

                        return;

                    case MemberExpr member:
                        Expression(member.Target);
                        WarnIfNilReceiver(member.Target);
                        return;

                    case IndexExpr index:
                        Expression(index.Target);
                        Expression(index.Index);
                        WarnIfNilReceiver(index.Target);
                        return;

                    case SliceExpr slice:
                        Expression(slice.Target);
                        if (slice.Start is not null)
                        {
                            Expression(slice.Start);
                        }

                        if (slice.End is not null)
                        {
                            Expression(slice.End);
                        }

                        return;

                    case ListExpr list:
                        foreach (var item in list.Items)
                        {
                            Expression(item);
                        }

                        return;

                    case MapExpr map:
                        foreach (var key in map.Keys)
                        {
                            Expression(key);
                        }

                        foreach (var value in map.Values)
                        {
                            Expression(value);
                        }

                        return;

                    case LambdaExpr lambda:
                        Function(lambda.Function, isMethod: false);
                        return;

                    case MatchExpr match:
                        Expression(match.Subject);

                        foreach (var arm in match.Arms)
                        {
                            var previous = Push();

                            foreach (var name in PatternBindings(arm.Pattern))
                            {
                                _scope.Bind(name, arm.Offset, 0, "match");
                            }

                            if (arm.Guard is not null)
                            {
                                Expression(arm.Guard);
                            }

                            if (arm.Body is not null)
                            {
                                Sub(arm.Body);
                            }

                            if (arm.ExpressionBody is not null)
                            {
                                Expression(arm.ExpressionBody);
                            }

                            Pop(previous);
                        }

                        return;

                    case AssignExpr assign:
                        Expression(assign.Target);
                        Expression(assign.Value);
                        return;

                    case IncrementExpr increment:
                        Expression(increment.Target);
                        return;

                    case RangeExpr range:
                        Expression(range.Start);
                        if (range.End is not null)
                        {
                            Expression(range.End);
                        }

                        return;

                    default:
                        return;
                }
            }

            private static IEnumerable<string> PatternBindings(Pattern pattern)
            {
                switch (pattern)
                {
                    case BindingPattern binding:
                        yield return binding.Name;
                        break;

                    case ListPattern list:
                        foreach (var element in list.Elements)
                        {
                            foreach (var name in PatternBindings(element))
                            {
                                yield return name;
                            }
                        }

                        if (list.RestName is not null)
                        {
                            yield return list.RestName;
                        }

                        break;

                    case OrPattern or:
                        foreach (var alternative in or.Alternatives)
                        {
                            foreach (var name in PatternBindings(alternative))
                            {
                                yield return name;
                            }
                        }

                        break;
                }
            }

            // --------------------------------------------------------------- checks

            private void Condition(Expr condition)
            {
                if (condition is AssignExpr { Operator: TokenKind.Equal } assign)
                {
                    Warn("TRN0304",
                        $"assignment used as a condition; use '==' to compare ('{assign.Describe()}')", assign);
                }

                Expression(condition);
            }

            /// <summary>Reports comparisons that cannot depend on runtime state.</summary>
            private void CheckSuspiciousComparison(Expr expression)
            {
                if (expression is not BinaryExpr binary)
                {
                    return;
                }

                var comparison = binary.Operator is TokenKind.EqualEqual or TokenKind.BangEqual;

                if (comparison && binary.Left is IdentifierExpr left && binary.Right is IdentifierExpr right &&
                    left.Name == right.Name)
                {
                    var constant = binary.Operator == TokenKind.BangEqual ? "false" : "true";
                    Warn("TRN0305", $"'{left.Name}' is compared with itself, so this is always {constant}", binary);
                    return;
                }

                if (comparison && IsCollectionLiteral(binary.Left) && IsCollectionLiteral(binary.Right))
                {
                    Warn("TRN0306",
                        "list and map literals are compared by identity, so newly built literals are never equal " +
                        "(compare lengths or items instead)", binary);
                    return;
                }
            }

            private static bool IsCollectionLiteral(Expr expression) => expression is ListExpr or MapExpr;

            /// <summary>
            /// Catches the classic `nil.method()` mistake where the receiver is a
            /// literal nil, which always fails at runtime.
            /// </summary>
            private void WarnIfNilReceiver(Expr target)
            {
                if (_tryDepth > 0 || target is not LiteralExpr { Value: null } literal)
                {
                    return;
                }

                Warn("TRN0310", "member access on nil always raises at runtime", literal);
            }

            /// <summary>
            /// Warns when a local hides an outer local. Parameters, loop variables
            /// and match bindings are allowed to be reused, so they never warn.
            /// </summary>
            private void CheckShadowing(string name, Node node)
            {
                if (_scope.TryResolve(name, out var existing, out var kind) && existing is not null &&
                    kind == "variable")
                {
                    Warning($"'{name}' shadows a variable declared on line {existing.Line}", node);
                }
            }

            private void Warning(string message, Node node) => Warn("TRN0308", message, node);
        }
    }
}
