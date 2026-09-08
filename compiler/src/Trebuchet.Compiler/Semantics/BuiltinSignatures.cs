using Trebuchet.Compiler.Syntax;

namespace Trebuchet.Compiler.Semantics;

/// <summary>
/// Typed signatures for the prototype standard library, written in the language's own
/// type syntax with a leading [T, U] list of type parameters. Kept in step with
/// <see cref="Runtime.Builtins"/> by hand until the stdlib is written in Trebuchet.
/// </summary>
public static class BuiltinSignatures
{
    public static readonly RecordT ArgumentError = new("ArgumentError", false) { Fields = { ("message", PrimT.String) } };
    /// <summary>What <c>supervise</c> yields as the error of a caught panic.</summary>
    public static readonly RecordT Panic = new("Panic", false) { Fields = { ("message", PrimT.String) } };

    public static Scope CreateBuiltinScope()
    {
        var g = new Scope(null, "builtins");
        foreach (var p in new[] { PrimT.Int, PrimT.Float, PrimT.Bool, PrimT.String, PrimT.Unit, PrimT.Instant, PrimT.Never })
            g.DefineType(p.Name, p);
        g.DefineType("ArgumentError", ArgumentError);
        g.DefineType("Panic", Panic);

        void Def(string name, params string[] sigs)
        {
            var alts = sigs.Select(s => Parse(s)).ToList();
            g.DefineValue(name, new ValueSym(alts.Count == 1 ? alts[0] : new OverloadT(name, alts)));
        }

        g.DefineValue("unit", new ValueSym(PrimT.Unit));
        g.DefineValue("None", new ValueSym(new AppT("Option", new TType[] { new VarT("t") })));
        Def("Some", "[T](T) -> Option[T]");
        Def("ok", "[T, E](T) -> Result[T, E]", "[E]() -> Result[Unit, E]");
        Def("error", "[T, E](E) -> Result[T, E]");
        Def("Ok", "[T, E](T) -> Result[T, E]");
        Def("Error", "[T, E](E) -> Result[T, E]");
        Def("fail", "[T](T) -> Never");
        g.DefineValue("ArgumentError", new ValueSym(new FnT(new TType[] { PrimT.String }, ArgumentError, new HashSet<string>(), new[] { "message" }, constructs: ArgumentError)));
        Def("print", "[T](T) -> Unit ! Write");
        Def("toString", "[T](T) -> String ! Pure");

        Def("mapError", "[T, E, F](Result[T, E], fn(E) -> F) -> Result[T, F] ! Pure");
        Def("isOk", "[T, E](Result[T, E]) -> Bool ! Pure");
        Def("isError", "[T, E](Result[T, E]) -> Bool ! Pure");
        Def("getOr", "[K, V](Map[K, V], K, V) -> V ! Pure", "[T](Option[T], T) -> T ! Pure", "[T, E](Result[T, E], T) -> T ! Pure");

        Def("append", "[T](Vector[T], T) -> Vector[T] ! Pure");
        Def("concat", "[T](Vector[T], Vector[T]) -> Vector[T] ! Pure");
        Def("length", "[T](Vector[T]) -> Int ! Pure", "(String) -> Int ! Pure", "[K, V](Map[K, V]) -> Int ! Pure", "[T](Set[T]) -> Int ! Pure");
        Def("isEmpty", "[T](Vector[T]) -> Bool ! Pure", "(String) -> Bool ! Pure", "[K, V](Map[K, V]) -> Bool ! Pure", "[T](Set[T]) -> Bool ! Pure");
        Def("map", "[T, U](Vector[T], fn(T) -> U) -> Vector[U]", "[T, E, U](Result[T, E], fn(T) -> U) -> Result[U, E]", "[T, U](Option[T], fn(T) -> U) -> Option[U]");
        Def("filter", "[T](Vector[T], fn(T) -> Bool) -> Vector[T]");
        Def("fold", "[T, A](Vector[T], A, fn(A, T) -> A) -> A");
        Def("any", "[T](Vector[T], fn(T) -> Bool) -> Bool");
        Def("all", "[T](Vector[T], fn(T) -> Bool) -> Bool");
        Def("find", "[T](Vector[T], fn(T) -> Bool) -> Option[T]");
        Def("forEach", "[T, U](Vector[T], fn(T) -> U) -> Unit");
        Def("first", "[T](Vector[T]) -> Option[T] ! Pure");
        Def("last", "[T](Vector[T]) -> Option[T] ! Pure");
        Def("reverse", "[T](Vector[T]) -> Vector[T] ! Pure");
        Def("contains", "[T](Vector[T], T) -> Bool ! Pure", "[K, V](Map[K, V], K) -> Bool ! Pure", "(String, String) -> Bool ! Pure", "[T](Set[T], T) -> Bool ! Pure");
        Def("sum", "(Vector[Int]) -> Int ! Pure");
        Def("take", "[T](Vector[T], Int) -> Vector[T] ! Pure");
        Def("drop", "[T](Vector[T], Int) -> Vector[T] ! Pure");
        Def("at", "[T](Vector[T], Int) -> Option[T] ! Pure");
        Def("sortBy", "[T, K](Vector[T], fn(T) -> K) -> Vector[T]");
        Def("traverse", "[T, U, E](Vector[T], fn(T) -> Result[U, E]) -> Result[Vector[U], E]");

        Def("get", "[T](Cell[T]) -> T ! Nondet", "[K, V](Map[K, V], K) -> Option[V] ! Pure");
        Def("set", "[T](Cell[T], T) -> Unit ! Write", "[K, V](Map[K, V], K, V) -> Map[K, V] ! Pure");
        Def("remove", "[K, V](Map[K, V], K) -> Map[K, V] ! Pure", "[T](Set[T], T) -> Set[T] ! Pure");

        Def("toSet", "[T](Vector[T]) -> Set[T] ! Pure");
        Def("add", "[T](Set[T], T) -> Set[T] ! Pure");
        Def("items", "[T](Set[T]) -> Vector[T] ! Pure");
        Def("merge", "[T](Set[T], Set[T]) -> Set[T] ! Pure");
        Def("intersect", "[T](Set[T], Set[T]) -> Set[T] ! Pure");
        Def("difference", "[T](Set[T], Set[T]) -> Set[T] ! Pure");
        Def("keys", "[K, V](Map[K, V]) -> Vector[K] ! Pure");
        Def("values", "[K, V](Map[K, V]) -> Vector[V] ! Pure");
        Def("update", "[T](Cell[T], fn(T) -> T) -> Unit ! Write");
        Def("getAndUpdate", "[T](Cell[T], fn(T) -> T) -> T ! Nondet Write");

        Def("trim", "(String) -> String ! Pure");
        Def("toUpper", "(String) -> String ! Pure");
        Def("toLower", "(String) -> String ! Pure");
        Def("startsWith", "(String, String) -> Bool ! Pure");
        Def("uuid", "() -> String ! Nondet");
        Def("sleep", "(Int) -> Unit ! Suspend");

        Namespace(g, "Cell", ("new", "[T](T) -> Cell[T] ! Nondet"));
        Namespace(g, "Instant", ("parse", "(String) -> Instant ! Pure"), ("now", "() -> Instant ! Nondet"));
        Namespace(g, "sys", ("clock", "() -> Instant ! Nondet"));
        Namespace(g, "env", ("get", "(String) -> String ! Nondet"));
        Namespace(g, "json", ("encode", "[T](T) -> String ! Pure"), ("decode", "[T](String) -> T ! Pure"));
        return g;
    }

    private static void Namespace(Scope g, string name, params (string member, string sig)[] members)
    {
        var scope = new Scope(null, name);
        foreach (var (m, sig) in members) scope.DefineValue(m, new ValueSym(Parse(sig)));
        g.DefineValue(name, new NamespaceSym(scope));
    }

    // ------------------------------------------------------------ signature parser

    public static FnT Parse(string sig)
    {
        var toks = Lexer.Tokenize(sig).Where(t => t.Kind is not (TokenKind.Newline or TokenKind.Indent or TokenKind.Dedent)).ToList();
        var p = new SigParser(toks);
        return p.Signature();
    }

    private sealed class SigParser
    {
        private readonly List<Token> _t;
        private int _i;
        private readonly List<string> _typeParams = new();
        public SigParser(List<Token> t) => _t = t;

        private Token Cur => _t[_i];
        private bool At(TokenKind k) => Cur.Kind == k;
        private Token Next() => _t[_i++];
        private Token Expect(TokenKind k)
        {
            if (!At(k)) throw new InvalidOperationException($"builtin signature: expected {k} but found {Cur} in '{string.Join(" ", _t.Select(x => x.Text))}'");
            return Next();
        }

        public FnT Signature()
        {
            if (At(TokenKind.LBracket))
            {
                Next();
                while (!At(TokenKind.RBracket))
                {
                    _typeParams.Add(Expect(TokenKind.TypeName).Text);
                    if (At(TokenKind.Comma)) Next();
                }
                Next();
            }
            var fn = FnRest();
            return new FnT(fn.Params, fn.Return, fn.Effects, null, false, _typeParams.ToList());
        }

        private FnT FnRest()
        {
            Expect(TokenKind.LParen);
            var ps = new List<TType>();
            while (!At(TokenKind.RParen))
            {
                ps.Add(Type());
                if (At(TokenKind.Comma)) Next();
            }
            Next();
            Expect(TokenKind.Arrow);
            var ret = Type();
            IReadOnlySet<string>? effects = null;
            if (At(TokenKind.Bang))
            {
                Next();
                var set = new HashSet<string>();
                while (At(TokenKind.TypeName))
                {
                    var name = Next().Text;
                    if (name != "Pure") set.Add(name);
                }
                effects = set;
            }
            return new FnT(ps, ret, effects);
        }

        private TType Type()
        {
            if (At(TokenKind.KwFn))
            {
                Next();
                return FnRest();
            }
            var name = Expect(TokenKind.TypeName).Text;
            if (_typeParams.Contains(name)) return new ParamT(name);
            if (PrimT.ByName(name) is { } prim) return prim;
            if (name == "ArgumentError") return ArgumentError;
            if (name == "Panic") return Panic;
            name = AppT.Normalize(name);
            if (!AppT.Arities.ContainsKey(name)) throw new InvalidOperationException($"builtin signature: unknown type {name}");
            var args = new List<TType>();
            Expect(TokenKind.LBracket);
            while (!At(TokenKind.RBracket))
            {
                args.Add(Type());
                if (At(TokenKind.Comma)) Next();
            }
            Next();
            return new AppT(name, args);
        }
    }
}
