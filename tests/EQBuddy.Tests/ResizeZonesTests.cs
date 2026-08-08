using EQBuddy.UI.Shared;

namespace EQBuddy.Tests;

/// <summary>The frameless-resize zone map (breakout windows): every side and corner must
/// answer, the interior and the title-drag area must not.</summary>
public class ResizeZonesTests
{
    private const double W = 272, H = 343;

    [Theory]
    [InlineData(268, 339, ResizeZones.BottomRight)]   // the corner that grew in the live test
    [InlineData(3, 3, ResizeZones.TopLeft)]
    [InlineData(268, 3, ResizeZones.TopRight)]
    [InlineData(3, 339, ResizeZones.BottomLeft)]
    [InlineData(2, 170, ResizeZones.Left)]
    [InlineData(269, 170, ResizeZones.Right)]
    [InlineData(136, 2, ResizeZones.Top)]
    [InlineData(136, 340, ResizeZones.Bottom)]
    public void EverySideAndCornerAnswers(double x, double y, int expected)
    {
        Assert.Equal(expected, ResizeZones.Hit(x, y, W, H));
    }

    [Theory]
    [InlineData(136, 170)]   // dead center
    [InlineData(136, 12)]    // title row: must DRAG, not resize
    [InlineData(30, 12)]     // title text area
    public void InteriorAndTitleRowStayDraggable(double x, double y)
    {
        Assert.Equal(ResizeZones.None, ResizeZones.Hit(x, y, W, H));
    }

    [Fact]
    public void CornersOutrankEdgesInsideTheCornerBand()
    {
        // 8px from both edges: inside the 10px corner band though outside the 6px edge
        // band on neither axis — the diagonal cursor must win.
        Assert.Equal(ResizeZones.BottomRight, ResizeZones.Hit(W - 8, H - 8, W, H));
    }
}
