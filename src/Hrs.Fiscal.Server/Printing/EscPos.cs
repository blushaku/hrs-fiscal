using System.Text;

namespace Hrs.Fiscal.Server.Printing;

/// <summary>
/// Builds an ESC/POS print job (Epson TM, Star in ESC/POS mode, Bixolon, Xprinter and most 58/80 mm receipt printers).
/// Text is sent in code page PC850, which has the Albanian letters ë, Ë, ç, Ç. The QR code uses the printer's own
/// QR command (GS ( k), so it is printed sharp at any size.
/// </summary>
public sealed class EscPos
{
    private static readonly Encoding Pc850 = CreatePc850();

    private static Encoding CreatePc850()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // code pages other than UTF/ASCII are opt-in in .NET
        return Encoding.GetEncoding(850, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }
    private readonly MemoryStream _out = new();

    /// <summary>Characters per line in the normal font (font A): 42 on an Epson TM-T88 with 80 mm paper, 48 on most
    /// other 80 mm printers, 30–32 on 58 mm.</summary>
    public int Columns { get; }
    private readonly int _qrModule;

    public EscPos(PrinterModel model)
    {
        Columns = model.Columns;
        _qrModule = model.QrModule;
        Raw(0x1B, 0x40);            // ESC @  initialise
        Raw(0x1B, 0x74, 0x02);      // ESC t 2  code page PC850 (Multilingual Latin I)
    }

    public EscPos Raw(params byte[] bytes) { _out.Write(bytes); return this; }

    public EscPos Align(TextAlign a) => Raw(0x1B, 0x61, (byte)a);
    public EscPos Bold(bool on) => Raw(0x1B, 0x45, (byte)(on ? 1 : 0));
    /// <summary>Double width and height (GS ! 0x11), or normal size.</summary>
    public EscPos Large(bool on) => Raw(0x1D, 0x21, (byte)(on ? 0x11 : 0x00));

    /// <summary>Prints one line of text; longer text wraps at word boundaries to the paper width.</summary>
    public EscPos Line(string text = "", int? width = null)
    {
        foreach (var part in Wrap(text, width ?? Columns)) { _out.Write(Pc850.GetBytes(part)); _out.WriteByte(0x0A); }
        return this;
    }

    /// <summary>A line with text on the left and a value on the right, e.g. "TOTALI PËR PAGESË         12.50 €".</summary>
    public EscPos LeftRight(string left, string right, int? width = null)
    {
        var w = width ?? Columns;
        if (left.Length + right.Length + 1 > w)
        {
            Line(left, w);
            return Line(right.PadLeft(w), w);
        }
        return Line(left + new string(' ', w - left.Length - right.Length) + right, w);
    }

    public EscPos Rule(char c = '-') => Line(new string(c, Columns));
    public EscPos Feed(int lines = 1) => Raw(0x1B, 0x64, (byte)Math.Clamp(lines, 0, 255));

    /// <summary>QR code with the printer's native command: model 2, error correction M.</summary>
    public EscPos Qr(string data, int? moduleSize = null)
    {
        var bytes = Encoding.ASCII.GetBytes(data);
        var size = (byte)Math.Clamp(moduleSize ?? _qrModule, 1, 16);
        Raw(0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00);   // model 2
        Raw(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, size);         // module size
        Raw(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31);         // error correction M
        var len = bytes.Length + 3;
        Raw(0x1D, 0x28, 0x6B, (byte)(len % 256), (byte)(len / 256), 0x31, 0x50, 0x30);
        _out.Write(bytes);
        return Raw(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30);  // print
    }

    /// <summary>Feeds the paper past the cutter and cuts (partial cut).</summary>
    public EscPos Cut() => Raw(0x1D, 0x56, 0x42, 0x03);

    public byte[] ToArray() => _out.ToArray();

    public static IEnumerable<string> Wrap(string text, int width)
    {
        if (text.Length <= width) { yield return text; yield break; }
        var rest = text;
        while (rest.Length > width)
        {
            var cut = rest.LastIndexOf(' ', width);
            if (cut <= 0) cut = width;
            yield return rest[..cut].TrimEnd();
            rest = rest[cut..].TrimStart();
        }
        if (rest.Length > 0) yield return rest;
    }
}

public enum TextAlign : byte { Left = 0, Center = 1, Right = 2 }
