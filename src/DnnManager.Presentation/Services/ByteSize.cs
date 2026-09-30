namespace DnnManager.Presentation.Services;

/// <summary>Byte counts for people: "812 KB", "245.3 MB", "3.02 GB".</summary>
internal static class ByteSize
{
    public static string Format(double bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (1L << 30):N2} GB",
        >= 1L << 20 => $"{bytes / (1L << 20):N1} MB",
        >= 1L << 10 => $"{bytes / (1L << 10):N0} KB",
        _ => $"{bytes:N0} B"
    };
}
