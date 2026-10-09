using System.Globalization;
using System.Net;
using System.Text;
using DnnManager.Application.Abstractions;

namespace DnnManager.Infrastructure.Hosts;

/// <summary>One line of DNN Manager's block in the hosts file: <c>127.0.0.1 mysite.dnndev.me</c>.</summary>
public sealed record HostsEntry(string Address, string Host);

/// <summary>
/// DNN Manager's block in the Windows hosts file - why it is there: <see cref="HostsFileService"/>. The block, between
/// its two marker lines, is DNN Manager's alone and written as a whole; every other line of the file stays byte for
/// byte as it was.
/// </summary>
public static class HostsFile
{
    public const string Begin = "# DNN Manager: local sites - begin. Kept by DNN Manager from its IIS sites; lines between these two are replaced.";
    public const string End = "# DNN Manager: local sites - end";

    // What the markers are known by - a marker line with its comment changed is still one.
    private const string BeginPrefix = "# DNN Manager: local sites - begin";
    private const string EndPrefix = "# DNN Manager: local sites - end";

    /// <summary>What a binding on every address is reached at - what dnndev.me's public DNS answers too.</summary>
    public const string Loopback = "127.0.0.1";

    public static string DefaultPath => Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    // Latin-1 turns every byte into one character and back: the rest of the file - whatever its encoding, a BOM
    // included - is written back exactly as it was read. What DNN Manager adds is ASCII.
    private static readonly Encoding Bytes = Encoding.Latin1;

    /// <summary>
    /// The entries <paramref name="sites"/> need: the host name of each web binding - <c>mysite.dnndev.me</c>, or a custom
    /// domain like <c>shop.test</c> or <c>www.customer.com</c> - to the address the binding listens on (<see cref="Loopback"/>
    /// for one on every address). Sorted by host name; a name with several bindings gets one entry, loopback first.
    /// </summary>
    public static IReadOnlyList<HostsEntry> EntriesFor(IEnumerable<IisSiteRuntime> sites) =>
        sites.SelectMany(s => s.Bindings)
            .Where(b => b.IsWeb)
            .Select(b => (Host: HostOf(b.Host), Address: AddressOf(b.Address)))
            .Where(e => e.Host is not null && e.Address is not null)
            .GroupBy(e => e.Host!, StringComparer.Ordinal)
            .Select(g => new HostsEntry(g.Select(e => e.Address!).OrderBy(a => a == Loopback ? 0 : 1).ThenBy(a => a, StringComparer.Ordinal).First(), g.Key))
            .OrderBy(e => e.Host, StringComparer.Ordinal)
            .ToList();

    // Every name a site on this PC is bound to: the binding says that name is served here - a real domain too, which is
    // how a copy of a live site is tried out locally. Not "localhost" (Windows answers it itself), an IP address, or a
    // wildcard binding (*.shop.test) - the hosts file has no wildcards.
    private static string? HostOf(string host)
    {
        var name = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (name.Length == 0 || name == "localhost" || Uri.CheckHostName(name) != UriHostNameType.Dns) return null;
        // An international name (café.test) as Windows looks it up: in punycode (xn--caf-dma.test) - the file is ASCII.
        if (!name.All(char.IsAscii))
        {
            try { name = new IdnMapping().GetAscii(name); }
            catch (ArgumentException) { return null; }
        }
        return name;
    }

    // "*" listens on every address; IIS writes an IPv6 one in brackets.
    private static string? AddressOf(string address)
    {
        var a = address.Trim();
        if (a.Length == 0 || a == "*") return Loopback;
        return IPAddress.TryParse(a.Trim('[', ']'), out var ip) ? ip.ToString() : null;
    }

    /// <summary>
    /// <paramref name="text"/> with DNN Manager's block holding <paramref name="entries"/> - where the block was, or at
    /// the end; without the block when there are none. A name the file already maps outside the block is left out: the
    /// user's own line wins.
    /// </summary>
    public static string WithEntries(string text, IReadOnlyList<HostsEntry> entries)
    {
        // Split on \n alone, each line keeping its \r: joined with \n again, the lines that stay are as they were.
        var lines = text.Split('\n').ToList();
        var begin = lines.FindIndex(l => Content(l).StartsWith(BeginPrefix, StringComparison.Ordinal));
        var end = begin < 0 ? -1 : lines.FindIndex(begin + 1, l => Content(l).StartsWith(EndPrefix, StringComparison.Ordinal));
        // A begin without an end (the end line deleted by hand): only the marker goes - lines after it may be the
        // user's, and the entries left are then the user's own lines.
        var blockLength = begin < 0 ? 0 : end < 0 ? 1 : end - begin + 1;

        var outside = lines.Where((_, i) => i < begin || i >= begin + blockLength);
        var mapped = new HashSet<string>(outside.SelectMany(NamesOn), StringComparer.OrdinalIgnoreCase);
        var wanted = entries.Where(e => !mapped.Contains(e.Host)).ToList();

        var cr = text.Contains("\r\n", StringComparison.Ordinal) || !text.Contains('\n') ? "\r" : "";
        var block = wanted.Count == 0 ? []
            : new[] { Begin }.Concat(wanted.Select(e => $"{e.Address,-15} {e.Host}")).Append(End).Select(l => l + cr).ToList();

        if (begin >= 0)
        {
            lines.RemoveRange(begin, blockLength);
            // The empty line put before the block goes with it.
            if (block.Count == 0 && begin > 0 && lines[begin - 1].Trim().Length == 0) lines.RemoveAt(begin - 1);
            else lines.InsertRange(begin, block);
            return string.Join('\n', lines);
        }
        if (block.Count == 0) return text;

        // At the end, after an empty line, ending with a line break as the file does.
        var head = text.Length == 0 ? "" : text.TrimEnd('\r', '\n') + cr + "\n" + cr + "\n";
        return head + string.Join('\n', block) + "\n";
    }

    // A UTF-8 byte order mark as Latin-1 reads it: an editor that saved the file as "UTF-8 with BOM" put it before line 1.
    private const string Bom = "ï»¿";

    // A line without the spaces around it - and the first without its byte order mark.
    private static string Content(string line)
    {
        var text = line.Trim();
        return text.StartsWith(Bom, StringComparison.Ordinal) ? text[Bom.Length..].Trim() : text;
    }

    // The names a hosts line maps: everything after the address, up to a comment.
    private static IEnumerable<string> NamesOn(string line)
    {
        line = Content(line);
        var hash = line.IndexOf('#');
        var parts = (hash < 0 ? line : line[..hash]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Skip(1).Select(n => n.TrimEnd('.'));
    }

    /// <summary>The copy of the hosts file as it was before DNN Manager last changed it - beside it, as hosts.dnnmanager.bak.</summary>
    public static string BackupPath(string path) => path + ".dnnmanager.bak";

    /// <summary>
    /// Puts <paramref name="entries"/> into the hosts file at <paramref name="path"/> (<see cref="WithEntries"/>), read and
    /// written through one handle so no other writer comes in between. What it held before is copied to
    /// <see cref="BackupPath"/> first: a write cut off (a crash, the power) leaves the file to be put back from it. False
    /// when it already held them.
    /// </summary>
    public static bool Write(string path, IReadOnlyList<HostsEntry> entries)
    {
        using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        var bytes = new byte[file.Length];
        file.ReadExactly(bytes);
        var text = Bytes.GetString(bytes);
        var next = WithEntries(text, entries);
        if (next == text) return false;

        // Written to a file of its own, then moved over the backup - never a half-written backup of a whole file.
        var backup = BackupPath(path);
        File.WriteAllBytes(backup + ".new", bytes);
        File.Move(backup + ".new", backup, overwrite: true);

        var written = Bytes.GetBytes(next);
        file.Position = 0;
        file.Write(written);
        file.SetLength(written.Length);
        file.Flush(flushToDisk: true);
        return true;
    }
}
