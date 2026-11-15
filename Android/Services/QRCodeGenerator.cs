using System.IO;
using QRCoder;

namespace FairyAI_Android.Services;

public class QRCodeGenerator
{
    public ImageSource Generate(string text, int size = 200)
    {
        var png = GeneratePng(text, size);
        return ImageSource.FromStream(() => new MemoryStream(png, writable: false));
    }

    public static byte[] GeneratePng(string text, int size = 200)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("QR payload cannot be empty.", nameof(text));
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));

        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.Q);
        using var qr = new PngByteQRCode(data);
        var pixelsPerModule = Math.Max(4, (int)Math.Ceiling((double)size / data.ModuleMatrix.Count));
        return qr.GetGraphic(pixelsPerModule, drawQuietZones: true);
    }
}
