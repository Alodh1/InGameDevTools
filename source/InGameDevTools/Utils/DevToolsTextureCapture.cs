using System.IO.Compression;
using System.Text;
using OpenTK.Graphics.OpenGL4;

namespace InGameDevTools.Utils;

internal static class DevToolsTextureCapture
{
    public static bool SaveTexture2D(int textureId, int width, int height, string label, out string status)
    {
        status = "";
        if (textureId <= 0)
        {
            status = "No viewport texture is available to save yet.";
            return false;
        }

        if (width <= 0 || height <= 0)
        {
            status = $"Invalid viewport size {width}x{height}.";
            return false;
        }

        try
        {
            byte[] rgba = ReadTextureRgba(textureId, width, height);
            string directory = GetScreenshotDirectory();
            Directory.CreateDirectory(directory);
            string fileName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{SanitizeFileName(label)}.png";
            string path = Path.Combine(directory, fileName);
            WritePng(path, rgba, width, height);
            status = $"Saved viewport screenshot to {path}.";
            return true;
        }
        catch (Exception exception)
        {
            status = $"Viewport screenshot failed: {exception.Message}";
            return false;
        }
    }

    public static string GetScreenshotDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VintagestoryData",
            "InGameDevTools",
            "Screenshots");
    }

    private static byte[] ReadTextureRgba(int textureId, int width, int height)
    {
        int restoreActiveTexture = 0;
        int restoreTexture2D = 0;
        int restorePackAlignment = 4;
        byte[] source = new byte[checked(width * height * 4)];

        GL.GetInteger(GetPName.ActiveTexture, out restoreActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.GetInteger(GetPName.TextureBinding2D, out restoreTexture2D);
        GL.GetInteger(GetPName.PackAlignment, out restorePackAlignment);

        try
        {
            GL.BindTexture(TextureTarget.Texture2D, textureId);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.UnsignedByte, source);
        }
        finally
        {
            GL.PixelStore(PixelStoreParameter.PackAlignment, restorePackAlignment);
            GL.BindTexture(TextureTarget.Texture2D, restoreTexture2D);
            GL.ActiveTexture((TextureUnit)restoreActiveTexture);
        }

        byte[] flipped = new byte[source.Length];
        int stride = width * 4;
        for (int y = 0; y < height; y++)
        {
            System.Buffer.BlockCopy(source, y * stride, flipped, (height - 1 - y) * stride, stride);
        }

        return flipped;
    }

    private static void WritePng(string path, byte[] rgba, int width, int height)
    {
        using FileStream stream = File.Create(path);
        stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        Span<byte> ihdr = stackalloc byte[13];
        WriteBigEndian(ihdr[..4], width);
        WriteBigEndian(ihdr[4..8], height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(stream, "IHDR", ihdr);

        byte[] scanlines = new byte[checked((width * 4 + 1) * height)];
        int sourceStride = width * 4;
        int destinationStride = sourceStride + 1;
        for (int y = 0; y < height; y++)
        {
            int destination = y * destinationStride;
            scanlines[destination] = 0;
            System.Buffer.BlockCopy(rgba, y * sourceStride, scanlines, destination + 1, sourceStride);
        }

        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(scanlines, 0, scanlines.Length);
        }

        WriteChunk(stream, "IDAT", compressed.ToArray());
        WriteChunk(stream, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, data.Length);
        stream.Write(length);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = Crc32(typeBytes, data);
        Span<byte> checksum = stackalloc byte[4];
        WriteBigEndian(checksum, unchecked((int)crc));
        stream.Write(checksum);
    }

    private static uint Crc32(byte[] type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xffffffffu;
        foreach (byte value in type)
        {
            crc = UpdateCrc(crc, value);
        }

        foreach (byte value in data)
        {
            crc = UpdateCrc(crc, value);
        }

        return crc ^ 0xffffffffu;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++)
        {
            crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        }

        return crc;
    }

    private static void WriteBigEndian(Span<byte> destination, int value)
    {
        destination[0] = (byte)((value >> 24) & 0xff);
        destination[1] = (byte)((value >> 16) & 0xff);
        destination[2] = (byte)((value >> 8) & 0xff);
        destination[3] = (byte)(value & 0xff);
    }

    private static string SanitizeFileName(string value)
    {
        string sanitized = string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "viewport" : sanitized;
    }
}
