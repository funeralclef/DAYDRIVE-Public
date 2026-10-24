namespace IntelligentScheduler.Application;

public enum AttachmentKind { Unsupported, Image, Document }

/// <summary>
/// The single answer to "can this file be attached, and what happens to it".
///
/// The picker, the composer chip and the request builder all used to decide separately, which
/// is how a file could be attached, look accepted, and only fail once the model was asked.
/// </summary>
public static class AttachmentSupport
{
    /// <summary>Formats the app decodes and re-encodes before sending.</summary>
    public static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".jfif", ".webp", ".bmp", ".gif"];

    /// <summary>Formats the app extracts text from before sending.</summary>
    public static readonly string[] DocumentExtensions =
        [".pdf", ".docx", ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".html", ".htm", ".ics", ".log", ".yml", ".yaml"];

    /// <summary>Anything larger is refused before it is read, not after.</summary>
    public const long MaximumBytes = 25L * 1024 * 1024;

    /// <summary>Longest edge an image is scaled down to before encoding.</summary>
    public const int MaximumImageEdge = 1536;

    public static AttachmentKind Classify(string path)
    {
        var extension = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
        if (extension.Length == 0) return AttachmentKind.Unsupported;
        if (ImageExtensions.Contains(extension)) return AttachmentKind.Image;
        return DocumentExtensions.Contains(extension) ? AttachmentKind.Document : AttachmentKind.Unsupported;
    }

    public static bool IsImage(string path) => Classify(path) == AttachmentKind.Image;

    /// <summary>Wildcards for the file dialog, so unsupported files cannot be chosen at all.</summary>
    public static string[] ImagePatterns => [.. ImageExtensions.Select(extension => "*" + extension)];
    public static string[] DocumentPatterns => [.. DocumentExtensions.Select(extension => "*" + extension)];
}
