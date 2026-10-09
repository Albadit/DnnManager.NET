using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

/// <param name="DesktopInstalled">Docker Desktop (or at least the docker CLI) is on this PC.</param>
/// <param name="ContainerState">Docker's state of the container ("running", "exited"…); null when it doesn't exist or the engine is down.</param>
/// <param name="ContainerStatus">Docker's description, e.g. "Up 2 hours (healthy)".</param>
public sealed record DockerStatus(bool DesktopInstalled, string? ClientVersion, bool EngineRunning, string? EngineVersion,
    string? ContainerState, string? ContainerStatus);
