using System.Runtime.InteropServices;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ForegroundState;

internal static partial class MacOSForegroundApplicationReader
{
    private static readonly Dictionary<string, (string Source, BinaryContent Content)> Icons = new(StringComparer.Ordinal);
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";

    private static BinaryContent? ReadIcon(int processId)
    {
        var pool = Send(Send(GetClass("NSAutoreleasePool"), "alloc"), "init");
        try
        {
            var application = SendProcess(GetClass("NSRunningApplication"), RegisterSelector("runningApplicationWithProcessIdentifier:"), processId);
            if (application == 0) return null;
            var image = Send(application, "icon");
            if (image == 0) return null;
            var path = ReadString(Send(Send(application, "executableURL"), "path"));
            var key = ReadString(Send(application, "bundleIdentifier")) + "\n" + path;
            var tiff = Send(image, "TIFFRepresentation");
            if (tiff == 0) return null;
            var source = Orisumi.Core.BinaryValue.ComputeDigest(CopyData(tiff));
            if (path is not null && Icons.TryGetValue(key, out var cached) && cached.Source == source) return cached.Content;
            var png = RenderPng(image);
            if (png is null) return null;
            var content = new BinaryContent(png, "image/png");
            if (path is not null) Icons[key] = (source, content);
            return content;
        }
        finally { Send(pool, "drain"); }
    }

    private static byte[]? RenderPng(nint image)
    {
        var rect = new Rectangle(0, 0, 1024, 1024);
        var source = ImageForRect(image, RegisterSelector("CGImageForProposedRect:context:hints:"), ref rect, 0, 0);
        if (source == 0) return null;
        var colorSpace = CGColorSpaceCreateDeviceRGB();
        var context = CGBitmapContextCreate(0, 1024, 1024, 8, 4096, colorSpace, 1);
        CGColorSpaceRelease(colorSpace);
        if (context == 0) return null;
        try
        {
            CGContextDrawImage(context, new(0, 0, 1024, 1024), source);
            var rendered = CGBitmapContextCreateImage(context);
            if (rendered == 0) return null;
            try
            {
                var data = CFDataCreateMutable(0, 0);
                var type = CFStringCreateWithCString(0, "public.png", 0x08000100);
                var destination = CGImageDestinationCreateWithData(data, type, 1, 0);
                try
                {
                    if (destination == 0) return null;
                    CGImageDestinationAddImage(destination, rendered, 0);
                    return CGImageDestinationFinalize(destination) != 0 ? CopyData(data) : null;
                }
                finally
                {
                    if (destination != 0) CFRelease(destination);
                    CFRelease(type);
                    CFRelease(data);
                }
            }
            finally { CGImageRelease(rendered); }
        }
        finally { CGContextRelease(context); }
    }

    private static byte[] CopyData(nint data)
    {
        var result = new byte[checked((int)CFDataGetLength(data))];
        Marshal.Copy(CFDataGetBytePtr(data), result, 0, result.Length);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Rectangle(double X, double Y, double Width, double Height);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static partial nint SendProcess(nint receiver, nint selector, int processId);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static partial nint ImageForRect(nint receiver, nint selector, ref Rectangle rectangle, nint context, nint hints);
    [LibraryImport(CoreGraphics)] private static partial nint CGColorSpaceCreateDeviceRGB();
    [LibraryImport(CoreGraphics)] private static partial void CGColorSpaceRelease(nint space);
    [LibraryImport(CoreGraphics)] private static partial nint CGBitmapContextCreate(nint data, nuint width, nuint height, nuint bits, nuint rowBytes, nint space, uint info);
    [LibraryImport(CoreGraphics)] private static partial void CGContextDrawImage(nint context, Rectangle rect, nint image);
    [LibraryImport(CoreGraphics)] private static partial nint CGBitmapContextCreateImage(nint context);
    [LibraryImport(CoreGraphics)] private static partial void CGContextRelease(nint context);
    [LibraryImport(CoreGraphics)] private static partial void CGImageRelease(nint image);
    [LibraryImport(CoreFoundation)] private static partial nint CFDataCreateMutable(nint allocator, nint capacity);
    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)] private static partial nint CFStringCreateWithCString(nint allocator, string value, uint encoding);
    [LibraryImport(CoreFoundation)] private static partial nint CFDataGetLength(nint data);
    [LibraryImport(CoreFoundation)] private static partial nint CFDataGetBytePtr(nint data);
    [LibraryImport(CoreFoundation)] private static partial void CFRelease(nint value);
    [LibraryImport(ImageIO)] private static partial nint CGImageDestinationCreateWithData(nint data, nint type, nuint count, nint options);
    [LibraryImport(ImageIO)] private static partial void CGImageDestinationAddImage(nint destination, nint image, nint properties);
    [LibraryImport(ImageIO)] private static partial byte CGImageDestinationFinalize(nint destination);
}
