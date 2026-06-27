using InGameDevTools.Utils;

namespace InGameDevTools.Tests;

public sealed class DevToolsTexturePaintCanvasTests
{
    [Fact]
    public void PaintCircle_ChangesExpectedPixelsAndMarksDirty()
    {
        DevToolsTexturePaintCanvas canvas = new(5, 5, new DevToolsTexturePaintColor(0, 0, 0, 0));
        canvas.Dirty = false;

        int changed = canvas.PaintCircle(2, 2, 1, new DevToolsTexturePaintColor(12, 34, 56, 255));

        Assert.Equal(1, changed);
        Assert.True(canvas.Dirty);
        Assert.Equal(new DevToolsTexturePaintColor(12, 34, 56, 255), canvas.GetPixel(2, 2));
        Assert.Equal(new DevToolsTexturePaintColor(0, 0, 0, 0), canvas.GetPixel(1, 2));
    }

    [Fact]
    public void FloodFill_StopsAtDifferentColorBoundary()
    {
        DevToolsTexturePaintCanvas canvas = new(4, 2, new DevToolsTexturePaintColor(1, 1, 1, 255));
        DevToolsTexturePaintColor boundary = new(9, 9, 9, 255);
        canvas.SetPixel(2, 0, boundary);
        canvas.SetPixel(2, 1, boundary);
        canvas.Dirty = false;

        int changed = canvas.FloodFill(0, 0, new DevToolsTexturePaintColor(4, 5, 6, 255));

        Assert.Equal(4, changed);
        Assert.True(canvas.Dirty);
        Assert.Equal(new DevToolsTexturePaintColor(4, 5, 6, 255), canvas.GetPixel(1, 1));
        Assert.Equal(boundary, canvas.GetPixel(2, 0));
        Assert.Equal(new DevToolsTexturePaintColor(1, 1, 1, 255), canvas.GetPixel(3, 1));
    }

    [Fact]
    public void EncodePng_RoundTripsPixelColors()
    {
        DevToolsTexturePaintCanvas canvas = new(2, 2, new DevToolsTexturePaintColor(0, 0, 0, 0));
        canvas.SetPixel(0, 0, new DevToolsTexturePaintColor(255, 0, 0, 255));
        canvas.SetPixel(1, 0, new DevToolsTexturePaintColor(0, 255, 0, 192));
        canvas.SetPixel(0, 1, new DevToolsTexturePaintColor(0, 0, 255, 128));
        canvas.SetPixel(1, 1, new DevToolsTexturePaintColor(7, 8, 9, 10));

        byte[] png = canvas.EncodePng();

        Assert.True(DevToolsTexturePaintCanvas.TryLoadPng(png, out DevToolsTexturePaintCanvas? loaded, out string error), error);
        Assert.NotNull(loaded);
        Assert.Equal(new DevToolsTexturePaintColor(255, 0, 0, 255), loaded.GetPixel(0, 0));
        Assert.Equal(new DevToolsTexturePaintColor(0, 255, 0, 192), loaded.GetPixel(1, 0));
        Assert.Equal(new DevToolsTexturePaintColor(0, 0, 255, 128), loaded.GetPixel(0, 1));
        Assert.Equal(new DevToolsTexturePaintColor(7, 8, 9, 10), loaded.GetPixel(1, 1));
    }
}
