using DnnManager.Application.Abstractions;

namespace DnnManager.Presentation.Services;

/// <summary>Adapts the activity log to the application-layer reporter interface.</summary>
public sealed class GuiProgressReporter(ActivityLog log) : IProgressReporter
{
    private readonly ActivityLog _log = log;

    public void Step(string title)   => _log.Step(title);
    public void Info(string message) => _log.Info(message);
    public void Success(string m)    => _log.Success(m);
    public void Fail(string m)       => _log.Fail(m);
    public void Warn(string m)       => _log.Warn(m);
    public void Progress(string m)   => _log.Progress(m);
    public void Step(string title, string name) => _log.Step(title, name);
    public void Fail(string m, IReadOnlyList<string> details, string? hint) => _log.Fail(m, details, hint);
    public void Plan(params string[] stages) => _log.Plan(stages);
    public void Context(string text) => _log.Context(text);
    public void Fact(string name, string value) => _log.Fact(name, value);
    public void Link(string url) => _log.Link(url);
}
