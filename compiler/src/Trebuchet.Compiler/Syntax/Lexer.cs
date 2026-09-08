using System.Text;

namespace Trebuchet.Compiler.Syntax;

/// <summary>
/// Indentation-aware lexer. Emits NEWLINE at the end of each non-blank line and
/// INDENT / DEDENT tokens when the leading whitespace of the next line changes.
/// Newlines inside brackets, parentheses, and braces are ignored so an argument
/// list may span lines.
/// </summary>
public sealed class Lexer
{
    private static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        ["module"] = TokenKind.KwModule,
        ["use"] = TokenKind.KwUse,
        ["record"] = TokenKind.KwRecord,
        ["entity"] = TokenKind.KwEntity,
        ["union"] = TokenKind.KwUnion,
        ["fn"] = TokenKind.KwFn,
        ["handler"] = TokenKind.KwHandler,
        ["service"] = TokenKind.KwService,
        ["scoped"] = TokenKind.KwScoped,
        ["shape"] = TokenKind.KwShape,
        ["root"] = TokenKind.KwRoot,
        ["match"] = TokenKind.KwMatch,
        ["with"] = TokenKind.KwWith,
        ["if"] = TokenKind.KwIf,
        ["then"] = TokenKind.KwThen,
        ["else"] = TokenKind.KwElse,
        ["init"] = TokenKind.KwInit,
        ["and"] = TokenKind.KwAnd,
        ["or"] = TokenKind.KwOr,
        ["not"] = TokenKind.KwNot,
        ["true"] = TokenKind.KwTrue,
        ["false"] = TokenKind.KwFalse,
        ["private"] = TokenKind.KwPrivate,
        ["extern"] = TokenKind.KwExtern,
        ["resource"] = TokenKind.KwResource,
        ["supervise"] = TokenKind.KwSupervise,
    };

    private readonly string _src;
    private readonly string? _file;
    private int _pos;
    private int _line = 1;
    private int _col = 1;
    private int _bracketDepth;
    private readonly Stack<int> _indents = new();
    private readonly List<Token> _out = new();
    private readonly List<Comment> _comments = new();
    private bool _lineHasTokens;

    /// <summary>Comments seen while lexing, in order. OwnLine is true when nothing but the comment is on the line.</summary>
    public IReadOnlyList<Comment> Comments => _comments;

    public Lexer(string source, string? file = null)
    {
        _src = source.Replace("\r\n", "\n");
        _file = file;
        _indents.Push(0);
    }

    public static IReadOnlyList<Token> Tokenize(string source, string? file = null) => new Lexer(source, file).Run();

    public static (IReadOnlyList<Token> tokens, IReadOnlyList<Comment> comments) TokenizeWithComments(string source, string? file = null)
    {
        var lexer = new Lexer(source, file);
        var tokens = lexer.Run();
        return (tokens, lexer.Comments);
    }

    private IReadOnlyList<Token> Run()
    {
        StartOfLine();
        while (_pos < _src.Length)
        {
            var c = _src[_pos];
            if (c == '\n')
            {
                EndOfLine();
                Advance();
                StartOfLine();
                continue;
            }
            if (c == ' ' || c == '\t') { Advance(); continue; }
            if (c == '/' && Peek(1) == '/') { SkipComment(); continue; }

            LexToken();
        }
        EndOfLine();
        while (_indents.Count > 1)
        {
            _indents.Pop();
            Emit(TokenKind.Dedent, "");
        }
        Emit(TokenKind.EndOfFile, "");
        return _out;
    }

    private void StartOfLine()
    {
        if (_bracketDepth > 0) return;

        // Measure leading whitespace; skip blank and comment-only lines entirely.
        var i = _pos;
        var width = 0;
        while (i < _src.Length && (_src[i] == ' ' || _src[i] == '\t'))
        {
            if (_src[i] == '\t') throw Error("tabs are not permitted for indentation; use spaces");
            width++; i++;
        }
        var blank = i >= _src.Length || _src[i] == '\n' || (_src[i] == '/' && i + 1 < _src.Length && _src[i + 1] == '/');
        if (blank) return;

        var current = _indents.Peek();
        if (width > current)
        {
            _indents.Push(width);
            Emit(TokenKind.Indent, "", new Position(_line, 1));
        }
        else
        {
            while (width < _indents.Peek())
            {
                _indents.Pop();
                Emit(TokenKind.Dedent, "", new Position(_line, 1));
            }
            if (width != _indents.Peek())
                throw Error($"unindent does not match any outer indentation level (column {width + 1})");
        }
    }

    private void EndOfLine()
    {
        if (_bracketDepth > 0) return;
        if (_lineHasTokens)
        {
            Emit(TokenKind.Newline, "");
            _lineHasTokens = false;
        }
    }

    private void SkipComment()
    {
        var start = _pos;
        var line = _line;
        while (_pos < _src.Length && _src[_pos] != '\n') Advance();
        _comments.Add(new Comment(line, _src[(start + 2).._pos].TrimEnd(), OwnLine: !_lineHasTokens));
    }

    private void LexToken()
    {
        var start = new Position(_line, _col);
        var c = _src[_pos];

        if (char.IsLetter(c) || c == '_')
        {
            var sb = new StringBuilder();
            while (_pos < _src.Length && (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_'))
            {
                sb.Append(_src[_pos]);
                Advance();
            }
            var word = sb.ToString();
            if (Keywords.TryGetValue(word, out var kw)) Emit(kw, word, start);
            else if (char.IsUpper(word[0])) Emit(TokenKind.TypeName, word, start);
            else Emit(TokenKind.Identifier, word, start);
            return;
        }

        if (char.IsDigit(c))
        {
            var sb = new StringBuilder();
            var isFloat = false;
            while (_pos < _src.Length && char.IsDigit(_src[_pos])) { sb.Append(_src[_pos]); Advance(); }
            if (_pos < _src.Length && _src[_pos] == '.' && _pos + 1 < _src.Length && char.IsDigit(_src[_pos + 1]))
            {
                isFloat = true;
                sb.Append('.'); Advance();
                while (_pos < _src.Length && char.IsDigit(_src[_pos])) { sb.Append(_src[_pos]); Advance(); }
            }
            Emit(isFloat ? TokenKind.Float : TokenKind.Integer, sb.ToString(), start);
            return;
        }

        if (c == '"')
        {
            Advance();
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= _src.Length || _src[_pos] == '\n') throw Error("unterminated string literal", start);
                var ch = _src[_pos];
                if (ch == '"') { Advance(); break; }
                if (ch == '\\')
                {
                    Advance();
                    if (_pos >= _src.Length) throw Error("unterminated escape", start);
                    sb.Append(_src[_pos] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        '"' => '"',
                        '\\' => '\\',
                        var e => throw Error($"unknown escape '\\{e}'"),
                    });
                    Advance();
                    continue;
                }
                sb.Append(ch);
                Advance();
            }
            Emit(TokenKind.String, sb.ToString(), start);
            return;
        }

        // two-character operators first
        var two = _pos + 1 < _src.Length ? _src.Substring(_pos, 2) : "";
        TokenKind? kind2 = two switch
        {
            "->" => TokenKind.Arrow,
            "=>" => TokenKind.FatArrow,
            "==" => TokenKind.Eq,
            "!=" => TokenKind.Neq,
            "<=" => TokenKind.Le,
            ">=" => TokenKind.Ge,
            _ => null,
        };
        if (kind2 is { } k2)
        {
            Advance(); Advance();
            Emit(k2, two, start);
            return;
        }

        TokenKind kind1 = c switch
        {
            '(' => TokenKind.LParen,
            ')' => TokenKind.RParen,
            '[' => TokenKind.LBracket,
            ']' => TokenKind.RBracket,
            '{' => TokenKind.LBrace,
            '}' => TokenKind.RBrace,
            ',' => TokenKind.Comma,
            ':' => TokenKind.Colon,
            '.' => TokenKind.Dot,
            '\\' => TokenKind.Backslash,
            '!' => TokenKind.Bang,
            '?' => TokenKind.Question,
            '-' => TokenKind.Dash,
            '=' => TokenKind.Assign,
            '+' => TokenKind.Plus,
            '*' => TokenKind.Star,
            '/' => TokenKind.Slash,
            '%' => TokenKind.Percent,
            '<' => TokenKind.Lt,
            '>' => TokenKind.Gt,
            _ => throw Error($"unexpected character '{c}'"),
        };
        Advance();
        if (kind1 is TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace) _bracketDepth++;
        if (kind1 is TokenKind.RParen or TokenKind.RBracket or TokenKind.RBrace)
        {
            if (_bracketDepth == 0) throw Error($"unbalanced '{c}'", start);
            _bracketDepth--;
        }
        Emit(kind1, c.ToString(), start);
    }

    private char Peek(int ahead) => _pos + ahead < _src.Length ? _src[_pos + ahead] : '\0';

    private void Advance()
    {
        if (_src[_pos] == '\n') { _line++; _col = 1; }
        else _col++;
        _pos++;
    }

    private void Emit(TokenKind kind, string text, Position? at = null)
    {
        _out.Add(new Token(kind, text, at ?? new Position(_line, _col)));
        if (kind is not (TokenKind.Indent or TokenKind.Dedent or TokenKind.Newline or TokenKind.EndOfFile))
            _lineHasTokens = true;
    }

    private SyntaxException Error(string message, Position? at = null) =>
        new(at ?? new Position(_line, _col), message, _file);
}
