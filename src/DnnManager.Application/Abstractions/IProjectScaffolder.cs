using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

/// <summary>Lays down supporting source-control files in a managed DNN project directory.</summary>
public interface IProjectScaffolder
{
    /// <summary>
    /// Writes a DNN-tuned <c>.gitignore</c> into <paramref name="projectDirectory"/> when one is not
    /// already present, so a freshly set-up or cloned site is ready to commit without dragging in
    /// build output, runtime data, caches, logs or portal uploads. A no-op (still Ok) when the
    /// project already has a <c>.gitignore</c>, so an existing site's file is never clobbered.
    /// </summary>
    Result EnsureGitignore(string projectDirectory);
}
