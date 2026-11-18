namespace PhotoFixer.Core;

/// <summary>
/// Pixel operations for one loaded source image. The website implements this
/// with the browser's own decoders/encoders (createImageBitmap + canvas);
/// the test harness implements it with a fake size model. All decisions
/// (geometry, quality search, when to give up) stay in C#.
/// </summary>
public interface ICodecSession
{
    /// <summary>Crop + resize the source into the working surface.</summary>
    Task RenderAsync(Geometry g);

    /// <summary>Encode the working surface as JPEG at quality 5-95. Keeps the result as "last". Returns size in bytes.</summary>
    Task<long> EncodeJpegAsync(int quality);

    /// <summary>Encode the working surface as PNG. Keeps the result as "last". Returns size in bytes.</summary>
    Task<long> EncodePngAsync();

    /// <summary>Bytes of the most recent encode.</summary>
    Task<byte[]> LastBytesAsync();
}
