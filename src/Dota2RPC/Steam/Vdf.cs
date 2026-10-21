using System.Text;

namespace Dota2RPC.Steam;

/// <summary>
/// Узел формата Valve KeyValues (VDF). Хранит позиции в исходном тексте,
/// чтобы правки выполнялись точечно, не переписывая остальной файл.
/// </summary>
internal sealed class VdfNode
{
    public string Key { get; init; } = "";
    public string? Value { get; init; }
    public List<VdfNode>? Children { get; init; }

    /// <summary>Для значения — начало токена (включая кавычку).</summary>
    public int ValueStart { get; init; } = -1;

    /// <summary>Для значения — позиция после токена.</summary>
    public int ValueEnd { get; init; } = -1;

    public int OpenBrace { get; init; } = -1;
    public int CloseBrace { get; init; } = -1;

    public bool IsBlock => Children is not null;

    public VdfNode? this[string key] =>
        Children?.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    public VdfNode? Get(params string[] path)
    {
        var node = this;
        foreach (var key in path)
        {
            node = node?[key];
            if (node is null) return null;
        }
        return node;
    }
}

internal static class Vdf
{
    enum TokenKind { String, Open, Close, End }

    readonly record struct Token(TokenKind Kind, string Text, int Start, int End, bool Conditional);

    public static VdfNode Parse(string text)
    {
        var pos = 0;
        var children = ParseBlock(text, ref pos, topLevel: true);
        return new VdfNode { Key = "", Children = children, OpenBrace = -1, CloseBrace = text.Length };
    }

    static List<VdfNode> ParseBlock(string text, ref int pos, bool topLevel)
    {
        var list = new List<VdfNode>();
        while (true)
        {
            var key = Next(text, ref pos);
            if (key.Kind == TokenKind.End)
            {
                if (!topLevel) throw new FormatException("Неожиданный конец VDF: не закрыт блок");
                return list;
            }
            if (key.Kind == TokenKind.Close)
            {
                if (topLevel) throw new FormatException($"Лишняя закрывающая скобка в позиции {key.Start}");
                pos = key.Start; // вызывающий заберёт '}'
                return list;
            }
            if (key.Kind != TokenKind.String || key.Conditional)
                throw new FormatException($"Ожидался ключ в позиции {key.Start}");

            var value = Next(text, ref pos);
            // Условия вида [$WIN32] после ключа пропускаем.
            while (value.Kind == TokenKind.String && value.Conditional) value = Next(text, ref pos);

            switch (value.Kind)
            {
                case TokenKind.Open:
                {
                    var inner = ParseBlock(text, ref pos, topLevel: false);
                    var close = Next(text, ref pos);
                    if (close.Kind != TokenKind.Close) throw new FormatException("Не закрыт блок VDF");
                    list.Add(new VdfNode { Key = key.Text, Children = inner, OpenBrace = value.Start, CloseBrace = close.Start });
                    break;
                }
                case TokenKind.String:
                    list.Add(new VdfNode { Key = key.Text, Value = value.Text, ValueStart = value.Start, ValueEnd = value.End });
                    SkipConditional(text, ref pos);
                    break;
                default:
                    throw new FormatException($"Ожидалось значение для ключа \"{key.Text}\" в позиции {value.Start}");
            }
        }
    }

    static void SkipConditional(string text, ref int pos)
    {
        var save = pos;
        var t = Next(text, ref pos);
        if (!(t.Kind == TokenKind.String && t.Conditional)) pos = save;
    }

    static Token Next(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            var c = text[pos];
            if (char.IsWhiteSpace(c) || c == '﻿')
            {
                pos++;
                continue;
            }
            if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
                continue;
            }
            break;
        }
        if (pos >= text.Length) return new Token(TokenKind.End, "", pos, pos, false);

        var start = pos;
        var ch = text[pos];
        if (ch == '{') { pos++; return new Token(TokenKind.Open, "{", start, pos, false); }
        if (ch == '}') { pos++; return new Token(TokenKind.Close, "}", start, pos, false); }

        if (ch == '"')
        {
            pos++;
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= text.Length) throw new FormatException($"Не закрыта строка, начатая в позиции {start}");
                var c = text[pos];
                if (c == '\\' && pos + 1 < text.Length)
                {
                    var n = text[pos + 1];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        default: sb.Append('\\').Append(n); break; // неизвестную последовательность сохраняем как есть
                    }
                    pos += 2;
                    continue;
                }
                pos++;
                if (c == '"') break;
                sb.Append(c);
            }
            return new Token(TokenKind.String, sb.ToString(), start, pos, false);
        }

        // Строка без кавычек (или условие [$WIN32]).
        while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not ('{' or '}' or '"')) pos++;
        var raw = text[start..pos];
        return new Token(TokenKind.String, raw, start, pos, raw.StartsWith('['));
    }

    public static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
