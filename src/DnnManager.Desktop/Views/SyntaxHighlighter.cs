namespace DnnManager.Desktop.Views;

/// <summary>What a piece of a line is, for its colour.</summary>
public enum TokenKind { Text, Keyword, String, Comment, Number, Tag, Attribute }

/// <summary>A piece of a line: where it starts, how long it is, and what it is.</summary>
public readonly record struct Token(int Start, int Length, TokenKind Kind);

/// <summary>
/// Colours the lines of a diff by what the file is: C#, JavaScript, TypeScript, CSS and JSON (code with <c>//</c> and
/// <c>/* */</c> comments), HTML, XML and DNN's ascx / aspx / config / .dnn (markup), and SQL. A light tokenizer, line by
/// line, carrying only whether a block comment is open - enough to read a diff by, not a compiler.
/// </summary>
public static class SyntaxHighlighter
{
    public enum Language { Plain, Code, Markup, Sql }

    private static readonly HashSet<string> CodeKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "class", "const", "continue", "decimal",
        "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "function", "get", "goto", "if", "implicit", "import", "export", "from", "in", "int", "interface", "internal", "is", "let",
        "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly", "record", "ref", "return", "sbyte", "sealed", "set", "short", "sizeof", "static", "string", "struct", "switch", "this",
        "throw", "true", "try", "typeof", "uint", "ulong", "undefined", "unsafe", "ushort", "using", "var", "virtual", "void", "volatile",
        "while", "yield", "const", "constructor", "extends", "implements", "type", "important", "init", "required", "with", "when", "where"
    };

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "from", "where", "and", "or", "not", "insert", "into", "values", "update", "set", "delete", "create", "alter", "drop",
        "table", "view", "procedure", "function", "as", "begin", "end", "if", "else", "exists", "null", "is", "join", "inner", "left", "right",
        "outer", "on", "group", "by", "order", "having", "declare", "exec", "execute", "return", "go", "top", "distinct", "union", "all",
        "case", "when", "then", "with", "nocount", "primary", "key", "foreign", "references", "index", "int", "nvarchar", "varchar", "bit",
        "datetime", "identity", "default", "constraint", "objectqualifier", "databaseowner"
    };

    public static Language For(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" or ".js" or ".mjs" or ".ts" or ".tsx" or ".jsx" or ".css" or ".scss" or ".less" or ".json" or ".java" or ".vb" => Language.Code,
        ".html" or ".htm" or ".xml" or ".config" or ".ascx" or ".aspx" or ".ashx" or ".asmx" or ".master" or ".cshtml" or ".vbhtml" or ".resx"
            or ".xsl" or ".xslt" or ".svg" or ".dnn" or ".csproj" or ".targets" or ".props" => Language.Markup,
        ".sql" or ".sqldataprovider" => Language.Sql,
        _ => Language.Plain
    };

    /// <summary>The tokens of each line, in order - a block comment open at a line's end goes on into the next.</summary>
    public static IReadOnlyList<Token[]> Tokenize(IReadOnlyList<string> lines, Language language)
    {
        var result = new Token[lines.Count][];
        var inComment = false;
        for (var i = 0; i < lines.Count; i++) result[i] = Line(lines[i], language, ref inComment);
        return result;
    }

    private static Token[] Line(string line, Language language, ref bool inComment)
    {
        if (language == Language.Plain || line.Length == 0) return [];
        var tokens = new List<Token>();
        var i = 0;
        var (open, close) = language == Language.Markup ? ("<!--", "-->") : ("/*", "*/");
        if (inComment)
        {
            var end = line.IndexOf(close, StringComparison.Ordinal);
            if (end < 0) return [new Token(0, line.Length, TokenKind.Comment)];
            tokens.Add(new Token(0, end + close.Length, TokenKind.Comment));
            i = end + close.Length;
            inComment = false;
        }
        var inTag = false;
        while (i < line.Length)
        {
            var c = line[i];
            if (string.CompareOrdinal(line, i, open, 0, open.Length) == 0)
            {
                var end = line.IndexOf(close, i + open.Length, StringComparison.Ordinal);
                if (end < 0)
                {
                    tokens.Add(new Token(i, line.Length - i, TokenKind.Comment));
                    inComment = true;
                    break;
                }
                tokens.Add(new Token(i, end + close.Length - i, TokenKind.Comment));
                i = end + close.Length;
                continue;
            }
            if (language == Language.Code && c == '/' && i + 1 < line.Length && line[i + 1] == '/' ||
                language == Language.Sql && c == '-' && i + 1 < line.Length && line[i + 1] == '-')
            {
                tokens.Add(new Token(i, line.Length - i, TokenKind.Comment));
                break;
            }
            if (language == Language.Markup)
            {
                if (c == '<')
                {
                    var start = i++;
                    if (i < line.Length && (line[i] == '/' || line[i] == '?' || line[i] == '%' || line[i] == '!')) i++;
                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is ':' or '-' or '_' or '.' or '@')) i++;
                    tokens.Add(new Token(start, i - start, TokenKind.Tag));
                    inTag = true;
                    continue;
                }
                if (inTag && c == '>') { tokens.Add(new Token(i, 1, TokenKind.Tag)); inTag = false; i++; continue; }
                if (inTag && (c == '"' || c == '\''))
                {
                    var end = line.IndexOf(c, i + 1);
                    end = end < 0 ? line.Length - 1 : end;
                    tokens.Add(new Token(i, end - i + 1, TokenKind.String));
                    i = end + 1;
                    continue;
                }
                if (inTag && (char.IsLetter(c) || c == '_'))
                {
                    var start = i;
                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is ':' or '-' or '_' or '.')) i++;
                    tokens.Add(new Token(start, i - start, TokenKind.Attribute));
                    continue;
                }
                i++;
                continue;
            }
            if (c is '"' or '\'' or '`')
            {
                var end = i + 1;
                while (end < line.Length && line[end] != c) end += line[end] == '\\' && language == Language.Code ? 2 : 1;
                end = Math.Min(end, line.Length - 1);
                tokens.Add(new Token(i, end - i + 1, TokenKind.String));
                i = end + 1;
                continue;
            }
            if (char.IsDigit(c) && (i == 0 || !char.IsLetterOrDigit(line[i - 1]) && line[i - 1] != '_'))
            {
                var start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '.')) i++;
                tokens.Add(new Token(start, i - start, TokenKind.Number));
                continue;
            }
            if (char.IsLetter(c) || c == '_' || c == '@')
            {
                var start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                var word = line[start..i].TrimStart('@');
                if (language == Language.Code ? CodeKeywords.Contains(word) : SqlKeywords.Contains(word))
                    tokens.Add(new Token(start, i - start, TokenKind.Keyword));
                if (i == start) i++;
                continue;
            }
            i++;
        }
        return [.. tokens];
    }

    /// <summary>The theme's brush key of a kind of token.</summary>
    public static string? Brush(TokenKind kind) => kind switch
    {
        TokenKind.Keyword => "SyntaxKeyword",
        TokenKind.String => "SyntaxString",
        TokenKind.Comment => "SyntaxComment",
        TokenKind.Number => "SyntaxNumber",
        TokenKind.Tag => "SyntaxTag",
        TokenKind.Attribute => "SyntaxAttribute",
        _ => null
    };
}
