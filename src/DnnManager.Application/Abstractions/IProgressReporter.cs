using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

/// <summary>Reports progress / status from use cases back to the presentation layer.</summary>
public interface IProgressReporter
{
    void Step(string title);
    void Info(string message);
    void Success(string message);
    void Fail(string message);
    /// <summary>Something the user must act on later, without failing the operation.</summary>
    void Warn(string message);
    /// <summary>Updates a single status line in place (e.g. a running download percentage).</summary>
    void Progress(string message);

    // What the Output tab can show beyond lines - each optional: a reporter that has no use for it ignores it.

    /// <summary>
    /// The stages the operation will go through, by their short names - shown as pending until they start, and as
    /// skipped when it fails (or never gets to them). Steps whose <c>name</c> is one of these fill them in.
    /// </summary>
    void Plan(params string[] stages) { }

    /// <summary>A step with a short <paramref name="name"/> (the stage list) besides its <paramref name="title"/> (the log).</summary>
    void Step(string title, string name) => Step(title);

    /// <summary>What the operation works with, e.g. the SQL Server and its version - shown next to its title.</summary>
    void Context(string text) { }

    /// <summary>A figure in the operation's summary, e.g. "Files copied" = "4 487 · 148,4 MB".</summary>
    void Fact(string name, string value) { }

    /// <summary>Where the result can be opened, e.g. the new site's address - offered once the operation is done.</summary>
    void Link(string url) { }

    /// <summary>A failure with what lies behind it (<paramref name="details"/>) and what to do about it (<paramref name="hint"/>).</summary>
    void Fail(string message, IReadOnlyList<string> details, string? hint) => Fail(message);
}
