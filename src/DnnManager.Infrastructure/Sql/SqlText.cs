namespace DnnManager.Infrastructure.Sql;

/// <summary>
/// Text put into T-SQL where a parameter can't go (sqlcmd has none; BACKUP, RESTORE and CREATE DATABASE take no
/// parameter for a name). Database names are not always DNN Manager's own - the one a site uses is read out of its
/// web.config, the logical file names out of a backup - so they are quoted, never interpolated as they are.
/// </summary>
public static class SqlText
{
    /// <summary><paramref name="name"/> as an identifier: <c>[</c> + the name with <c>]</c> doubled + <c>]</c>.</summary>
    public static string Identifier(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary><paramref name="value"/> for inside a string literal (<c>N'…'</c>): <c>'</c> doubled.</summary>
    public static string Literal(string value) => value.Replace("'", "''");

    /// <summary>The characters LIKE reads as wildcards ([, % and _) as themselves.</summary>
    public static string EscapeLike(string value) => value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
}
