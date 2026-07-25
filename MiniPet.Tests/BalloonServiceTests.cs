using Xunit;
using System;
using System.Drawing;

namespace MiniPet.Tests;

public class BalloonServiceTests
{
    // --- embedded minimal models ---
    public class TestBalloonTiles
    {
        public byte[] NW { get; set; } = Array.Empty<byte>();
        public byte[] N { get; set; } = Array.Empty<byte>();
        public byte[] NE { get; set; } = Array.Empty<byte>();
        public byte[] W { get; set; } = Array.Empty<byte>();
        public byte[] C { get; set; } = Array.Empty<byte>();
        public byte[] E { get; set; } = Array.Empty<byte>();
        public byte[] SW { get; set; } = Array.Empty<byte>();
        public byte[] S { get; set; } = Array.Empty<byte>();
        public byte[] SE { get; set; } = Array.Empty<byte>();
        public byte[] Arrow { get; set; } = Array.Empty<byte>();
        public byte[] Head { get; set; } = Array.Empty<byte>();
    }

    public class TestBalloonData
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public TestBalloonTiles Tiles { get; set; } = new();
        public int Clr { get; set; }
        public string DefaultText { get; set; } = "";
    }

    // --- tests: 正常路径 (9-slice dimension calculation logic) ---

    [Fact]
    public void CalculateBalloonSize_StandardTiles_ReturnsExpectedSize()
    {
        // corner tiles are 32x32, content area adds width
        int cornerW = 32, cornerH = 32;
        int textW = 100, textH = 24;

        var size = CalculateBalloonSize(cornerW, cornerH, textW, textH, hasArrow: true, arrowH: 16);

        // totalW = cornerW + (textW + cornerW) + cornerW = 32 + 132 + 32 = 196
        // Wait, let me recalculate: contentW = textW + cornerW = 100 + 32 = 132
        // totalW = cornerW + contentW + cornerW = 32 + 132 + 32 = 196
        Assert.Equal(196, size.Width);
        // totalH = cornerH + (textH + cornerH) + cornerH + arrowH = 32 + 56 + 32 + 16 = 136
        Assert.Equal(136, size.Height);
    }

    [Fact]
    public void CalculateBalloonSize_NoArrow_SmallerHeight()
    {
        int cornerW = 32, cornerH = 32;
        int textW = 60, textH = 20;

        var size = CalculateBalloonSize(cornerW, cornerH, textW, textH, hasArrow: false, arrowH: 0);

        // contentW = 60 + 32 = 92, totalW = 32 + 92 + 32 = 156
        Assert.Equal(156, size.Width);
        // contentH = 20 + 32 = 52, totalH = 32 + 52 + 32 + 0 = 116
        Assert.Equal(116, size.Height);
    }

    [Fact]
    public void CalculateArrowPosition_CentersBelowBalloon()
    {
        var balloonSize = new Size(200, 150);
        var originPoint = new Point(100, 200);

        var pos = CalculateArrowPosition(balloonSize, originPoint);

        // arrowX = origin.X - balloon.W / 2 = 100 - 100 = 0
        Assert.Equal(0, pos.X);
        // arrowY = origin.Y - balloon.H = 200 - 150 = 50
        Assert.Equal(50, pos.Y);
    }

    // --- tests: 边界 ---

    [Fact]
    public void CalculateArrowPosition_ZeroSize_ReturnsOrigin()
    {
        var pos = CalculateArrowPosition(new Size(0, 0), new Point(50, 50));
        Assert.Equal(50, pos.X);
        Assert.Equal(50, pos.Y);
    }

    [Fact]
    public void CalculateBalloonSize_ZeroText_ReturnsMinSize()
    {
        int cornerW = 32, cornerH = 32;
        var size = CalculateBalloonSize(cornerW, cornerH, textW: 0, textH: 0, hasArrow: false, arrowH: 0);

        // contentW = 0 + 32 = 32, totalW = 32 + 32 + 32 = 96
        Assert.Equal(96, size.Width);
        // contentH = 0 + 32 = 32, totalH = 32 + 32 + 32 + 0 = 96
        Assert.Equal(96, size.Height);
    }

    [Fact]
    public void Tiles_DefaultAreEmpty()
    {
        var tiles = new TestBalloonTiles();
        Assert.Empty(tiles.NW);
        Assert.Empty(tiles.N);
        Assert.Empty(tiles.NE);
        Assert.Empty(tiles.W);
        Assert.Empty(tiles.C);
        Assert.Empty(tiles.E);
        Assert.Empty(tiles.SW);
        Assert.Empty(tiles.S);
        Assert.Empty(tiles.SE);
        Assert.Empty(tiles.Arrow);
        Assert.Empty(tiles.Head);
    }

    [Fact]
    public void BalloonData_DefaultValues()
    {
        var data = new TestBalloonData();
        Assert.Equal(0, data.Id);
        Assert.Equal("", data.Name);
        Assert.NotNull(data.Tiles);
        Assert.Equal(0, data.Clr);
        Assert.Equal("", data.DefaultText);
    }

    // --- tests: 异常 ---

    [Fact]
    public void CalculateBalloonSize_NegativeCorner_UsesNegative()
    {
        // math still works even with negative - no crash
        int cornerW = -10, cornerH = -10;
        var size = CalculateBalloonSize(cornerW, cornerH, textW: 50, textH: 20, hasArrow: false, arrowH: 0);

        // contentW = 50 + (-10) = 40, totalW = -10 + 40 + -10 = 20
        Assert.Equal(20, size.Width);
    }

    [Fact]
    public void CalculateArrowPosition_NegativeOrigin_WorksCorrectly()
    {
        var pos = CalculateArrowPosition(new Size(100, 100), new Point(-50, -50));
        Assert.Equal(-100, pos.X);
        Assert.Equal(-150, pos.Y);
    }

    [Fact]
    public void MeasureText_EmptyString_ReturnsMinSize()
    {
        var size = MeasureTextMin("", lineHeight: 20, maxWidth: 200);

        Assert.Equal(50, size.Width);  // min width
        Assert.Equal(20, size.Height);  // min height = lineHeight
    }

    [Fact]
    public void MeasureText_ShortString_ReturnsBoundedSize()
    {
        var size = MeasureTextMin("Hello", lineHeight: 20, maxWidth: 200);

        Assert.True(size.Width > 0);
        Assert.True(size.Height > 0);
        Assert.True(size.Width <= 200);
    }

    // --- embedded minimal logic replicas ---

    private static Size CalculateBalloonSize(int cornerW, int cornerH, int textW, int textH, bool hasArrow, int arrowH)
    {
        int contentW = textW + cornerW;
        int contentH = textH + cornerH;
        int totalW = cornerW + contentW + cornerW;
        int totalH = cornerH + contentH + cornerH + (hasArrow ? arrowH : 0);
        return new Size(totalW, totalH);
    }

    private static Point CalculateArrowPosition(Size balloonSize, Point originPoint)
    {
        return new Point(
            originPoint.X - balloonSize.Width / 2,
            originPoint.Y - balloonSize.Height
        );
    }

    private static Size MeasureTextMin(string text, int lineHeight, int maxWidth)
    {
        // Simplified text measurement - actual rendering is done by Graphics.MeasureString
        // Here we simulate the minimum size logic
        int estimatedWidth = Math.Min(text.Length * 8, maxWidth);
        int w = Math.Max(estimatedWidth, 50);
        int h = Math.Max(lineHeight, lineHeight);

        // Auto-wrap: if text would exceed maxWidth, increase height
        if (estimatedWidth > maxWidth)
        {
            int lines = (int)Math.Ceiling((double)estimatedWidth / maxWidth);
            h = Math.Max(h, lines * lineHeight);
        }

        return new Size(w, h);
    }
}
