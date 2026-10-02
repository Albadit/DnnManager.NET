using DnnManager.Application.Abstractions;
using DnnManager.Domain;

namespace DnnManager.Application.UseCases;

/// <summary>
/// Starts, stops or restarts IIS as a whole (<c>iisreset</c>). A restart clears stuck worker processes and picks up
/// IIS changes made outside the app - e.g. a newly installed URL Rewrite module behind a 500.19 error.
/// </summary>
public sealed class IisServerUseCase
{
    private readonly IIisManager _iis;
    private readonly IUserPrompt _prompt;

    public IisServerUseCase(IIisManager iis, IUserPrompt prompt)
    {
        _iis = iis; _prompt = prompt;
    }

    public async Task<Result> ExecuteAsync(IisServerAction action, IProgressReporter reporter, CancellationToken ct)
    {
        if (!_iis.IsAvailable())
            return Result.Fail("IIS isn't installed or its configuration can't be read - check Settings → IIS.");

        // Every site on this machine goes down, not just the DNN projects.
        var (question, yes) = action switch
        {
            IisServerAction.Stop => ("Stop IIS? Every website on this machine stops until IIS is started again.", "Stop IIS"),
            IisServerAction.Restart => ("Restart IIS? Every website on this machine stops for a few seconds.", "Restart IIS"),
            _ => ((string?)null, "")
        };
        if (question is not null && !await _prompt.ConfirmAsync(question, yes, "Cancel", false, ct))
            return Result.Aborted();

        var (step, done) = action switch
        {
            IisServerAction.Start => ("Starting IIS (iisreset /start)", "IIS started."),
            IisServerAction.Stop => ("Stopping IIS (iisreset /stop)", "IIS stopped."),
            _ => ("Restarting IIS (iisreset)", "IIS restarted.")
        };
        reporter.Step(step);
        var result = await _iis.ControlServerAsync(action, ct);
        if (!result.Success) return result;

        reporter.Success(done);
        return Result.Ok();
    }
}
