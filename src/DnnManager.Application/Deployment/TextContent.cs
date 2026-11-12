using System.Text;

namespace DnnManager.Application.Deployment;

/// <summary>
/// A file's bytes as text, for a diff or a merge - or not, when it is binary or too big to show. Remembers how it was
/// written (encoding, byte order mark, line ending, a last line ending or not) so a merge is written back the same way.
/// </summary>
public sealed class TextContent
{
    /// <summary>Bigger files aren't shown as text: a diff of them is no help, and it would take long.</summary>
    public const long MaxBytes = 4 * 1024 * 1024;

    private TextContent(IReadOnlyList<string> lines, Encoding encoding, bool bom, string newline, bool endsWithNewline)
    {
        Lines = lines;
        Encoding = encoding;
        HasBom = bom;
        Newline = newline;
        EndsWithNewline = endsWithNewline;
    }

    /// <summary>The lines, without their line endings.</summary>
    public IReadOnlyList<string> Lines { get; }
    public Encoding Encoding { get; }
    public bool HasBom { get; }

    /// <summary>"\r\n" or "\n" - whichever most of its lines end with.</summary>
    public string Newline { get; }
    public bool EndsWithNewline { get; }

    public static TextContent Empty { get; } = new([], new UTF8Encoding(false), false, "\r\n", false);

    /// <summary>
    /// <paramref name="bytes"/> as text - null when they are binary (a NUL byte, or not text in any encoding tried) or
    /// longer than <see cref="MaxBytes"/>.
    /// </summary>
    public static TextContent? From(byte[] bytes) => Decode(bytes) is { } decoded ? FromText(decoded.Text, decoded.Encoding, decoded.HasBom) : null;

    /// <summary>
    /// <paramref name="bytes"/> decoded as <see cref="From"/> does - the text exactly as written, every line ending kept -
    /// or null when they are binary or longer than <see cref="MaxBytes"/>.
    /// </summary>
    public static (string Text, Encoding Encoding, bool HasBom)? Decode(byte[] bytes)
    {
        if (bytes.LongLength > MaxBytes) return null;
        string text;
        Encoding encoding;
        var bom = false;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = new UTF8Encoding(true);
            bom = true;
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = new UnicodeEncoding(false, true);
            bom = true;
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = new UnicodeEncoding(true, true);
            bom = true;
            text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else
        {
            if (Array.IndexOf(bytes, (byte)0) >= 0) return null;
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
                encoding = new UTF8Encoding(false);
            }
            catch (DecoderFallbackException)
            {
                // Not UTF-8: an older file in Windows' Western code page - Latin-1 reads every byte as one character.
                text = Encoding.Latin1.GetString(bytes);
                encoding = Encoding.Latin1;
            }
        }
        if (text.Contains('\0')) return null;
        return (text, encoding, bom);
    }

    /// <summary><paramref name="text"/> split into lines, written with <paramref name="encoding"/>.</summary>
    public static TextContent FromText(string text, Encoding? encoding = null, bool bom = false)
    {
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++;
            else lf++;
        }
        var newline = crlf >= lf && crlf > 0 ? "\r\n" : lf > 0 ? "\n" : "\r\n";
        var endsWithNewline = text.EndsWith('\n');
        var body = endsWithNewline ? text[..^1] : text;
        if (endsWithNewline && body.EndsWith('\r')) body = body[..^1];
        var lines = body.Length == 0 && !endsWithNewline ? [] : body.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
        return new TextContent(lines, encoding ?? new UTF8Encoding(false), bom, newline, endsWithNewline);
    }

    /// <summary><paramref name="lines"/> written the way this file is - its encoding, byte order mark and line ending.</summary>
    public byte[] Encode(IReadOnlyList<string> lines, bool? endsWithNewline = null)
    {
        var text = string.Join(Newline, lines) + ((endsWithNewline ?? EndsWithNewline) && lines.Count > 0 ? Newline : "");
        var body = Encoding.GetBytes(text);
        if (!HasBom) return body;
        var preamble = Encoding.GetPreamble();
        return [.. preamble, .. body];
    }

    /// <summary>The text as written, line endings included.</summary>
    public string Text => string.Join(Newline, Lines) + (EndsWithNewline && Lines.Count > 0 ? Newline : "");
}
