namespace Trebuchet.Compiler.Syntax;

/// <summary>
/// Recursive-descent parser over the token stream produced by <see cref="Lexer"/>.
/// The grammar is line-oriented: a line is an expression, and its indented children
/// are further arguments to it. Constructs that own their children (match, with, if,
/// lambda, blocks) consume the NEWLINE INDENT ... DEDENT themselves.
/// </summary>
public sealed class Parser
{
    private static readonly HashSet<string> EffectNames = new() { "Nondet", "Write", "Suspend" };

    private readonly IReadOnlyList<Token> _tokens;
    private readonly string? _file;
    private int _i;

    private Parser(IReadOnlyList<Token> tokens, string? file)
    {
        _tokens = tokens;
        _file = file;
    }

    public static SourceFile ParseFile(string source, string? file = null)
    {
        var (tokens, comments) = Lexer.TokenizeWithComments(source, file);
        return new Parser(tokens, file).File() with { Comments = comments };
    }

    // ------------------------------------------------------------ token helpers

    private Token Cur => _tokens[_i];
    private TokenKind Kind => Cur.Kind;
    private TokenKind KindAt(int ahead) => _i + ahead < _tokens.Count ? _tokens[_i + ahead].Kind : TokenKind.EndOfFile;
    private bool At(TokenKind k) => Kind == k;
    private Token Next() => _tokens[_i++];

    private bool Accept(TokenKind k)
    {
        if (!At(k)) return false;
        _i++;
        return true;
    }

    private Token Expect(TokenKind k, string? what = null)
    {
        if (At(k)) return Next();
        throw Error($"expected {what ?? Describe(k)} but found {Describe(Cur)}");
    }

    private void ExpectNewline() => Expect(TokenKind.Newline, "end of line");

    private SyntaxException Error(string message) => new(Cur.Start, message, _file);

    private static string Describe(TokenKind k) => k switch
    {
        TokenKind.Newline => "end of line",
        TokenKind.Indent => "an indented block",
        TokenKind.Dedent => "end of block",
        TokenKind.EndOfFile => "end of file",
        TokenKind.Identifier => "a name",
        TokenKind.TypeName => "a type name",
        TokenKind.Colon => "':'",
        TokenKind.LParen => "'('",
        TokenKind.RParen => "')'",
        TokenKind.Arrow => "'->'",
        TokenKind.FatArrow => "'=>'",
        TokenKind.Assign => "'='",
        _ => k.ToString(),
    };

    private static string Describe(Token t) => t.Kind switch
    {
        TokenKind.Newline or TokenKind.Indent or TokenKind.Dedent or TokenKind.EndOfFile => Describe(t.Kind),
        TokenKind.String => $"string \"{t.Text}\"",
        _ => $"'{t.Text}'",
    };

    // ------------------------------------------------------------ file

    private SourceFile File()
    {
        var pos = Cur.Start;
        QualifiedName? module = null;
        if (Accept(TokenKind.KwModule))
        {
            module = QualifiedName();
            ExpectNewline();
        }
        var uses = new List<UseDecl>();
        while (At(TokenKind.KwUse))
        {
            var upos = Next().Start;
            uses.Add(new UseDecl(upos, QualifiedName()));
            ExpectNewline();
        }
        var decls = new List<Decl>();
        while (!At(TokenKind.EndOfFile))
            decls.Add(Declaration());
        return new SourceFile(pos, module, uses, decls);
    }

    private QualifiedName QualifiedName()
    {
        var parts = new List<string> { Expect(TokenKind.Identifier, "a module name").Text };
        while (Accept(TokenKind.Dot))
            parts.Add(Expect(TokenKind.Identifier, "a module name segment").Text);
        return new QualifiedName(parts);
    }

    // ------------------------------------------------------------ declarations

    private Decl Declaration()
    {
        var pos = Cur.Start;
        var isPrivate = Accept(TokenKind.KwPrivate);
        switch (Kind)
        {
            case TokenKind.KwRecord:
            case TokenKind.KwEntity:
                return RecordDecl(pos, isPrivate, Next().Kind == TokenKind.KwEntity);
            case TokenKind.KwUnion:
                Next();
                return UnionDecl(pos, isPrivate);
            case TokenKind.KwFn:
            case TokenKind.KwHandler:
                return FnDecl(pos, isPrivate);
            case TokenKind.KwExtern:
                Next();
                return ExternDecl(pos, isPrivate);
            case TokenKind.KwScoped:
            case TokenKind.KwResource:
            case TokenKind.KwService:
                return ServiceDecl(pos, isPrivate);
            case TokenKind.KwShape:
                Next();
                return ShapeDecl(pos, isPrivate);
            case TokenKind.KwInstance:
                Next();
                return InstanceDecl(pos, isPrivate);
            case TokenKind.KwRoot:
                Next();
                return RootDecl(pos, isPrivate);
            default:
                throw Error($"expected a declaration but found {Describe(Cur)}");
        }
    }

    /// <summary>Constraints parsed by the last <see cref="TypeParams"/> call: parameter name to shape names.</summary>
    private Dictionary<string, IReadOnlyList<string>> _lastConstraints = new();

    /// <summary>Optional [T, U] after a declaration name; a parameter may carry constraints, [T: Ord, K: Monoid Show].</summary>
    private List<string> TypeParams()
    {
        var ps = new List<string>();
        _lastConstraints = new Dictionary<string, IReadOnlyList<string>>();
        if (!Accept(TokenKind.LBracket)) return ps;
        while (!At(TokenKind.RBracket))
        {
            var name = Expect(TokenKind.TypeName, "a type parameter name").Text;
            ps.Add(name);
            if (Accept(TokenKind.Colon))
            {
                var shapes = new List<string> { Expect(TokenKind.TypeName, "a shape name after ':'").Text };
                while (At(TokenKind.TypeName)) shapes.Add(Next().Text);
                _lastConstraints[name] = shapes;
            }
            if (!Accept(TokenKind.Comma)) break;
        }
        Expect(TokenKind.RBracket);
        return ps;
    }

    private InstanceDecl InstanceDecl(Position pos, bool isPrivate)
    {
        var shape = Expect(TokenKind.TypeName, "a shape name").Text;
        Expect(TokenKind.LBracket, "'[' and the instance's type");
        var target = Type();
        Expect(TokenKind.RBracket, "']' after the instance's type");
        ExpectNewline();
        Expect(TokenKind.Indent, "an indented list of fn definitions");
        var methods = new List<FnDecl>();
        while (!At(TokenKind.Dedent))
        {
            var mpos = Cur.Start;
            if (!At(TokenKind.KwFn)) throw Error($"expected 'fn' inside instance but found {Describe(Cur)}");
            methods.Add(FnDecl(mpos, false));
        }
        Next();
        return new InstanceDecl(pos, isPrivate, shape, target, methods);
    }

    private RecordDecl RecordDecl(Position pos, bool isPrivate, bool isEntity)
    {
        var name = Expect(TokenKind.TypeName, "a record name").Text;
        var typeParams = TypeParams();
        var fields = new List<FieldDecl>();
        var inline = false;
        if (At(TokenKind.LParen))
        {
            inline = true;
            foreach (var p in Params())
                fields.Add(new FieldDecl(p.Pos, p.Name, p.Type, null));
        }
        ExpectNewline();
        if (Accept(TokenKind.Indent))
        {
            if (inline) throw Error("a record declared inline cannot also have a field block");
            while (!At(TokenKind.Dedent))
                fields.Add(FieldDecl());
            Next();
        }
        return new RecordDecl(pos, isPrivate, isEntity, name, fields, inline, typeParams);
    }

    private FieldDecl FieldDecl()
    {
        var pos = Cur.Start;
        var name = Expect(TokenKind.Identifier, "a field name").Text;
        Expect(TokenKind.Colon);
        var type = Type();
        ExpectNewline();
        Expr? init = null;
        if (Accept(TokenKind.Indent))
        {
            Expect(TokenKind.KwInit, "'init'");
            Expect(TokenKind.FatArrow);
            init = LineExpr();
            Expect(TokenKind.Dedent, "end of field block");
        }
        return new FieldDecl(pos, name, type, init);
    }

    private UnionDecl UnionDecl(Position pos, bool isPrivate)
    {
        var name = Expect(TokenKind.TypeName, "a union name").Text;
        var typeParams = TypeParams();
        ExpectNewline();
        Expect(TokenKind.Indent, "an indented list of variants");
        var variants = new List<Variant>();
        while (!At(TokenKind.Dedent))
        {
            var vpos = Cur.Start;
            var vname = Expect(TokenKind.TypeName, "a variant name").Text;
            var ps = At(TokenKind.LParen) ? Params() : new List<Param>();
            ExpectNewline();
            variants.Add(new Variant(vpos, vname, ps));
        }
        Next();
        return new UnionDecl(pos, isPrivate, name, variants, typeParams);
    }

    private FnDecl FnDecl(Position pos, bool isPrivate)
    {
        var kind = Next().Kind == TokenKind.KwHandler ? FnKind.Handler : FnKind.Fn;
        var sig = Signature();
        if (kind == FnKind.Handler && sig.IsPure)
            throw new SyntaxException(sig.Pos, "a handler cannot be Pure: handlers are the Write sites", _file);
        ExpectNewline();
        var body = Block();
        return new FnDecl(pos, isPrivate, kind, sig, body);
    }

    private ExternDecl ExternDecl(Position pos, bool isPrivate)
    {
        Expect(TokenKind.KwFn, "'fn' after 'extern'");
        var sig = Signature();
        if (sig.Effects is null)
            throw new SyntaxException(sig.Pos, $"extern '{sig.Name}' has no body, so its effects cannot be inferred: write '! Pure' or '! <effects>'", _file);
        ExpectNewline();
        Expect(TokenKind.Indent, "an indented list of host bindings (csharp \"...\", cpp \"...\", catch <target> \"...\" -> Variant)");
        var bindings = new List<ExternBinding>();
        var catches = new List<ExternCatch>();
        while (!At(TokenKind.Dedent))
        {
            var lpos = Cur.Start;
            var head = Expect(TokenKind.Identifier, "a binding line").Text;
            if (head == "catch")
            {
                var target = Expect(TokenKind.Identifier, "a target name (csharp or cpp)").Text;
                var type = Expect(TokenKind.String, "an exception type name in quotes").Text;
                Expect(TokenKind.Arrow, "'->' and a variant name");
                var variant = Expect(TokenKind.TypeName, "a variant name").Text;
                catches.Add(new ExternCatch(lpos, target, type, variant));
            }
            else
            {
                var symbol = Expect(TokenKind.String, "a host symbol in quotes").Text;
                bindings.Add(new ExternBinding(lpos, head, symbol));
            }
            ExpectNewline();
        }
        Next();
        return new ExternDecl(pos, isPrivate, sig, bindings, catches);
    }

    private FnSignature Signature()
    {
        var pos = Cur.Start;
        var name = Expect(TokenKind.Identifier, "a function name").Text;
        var typeParams = TypeParams();
        var constraints = _lastConstraints.Count > 0 ? _lastConstraints : null;
        var ps = Params();
        Expect(TokenKind.Arrow, "'->' and a return type");
        var ret = Type();
        var effects = Effects();
        return new FnSignature(pos, name, ps, ret, effects, effects is { Count: 0 }, typeParams, constraints);
    }

    /// <summary>
    /// The '!' clause. Returns null when absent, meaning "infer"; an empty list for
    /// '! Pure', the explicit assertion of no effects.
    /// </summary>
    private IReadOnlyList<string>? Effects()
    {
        if (!Accept(TokenKind.Bang)) return null;
        var effects = new List<string>();
        var pure = false;
        while (At(TokenKind.TypeName) && (EffectNames.Contains(Cur.Text) || Cur.Text == "Pure"))
        {
            var t = Next();
            if (t.Text == "Pure") pure = true;
            else effects.Add(t.Text);
        }
        if (pure && effects.Count > 0)
            throw Error("'Pure' cannot be combined with other effects");
        if (!pure && effects.Count == 0)
            throw Error("expected Pure, or one or more of Nondet, Write, Suspend, after '!'");
        return effects;
    }

    private ServiceDecl ServiceDecl(Position pos, bool isPrivate)
    {
        bool scoped = false, resource = false;
        while (At(TokenKind.KwScoped) || At(TokenKind.KwResource))
        {
            if (Next().Kind == TokenKind.KwScoped) scoped = true; else resource = true;
        }
        Expect(TokenKind.KwService, "'service'");
        var name = Expect(TokenKind.TypeName, "a service name").Text;
        var deps = At(TokenKind.LParen) ? Params() : new List<Param>();
        ExpectNewline();
        var methods = new List<FnDecl>();
        if (Accept(TokenKind.Indent))
        {
            while (!At(TokenKind.Dedent))
            {
                var mpos = Cur.Start;
                var mprivate = Accept(TokenKind.KwPrivate);
                if (!At(TokenKind.KwFn)) throw Error($"expected 'fn' inside service but found {Describe(Cur)}");
                methods.Add(FnDecl(mpos, mprivate));
            }
            Next();
        }
        return new ServiceDecl(pos, isPrivate, scoped, name, deps, methods, resource);
    }

    private ShapeDecl ShapeDecl(Position pos, bool isPrivate)
    {
        var name = Expect(TokenKind.TypeName, "a shape name").Text;
        var typeParams = TypeParams();
        if (typeParams.Count > 1) throw Error("a shape over a type takes exactly one type parameter");
        ExpectNewline();
        Expect(TokenKind.Indent, "an indented list of members");
        var members = new List<FnSignature>();
        while (!At(TokenKind.Dedent))
        {
            Expect(TokenKind.KwFn, "'fn'");
            var sig = Signature();
            if (sig.Effects is null)
                throw new SyntaxException(sig.Pos, $"shape member '{sig.Name}' has no body, so its effects cannot be inferred: write '! Pure' or '! <effects>'", _file);
            members.Add(sig);
            ExpectNewline();
        }
        Next();
        return new ShapeDecl(pos, isPrivate, name, members, typeParams);
    }

    private RootDecl RootDecl(Position pos, bool isPrivate)
    {
        var name = Expect(TokenKind.Identifier, "a root name").Text;
        ExpectNewline();
        Expect(TokenKind.Indent, "an indented list of entries");
        var entries = BlockArgs();
        Expect(TokenKind.Dedent);
        return new RootDecl(pos, isPrivate, name, entries);
    }

    private List<Param> Params()
    {
        Expect(TokenKind.LParen);
        var ps = new List<Param>();
        while (!At(TokenKind.RParen))
        {
            var pos = Cur.Start;
            var name = Expect(TokenKind.Identifier, "a parameter name").Text;
            Expect(TokenKind.Colon);
            ps.Add(new Param(pos, name, Type()));
            if (!Accept(TokenKind.Comma)) break;
        }
        Expect(TokenKind.RParen);
        return ps;
    }

    // ------------------------------------------------------------ types

    private TypeRef Type()
    {
        var pos = Cur.Start;
        if (Accept(TokenKind.LParen))
        {
            var items = new List<TypeRef> { Type() };
            while (Accept(TokenKind.Comma)) items.Add(Type());
            Expect(TokenKind.RParen, "')' to close the tuple type");
            if (items.Count == 1) return items[0];
            return new TupleType(pos, items);
        }
        if (Accept(TokenKind.KwFn))
        {
            Expect(TokenKind.LParen);
            var ps = new List<TypeRef>();
            while (!At(TokenKind.RParen))
            {
                ps.Add(Type());
                if (!Accept(TokenKind.Comma)) break;
            }
            Expect(TokenKind.RParen);
            Expect(TokenKind.Arrow, "'->' in function type");
            var ret = Type();
            var effects = Effects();
            return new FnType(pos, ps, ret, effects);
        }
        var name = Expect(TokenKind.TypeName, "a type").Text;
        var args = new List<TypeRef>();
        if (Accept(TokenKind.LBracket))
        {
            while (!At(TokenKind.RBracket))
            {
                args.Add(Type());
                if (!Accept(TokenKind.Comma)) break;
            }
            Expect(TokenKind.RBracket);
        }
        return new NamedType(pos, name, args);
    }

    // ------------------------------------------------------------ blocks and statements

    private Block Block()
    {
        var pos = Cur.Start;
        Expect(TokenKind.Indent, "an indented block");
        var stmts = new List<Stmt>();
        while (!At(TokenKind.Dedent))
            stmts.Add(Statement());
        Next();
        return new Block(pos, stmts);
    }

    private Stmt Statement()
    {
        var pos = Cur.Start;
        if (At(TokenKind.KwUse) && KindAt(1) == TokenKind.Identifier && KindAt(2) == TokenKind.Assign)
        {
            Next();
            var name = Next().Text;
            Next();
            return new UseStmt(pos, name, Value());
        }
        if (At(TokenKind.Identifier) && KindAt(1) == TokenKind.Assign)
        {
            var name = Next().Text;
            Next();
            return new BindingStmt(pos, name, Value());
        }
        if (At(TokenKind.KwFn) || At(TokenKind.KwHandler))
            return new LocalFnStmt(pos, FnDecl(pos, false));
        if (At(TokenKind.LParen) || At(TokenKind.LBracket) || (At(TokenKind.Identifier) && KindAt(1) == TokenKind.At))
        {
            // a destructuring binding if a pattern followed by '=' parses; otherwise an expression line
            var save = _i;
            try
            {
                var pat = PatternExpr();
                if (Accept(TokenKind.Assign)) return new DestructureStmt(pos, pat, Value());
            }
            catch (SyntaxException) { }
            _i = save;
        }
        return new ExprStmt(pos, LineExpr());
    }

    /// <summary>
    /// The value after '=' or ':'. Either an expression on the same line (with optional
    /// indented children), or nothing on the line and a block of children below.
    /// </summary>
    private Expr Value()
    {
        if (!At(TokenKind.Newline)) return LineExpr();
        var pos = Cur.Start;
        Next();
        Expect(TokenKind.Indent, "a value or an indented block");
        var args = BlockArgs();
        Expect(TokenKind.Dedent);
        if (args.Count == 1 && args[0].Name is null) return args[0].Value;
        throw new SyntaxException(pos, "a block value must be a list of '- item' lines or a single expression", _file);
    }

    /// <summary>An expression that ends the line, followed by optional indented children.</summary>
    private Expr LineExpr()
    {
        var e = Expression();
        if (Accept(TokenKind.Newline) && At(TokenKind.Indent))
        {
            Next();
            var args = BlockArgs();
            Expect(TokenKind.Dedent);
            e = AttachBlockArgs(e, args);
        }
        return e;
    }

    private Expr AttachBlockArgs(Expr e, IReadOnlyList<Arg> args) => e switch
    {
        CallExpr c => c with { BlockArgs = c.BlockArgs.Concat(args).ToList() },
        NameExpr or TypeNameExpr or MemberExpr => new CallExpr(e.Pos, e, Array.Empty<TypeRef>(), Array.Empty<Arg>(), args, false),
        _ => throw new SyntaxException(e.Pos, "only a call or a name can take indented children", _file),
    };

    /// <summary>Children of a line: '- item' sequence lines, 'name: value' entries, or plain expressions. Assumes INDENT consumed; stops at DEDENT.</summary>
    private List<Arg> BlockArgs()
    {
        var args = new List<Arg>();
        while (!At(TokenKind.Dedent))
        {
            var pos = Cur.Start;
            if (At(TokenKind.Dash))
            {
                args.Add(new Arg(pos, null, SequenceItems()));
            }
            else if (At(TokenKind.Identifier) && KindAt(1) == TokenKind.Colon)
            {
                var name = Next().Text;
                Next();
                args.Add(new Arg(pos, name, Value()));
            }
            else
            {
                args.Add(new Arg(pos, null, LineExpr()));
            }
        }
        return args;
    }

    /// <summary>Consecutive '- item' or '- key: value' lines. All must be the same kind.</summary>
    private Expr SequenceItems()
    {
        var pos = Cur.Start;
        var items = new List<Expr>();
        var entries = new List<MapEntry>();
        while (At(TokenKind.Dash))
        {
            var ipos = Cur.Start;
            Next();
            var head = Expression();
            if (Accept(TokenKind.Colon))
            {
                if (items.Count > 0) throw new SyntaxException(ipos, "cannot mix list items and map entries", _file);
                entries.Add(new MapEntry(ipos, head, Value()));
            }
            else
            {
                if (entries.Count > 0) throw new SyntaxException(ipos, "cannot mix list items and map entries", _file);
                if (Accept(TokenKind.Newline) && At(TokenKind.Indent))
                {
                    Next();
                    var children = BlockArgs();
                    Expect(TokenKind.Dedent);
                    head = AttachBlockArgs(head, children);
                }
                items.Add(head);
            }
        }
        return entries.Count > 0
            ? new MapLit(pos, entries, true)
            : new ListLit(pos, items, true);
    }

    // ------------------------------------------------------------ expressions

    private Expr Expression() => Or();

    private Expr Or()
    {
        var left = And();
        while (At(TokenKind.KwOr))
        {
            var pos = Next().Start;
            left = new BinaryExpr(pos, "or", left, And());
        }
        return left;
    }

    private Expr And()
    {
        var left = Not();
        while (At(TokenKind.KwAnd))
        {
            var pos = Next().Start;
            left = new BinaryExpr(pos, "and", left, Not());
        }
        return left;
    }

    private Expr Not()
    {
        if (At(TokenKind.KwNot))
        {
            var pos = Next().Start;
            return new UnaryExpr(pos, "not", Not());
        }
        if (At(TokenKind.KwSupervise))
        {
            // supervise expr: a supervisor point; the operand's panic becomes Error(Panic(message))
            var pos = Next().Start;
            return new UnaryExpr(pos, "supervise", Not());
        }
        return Comparison();
    }

    private Expr Comparison()
    {
        var left = Additive();
        while (true)
        {
            string? op = Kind switch
            {
                TokenKind.Eq => "==",
                TokenKind.Neq => "!=",
                TokenKind.Lt => "<",
                TokenKind.Le => "<=",
                TokenKind.Gt => ">",
                TokenKind.Ge => ">=",
                _ => null,
            };
            if (op is null) return left;
            var pos = Next().Start;
            left = new BinaryExpr(pos, op, left, Additive());
        }
    }

    private Expr Additive()
    {
        var left = Multiplicative();
        while (At(TokenKind.Plus) || At(TokenKind.Dash))
        {
            var t = Next();
            left = new BinaryExpr(t.Start, t.Text, left, Multiplicative());
        }
        return left;
    }

    private Expr Multiplicative()
    {
        var left = Unary();
        while (At(TokenKind.Star) || At(TokenKind.Slash) || At(TokenKind.Percent))
        {
            var t = Next();
            left = new BinaryExpr(t.Start, t.Text, left, Unary());
        }
        return left;
    }

    private Expr Unary()
    {
        if (At(TokenKind.Dash))
        {
            var pos = Next().Start;
            return new UnaryExpr(pos, "-", Unary());
        }
        return Postfix();
    }

    private Expr Postfix()
    {
        var e = Primary();
        while (true)
        {
            if (At(TokenKind.Dot))
            {
                Next();
                var name = At(TokenKind.TypeName) || At(TokenKind.Integer)
                    ? Next().Text
                    : Expect(TokenKind.Identifier, "a member name").Text;
                e = new MemberExpr(e.Pos, e, name);
            }
            else if (At(TokenKind.LParen))
            {
                e = new CallExpr(e.Pos, e, Array.Empty<TypeRef>(), CallArgs(), Array.Empty<Arg>(), true);
            }
            else if (At(TokenKind.LBracket) && e is NameExpr or MemberExpr or TypeNameExpr)
            {
                // explicit type arguments on a call: f[T](x)
                Next();
                var targs = new List<TypeRef>();
                while (!At(TokenKind.RBracket))
                {
                    targs.Add(Type());
                    if (!Accept(TokenKind.Comma)) break;
                }
                Expect(TokenKind.RBracket);
                if (!At(TokenKind.LParen)) throw Error("expected '(' after type arguments");
                e = new CallExpr(e.Pos, e, targs, CallArgs(), Array.Empty<Arg>(), true);
            }
            else if (At(TokenKind.Question))
            {
                Next();
                e = new PropagateExpr(e.Pos, e);
            }
            else if (At(TokenKind.KwWith))
            {
                Next();
                return With(e);
            }
            else return e;
        }
    }

    private List<Arg> CallArgs()
    {
        Expect(TokenKind.LParen);
        var args = new List<Arg>();
        while (!At(TokenKind.RParen))
        {
            var pos = Cur.Start;
            if (At(TokenKind.Identifier) && KindAt(1) == TokenKind.Colon)
            {
                var name = Next().Text;
                Next();
                args.Add(new Arg(pos, name, Expression()));
            }
            else args.Add(new Arg(pos, null, Expression()));
            if (!Accept(TokenKind.Comma)) break;
        }
        Expect(TokenKind.RParen);
        return args;
    }

    private Expr Primary()
    {
        var t = Cur;
        switch (t.Kind)
        {
            case TokenKind.Identifier: Next(); return new NameExpr(t.Start, t.Text);
            case TokenKind.TypeName: Next(); return new TypeNameExpr(t.Start, t.Text);
            case TokenKind.Integer: Next(); return new IntLit(t.Start, t.Text);
            case TokenKind.Float: Next(); return new FloatLit(t.Start, t.Text);
            case TokenKind.String: Next(); return new StringLit(t.Start, t.Text);
            case TokenKind.KwTrue: Next(); return new BoolLit(t.Start, true);
            case TokenKind.KwFalse: Next(); return new BoolLit(t.Start, false);
            case TokenKind.LParen:
            {
                Next();
                var inner = Expression();
                if (At(TokenKind.Comma))
                {
                    var items = new List<Expr> { inner };
                    while (Accept(TokenKind.Comma)) items.Add(Expression());
                    Expect(TokenKind.RParen, "')' to close the tuple");
                    return new TupleLit(t.Start, items);
                }
                Expect(TokenKind.RParen);
                return inner;
            }
            case TokenKind.LBracket:
            {
                Next();
                var items = new List<Expr>();
                while (!At(TokenKind.RBracket))
                {
                    items.Add(Expression());
                    if (!Accept(TokenKind.Comma)) break;
                }
                Expect(TokenKind.RBracket);
                return new ListLit(t.Start, items, false);
            }
            case TokenKind.LBrace:
            {
                Next();
                var entries = new List<MapEntry>();
                while (!At(TokenKind.RBrace))
                {
                    var epos = Cur.Start;
                    var key = Expression();
                    Expect(TokenKind.Colon);
                    entries.Add(new MapEntry(epos, key, Expression()));
                    if (!Accept(TokenKind.Comma)) break;
                }
                Expect(TokenKind.RBrace);
                return new MapLit(t.Start, entries, false);
            }
            case TokenKind.Backslash: return Lambda();
            case TokenKind.KwIf: return If();
            case TokenKind.KwMatch: return Match();
            default:
                throw Error($"expected an expression but found {Describe(t)}");
        }
    }

    private LambdaExpr Lambda()
    {
        var pos = Expect(TokenKind.Backslash).Start;
        var ps = new List<LambdaParam>();
        var typed = false;
        if (At(TokenKind.LParen))
        {
            typed = true;
            foreach (var p in Params()) ps.Add(new LambdaParam(p.Pos, p.Name, p.Type));
        }
        else
        {
            while (At(TokenKind.Identifier))
            {
                var p = Next();
                ps.Add(new LambdaParam(p.Start, p.Text, null));
                if (!Accept(TokenKind.Comma)) break;
            }
        }
        Expect(TokenKind.Arrow, "'->' after lambda parameters");
        if (At(TokenKind.Newline))
        {
            Next();
            return new LambdaExpr(pos, ps, typed, Block(), false);
        }
        var body = Expression();
        return new LambdaExpr(pos, ps, typed, new Block(body.Pos, new[] { new ExprStmt(body.Pos, body) }), true);
    }

    private IfExpr If()
    {
        var pos = Expect(TokenKind.KwIf).Start;
        var cond = Expression();
        var hadThen = Accept(TokenKind.KwThen);
        if (At(TokenKind.Newline))
        {
            Next();
            var thenBlock = Block();
            Block? elseBlock = null;
            if (Accept(TokenKind.KwElse))
            {
                if (At(TokenKind.KwIf))
                {
                    var nested = If();
                    elseBlock = new Block(nested.Pos, new[] { new ExprStmt(nested.Pos, nested) });
                }
                else
                {
                    ExpectNewline();
                    elseBlock = Block();
                }
            }
            return new IfExpr(pos, cond, thenBlock, elseBlock, false);
        }
        if (!hadThen) throw Error("expected 'then'");
        var thenExpr = Expression();
        Expect(TokenKind.KwElse, "'else' in inline if");
        var elseExpr = Expression();
        return new IfExpr(
            pos, cond,
            new Block(thenExpr.Pos, new[] { new ExprStmt(thenExpr.Pos, thenExpr) }),
            new Block(elseExpr.Pos, new[] { new ExprStmt(elseExpr.Pos, elseExpr) }),
            true);
    }

    private MatchExpr Match()
    {
        var pos = Expect(TokenKind.KwMatch).Start;
        var scrutinee = Expression();
        ExpectNewline();
        Expect(TokenKind.Indent, "indented match arms");
        var arms = new List<MatchArm>();
        while (!At(TokenKind.Dedent))
        {
            var apos = Cur.Start;
            var pat = PatternExpr();
            Expr? guard = null;
            if (Accept(TokenKind.KwIf)) guard = Expression();
            Expect(TokenKind.FatArrow, "'=>' after pattern");
            if (At(TokenKind.Newline))
            {
                Next();
                arms.Add(new MatchArm(apos, pat, Block(), false, guard));
            }
            else
            {
                var body = LineExpr();
                arms.Add(new MatchArm(apos, pat, new Block(body.Pos, new[] { new ExprStmt(body.Pos, body) }), true, guard));
            }
        }
        Next();
        return new MatchExpr(pos, scrutinee, arms);
    }

    private Pattern PatternExpr()
    {
        var t = Cur;
        switch (t.Kind)
        {
            case TokenKind.LBracket:
            {
                Next();
                var items = new List<Pattern>();
                Pattern? rest = null;
                while (!At(TokenKind.RBracket))
                {
                    if (Accept(TokenKind.Ellipsis))
                    {
                        var rt = Cur;
                        if (rt.Kind != TokenKind.Identifier) throw Error("expected a name or '_' after '...'");
                        Next();
                        rest = rt.Text == "_" ? new WildcardPattern(rt.Start) : new BindPattern(rt.Start, rt.Text);
                        Accept(TokenKind.Comma);
                        break;
                    }
                    items.Add(PatternExpr());
                    if (!Accept(TokenKind.Comma)) break;
                }
                Expect(TokenKind.RBracket, "']' to close the list pattern");
                return new ListPattern(t.Start, items, rest);
            }
            case TokenKind.LParen:
            {
                Next();
                var items = new List<Pattern> { PatternExpr() };
                while (Accept(TokenKind.Comma)) items.Add(PatternExpr());
                Expect(TokenKind.RParen, "')' to close the tuple pattern");
                if (items.Count == 1) return items[0];
                return new TuplePattern(t.Start, items);
            }
            case TokenKind.Identifier when KindAt(1) == TokenKind.At:
            {
                Next(); Next();
                return new AsPattern(t.Start, t.Text, PatternExpr());
            }
            case TokenKind.TypeName:
            {
                Next();
                var args = new List<Pattern>();
                var named = new List<(string, Pattern)>();
                var rest = false;
                if (Accept(TokenKind.LParen))
                {
                    while (!At(TokenKind.RParen))
                    {
                        if (Accept(TokenKind.Ellipsis))
                        {
                            // ... ignores the fields not named: only at the end
                            rest = true;
                            Accept(TokenKind.Comma);
                            break;
                        }
                        if (At(TokenKind.Identifier) && KindAt(1) == TokenKind.Colon)
                        {
                            var field = Next().Text;
                            Next();
                            named.Add((field, PatternExpr()));
                        }
                        else
                        {
                            if (named.Count > 0) throw Error("positional patterns must come before named ones");
                            args.Add(PatternExpr());
                        }
                        if (!Accept(TokenKind.Comma)) break;
                    }
                    Expect(TokenKind.RParen);
                }
                return new VariantPattern(t.Start, t.Text, args, named.Count > 0 ? named : null, rest);
            }
            case TokenKind.Identifier:
                Next();
                return t.Text == "_" ? new WildcardPattern(t.Start) : new BindPattern(t.Start, t.Text);
            case TokenKind.Integer: Next(); return new LiteralPattern(t.Start, new IntLit(t.Start, t.Text));
            case TokenKind.String: Next(); return new LiteralPattern(t.Start, new StringLit(t.Start, t.Text));
            case TokenKind.KwTrue: Next(); return new LiteralPattern(t.Start, new BoolLit(t.Start, true));
            case TokenKind.KwFalse: Next(); return new LiteralPattern(t.Start, new BoolLit(t.Start, false));
            default:
                throw Error($"expected a pattern but found {Describe(t)}");
        }
    }

    private WithExpr With(Expr target)
    {
        var pos = target.Pos;
        var fields = new List<WithField>();
        if (Accept(TokenKind.LBrace))
        {
            while (!At(TokenKind.RBrace))
            {
                var fpos = Cur.Start;
                var path = FieldPath();
                Expect(TokenKind.Colon);
                fields.Add(new WithField(fpos, path, Expression()));
                if (!Accept(TokenKind.Comma)) break;
            }
            Expect(TokenKind.RBrace);
            return new WithExpr(pos, target, fields, false);
        }
        ExpectNewline();
        Expect(TokenKind.Indent, "indented 'path: value' lines after 'with'");
        while (!At(TokenKind.Dedent))
        {
            var fpos = Cur.Start;
            var path = FieldPath();
            Expect(TokenKind.Colon);
            fields.Add(new WithField(fpos, path, Value()));
        }
        Next();
        return new WithExpr(pos, target, fields, true);
    }

    private List<string> FieldPath()
    {
        var path = new List<string> { Expect(TokenKind.Identifier, "a field name").Text };
        while (Accept(TokenKind.Dot))
            path.Add(Expect(TokenKind.Identifier, "a field name").Text);
        return path;
    }
}
