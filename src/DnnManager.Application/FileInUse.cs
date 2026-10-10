namespace DnnManager.Application;

/// <summary>A file another program has open - what goes away by itself, so it is worth trying again.</summary>
public static class FileInUse
{
    /// <summary>
    /// A sharing or lock violation, or a DLL a process has mapped (the worker process letting go). Not access denied,
    /// a full disk or a damaged file: those fail the same way each time.
    /// </summary>
    public static bool Is(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33 or 1224;
}
