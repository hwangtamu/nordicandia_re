using System.Globalization;
using System.Text.RegularExpressions;

namespace Nordicandia.Simulation;

/// <summary>
/// Evaluates the client's scripted attribute formulas (see
/// <c>tools/web-content/generated/attribute_formulas.json</c>). The expressions are
/// infix arithmetic with parentheses plus a small set of helpers the client uses:
/// <list type="bullet">
/// <item><c>Pin(a, min, max)</c> — clamp <c>a</c> to <c>[min, max]</c>.</item>
/// <item><c>Min(a, b)</c> / <c>Max(a, b)</c> — pairwise min/max (variadic folds left).</item>
/// <item><c>Pow(a, b)</c> — <c>a^b</c>.</item>
/// <item><c>Constants.X</c> — a named constant resolved by the caller.</item>
/// <item><c>MultiplyWith:X</c> suffix — the whole expression times <c>(1 + X)</c>.</item>
/// <item><c>(Cond ? A : B)</c> — <c>A</c> when <c>Cond</c> is non-zero, else <c>B</c>.</item>
/// </list>
/// Identifiers (attribute names, constants) are resolved by the supplied callback.
/// </summary>
public static class AttributeFormula
{
    private static readonly Regex MultiplyWith =
        new(@"MultiplyWith:([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex SubtractWith =
        new(@"SubtractWith:([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    /// <summary>Evaluate <paramref name="formula"/>, resolving names through <paramref name="resolve"/>.</summary>
    public static double Evaluate(string formula, Func<string, double> resolve)
    {
        if (string.IsNullOrWhiteSpace(formula)) return 0.0;
        var expanded = MultiplyWith.Replace(formula, "*(1+$1)");
        // SubtractWith:X subtracts X from the whole expression (used by the resistance totals
        // to remove the world-level resistance penalty).
        expanded = SubtractWith.Replace(expanded, "-($1)");
        var parser = new Parser(expanded, resolve);
        var value = parser.ParseExpression();
        parser.ExpectEnd();
        return value;
    }

    private sealed class Parser
    {
        private readonly Func<string, double> resolve;
        private readonly string text;
        private int pos;

        public Parser(string text, Func<string, double> resolve)
        {
            this.text = text;
            this.resolve = resolve;
        }

        public double ParseExpression() => ParseTernary();

        private double ParseTernary()
        {
            var condition = ParseAdditive();
            SkipSpaces();
            if (Peek() != '?') return condition;
            pos++; // '?'
            var whenTrue = ParseExpression();
            SkipSpaces();
            if (Peek() != ':') throw Error("expected ':' in ternary");
            pos++; // ':'
            var whenFalse = ParseExpression();
            return condition != 0.0 ? whenTrue : whenFalse;
        }

        private double ParseAdditive()
        {
            var value = ParseMultiplicative();
            while (true)
            {
                SkipSpaces();
                var c = Peek();
                if (c == '+') { pos++; value += ParseMultiplicative(); }
                else if (c == '-') { pos++; value -= ParseMultiplicative(); }
                else return value;
            }
        }

        private double ParseMultiplicative()
        {
            var value = ParseUnary();
            while (true)
            {
                SkipSpaces();
                var c = Peek();
                if (c == '*') { pos++; value *= ParseUnary(); }
                else if (c == '/') { pos++; value /= ParseUnary(); }
                else return value;
            }
        }

        private double ParseUnary()
        {
            SkipSpaces();
            if (Peek() == '-') { pos++; return -ParseUnary(); }
            if (Peek() == '+') { pos++; return ParseUnary(); }
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipSpaces();
            var c = Peek();
            if (c == '(')
            {
                pos++;
                var value = ParseExpression();
                SkipSpaces();
                if (Peek() != ')') throw Error("expected ')'");
                pos++;
                return value;
            }
            if (char.IsDigit(c) || c == '.') return ParseNumber();

            var ident = ParseIdentifier();
            if (ident.Length == 0) throw Error("unexpected token");
            SkipSpaces();
            if (Peek() == '(') return ParseCall(ident);
            return resolve(ident);
        }

        private double ParseCall(string name)
        {
            pos++; // '('
            var args = new List<double>();
            SkipSpaces();
            if (Peek() != ')')
            {
                args.Add(ParseExpression());
                SkipSpaces();
                while (Peek() == ',')
                {
                    pos++;
                    args.Add(ParseExpression());
                    SkipSpaces();
                }
            }
            if (Peek() != ')') throw Error("expected ')' after arguments");
            pos++;

            return name.ToLowerInvariant() switch
            {
                "pin" => Math.Clamp(args[0], args[1], args[2]),
                "min" => args.Aggregate(Math.Min),
                "max" => args.Aggregate(Math.Max),
                "pow" => Math.Pow(args[0], args[1]),
                "abs" => Math.Abs(args[0]),
                "floor" => Math.Floor(args[0]),
                "ceil" => Math.Ceiling(args[0]),
                "round" => Math.Round(args[0]),
                _ => throw Error($"unknown function '{name}'"),
            };
        }

        private double ParseNumber()
        {
            var start = pos;
            while (pos < text.Length && (char.IsDigit(text[pos]) || text[pos] == '.')) pos++;
            var slice = text[start..pos];
            if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw Error($"bad number '{slice}'");
            return value;
        }

        // Attribute names and Constants.X both include letters, digits and underscores; a dot is
        // kept so 'Constants.Armor_Per_Constitution_Factor' resolves as one identifier.
        private string ParseIdentifier()
        {
            var start = pos;
            while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_' || text[pos] == '.')) pos++;
            return text[start..pos];
        }

        private char Peek() => pos < text.Length ? text[pos] : '\0';

        private void SkipSpaces()
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        }

        public void ExpectEnd()
        {
            SkipSpaces();
            if (pos != text.Length) throw Error($"trailing input at {pos}: '{text[pos..]}'");
        }

        private FormatException Error(string message) => new($"{message} in formula '{text}'");
    }
}
