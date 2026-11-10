using System.Text;
using System.Text.RegularExpressions;

namespace DnnManager.Application.Deployment;

/// <summary>
/// A connection's ignore rules, written like a <c>.gitignore</c>: one pattern a line, <c>#</c> a comment, <c>!</c> takes a
/// file back in, a trailing <c>/</c> matches only a folder, a <c>/</c> at the start or in the middle ties the pattern to
/// the top folder, <c>*</c> and <c>?</c> stay within a name, <c>**</c> crosses folders. As in git, the last pattern that
/// matches decides, and nothing inside an ignored folder can be taken back in. Names are matched without case (Windows).
/// What is ignored is neither sent nor fetched nor compared - on either side.
/// </summary>
public sealed class IgnoreRules
{
    /// <summary>
    /// The rules a new connection starts with: what belongs to one PC or one server (web.config, App_Data, logs, caches),
    /// and the files of tools and builds.
    /// </summary>
    public const string Defaults =
        """
        # What belongs to this PC or to the server - neither sent nor fetched. Only the site's own web.config:
        # the ones in DesktopModules and Install are DNN's code.
        /web.config
        /App_Data/
        Portals/_default/Logs/
        Portals/*/Cache/
        *.log

        # Tools and builds.
        .git/
        .vs/
        .vscode/
        .idea/
        node_modules/
        obj/
        *.user
        *.suo
        Thumbs.db
        desktop.ini
        """;

    /// <summary>
    /// Files that hold secrets or an environment's own configuration. Not ignored by these (a project may deploy one on
    /// purpose), but flagged: never staged by "all", and a push or pull of one is asked about first.
    /// </summary>
    private static readonly IgnoreRules SensitivePatterns = Parse(
        """
        /web.config
        connectionStrings.config
        appsettings*.json
        .env
        .env.*
        *.pfx
        *.p12
        *.pem
        *.key
        *.snk
        id_rsa*
        id_ed25519*
        id_ecdsa*
        *.publishsettings
        *.pubxml.user
        *.mdf
        *.ldf
        *.bak
        *.bacpac
        """);

    private readonly List<Rule> _rules;

    private IgnoreRules(List<Rule> rules) => _rules = rules;

    /// <summary>No rules: nothing is ignored.</summary>
    public static IgnoreRules None { get; } = new([]);

    /// <summary>The problems of each line that isn't a pattern - shown under the rules while they are edited.</summary>
    public IReadOnlyList<string> Problems { get; private init; } = [];

    public static IgnoreRules Parse(string? text)
    {
        var rules = new List<Rule>();
        var problems = new List<string>();
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            try
            {
                if (Rule.From(line.TrimStart()) is { } rule) rules.Add(rule);
            }
            catch (ArgumentException ex)
            {
                problems.Add($"Line {i + 1}: '{line}' isn't a pattern ({ex.Message}).");
            }
        }
        return new IgnoreRules(rules) { Problems = problems };
    }

    /// <summary>Whether <paramref name="path"/> looks like a secret or an environment's configuration.</summary>
    public static bool IsSensitive(string path) => SensitivePatterns.IsIgnored(path, isFolder: false);

    /// <summary>
    /// Whether <paramref name="path"/> (relative, with <c>/</c>) is ignored - itself, or because a folder it is in is.
    /// <paramref name="isFolder"/>: the path is a folder (a pattern ending in <c>/</c> matches only those).
    /// </summary>
    public bool IsIgnored(string path, bool isFolder)
    {
        if (_rules.Count == 0) return false;
        var parts = path.Split('/');
        // A folder on the way that is ignored ignores everything in it - nothing in it can be taken back in.
        for (var depth = 1; depth < parts.Length; depth++)
            if (Matches(string.Join('/', parts, 0, depth), true)) return true;
        return Matches(path, isFolder);
    }

    private bool Matches(string path, bool isFolder)
    {
        var ignored = false;
        foreach (var rule in _rules)
        {
            if (rule.FolderOnly && !isFolder) continue;
            if (rule.Pattern.IsMatch(path)) ignored = !rule.Negated;
        }
        return ignored;
    }

    private sealed record Rule(Regex Pattern, bool Negated, bool FolderOnly)
    {
        public static Rule? From(string line)
        {
            var negated = false;
            if (line.StartsWith('!'))
            {
                negated = true;
                line = line[1..];
            }
            else if (line.StartsWith("\\!", StringComparison.Ordinal) || line.StartsWith("\\#", StringComparison.Ordinal))
            {
                line = line[1..];
            }
            var folderOnly = line.EndsWith('/');
            line = line.TrimEnd('/');
            if (line.Length == 0) return null;
            // A slash at the start or in the middle ties the pattern to the top; otherwise it matches a name at any depth.
            var anchored = line.Contains('/');
            line = line.TrimStart('/');
            if (line.Length == 0) return null;
            var regex = new StringBuilder("^");
            if (!anchored) regex.Append("(?:.*/)?");
            regex.Append(Translate(line));
            regex.Append('$');
            return new Rule(new Regex(regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
                negated, folderOnly);
        }

        private static string Translate(string glob)
        {
            var result = new StringBuilder();
            for (var i = 0; i < glob.Length; i++)
            {
                var c = glob[i];
                switch (c)
                {
                    case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                        // "**/" - any folders, none too; "/**" at the end - everything inside; "**" elsewhere - anything.
                        var atStart = i == 0 || glob[i - 1] == '/';
                        i++;
                        if (atStart && i + 1 < glob.Length && glob[i + 1] == '/')
                        {
                            result.Append("(?:.*/)?");
                            i++;
                        }
                        else
                        {
                            result.Append(".*");
                        }
                        break;
                    case '*':
                        result.Append("[^/]*");
                        break;
                    case '?':
                        result.Append("[^/]");
                        break;
                    case '[':
                        var close = glob.IndexOf(']', i + 1);
                        if (close < 0) throw new ArgumentException("a [ without its ]");
                        var set = glob[(i + 1)..close];
                        if (set.StartsWith('!')) set = "^" + set[1..];
                        result.Append('[').Append(set.Replace("\\", "\\\\")).Append(']');
                        i = close;
                        break;
                    case '\\' when i + 1 < glob.Length:
                        result.Append(Regex.Escape(glob[++i].ToString()));
                        break;
                    default:
                        result.Append(Regex.Escape(c.ToString()));
                        break;
                }
            }
            return result.ToString();
        }
    }
}
