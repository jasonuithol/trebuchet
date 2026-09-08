using System.Text;

namespace Trebuchet.Compiler.Syntax;

/// <summary>
/// Prints an AST back as canonical source. Because the tree rule means a line's
/// indented children always belong to the last expression on that line, printing is
/// split into an inline head (<see cref="Inline"/>) and a tail of child lines
/// (<see cref="Tail"/>).
/// </summary>
public sealed class Printer
{
    private const string IndentUnit = "  ";
    private readonly StringBuilder _sb = new();
    private int _indent;
    private IReadOnlyList<Comment> _comments = Array.Empty<Comment>();
    private int _nextComment;

    public static string Print(SourceFile file)
    {
        var p = new Printer { _comments = file.Comments };
        p.File(file);
        p.FlushComments(int.MaxValue);
        return p._sb.ToString();
    }

    /// <summary>Emits every own-line comment that precedes the given source line, at the current indent.</summary>
    private void FlushComments(int beforeLine)
    {
        while (_nextComment < _comments.Count && _comments[_nextComment].Line < beforeLine)
        {
            var c = _comments[_nextComment++];
            if (!c.OwnLine) continue; // a trailing comment whose line was never printed: drop rather than misplace
            for (var i = 0; i < _indent; i++) _sb.Append(IndentUnit);
            _sb.Append("//").Append(c.Text).Append('\n');
        }
    }

    /// <summary>The trailing comment on a given source line, if any, consuming it.</summary>
    private string TrailingComment(int line)
    {
        FlushComments(line);
        if (_nextComment < _comments.Count && _comments[_nextComment].Line == line && !_comments[_nextComment].OwnLine)
            return "  //" + _comments[_nextComment++].Text;
        return "";
    }

    /// <summary>A line that comes from a source node: own-line comments before it are flushed and its trailing comment kept.</summary>
    private void Line(string text, Position pos)
    {
        FlushComments(pos.Line);
        for (var i = 0; i < _indent; i++) _sb.Append(IndentUnit);
        _sb.Append(text).Append(TrailingComment(pos.Line)).Append('\n');
    }

    public static string Print(Expr expr) => new Printer().Inline(expr);

    public static string PrintType(TypeRef type) => new Printer().Type(type);

    private void Line(string text)
    {
        for (var i = 0; i < _indent; i++) _sb.Append(IndentUnit);
        _sb.Append(text).Append('\n');
    }

    private void Blank() => _sb.Append('\n');

    private void Indented(Action body)
    {
        _indent++;
        body();
        _indent--;
    }

    // ------------------------------------------------------------ file and declarations

    private void File(SourceFile f)
    {
        var first = true;
        if (f.Module is not null)
        {
            Line($"module {f.Module}", f.Pos);
            first = false;
        }
        if (f.Uses.Count > 0)
        {
            if (!first) Blank();
            foreach (var u in f.Uses) Line($"use {u.Name}", u.Pos);
            first = false;
        }
        foreach (var d in f.Decls)
        {
            if (!first) Blank();
            FlushComments(d.Pos.Line);
            Decl(d);
            first = false;
        }
    }

    private void Decl(Decl d)
    {
        var prefix = d.IsPrivate ? "private " : "";
        switch (d)
        {
            case RecordDecl r:
                RecordDecl(r, prefix);
                break;
            case UnionDecl u:
                Line($"{prefix}union {u.Name}{TypeParams(u.TypeParams)}", u.Pos);
                Indented(() =>
                {
                    foreach (var v in u.Variants)
                        Line(v.Params.Count == 0 ? v.Name : $"{v.Name}({Params(v.Params)})", v.Pos);
                });
                break;
            case FnDecl fn:
                FnDecl(fn, prefix);
                break;
            case ServiceDecl s:
                Line($"{prefix}{(s.Scoped ? "scoped " : "")}{(s.IsResource ? "resource " : "")}service {s.Name}{(s.Dependencies.Count > 0 ? $"({Params(s.Dependencies)})" : "")}", s.Pos);
                Indented(() =>
                {
                    for (var i = 0; i < s.Methods.Count; i++)
                    {
                        if (i > 0) Blank();
                        FnDecl(s.Methods[i], s.Methods[i].IsPrivate ? "private " : "");
                    }
                });
                break;
            case ShapeDecl sh:
                Line($"{prefix}shape {sh.Name}", sh.Pos);
                Indented(() =>
                {
                    foreach (var m in sh.Members) Line("fn " + Signature(m), m.Pos);
                });
                break;
            case RootDecl root:
                Line($"{prefix}root {root.Name}", root.Pos);
                Indented(() => Args(root.Entries));
                break;
            case ExternDecl ex:
                Line($"{prefix}extern fn {Signature(ex.Signature)}", ex.Pos);
                Indented(() =>
                {
                    foreach (var b in ex.Bindings) Line($"{b.Target} {Quote(b.Symbol)}", b.Pos);
                    foreach (var c in ex.Catches) Line($"catch {c.Target} {Quote(c.ExceptionType)} -> {c.Variant}", c.Pos);
                });
                break;
            default:
                throw new InvalidOperationException($"unknown declaration {d.GetType().Name}");
        }
    }

    private void RecordDecl(RecordDecl r, string prefix)
    {
        var kw = r.IsEntity ? "entity" : "record";
        if (r.Inline)
        {
            Line($"{prefix}{kw} {r.Name}{TypeParams(r.TypeParams)}({string.Join(", ", r.Fields.Select(f => $"{f.Name}: {Type(f.Type)}"))})", r.Pos);
            return;
        }
        Line($"{prefix}{kw} {r.Name}{TypeParams(r.TypeParams)}", r.Pos);
        Indented(() =>
        {
            foreach (var f in r.Fields)
            {
                Line($"{f.Name}: {Type(f.Type)}", f.Pos);
                if (f.Init is not null)
                    Indented(() => ExprLine("init => ", f.Init));
            }
        });
    }

    private void FnDecl(FnDecl fn, string prefix)
    {
        var kw = fn.Kind == FnKind.Handler ? "handler" : "fn";
        Line($"{prefix}{kw} {Signature(fn.Signature)}", fn.Pos);
        Indented(() => Block(fn.Body));
    }

    private string Signature(FnSignature s) =>
        $"{s.Name}{TypeParams(s.TypeParams)}({Params(s.Params)}) -> {Type(s.Return)}{Effects(s.Effects)}";

    private static string TypeParams(IReadOnlyList<string> ps) => ps.Count == 0 ? "" : $"[{string.Join(", ", ps)}]";

    /// <summary>null prints nothing (inferred); an empty list prints "! Pure".</summary>
    private static string Effects(IReadOnlyList<string>? effects) =>
        effects is null ? "" : effects.Count == 0 ? " ! Pure" : " ! " + string.Join(" ", effects);

    private string Params(IReadOnlyList<Param> ps) =>
        string.Join(", ", ps.Select(p => $"{p.Name}: {Type(p.Type)}"));

    private string Type(TypeRef t) => t switch
    {
        NamedType n => n.Args.Count == 0 ? n.Name : $"{n.Name}[{string.Join(", ", n.Args.Select(Type))}]",
        FnType f => $"fn({string.Join(", ", f.Params.Select(Type))}) -> {Type(f.Return)}{Effects(f.Effects)}",
        _ => throw new InvalidOperationException($"unknown type {t.GetType().Name}"),
    };

    // ------------------------------------------------------------ statements

    private void Block(Block b)
    {
        foreach (var s in b.Stmts)
        {
            switch (s)
            {
                case BindingStmt bind: ValueLine($"{bind.Name} = ", bind.Value); break;
                case UseStmt use: ValueLine($"use {use.Name} = ", use.Value); break;
                case ExprStmt e: ExprLine("", e.Value); break;
                default: throw new InvalidOperationException($"unknown statement {s.GetType().Name}");
            }
        }
    }

    /// <summary>A value after '=' or ':'. Block-form list and map literals go on the lines below.</summary>
    private void ValueLine(string prefix, Expr value)
    {
        if (value is ListLit { IsBlock: true } or MapLit { IsBlock: true })
        {
            Line(prefix.TrimEnd(), value.Pos);
            Indented(() => SequenceItems(value));
            return;
        }
        ExprLine(prefix, value);
    }

    /// <summary>An expression that occupies a line, followed by its children.</summary>
    private void ExprLine(string prefix, Expr e)
    {
        if (e is IfExpr { Inline: false } ifx)
        {
            IfBlock(prefix, ifx);
            return;
        }
        Line(prefix + Inline(e), e.Pos);
        Tail(e);
    }

    private void IfBlock(string prefix, IfExpr ifx)
    {
        Line($"{prefix}if {Inline(ifx.Condition)} then", ifx.Pos);
        Indented(() => Block(ifx.Then));
        var els = ifx.Else;
        while (els is not null)
        {
            if (els.Stmts.Count == 1 && els.Stmts[0] is ExprStmt { Value: IfExpr { Inline: false } chained })
            {
                Line($"else if {Inline(chained.Condition)} then", chained.Pos);
                Indented(() => Block(chained.Then));
                els = chained.Else;
                continue;
            }
            Line("else", els.Pos);
            Indented(() => Block(els));
            break;
        }
    }

    /// <summary>The indented children that belong to the last expression on a line.</summary>
    private void Tail(Expr e)
    {
        switch (e)
        {
            case CallExpr c when c.BlockArgs.Count > 0:
                Indented(() => Args(c.BlockArgs));
                break;
            case WithExpr { IsBlock: true } w:
                Indented(() =>
                {
                    foreach (var f in w.Fields) ValueLine($"{string.Join(".", f.Path)}: ", f.Value);
                });
                break;
            case MatchExpr m:
                Indented(() =>
                {
                    foreach (var arm in m.Arms)
                    {
                        if (arm.Inline) ExprLine($"{Pattern(arm.Pattern)} => ", ((ExprStmt)arm.Body.Stmts[0]).Value);
                        else
                        {
                            Line($"{Pattern(arm.Pattern)} =>", arm.Pos);
                            Indented(() => Block(arm.Body));
                        }
                    }
                });
                break;
            case LambdaExpr { Inline: false } l:
                Indented(() => Block(l.Body));
                break;
            case PropagateExpr p: Tail(p.Inner); break;
            case UnaryExpr u: Tail(u.Operand); break;
            case BinaryExpr b: Tail(b.Right); break;
        }
    }

    private void Args(IReadOnlyList<Arg> args)
    {
        foreach (var a in args)
        {
            if (a.Name is not null) ValueLine($"{a.Name}: ", a.Value);
            else if (a.Value is ListLit { IsBlock: true } or MapLit { IsBlock: true }) SequenceItems(a.Value);
            else ExprLine("", a.Value);
        }
    }

    private void SequenceItems(Expr seq)
    {
        switch (seq)
        {
            case ListLit l:
                foreach (var item in l.Items) ExprLine("- ", item);
                break;
            case MapLit m:
                foreach (var entry in m.Entries) ValueLine($"- {Inline(entry.Key)}: ", entry.Value);
                break;
        }
    }

    // ------------------------------------------------------------ expressions

    private const int PrecLowest = 0;   // lambda, inline if, with, match: extend as far right as possible
    private const int PrecOr = 1, PrecAnd = 2, PrecNot = 3, PrecCompare = 4, PrecAdd = 5, PrecMul = 6, PrecNeg = 7, PrecPostfix = 8, PrecPrimary = 9;

    private static int Prec(Expr e) => e switch
    {
        BinaryExpr { Op: "or" } => PrecOr,
        BinaryExpr { Op: "and" } => PrecAnd,
        UnaryExpr { Op: "not" or "supervise" } => PrecNot,
        BinaryExpr { Op: "==" or "!=" or "<" or "<=" or ">" or ">=" } => PrecCompare,
        BinaryExpr { Op: "+" or "-" } => PrecAdd,
        BinaryExpr => PrecMul,
        UnaryExpr => PrecNeg,
        MemberExpr or CallExpr or PropagateExpr => PrecPostfix,
        LambdaExpr or IfExpr or WithExpr or MatchExpr => PrecLowest,
        _ => PrecPrimary,
    };

    private string Wrap(Expr e, bool needParens) => needParens ? $"({Inline(e)})" : Inline(e);

    private string Inline(Expr e) => e switch
    {
        NameExpr n => n.Name,
        TypeNameExpr t => t.Name,
        IntLit i => i.Text,
        FloatLit f => f.Text,
        StringLit s => Quote(s.Value),
        BoolLit b => b.Value ? "true" : "false",
        ListLit l => $"[{string.Join(", ", l.Items.Select(Inline))}]",
        MapLit m => m.Entries.Count == 0 ? "{}" : $"{{{string.Join(", ", m.Entries.Select(en => $"{Inline(en.Key)}: {Inline(en.Value)}"))}}}",
        MemberExpr mem => $"{Wrap(mem.Target, Prec(mem.Target) < PrecPostfix)}.{mem.Name}",
        CallExpr c => Call(c),
        UnaryExpr { Op: "not" or "supervise" } u => $"{u.Op} {Wrap(u.Operand, Prec(u.Operand) < PrecNot && Prec(u.Operand) != PrecLowest)}",
        UnaryExpr u => $"-{Wrap(u.Operand, Prec(u.Operand) < PrecNeg)}",
        BinaryExpr b => Binary(b),
        PropagateExpr p => $"{Wrap(p.Inner, Prec(p.Inner) < PrecPostfix)}?",
        LambdaExpr l => Lambda(l),
        IfExpr ifx => ifx.Inline
            ? $"if {Inline(ifx.Condition)} then {Inline(Single(ifx.Then))} else {Inline(Single(ifx.Else!))}"
            : $"if {Inline(ifx.Condition)} then",
        MatchExpr m => $"match {Inline(m.Scrutinee)}",
        WithExpr w => w.IsBlock
            ? $"{Wrap(w.Target, Prec(w.Target) < PrecPostfix)} with"
            : $"{Wrap(w.Target, Prec(w.Target) < PrecPostfix)} with {{ {string.Join(", ", w.Fields.Select(f => $"{string.Join(".", f.Path)}: {Inline(f.Value)}"))} }}",
        _ => throw new InvalidOperationException($"unknown expression {e.GetType().Name}"),
    };

    private static Expr Single(Block b) => ((ExprStmt)b.Stmts[0]).Value;

    private string Call(CallExpr c)
    {
        var sb = new StringBuilder(Wrap(c.Callee, Prec(c.Callee) < PrecPostfix));
        if (c.TypeArgs.Count > 0) sb.Append('[').Append(string.Join(", ", c.TypeArgs.Select(Type))).Append(']');
        if (c.HasParens)
            sb.Append('(').Append(string.Join(", ", c.Args.Select(a => a.Name is null ? Inline(a.Value) : $"{a.Name}: {Inline(a.Value)}"))).Append(')');
        return sb.ToString();
    }

    private string Binary(BinaryExpr b)
    {
        var p = Prec(b);
        var left = Wrap(b.Left, Prec(b.Left) < p || Prec(b.Left) == PrecLowest);
        var rp = Prec(b.Right);
        var right = Wrap(b.Right, rp != PrecLowest && rp <= p);
        return $"{left} {b.Op} {right}";
    }

    private string Lambda(LambdaExpr l)
    {
        string ps;
        if (l.TypedParams) ps = $"({string.Join(", ", l.Params.Select(p => $"{p.Name}: {Type(p.Type!)}"))})";
        else ps = string.Join(", ", l.Params.Select(p => p.Name));
        var head = ps.Length == 0 ? "\\->" : $"\\{ps} ->";
        return l.Inline ? $"{head} {Inline(Single(l.Body))}" : head;
    }

    private string Pattern(Pattern p) => p switch
    {
        VariantPattern v => v.Args.Count == 0 ? v.Name : $"{v.Name}({string.Join(", ", v.Args.Select(Pattern))})",
        BindPattern b => b.Name,
        WildcardPattern => "_",
        LiteralPattern lit => Inline(lit.Literal),
        _ => throw new InvalidOperationException($"unknown pattern {p.GetType().Name}"),
    };

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in s)
        {
            sb.Append(ch switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => ch.ToString(),
            });
        }
        return sb.Append('"').ToString();
    }
}
