using DnnManager.Application.Abstractions;
using DnnManager.Domain;

namespace DnnManager.Application.UseCases;

public sealed class CheckPrerequisitesUseCase
{
    private readonly IPrerequisiteChecker _prereq;
    private readonly IUserPrompt _prompt;

    public CheckPrerequisitesUseCase(IPrerequisiteChecker prereq, IUserPrompt prompt)
    {
        _prereq = prereq; _prompt = prompt;
    }

    public async Task<Result> ExecuteAsync(IProgressReporter reporter, CancellationToken ct)
    {
        reporter.Step("Docker");
        var docker = await _prereq.CheckDockerAsync(reporter, ct);
        reporter.Step("IIS features");
        var iis = await _prereq.EnsureIisFeaturesAsync(reporter, _prompt, ct);
        return docker.Success && iis.Success ? Result.Ok() : Result.Fail("Some prerequisites are missing.");
    }
}
