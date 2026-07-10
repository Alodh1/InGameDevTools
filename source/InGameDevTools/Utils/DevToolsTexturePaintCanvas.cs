using System.Buffers;
using System.IO.Compression;
using System.Text;

namespace InGameDevTools.Utils;

internal sealed class DevToolsTexturePaintCanvas
{
    private bool _dirty;
    private int _uploadMinX;
    private int _uploadMinY;
    private int _uploadMaxX = -1;
    private int _uploadMaxY = -1;

    public DevToolsTexturePaintCanvas(int width, int height, DevToolsTexturePaintColor clearColor)
    {
        Width = Math.Clamp(width, 1, 4096);
        Height = Math.Clamp(height, 1, 4096);
        Rgba = new byte[checked(Width * Height * 4)];
        Clear(clearColor);
        Dirty = false;
    }

    private DevToolsTexturePaintCanvas(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Rgba { get; }

    public bool Dirty
    {
        get => _dirty;
        set
        {
            _dirty = value;
            if (value) MarkWholeCanvasForUpload();
        }
    }

    public static bool TryLoadPng(byte[] data, out DevToolsTexturePaintCanvas? canvas, out string error)
    {
        canvas = null;
        error = "";
        try
        {
            canvas = DecodePng(data);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public byte[] EncodePng()
    {
        Span<byte> ihdr = stackalloc byte[13];
        WriteBigEndian(ihdr[..4], Width);
        WriteBigEndian(ihdr[4..8], Height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        int sourceStride = Width * 4;
        int scanlineLength = checked(sourceStride + 1);

        using MemoryStream compressed = new();
        byte[] scanline = ArrayPool<byte>.Shared.Rent(scanlineLength);
        try
        {
            scanline[0] = 0;
            using ZLibStream zlib = new(compressed, CompressionLevel.Fastest, leaveOpen: true);
            for (int y = 0; y < Height; y++)
            {
                Buffer.BlockCopy(Rgba, y * sourceStride, scanline, 1, sourceStride);
                zlib.Write(scanline, 0, scanlineLength);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scanline);
        }

        if (!compressed.TryGetBuffer(out ArraySegment<byte> compressedBuffer))
        {
            throw new InvalidOperationException("Unable to access the encoded PNG buffer.");
        }

        int pngLength = checked(8 + 12 + ihdr.Length + 12 + compressedBuffer.Count + 12);
        byte[] png = new byte[pngLength];
        using MemoryStream stream = new(png, 0, png.Length, writable: true, publiclyVisible: true);
        stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        WriteChunk(stream, "IHDR", ihdr);
        WriteChunk(stream, "IDAT", compressedBuffer.AsSpan());
        WriteChunk(stream, "IEND", ReadOnlySpan<byte>.Empty);
        return png;
    }

    public bool TryGetUploadRegion(out int x, out int y, out int width, out int height)
    {
        if (_uploadMaxX < _uploadMinX || _uploadMaxY < _uploadMinY)
        {
            x = 0;
            y = 0;
            width = 0;
            height = 0;
            return false;
        }

        x = _uploadMinX;
        y = _uploadMinY;
        width = _uploadMaxX - _uploadMinX + 1;
        height = _uploadMaxY - _uploadMinY + 1;
        return true;
    }

    public void ClearUploadRegion()
    {
        _uploadMinX = 0;
        _uploadMinY = 0;
        _uploadMaxX = -1;
        _uploadMaxY = -1;
    }

    public DevToolsTexturePaintColor GetPixel(int x, int y)
    {
        if (!Contains(x, y)) return default;
        int index = PixelIndex(Width, x, y);
        return new DevToolsTexturePaintColor(Rgba[index], Rgba[index + 1], Rgba[index + 2], Rgba[index + 3]);
    }

    public bool SetPixel(int x, int y, DevToolsTexturePaintColor color)
    {
        if (!Contains(x, y)) return false;
        int index = PixelIndex(Width, x, y);
        if (Rgba[index] == color.R && Rgba[index + 1] == color.G && Rgba[index + 2] == color.B && Rgba[index + 3] == color.A)
        {
            return false;
        }

        Rgba[index] = color.R;
        Rgba[index + 1] = color.G;
        Rgba[index + 2] = color.B;
        Rgba[index + 3] = color.A;
        _dirty = true;
        MarkPixelForUpload(x, y);
        return true;
    }

    public int PaintCircle(int centerX, int centerY, int radius, DevToolsTexturePaintColor color)
    {
        radius = Math.Max(1, radius);
        int radiusSquared = radius * radius;
        int changed = 0;
        int minX = Math.Max(0, centerX - radius + 1);
        int maxX = Math.Min(Width - 1, centerX + radius - 1);
        int minY = Math.Max(0, centerY - radius + 1);
        int maxY = Math.Min(Height - 1, centerY + radius - 1);
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float dx = x - centerX;
                float dy = y - centerY;
                if (dx * dx + dy * dy > radiusSquared) continue;
                if (SetPixel(x, y, color)) changed++;
            }
        }

        return changed;
    }

    public int FloodFill(int startX, int startY, DevToolsTexturePaintColor color)
    {
        if (!Contains(startX, startY)) return 0;
        DevToolsTexturePaintColor target = GetPixel(startX, startY);
        if (target.Equals(color)) return 0;

        int changed = 0;
        Queue<(int X, int Y)> queue = new();
        bool[] seen = new bool[Width * Height];
        queue.Enqueue((startX, startY));
        while (queue.Count > 0)
        {
            (int x, int y) = queue.Dequeue();
            if (!Contains(x, y)) continue;
            int seenIndex = y * Width + x;
            if (seen[seenIndex]) continue;
            seen[seenIndex] = true;
            if (!GetPixel(x, y).Equals(target)) continue;

            if (SetPixel(x, y, color)) changed++;
            queue.Enqueue((x - 1, y));
            queue.Enqueue((x + 1, y));
            queue.Enqueue((x, y - 1));
            queue.Enqueue((x, y + 1));
        }

        return changed;
    }

    public void Clear(DevToolsTexturePaintColor color)
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int index = PixelIndex(Width, x, y);
                Rgba[index] = color.R;
                Rgba[index + 1] = color.G;
                Rgba[index + 2] = color.B;
                Rgba[index + 3] = color.A;
            }
        }

        _dirty = true;
        MarkWholeCanvasForUpload();
    }

    private void MarkPixelForUpload(int x, int y)
    {
        if (_uploadMaxX < _uploadMinX || _uploadMaxY < _uploadMinY)
        {
            _uploadMinX = x;
            _uploadMaxX = x;
            _uploadMinY = y;
            _uploadMaxY = y;
            return;
        }

        _uploadMinX = Math.Min(_uploadMinX, x);
        _uploadMinY = Math.Min(_uploadMinY, y);
        _uploadMaxX = Math.Max(_uploadMaxX, x);
        _uploadMaxY = Math.Max(_uploadMaxY, y);
    }

    private void MarkWholeCanvasForUpload()
    {
        _uploadMinX = 0;
        _uploadMinY = 0;
        _uploadMaxX = Width - 1;
        _uploadMaxY = Height - 1;
    }

    private bool Contains(int x, int y)
    {
        return x >= 0 && x < Width && y >= 0 && y < Height;
    }

    private static int PixelIndex(int width, int x, int y)
    {
        return checked((y * width + x) * 4);
    }

    private static DevToolsTexturePaintCanvas DecodePng(byte[] data)
    {
        if (data.Length < 8 ||
            data[0] != 137 ||
            data[1] != 80 ||
            data[2] != 78 ||
            data[3] != 71 ||
            data[4] != 13 ||
            data[5] != 10 ||
            data[6] != 26 ||
            data[7] != 10)
        {
            throw new InvalidDataException("Not a PNG file.");
        }

        int width = 0;
        int height = 0;
        int bitDepth = 0;
        int colorType = 0;
        int interlace = 0;
        byte[] palette = [];
        byte[] transparency = [];
        using MemoryStream idat = new();

        int offset = 8;
        while (offset + 12 <= data.Length)
        {
            int length = ReadBigEndianInt(data.AsSpan(offset, 4));
            offset += 4;
            if (length < 0 || offset + 4 + length + 4 > data.Length)
            {
                throw new InvalidDataException("Invalid PNG chunk length.");
            }

            string type = Encoding.ASCII.GetString(data, offset, 4);
            offset += 4;
            ReadOnlySpan<byte> chunk = data.AsSpan(offset, length);
            offset += length + 4; // Skip CRC.

            switch (type)
            {
                case "IHDR":
                    if (length != 13) throw new InvalidDataException("Invalid PNG IHDR chunk.");
                    width = ReadBigEndianInt(chunk[..4]);
                    height = ReadBigEndianInt(chunk[4..8]);
                    bitDepth = chunk[8];
                    colorType = chunk[9];
                    interlace = chunk[12];
                    break;
                case "PLTE":
                    palette = chunk.ToArray();
                    break;
                case "tRNS":
                    transparency = chunk.ToArray();
                    break;
                case "IDAT":
                    idat.Write(chunk);
                    break;
                case "IEND":
                    offset = data.Length;
                    break;
            }
        }

        if (width <= 0 || height <= 0) throw new InvalidDataException("PNG has no valid dimensions.");
        if (width > 4096 || height > 4096) throw new InvalidDataException($"PNG is too large ({width}x{height}).");
        if (bitDepth != 8) throw new InvalidDataException("Only 8-bit PNG textures are supported.");
        if (interlace != 0) throw new InvalidDataException("Interlaced PNG textures are not supported.");

        int channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException($"Unsupported PNG color type {colorType}.")
        };

        byte[] filtered = InflatePngData(idat.ToArray());
        int sourceStride = checked(width * channels);
        int expectedLength = checked((sourceStride + 1) * height);
        if (filtered.Length < expectedLength)
        {
            throw new InvalidDataException("PNG image data is truncated.");
        }

        byte[] rgba = new byte[checked(width * height * 4)];
        byte[] previous = new byte[sourceStride];
        byte[] current = new byte[sourceStride];
        int source = 0;
        for (int y = 0; y < height; y++)
        {
            int filter = filtered[source++];
            filtered.AsSpan(source, sourceStride).CopyTo(current);
            source += sourceStride;
            UnfilterScanline(current, previous, channels, filter);
            ConvertScanlineToRgba(current, rgba, width, y, colorType, palette, transparency);
            (previous, current) = (current, previous);
        }

        return new DevToolsTexturePaintCanvas(width, height, rgba);
    }

    private static byte[] InflatePngData(byte[] compressed)
    {
        using MemoryStream input = new(compressed);
        using ZLibStream zlib = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static void UnfilterScanline(byte[] line, byte[] previous, int bytesPerPixel, int filter)
    {
        for (int index = 0; index < line.Length; index++)
        {
            int left = index >= bytesPerPixel ? line[index - bytesPerPixel] : 0;
            int up = previous[index];
            int upLeft = index >= bytesPerPixel ? previous[index - bytesPerPixel] : 0;
            int predictor = filter switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) >> 1,
                4 => Paeth(left, up, upLeft),
                _ => throw new InvalidDataException($"Unsupported PNG filter {filter}.")
            };
            line[index] = unchecked((byte)(line[index] + predictor));
        }
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        int estimate = left + up - upLeft;
        int leftDistance = Math.Abs(estimate - left);
        int upDistance = Math.Abs(estimate - up);
        int upLeftDistance = Math.Abs(estimate - upLeft);
        if (leftDistance <= upDistance && leftDistance <= upLeftDistance) return left;
        return upDistance <= upLeftDistance ? up : upLeft;
    }

    private static void ConvertScanlineToRgba(byte[] line, byte[] rgba, int width, int y, int colorType, byte[] palette, byte[] transparency)
    {
        for (int x = 0; x < width; x++)
        {
            int target = PixelIndex(width, x, y);
            switch (colorType)
            {
                case 0:
                {
                    byte gray = line[x];
                    rgba[target] = gray;
                    rgba[target + 1] = gray;
                    rgba[target + 2] = gray;
                    rgba[target + 3] = IsTransparentGray(gray, transparency) ? (byte)0 : (byte)255;
                    break;
                }
                case 2:
                {
                    int source = x * 3;
                    byte r = line[source];
                    byte g = line[source + 1];
                    byte b = line[source + 2];
                    rgba[target] = r;
                    rgba[target + 1] = g;
                    rgba[target + 2] = b;
                    rgba[target + 3] = IsTransparentRgb(r, g, b, transparency) ? (byte)0 : (byte)255;
                    break;
                }
                case 3:
                {
                    int index = line[x];
                    int paletteIndex = index * 3;
                    if (paletteIndex + 2 >= palette.Length) throw new InvalidDataException("PNG palette index is out of range.");
                    rgba[target] = palette[paletteIndex];
                    rgba[target + 1] = palette[paletteIndex + 1];
                    rgba[target + 2] = palette[paletteIndex + 2];
                    rgba[target + 3] = index < transparency.Length ? transparency[index] : (byte)255;
                    break;
                }
                case 4:
                {
                    int source = x * 2;
                    byte gray = line[source];
                    rgba[target] = gray;
                    rgba[target + 1] = gray;
                    rgba[target + 2] = gray;
                    rgba[target + 3] = line[source + 1];
                    break;
                }
                case 6:
                {
                    int source = x * 4;
                    rgba[target] = line[source];
                    rgba[target + 1] = line[source + 1];
                    rgba[target + 2] = line[source + 2];
                    rgba[target + 3] = line[source + 3];
                    break;
                }
            }
        }
    }

    private static bool IsTransparentGray(byte gray, byte[] transparency)
    {
        return transparency.Length >= 2 && gray == transparency[1];
    }

    private static bool IsTransparentRgb(byte r, byte g, byte b, byte[] transparency)
    {
        return transparency.Length >= 6 && r == transparency[1] && g == transparency[3] && b == transparency[5];
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

    private static int ReadBigEndianInt(ReadOnlySpan<byte> source)
    {
        return (source[0] << 24) | (source[1] << 16) | (source[2] << 8) | source[3];
    }

    private static void WriteBigEndian(Span<byte> destination, int value)
    {
        destination[0] = (byte)((value >> 24) & 0xff);
        destination[1] = (byte)((value >> 16) & 0xff);
        destination[2] = (byte)((value >> 8) & 0xff);
        destination[3] = (byte)(value & 0xff);
    }
}

internal readonly record struct DevToolsTexturePaintColor(byte R, byte G, byte B, byte A);
