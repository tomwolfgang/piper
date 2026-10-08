using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Memory;

namespace Piper.App.Controls;

/// <summary>
/// How the inspector decodes a body that <see cref="ImageGuard"/> has allowed. SkipMetadata stops the
/// JPEG and WebP decoders from keeping an ICC profile, EXIF or XMP, but the PNG and BMP decoders in
/// 3.1.12 keep a profile regardless (checked against the library), which is why the guard refuses
/// profiles itself. One frame is all the viewer shows. The allocator limit bounds what a header the
/// guard cannot check (a GIF frame descriptor, say) may ask for.
/// </summary>
internal static class ImageDecoding
{
    public static DecoderOptions Options { get; } = new()
    {
        SkipMetadata = true,
        MaxFrames = 1,
        Configuration = CreateConfiguration(),
    };

    // A clone of the default keeps the format decoders registered (a bare new Configuration() has
    // none) and, unlike changing the default, leaves the process-wide allocator alone.
    private static Configuration CreateConfiguration()
    {
        var configuration = Configuration.Default.Clone();
        configuration.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions { AllocationLimitMegabytes = 512 });
        return configuration;
    }
}
