using System.IO.Compression;
using System.Text;

namespace LLMBench.Heatmap;

/// <summary>A plain RGB image and a minimal PNG encoder (no external libraries, runs on Windows and Linux).</summary>
public sealed class RgbImage(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    private readonly byte[] _px = new byte[width * height * 3];

    public void Fill(uint rgb) => FillRect(0, 0, Width, Height, rgb);

    public void FillRect(int x, int y, int w, int h, uint rgb)
    {
        byte r = (byte)(rgb >> 16), g = (byte)(rgb >> 8), b = (byte)rgb;
        for (int yy = Math.Max(0, y); yy < Math.Min(Height, y + h); yy++)
            for (int xx = Math.Max(0, x); xx < Math.Min(Width, x + w); xx++)
            {
                int i = (yy * Width + xx) * 3;
                _px[i] = r; _px[i + 1] = g; _px[i + 2] = b;
            }
    }

    public void SavePng(string path)
    {
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)Width);
        WriteBE(ihdr, 4, (uint)Height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // colour type RGB
        Chunk(fs, "IHDR", ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            for (int y = 0; y < Height; y++)
            {
                z.WriteByte(0); // filter: none
                z.Write(_px, y * Width * 3, Width * 3);
            }
        Chunk(fs, "IDAT", raw.ToArray());
        Chunk(fs, "IEND", []);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, (uint)data.Length);
        s.Write(len);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var crc = new byte[4];
        WriteBE(crc, 0, Crc32(t, data));
        s.Write(crc);
    }

    private static void WriteBE(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
