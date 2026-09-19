using System;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace ScrewCalendar;

public sealed class MarkdownImageStore
{
    private static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    public MarkdownImageStore(string assetsDirectory)
    {
        AssetsDirectory = Path.GetFullPath(assetsDirectory);
    }

    public string AssetsDirectory { get; }

    public static bool IsSupportedSourceFile(string path)
    {
        var extension = Path.GetExtension(path);
        return Array.Exists(SupportedExtensions, item => extension.Equals(item, StringComparison.OrdinalIgnoreCase));
    }

    public string Save(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        var bytes = buffer.ToArray();
        var fileName = $"{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}.png";
        Directory.CreateDirectory(AssetsDirectory);
        var path = Path.Combine(AssetsDirectory, fileName);
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        return $"assets/{fileName}";
    }

    public string Import(string sourcePath)
    {
        using var stream = File.OpenRead(sourcePath);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return Save(decoder.Frames[0]);
    }

    public BitmapSource? Load(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(AssetsDirectory)!, normalized));
        var assetsRoot = AssetsDirectory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) return null;
        using var stream = File.OpenRead(fullPath);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }
}
