using QRCoder;

namespace Hrs.Fiscal.Server.Services;

/// <summary>Renders the ATK QR string ("base64 CitizenCoupon|base64 signature") as an image.</summary>
public static class QrRenderer
{
    // Level M: the payload is ~250 characters; M keeps the code scannable on a folio print.
    public static string Svg(string content)
    {
        using var data = QRCodeGenerator.GenerateQrCode(content, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(4);
    }

    public static byte[] Png(string content, int pixelsPerModule = 6)
    {
        using var data = QRCodeGenerator.GenerateQrCode(content, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }
}
