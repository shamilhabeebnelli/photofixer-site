namespace PhotoFixer.Api;

/// <summary>
/// Identifies the real image format from magic bytes; never trusts the
/// client's MIME hint. Mirrors the sniffer in the website's wwwroot/js/app.js.
/// </summary>
public static class ImageSniff
{
    public static string? Sniff(byte[] b)
    {
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length > 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "image/gif";
        if (b.Length > 2 && b[0] == 0x42 && b[1] == 0x4D) return "image/bmp";
        if (b.Length > 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
            b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";
        if (b.Length > 12 && b[4] == 0x66 && b[5] == 0x74 && b[6] == 0x79 && b[7] == 0x70)
        {
            var brand = System.Text.Encoding.ASCII.GetString(b, 8, 4);
            if (brand is "avif" or "avis") return "image/avif";
            // heic/heix/hevc/hevx/heim/heis/hevm/hevs/mif1/msf1 -> HEIF container
            if (brand is "mif1" or "msf1" || brand.StartsWith("he")) return "image/heic";
        }
        return null;
    }
}
