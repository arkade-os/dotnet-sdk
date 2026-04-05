using QRCoder;

namespace NArk.Wallet.Client.Services;

/// <summary>
/// Generates scannable QR code SVG strings using QRCoder.
/// </summary>
public static class QrCodeHelper
{
    /// <summary>
    /// Generates an SVG string for the given data, suitable for embedding in Blazor markup.
    /// </summary>
    public static string GenerateSvg(string data, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(data, QRCodeGenerator.ECCLevel.L);
        using var svgQr = new SvgQRCode(qrData);
        return svgQr.GetGraphic(pixelsPerModule, "#000000", "#FFFFFF");
    }
}
