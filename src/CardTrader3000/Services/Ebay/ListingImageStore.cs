using Microsoft.Extensions.Options;

namespace CardTrader3000.Services.Ebay;

/// <summary>
/// Stores uploaded listing photos on disk under {ImageStoragePath}/{listingId}/{guid}.{ext}.
/// File names are generated here, never taken from the browser, so requests can't escape the folder.
/// </summary>
public sealed class ListingImageStore(IWebHostEnvironment env, IOptions<EbayOptions> options)
{
    public const long MaxBytes = 12 * 1024 * 1024; // eBay's per-image limit
    public const int MaxImagesPerListing = 24;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff"
    };

    private string Root => Path.GetFullPath(Path.Combine(env.ContentRootPath, options.Value.ImageStoragePath));

    public static bool IsSupported(string fileName) => ContentTypes.ContainsKey(Path.GetExtension(fileName));

    public static string AcceptAttribute => string.Join(',', ContentTypes.Keys);

    /// <summary>Saves the stream and returns the generated file name.</summary>
    public async Task<string> SaveAsync(int listingId, Stream content, string originalFileName, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (!ContentTypes.ContainsKey(ext))
            throw new InvalidOperationException($"{originalFileName}: unsupported image type. Use JPG, PNG, GIF, WEBP, BMP or TIFF.");

        var dir = Path.Combine(Root, listingId.ToString());
        Directory.CreateDirectory(dir);

        var fileName = $"{Guid.NewGuid():N}{ext}";
        await using var file = File.Create(Path.Combine(dir, fileName));
        await content.CopyToAsync(file, ct);
        return fileName;
    }

    /// <summary>Full path and content type, or null if the name is invalid or the file is gone.</summary>
    public (string Path, string ContentType)? Find(int listingId, string fileName)
    {
        // Only names this class generated: 32 hex chars + a known extension.
        var ext = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (!ContentTypes.TryGetValue(ext, out var type) || stem.Length != 32 || !stem.All(Uri.IsHexDigit))
            return null;

        var path = Path.Combine(Root, listingId.ToString(), fileName);
        return File.Exists(path) ? (path, type) : null;
    }

    public void Delete(int listingId, string fileName)
    {
        if (Find(listingId, fileName) is { } found) File.Delete(found.Path);
    }

    public void DeleteAll(int listingId)
    {
        var dir = Path.Combine(Root, listingId.ToString());
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
