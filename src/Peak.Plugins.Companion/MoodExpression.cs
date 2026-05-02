namespace Peak.Plugins.Companion;

/// <summary>
/// Variable resolver passed into <see cref="MoodExpression.Evaluate"/>.
/// Returns <c>bool</c>, <c>double</c>, <c>string</c>, or <c>null</c>.
/// </summary>
internal interface IMoodContext
{
    object? Get(string name);
}

/// <summary>
/// Tiny expression DSL used in <c>moods.json</c>'s <c>when</c> field. It
/// supports just enough to express realistic mood triggers — kept minimal so
/// the parser stays a single file with no dependency.
///
/// Grammar (top-down precedence):
/// <code>
/// or         := and ('||' and)*
/// and        := not ('&amp;&amp;' not)*
/// not        := '!' not | comparison
/// comparison := primary (compOp primary)?         compOp = == != &gt; &lt; &gt;= &lt;=
/// primary    := number | string | bool | identifier | '(' or ')'
/// </code>
///
/// Coercion rules (matches what users intuitively expect):
/// <list type="bullet">
///   <item><b>truthy</b>: bool-true, non-zero number, non-empty string.</item>
///   <item><b>numeric coerce</b>: bool→0/1, string→parsed double or 0.</item>
///   <item><b>string coerce</b>: ToString().</item>
/// </list>
/// </summary>
internal sealed class MoodExpression
{
    private readonly Node _root;
    public MoodExpression(string source)
    {
        var tokens = new Lexer(source).Tokenize();
        _root = new Parser(tokens).ParseRoot();
    }

    public bool Evaluate(IMoodContext ctx) => ToBool(_root.Eval(ctx));

    // ── Coercion helpers ────────────────────────────────────────────

    private static bool ToBool(object? v) => v switch
    {
        bool b   => b,
        double d => d != 0,
        string s => !string.IsNullOrEmpty(s),
        null     => false,
        _        => true
    };

    private static double ToNum(object? v) => v switch
    {
        double d => d,
        bool b   => b ? 1 : 0,
        string s => double.TryParse(s, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0,
        null     => 0,
        _        => 0
    };

    private static string ToStr(object? v) => v?.ToString() ?? "";

    // ── Tokens / Lexer ──────────────────────────────────────────────

    private enum TokKind { Num, Str, Ident, LParen, RParen, AndAnd, OrOr, Bang, Eq, Neq, Gt, Lt, Gte, Lte, End }
    private record struct Tok(TokKind Kind, string Text, double Num);

    private sealed class Lexer
    {
        private readonly string _s; private int _i;
        public Lexer(string s) { _s = s; _i = 0; }
        public List<Tok> Tokenize()
        {
            var result = new List<Tok>();
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (char.IsWhiteSpace(c)) { _i++; continue; }
                if (c == '(') { result.Add(new Tok(TokKind.LParen, "(", 0)); _i++; continue; }
                if (c == ')') { result.Add(new Tok(TokKind.RParen, ")", 0)); _i++; continue; }
                if (c == '!' && Peek(1) == '=') { result.Add(new Tok(TokKind.Neq, "!=", 0)); _i += 2; continue; }
                if (c == '!') { result.Add(new Tok(TokKind.Bang, "!", 0)); _i++; continue; }
                if (c == '=' && Peek(1) == '=') { result.Add(new Tok(TokKind.Eq, "==", 0)); _i += 2; continue; }
                if (c == '&' && Peek(1) == '&') { result.Add(new Tok(TokKind.AndAnd, "&&", 0)); _i += 2; continue; }
                if (c == '|' && Peek(1) == '|') { result.Add(new Tok(TokKind.OrOr, "||", 0)); _i += 2; continue; }
                if (c == '>' && Peek(1) == '=') { result.Add(new Tok(TokKind.Gte, ">=", 0)); _i += 2; continue; }
                if (c == '<' && Peek(1) == '=') { result.Add(new Tok(TokKind.Lte, "<=", 0)); _i += 2; continue; }
                if (c == '>') { result.Add(new Tok(TokKind.Gt, ">", 0)); _i++; continue; }
                if (c == '<') { result.Add(new Tok(TokKind.Lt, "<", 0)); _i++; continue; }
                if (c == '"' || c == '\'') { result.Add(ReadString(c)); continue; }
                if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek(1)))) { result.Add(ReadNumber()); continue; }
                if (char.IsLetter(c) || c == '_') { result.Add(ReadIdent()); continue; }
                throw new FormatException($"Unexpected character '{c}' at {_i}");
            }
            result.Add(new Tok(TokKind.End, "", 0));
            return result;
        }

        private char Peek(int o) => _i + o < _s.Length ? _s[_i + o] : '\0';

        private Tok ReadString(char quote)
        {
            _i++; // opening quote
            var sb = new System.Text.StringBuilder();
            while (_i < _s.Length && _s[_i] != quote)
            {
                if (_s[_i] == '\\' && _i + 1 < _s.Length) { sb.Append(_s[_i + 1]); _i += 2; }
                else sb.Append(_s[_i++]);
            }
            if (_i < _s.Length) _i++; // closing quote
            return new Tok(TokKind.Str, sb.ToString(), 0);
        }

        private Tok ReadNumber()
        {
            int start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            var text = _s.Substring(start, _i - start);
            var n = double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            return new Tok(TokKind.Num, text, n);
        }

        private Tok ReadIdent()
        {
            int start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
            return new Tok(TokKind.Ident, _s.Substring(start, _i - start), 0);
        }
    }

    // ── AST ─────────────────────────────────────────────────────────

    private abstract record Node { public abstract object? Eval(IMoodContext ctx); }
    private sealed record NumLit(double Value) : Node { public override object? Eval(IMoodContext _) => Value; }
    private sealed record StrLit(string Value) : Node { public override object? Eval(IMoodContext _) => Value; }
    private sealed record BoolLit(bool Value) : Node { public override object? Eval(IMoodContext _) => Value; }
    private sealed record IdentRef(string Name) : Node { public override object? Eval(IMoodContext ctx) => ctx.Get(Name); }
    private sealed record Not(Node Inner) : Node { public override object? Eval(IMoodContext ctx) => !ToBool(Inner.Eval(ctx)); }

    private sealed record BinOp(TokKind Op, Node Left, Node Right) : Node
    {
        public override object? Eval(IMoodContext ctx)
        {
            // Short-circuit on logical ops.
            if (Op == TokKind.AndAnd) return ToBool(Left.Eval(ctx)) && ToBool(Right.Eval(ctx));
            if (Op == TokKind.OrOr)   return ToBool(Left.Eval(ctx)) || ToBool(Right.Eval(ctx));

            var l = Left.Eval(ctx);
            var r = Right.Eval(ctx);
            return Op switch
            {
                TokKind.Eq  => Equals(l, r) || ToNum(l) == ToNum(r),
                TokKind.Neq => !(Equals(l, r) || ToNum(l) == ToNum(r)),
                TokKind.Gt  => ToNum(l) > ToNum(r),
                TokKind.Lt  => ToNum(l) < ToNum(r),
                TokKind.Gte => ToNum(l) >= ToNum(r),
                TokKind.Lte => ToNum(l) <= ToNum(r),
                _ => throw new InvalidOperationException($"Unexpected op {Op}")
            };
        }
    }

    // ── Parser (recursive descent) ──────────────────────────────────

    private sealed class Parser
    {
        private readonly List<Tok> _t; private int _p;
        public Parser(List<Tok> tokens) { _t = tokens; _p = 0; }

        public Node ParseRoot()
        {
            var n = ParseOr();
            if (Peek().Kind != TokKind.End) throw new FormatException($"Trailing tokens after expression: '{Peek().Text}'");
            return n;
        }

        private Node ParseOr()
        {
            var n = ParseAnd();
            while (Peek().Kind == TokKind.OrOr) { Next(); n = new BinOp(TokKind.OrOr, n, ParseAnd()); }
            return n;
        }
        private Node ParseAnd()
        {
            var n = ParseNot();
            while (Peek().Kind == TokKind.AndAnd) { Next(); n = new BinOp(TokKind.AndAnd, n, ParseNot()); }
            return n;
        }
        private Node ParseNot()
        {
            if (Peek().Kind == TokKind.Bang) { Next(); return new Not(ParseNot()); }
            return ParseComparison();
        }
        private Node ParseComparison()
        {
            var l = ParsePrimary();
            var k = Peek().Kind;
            if (k is TokKind.Eq or TokKind.Neq or TokKind.Gt or TokKind.Lt or TokKind.Gte or TokKind.Lte)
            {
                Next();
                var r = ParsePrimary();
                return new BinOp(k, l, r);
            }
            return l;
        }
        private Node ParsePrimary()
        {
            var t = Next();
            return t.Kind switch
            {
                TokKind.Num    => new NumLit(t.Num),
                TokKind.Str    => new StrLit(t.Text),
                TokKind.Ident  => t.Text switch
                {
                    "true"  => new BoolLit(true),
                    "false" => new BoolLit(false),
                    _       => new IdentRef(t.Text)
                },
                TokKind.LParen => ParseParenBody(),
                _ => throw new FormatException($"Unexpected token '{t.Text}' at position {_p}")
            };
        }
        private Node ParseParenBody()
        {
            var n = ParseOr();
            if (Next().Kind != TokKind.RParen) throw new FormatException("Missing ')'");
            return n;
        }
        private Tok Peek() => _t[_p];
        private Tok Next() => _t[_p++];
    }
}
