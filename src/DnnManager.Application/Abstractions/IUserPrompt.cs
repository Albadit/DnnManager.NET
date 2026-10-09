using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

public interface IUserPrompt
{
    /// <summary>
    /// Asks <paramref name="question"/> with two buttons that say what they do - e.g. "Drop database" and "Keep
    /// database", never a bare Yes / No. True when <paramref name="yes"/> is chosen.
    /// </summary>
    Task<bool> ConfirmAsync(string question, string yes, string no, bool defaultYes = false, CancellationToken ct = default);

    /// <summary>
    /// Asks before something that can't be taken back - deleting a project, dropping a database: <paramref name="yes"/>
    /// looks dangerous, and the safe answer is the default.
    /// </summary>
    Task<bool> ConfirmDangerAsync(string question, string yes, string no, CancellationToken ct = default) =>
        ConfirmAsync(question, yes, no, false, ct);
}
