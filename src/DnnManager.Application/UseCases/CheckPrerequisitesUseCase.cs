using DnnManager.Application.Abstractions;
using DnnManager.Domain;

namespace DnnManager.Application.UseCases;

public sealed class CheckPrerequisitesUseCase
{
    private readonly IPrerequisiteChecker _prereq;
    private readonly LocalSqlContainer _sqlContainer;
    private readonly IUserPrompt _prompt;

    public CheckPrerequisitesUseCase(IPrerequisiteChecker prereq, LocalSqlContainer sqlContainer, IUserPrompt prompt)
    {
        _prereq = prereq; _sqlContainer = sqlContainer; _prompt = prompt;
    }

    public async Task<Result> ExecuteAsync(IProgressReporter reporter, CancellationToken ct)
    {
        reporter.Step("SQL Server");
        var sql = await _sqlContainer.CheckAsync(reporter, ct);
        reporter.Step("IIS features");
        var iis = await _prereq.EnsureIisFeaturesAsync(reporter, _prompt, ct);
        return sql.Success && iis.Success ? Result.Ok() : Result.Fail("Some prerequisites are missing.");
    }
}
