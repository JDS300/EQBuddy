namespace EQBuddy.UI.Shared;

/// <summary>
/// Frameless-window resize hit-testing: maps a point (window-local, DIPs) to the Win32
/// HT* code for the edge or corner under it, 0 for the interior. Pure so it's testable —
/// the WM_NCHITTEST hook in the WPF breakout windows is just plumbing around this.
/// Edges are deliberately thin (6px) and corners slightly generous (10px) so the title
/// row still drags and ✕/toggle controls keep their hit areas.
/// </summary>
public static class ResizeZones
{
    public const int None = 0;
    public const int Left = 10, Right = 11, Top = 12, TopLeft = 13,
        TopRight = 14, Bottom = 15, BottomLeft = 16, BottomRight = 17;

    public static int Hit(double x, double y, double width, double height,
        double edge = 6, double corner = 10)
    {
        var l = x < edge; var r = x >= width - edge;
        var t = y < edge; var b = y >= height - edge;
        var cl = x < corner; var cr = x >= width - corner;
        var ct = y < corner; var cb = y >= height - corner;
        return
            ct && cl ? TopLeft : ct && cr ? TopRight :
            cb && cl ? BottomLeft : cb && cr ? BottomRight :
            l ? Left : r ? Right : t ? Top : b ? Bottom : None;
    }
}
