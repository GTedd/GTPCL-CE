using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PCL.Core.Minecraft.Horizon;

/// <summary>
///     读取 Horizon 清单（<c>horizon-mod.toml</c>、<c>horizon-mixin.toml</c>、<c>horizon-shaders.toml</c> 等）用的
///     TOML 子集解析器：注释、表头 <c>[a.b]</c> 与 <c>[[a]]</c>、点号键、基本/字面/多行字符串、整数、浮点、布尔、
///     数组与内联表。日期时间按字符串保留。
/// </summary>
/// <remarks>
///     表解析为 <see cref="Dictionary{TKey,TValue}" />（键区分大小写），数组为 <see cref="List{T}" />，
///     整数为 <see cref="long" />，浮点为 <see cref="double" />。语法错误抛出 <see cref="InvalidDataException" />。
/// </remarks>
public sealed class HorizonToml
{
    private readonly string _text;
    private int _pos;

    private HorizonToml(string text)
    {
        _text = text.Replace("\r\n", "\n");
    }

    public static Dictionary<string, object?> Parse(string text) => new HorizonToml(text)._ParseDocument();

    /// <summary>按点号路径取值，如 <c>compatibility.horizon_revision</c>。</summary>
    public static object? Get(Dictionary<string, object?> table, string path)
    {
        object? current = table;
        foreach (var part in path.Split('.'))
        {
            if (current is not Dictionary<string, object?> dict || !dict.TryGetValue(part, out current)) return null;
        }

        return current;
    }

    public static string? GetString(Dictionary<string, object?> table, string path) => Get(table, path) as string;

    public static long? GetLong(Dictionary<string, object?> table, string path) => Get(table, path) switch
    {
        long l => l,
        double d when Math.Abs(d % 1) < double.Epsilon => (long)d,
        _ => null
    };

    public static List<string> GetStrings(Dictionary<string, object?> table, string path) =>
        Get(table, path) is List<object?> list ? list.OfType<string>().ToList() : [];

    private Dictionary<string, object?> _ParseDocument()
    {
        var root = new Dictionary<string, object?>(StringComparer.Ordinal);
        var current = root;
        while (true)
        {
            _SkipWhitespaceCommentsAndNewlines();
            if (_pos >= _text.Length) return root;

            if (_text[_pos] == '[')
            {
                var isArray = _pos + 1 < _text.Length && _text[_pos + 1] == '[';
                _pos += isArray ? 2 : 1;
                var keys = _ParseKeyPath();
                _SkipInlineWhitespace();
                _Expect(']');
                if (isArray) _Expect(']');
                _ExpectLineEnd();
                current = isArray ? _AppendArrayTable(root, keys) : _OpenTable(root, keys);
                continue;
            }

            var path = _ParseKeyPath();
            _SkipInlineWhitespace();
            _Expect('=');
            _SkipInlineWhitespace();
            var value = _ParseValue();
            _Assign(current, path, value);
            _ExpectLineEnd();
        }
    }

    private static Dictionary<string, object?> _OpenTable(Dictionary<string, object?> root, List<string> keys)
    {
        var table = root;
        foreach (var key in keys)
        {
            if (!table.TryGetValue(key, out var next))
            {
                next = new Dictionary<string, object?>(StringComparer.Ordinal);
                table[key] = next;
            }

            table = next switch
            {
                Dictionary<string, object?> dict => dict,
                List<object?> { Count: > 0 } list when list[^1] is Dictionary<string, object?> last => last,
                _ => throw new InvalidDataException($"TOML: '{string.Join('.', keys)}' is not a table")
            };
        }

        return table;
    }

    private static Dictionary<string, object?> _AppendArrayTable(Dictionary<string, object?> root, List<string> keys)
    {
        var parent = _OpenTable(root, keys.Take(keys.Count - 1).ToList());
        var name = keys[^1];
        if (!parent.TryGetValue(name, out var existing))
        {
            existing = new List<object?>();
            parent[name] = existing;
        }

        if (existing is not List<object?> list)
            throw new InvalidDataException($"TOML: '{string.Join('.', keys)}' is not an array of tables");
        var table = new Dictionary<string, object?>(StringComparer.Ordinal);
        list.Add(table);
        return table;
    }

    private static void _Assign(Dictionary<string, object?> table, List<string> path, object? value)
    {
        var target = table;
        for (var i = 0; i < path.Count - 1; i++)
        {
            if (!target.TryGetValue(path[i], out var next))
            {
                next = new Dictionary<string, object?>(StringComparer.Ordinal);
                target[path[i]] = next;
            }

            target = next as Dictionary<string, object?>
                     ?? throw new InvalidDataException($"TOML: '{path[i]}' is not a table");
        }

        if (target.ContainsKey(path[^1]))
            throw new InvalidDataException($"TOML: duplicate key '{string.Join('.', path)}'");
        target[path[^1]] = value;
    }

    private List<string> _ParseKeyPath()
    {
        var keys = new List<string>();
        while (true)
        {
            _SkipInlineWhitespace();
            keys.Add(_ParseKey());
            _SkipInlineWhitespace();
            if (_pos < _text.Length && _text[_pos] == '.')
            {
                _pos++;
                continue;
            }

            return keys;
        }
    }

    private string _ParseKey()
    {
        if (_pos >= _text.Length) throw _Error("key expected");
        var c = _text[_pos];
        if (c == '"') return _ParseBasicString();
        if (c == '\'') return _ParseLiteralString();
        var start = _pos;
        while (_pos < _text.Length && (char.IsAsciiLetterOrDigit(_text[_pos]) || _text[_pos] is '_' or '-')) _pos++;
        if (start == _pos) throw _Error("key expected");
        return _text[start.._pos];
    }

    private object? _ParseValue()
    {
        if (_pos >= _text.Length) throw _Error("value expected");
        var c = _text[_pos];
        switch (c)
        {
            case '"':
                return _Peek("\"\"\"") ? _ParseMultilineBasicString() : _ParseBasicString();
            case '\'':
                return _Peek("'''") ? _ParseMultilineLiteralString() : _ParseLiteralString();
            case '[':
                return _ParseArray();
            case '{':
                return _ParseInlineTable();
        }

        if (_Peek("true") && !_IsBareChar(4)) { _pos += 4; return true; }
        if (_Peek("false") && !_IsBareChar(5)) { _pos += 5; return false; }

        var start = _pos;
        while (_pos < _text.Length && _text[_pos] is not (',' or ']' or '}' or '\n' or '#') &&
               !(_text[_pos] is ' ' or '\t' && !_LooksLikeDateTimeSpace()))
            _pos++;
        var token = _text[start.._pos].Trim();
        if (token.Length == 0) throw _Error("value expected");
        var number = token.Replace("_", "");
        if (number.StartsWith("0x", StringComparison.Ordinal) &&
            long.TryParse(number[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return hex;
        if (long.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        if (number is "inf" or "+inf") return double.PositiveInfinity;
        if (number is "-inf") return double.NegativeInfinity;
        if (number is "nan" or "+nan" or "-nan") return double.NaN;
        if (char.IsAsciiDigit(token[0])) return token; // 日期时间，按字符串保留
        throw _Error($"invalid value '{token}'");
    }

    private bool _LooksLikeDateTimeSpace() =>
        // 1979-05-27 07:32:00 这种用空格分隔日期与时间的写法
        _pos >= 10 && _pos + 1 < _text.Length && char.IsAsciiDigit(_text[_pos - 1]) &&
        char.IsAsciiDigit(_text[_pos + 1]) && _text[_pos - 3] == '-';

    private List<object?> _ParseArray()
    {
        _Expect('[');
        var list = new List<object?>();
        while (true)
        {
            _SkipWhitespaceCommentsAndNewlines();
            if (_pos < _text.Length && _text[_pos] == ']')
            {
                _pos++;
                return list;
            }

            list.Add(_ParseValue());
            _SkipWhitespaceCommentsAndNewlines();
            if (_pos < _text.Length && _text[_pos] == ',')
            {
                _pos++;
                continue;
            }

            _SkipWhitespaceCommentsAndNewlines();
            _Expect(']');
            return list;
        }
    }

    private Dictionary<string, object?> _ParseInlineTable()
    {
        _Expect('{');
        var table = new Dictionary<string, object?>(StringComparer.Ordinal);
        _SkipInlineWhitespace();
        if (_pos < _text.Length && _text[_pos] == '}')
        {
            _pos++;
            return table;
        }

        while (true)
        {
            var path = _ParseKeyPath();
            _SkipInlineWhitespace();
            _Expect('=');
            _SkipInlineWhitespace();
            _Assign(table, path, _ParseValue());
            _SkipInlineWhitespace();
            if (_pos < _text.Length && _text[_pos] == ',')
            {
                _pos++;
                continue;
            }

            _Expect('}');
            return table;
        }
    }

    private string _ParseBasicString()
    {
        _Expect('"');
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _text.Length || _text[_pos] == '\n') throw _Error("unterminated string");
            var c = _text[_pos++];
            if (c == '"') return sb.ToString();
            if (c == '\\') _AppendEscape(sb);
            else sb.Append(c);
        }
    }

    private string _ParseMultilineBasicString()
    {
        _pos += 3;
        if (_pos < _text.Length && _text[_pos] == '\n') _pos++;
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _text.Length) throw _Error("unterminated string");
            if (_Peek("\"\"\"") && !_Peek("\"\"\"\"\"\"", 1))
            {
                // 结尾最多可以多带两个引号
                var extra = 0;
                while (_pos + 3 + extra < _text.Length && _text[_pos + 3 + extra] == '"' && extra < 2) extra++;
                sb.Append('"', extra);
                _pos += 3 + extra;
                return sb.ToString();
            }

            var c = _text[_pos++];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            // 行尾反斜杠：吞掉换行与后续空白
            var look = _pos;
            while (look < _text.Length && _text[look] is ' ' or '\t') look++;
            if (look < _text.Length && _text[look] == '\n')
            {
                _pos = look;
                while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
                continue;
            }

            _AppendEscape(sb);
        }
    }

    private string _ParseLiteralString()
    {
        _Expect('\'');
        var end = _text.IndexOf('\'', _pos);
        var newline = _text.IndexOf('\n', _pos);
        if (end < 0 || (newline >= 0 && newline < end)) throw _Error("unterminated string");
        var value = _text[_pos..end];
        _pos = end + 1;
        return value;
    }

    private string _ParseMultilineLiteralString()
    {
        _pos += 3;
        if (_pos < _text.Length && _text[_pos] == '\n') _pos++;
        var end = _text.IndexOf("'''", _pos, StringComparison.Ordinal);
        if (end < 0) throw _Error("unterminated string");
        while (end + 3 < _text.Length && _text[end + 3] == '\'' && end + 3 - _pos >= 0) end++;
        var value = _text[_pos..end];
        _pos = end + 3;
        return value;
    }

    private void _AppendEscape(StringBuilder sb)
    {
        if (_pos >= _text.Length) throw _Error("bad escape");
        var e = _text[_pos++];
        switch (e)
        {
            case 'b': sb.Append('\b'); break;
            case 't': sb.Append('\t'); break;
            case 'n': sb.Append('\n'); break;
            case 'f': sb.Append('\f'); break;
            case 'r': sb.Append('\r'); break;
            case 'e': sb.Append('\u001b'); break;
            case '"': sb.Append('"'); break;
            case '\\': sb.Append('\\'); break;
            case 'u' or 'U':
            {
                var length = e == 'u' ? 4 : 8;
                if (_pos + length > _text.Length) throw _Error("bad unicode escape");
                var code = int.Parse(_text.AsSpan(_pos, length), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                sb.Append(char.ConvertFromUtf32(code));
                _pos += length;
                break;
            }
            default: throw _Error($"bad escape '\\{e}'");
        }
    }

    private bool _Peek(string s, int offset = 0) =>
        _pos + offset + s.Length <= _text.Length &&
        string.CompareOrdinal(_text, _pos + offset, s, 0, s.Length) == 0;

    private bool _IsBareChar(int offset) =>
        _pos + offset < _text.Length && (char.IsAsciiLetterOrDigit(_text[_pos + offset]) || _text[_pos + offset] is '_' or '-');

    private void _Expect(char c)
    {
        if (_pos >= _text.Length || _text[_pos] != c) throw _Error($"'{c}' expected");
        _pos++;
    }

    private void _ExpectLineEnd()
    {
        _SkipInlineWhitespace();
        if (_pos < _text.Length && _text[_pos] == '#')
            while (_pos < _text.Length && _text[_pos] != '\n') _pos++;
        if (_pos < _text.Length && _text[_pos] != '\n') throw _Error("end of line expected");
    }

    private void _SkipInlineWhitespace()
    {
        while (_pos < _text.Length && _text[_pos] is ' ' or '\t') _pos++;
    }

    private void _SkipWhitespaceCommentsAndNewlines()
    {
        while (_pos < _text.Length)
        {
            var c = _text[_pos];
            if (c is ' ' or '\t' or '\n' or '\r') _pos++;
            else if (c == '#')
                while (_pos < _text.Length && _text[_pos] != '\n') _pos++;
            else return;
        }
    }

    private InvalidDataException _Error(string message)
    {
        var line = 1 + _text.Take(Math.Min(_pos, _text.Length)).Count(ch => ch == '\n');
        return new InvalidDataException($"TOML line {line}: {message}");
    }
}
