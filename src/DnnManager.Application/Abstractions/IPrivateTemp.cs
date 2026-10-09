namespace DnnManager.Application.Abstractions;

/// <summary>
/// A folder for temporary files that only administrators can change. DNN Manager runs elevated: a file it writes and
/// later reads back or runs (a database export, an update) goes here, never to %TEMP%, which every program of the
/// signed-in user can change.
/// </summary>
public interface IPrivateTemp
{
    string Folder { get; }
}
