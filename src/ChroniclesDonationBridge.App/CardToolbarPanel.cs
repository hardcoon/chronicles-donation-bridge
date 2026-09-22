using System.Windows;
using System.Windows.Controls;

namespace ChroniclesDonationBridge.App;

/// <summary>Wrap card actions at narrow grid densities without clipping their hit targets.</summary>
public sealed class CardToolbarPanel : Panel
{
    private const double Gap = 6;
    private List<List<UIElement>> Rows(double width)
    {
        var rows = new List<List<UIElement>>();
        var row = new List<UIElement>();
        double used = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            if (row.Count > 0 && used + Gap + child.DesiredSize.Width > width)
            {
                rows.Add(row); row = []; used = 0;
            }
            used += (row.Count > 0 ? Gap : 0) + child.DesiredSize.Width;
            row.Add(child);
        }
        if (row.Count > 0) rows.Add(row);
        return rows;
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 600;
        foreach (UIElement child in InternalChildren) child.Measure(new Size(width, double.PositiveInfinity));
        var rows = Rows(width);
        return new Size(width, rows.Sum(row => row.Max(child => child.DesiredSize.Height)) + Math.Max(0, rows.Count - 1) * Gap);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (var row in Rows(finalSize.Width))
        {
            var height = row.Max(child => child.DesiredSize.Height);
            var spare = Math.Max(0, finalSize.Width - row.Sum(child => child.DesiredSize.Width) - Gap * (row.Count - 1));
            double x = 0;
            for (var index = 0; index < row.Count; index++)
            {
                var child = row[index];
                child.Arrange(new Rect(x, y, child.DesiredSize.Width, height));
                x += child.DesiredSize.Width + Gap + (index == 0 && row.Count > 1 ? spare : 0);
            }
            y += height + Gap;
        }
        return finalSize;
    }
}
