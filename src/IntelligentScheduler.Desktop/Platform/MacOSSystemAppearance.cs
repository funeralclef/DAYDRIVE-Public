using System.Runtime.InteropServices;
using Avalonia.Media;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// Small AppKit bridge for the two values that Avalonia does not expose: the user's current
/// accent colour and the actual desktop image. Reading them through AppKit also works for
/// separately configured displays and does not depend on the on-disk format of macOS settings.
/// </summary>
internal static class MacOSSystemAppearance
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphicsLibrary = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const int SampleDimension = 96;
    // kCGImageAlphaPremultipliedLast | kCGBitmapByteOrder32Big, so the byte sequence is RGBA.
    private const uint RgbaBitmapFormat = 1u | (4u << 12);
    private static readonly IntPtr AppKitLibrary = LoadAppKit();

    public static Color? TryGetAccentColor()
    {
        if (!OperatingSystem.IsMacOS() || AppKitLibrary == IntPtr.Zero) return null;
        try
        {
            var color = SendPointer(GetClass("NSColor"), Selector("controlAccentColor"));
            if (color == IntPtr.Zero) return null;

            // NSColor can represent a dynamic/system colour. Resolve it in a concrete RGB colour
            // space before asking for components, otherwise getRed:green:blue:alpha: can decline it.
            var colorSpace = SendPointer(GetClass("NSColorSpace"), Selector("sRGBColorSpace"));
            if (colorSpace != IntPtr.Zero)
                color = SendPointer(color, Selector("colorUsingColorSpace:"), colorSpace);
            if (color == IntPtr.Zero ||
                !GetComponents(color, Selector("getRed:green:blue:alpha:"),
                    out var red, out var green, out var blue, out _)) return null;

            return Color.FromRgb(ToByte(red), ToByte(green), ToByte(blue));
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // AppKit is always present on a normal macOS installation. If it is unavailable on a
            // future runtime, the caller simply falls back to the selected DAYDRIVE palette.
            return null;
        }
    }

    public static string? TryGetWallpaperPath()
    {
        if (!OperatingSystem.IsMacOS() || AppKitLibrary == IntPtr.Zero) return null;
        try
        {
            var workspace = SendPointer(GetClass("NSWorkspace"), Selector("sharedWorkspace"));
            var screen = SendPointer(GetClass("NSScreen"), Selector("mainScreen"));
            if (workspace == IntPtr.Zero || screen == IntPtr.Zero) return null;

            var url = SendPointer(workspace, Selector("desktopImageURLForScreen:"), screen);
            if (url == IntPtr.Zero) return null;
            var path = SendPointer(url, Selector("path"));
            var utf8 = path == IntPtr.Zero ? IntPtr.Zero : SendPointer(path, Selector("UTF8String"));
            return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the currently displayed desktop through AppKit and downscales it before sampling.
    /// This is deliberately separate from the cross-platform Skia path: Apple's default
    /// wallpapers are often HEIC files, which an application-bundled Skia codec is not required
    /// to understand, while NSImage handles every desktop format macOS itself can display.
    /// </summary>
    public static Color? TryGetWallpaperAccent()
    {
        var path = TryGetWallpaperPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        IntPtr colorSpace = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;
        IntPtr image = IntPtr.Zero;
        try
        {
            var pathString = SendPointerUtf8(GetClass("NSString"), Selector("stringWithUTF8String:"), path);
            var imageClass = GetClass("NSImage");
            image = pathString == IntPtr.Zero || imageClass == IntPtr.Zero
                ? IntPtr.Zero
                : SendPointer(SendPointer(imageClass, Selector("alloc")),
                    Selector("initWithContentsOfFile:"), pathString);
            var cgImage = image == IntPtr.Zero
                ? IntPtr.Zero
                : SendPointer(image, Selector("CGImageForProposedRect:context:hints:"),
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (cgImage == IntPtr.Zero) return null;

            colorSpace = CGColorSpaceCreateDeviceRGB();
            context = CGBitmapContextCreate(IntPtr.Zero, SampleDimension, SampleDimension, 8,
                SampleDimension * 4, colorSpace, RgbaBitmapFormat);
            if (context == IntPtr.Zero) return null;

            CGContextDrawImage(context, new CGRect(0, 0, SampleDimension, SampleDimension), cgImage);
            var pixels = CGBitmapContextGetData(context);
            if (pixels == IntPtr.Zero) return null;

            double red = 0, green = 0, blue = 0, weight = 0;
            for (var index = 0; index < SampleDimension * SampleDimension; index++)
            {
                var offset = index * 4;
                var pixelRed = Marshal.ReadByte(pixels, offset);
                var pixelGreen = Marshal.ReadByte(pixels, offset + 1);
                var pixelBlue = Marshal.ReadByte(pixels, offset + 2);
                var max = Math.Max(pixelRed, Math.Max(pixelGreen, pixelBlue));
                var min = Math.Min(pixelRed, Math.Min(pixelGreen, pixelBlue));
                var saturation = (max - min) / 255d;
                var brightness = (max + min) / 510d;
                var sampleWeight = 0.25 + saturation * 1.8 +
                    (brightness is > 0.18 and < 0.86 ? 0.35 : 0);
                red += pixelRed * sampleWeight;
                green += pixelGreen * sampleWeight;
                blue += pixelBlue * sampleWeight;
                weight += sampleWeight;
            }

            return weight <= 0 ? null : Color.FromRgb(
                (byte)(red / weight), (byte)(green / weight), (byte)(blue / weight));
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (context != IntPtr.Zero) CGContextRelease(context);
            if (colorSpace != IntPtr.Zero) CGColorSpaceRelease(colorSpace);
            if (image != IntPtr.Zero) SendVoid(image, Selector("release"));
        }
    }

    private static IntPtr LoadAppKit()
    {
        if (!OperatingSystem.IsMacOS()) return IntPtr.Zero;
        try
        {
            return NativeLibrary.TryLoad("/System/Library/Frameworks/AppKit.framework/AppKit", out var handle)
                ? handle
                : IntPtr.Zero;
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
        {
            return IntPtr.Zero;
        }
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);

    private static IntPtr Selector(string name) => RegisterSelector(name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass(string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern IntPtr RegisterSelector(string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector, IntPtr value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second,
        IntPtr third);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPointerUtf8(IntPtr receiver, IntPtr selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool GetComponents(IntPtr receiver, IntPtr selector, out double red,
        out double green, out double blue, out double alpha);

    [DllImport(CoreGraphicsLibrary)]
    private static extern IntPtr CGColorSpaceCreateDeviceRGB();

    [DllImport(CoreGraphicsLibrary)]
    private static extern void CGColorSpaceRelease(IntPtr colorSpace);

    [DllImport(CoreGraphicsLibrary)]
    private static extern IntPtr CGBitmapContextCreate(IntPtr data, nint width, nint height,
        nint bitsPerComponent, nint bytesPerRow, IntPtr colorSpace, uint bitmapInfo);

    [DllImport(CoreGraphicsLibrary)]
    private static extern IntPtr CGBitmapContextGetData(IntPtr context);

    [DllImport(CoreGraphicsLibrary)]
    private static extern void CGContextDrawImage(IntPtr context, CGRect rect, IntPtr image);

    [DllImport(CoreGraphicsLibrary)]
    private static extern void CGContextRelease(IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGRect(double X, double Y, double Width, double Height);
}
