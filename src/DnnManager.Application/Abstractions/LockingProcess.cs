using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

/// <param name="Name">A readable name, e.g. "Visual Studio Code".</param>
/// <param name="Reason">Why it blocks the folder, e.g. "has files open" or "working folder is inside it".</param>
/// <param name="CanClose">False for Windows itself, services and DNN Manager - those are never closed.</param>
public sealed record LockingProcess(int Id, string Name, string ExeName, string Reason, bool CanClose)
{
    public override string ToString() => $"{Name} ({ExeName}, pid {Id}) - {Reason}";
}
