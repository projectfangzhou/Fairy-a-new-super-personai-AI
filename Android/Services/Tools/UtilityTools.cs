using System.Globalization;
using System.Text.Json.Nodes;

namespace FairyAI_Android.Services.Tools;

/// <summary>Local clock. Cheap, offline, and something models get wrong when they guess.</summary>
public sealed class CurrentTimeTool : ITool
{
    public string Name => "current_time";

    public string Description => "获取本机当前的日期、时间与星期";

    public List<ToolParameter> Parameters => new();

    public Task<string> ExecuteAsync(JsonObject args) =>
        Task.FromResult(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss dddd", CultureInfo.GetCultureInfo("zh-CN")));
}

/// <summary>
/// Arithmetic only. Deliberately not an expression evaluator over the language: the model supplies the
/// expression, so anything that could reach a compiler or a shell is simply not in the grammar.
/// </summary>
public sealed class CalculateTool : ITool
{
    public string Name => "calculate";

    public string Description => "计算一个算术表达式，支持 + - * / % 与括号";

    public List<ToolParameter> Parameters => new()
    {
        new() { Name = "expression", Type = "string", Description = "例如 (12 + 3) * 4 / 2", Required = true },
    };

    public Task<string> ExecuteAsync(JsonObject args)
    {
        var expression = args?["expression"]?.GetValue<string>() ?? "";

        if (!ArithmeticExpression.TryEvaluate(expression, out var value, out var error))
            return Task.FromResult("算不出来：" + error);

        return Task.FromResult(value.ToString("0.########", CultureInfo.InvariantCulture));
    }
}

/// <summary>Recursive-descent parser for + - * / % and parentheses over decimal numbers.</summary>
public static class ArithmeticExpression
{
    public static bool TryEvaluate(string? expression, out double value, out string error)
    {
        value = 0;
        error = "";

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "表达式是空的";
            return false;
        }

        var parser = new Parser(expression);
        try
        {
            value = parser.ParseExpression();
            parser.SkipSpaces();
            if (!parser.AtEnd)
            {
                error = $"多余的内容：{parser.Rest}";
                return false;
            }

            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                error = "结果不是有限数";
                return false;
            }

            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (DivideByZeroException)
        {
            error = "除以零";
            return false;
        }
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _position;

        public Parser(string text) => _text = text;

        public bool AtEnd => _position >= _text.Length;

        public string Rest => _text[Math.Min(_position, _text.Length)..];

        public void SkipSpaces()
        {
            while (!AtEnd && char.IsWhiteSpace(_text[_position])) _position++;
        }

        public double ParseExpression()
        {
            var value = ParseTerm();

            while (true)
            {
                SkipSpaces();
                if (AtEnd) return value;

                var op = _text[_position];
                if (op != '+' && op != '-') return value;

                _position++;
                var right = ParseTerm();
                value = op == '+' ? value + right : value - right;
            }
        }

        private double ParseTerm()
        {
            var value = ParseUnary();

            while (true)
            {
                SkipSpaces();
                if (AtEnd) return value;

                var op = _text[_position];
                if (op != '*' && op != '/' && op != '%') return value;

                _position++;
                var right = ParseUnary();

                switch (op)
                {
                    case '*':
                        value *= right;
                        break;
                    case '/':
                        if (right == 0) throw new DivideByZeroException();
                        value /= right;
                        break;
                    default:
                        if (right == 0) throw new DivideByZeroException();
                        value %= right;
                        break;
                }
            }
        }

        private double ParseUnary()
        {
            SkipSpaces();
            if (AtEnd) throw new FormatException("表达式在预期数字的位置结束了");

            if (_text[_position] == '-')
            {
                _position++;
                return -ParseUnary();
            }

            if (_text[_position] == '+')
            {
                _position++;
                return ParseUnary();
            }

            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipSpaces();
            if (AtEnd) throw new FormatException("表达式在预期数字的位置结束了");

            if (_text[_position] == '(')
            {
                _position++;
                var inner = ParseExpression();
                SkipSpaces();
                if (AtEnd || _text[_position] != ')') throw new FormatException("括号没有闭合");
                _position++;
                return inner;
            }

            var start = _position;
            while (!AtEnd && (char.IsDigit(_text[_position]) || _text[_position] == '.')) _position++;

            if (start == _position) throw new FormatException($"无法识别的字符：{_text[_position]}");

            var literal = _text[start.._position];
            if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                throw new FormatException($"不是合法数字：{literal}");

            return number;
        }
    }
}
