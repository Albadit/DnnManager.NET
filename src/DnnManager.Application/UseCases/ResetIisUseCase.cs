using DnnManager.Application.Abstractions;
using DnnManager.Domain;

namespace DnnManager.Application.UseCases;

/// <summary>
/// Restarts IIS (<c>iisreset</c>). Clears stuck worker processes and picks up IIS changes made
/// outside the app - e.g. a newly installed URL Rewrite module behind a 500.19 error.
/// </summary>
public sealed class ResetIisUseCase
{
    private readonly IIisManager _iis;
    private readonly IUserPrompt _prompt;

    public ResetIisUseCase(IIisManager iis, IUserPrompt prompt)
    {
        _iis = iis; _prompt = prompt;
    }

    public async Task<Result> ExecuteAsync(IProgressReporter reporter, CancellationToken ct)
    {
        if (!_iis.IsAvailable())
            return Result.Fail("IIS isn't installed or its configuration can't be read - check Prerequisites.");

        // Every site on this machine goes down for a moment, not just the DNN projects.
        if (!await _prompt.ConfirmAsync("Restart IIS? Every website on this machine stops for a few seconds.", false, ct))
            return Result.Fail("Aborted by user.");

        reporter.Step("Restarting IIS (iisreset)");
        var reset = await _iis.ResetAsync(ct);
        if (!reset.Success) return reset;

        reporter.Success("IIS restarted.");
        return Result.Ok();
    }
}
