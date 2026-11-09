using System.Windows;
using System.Windows.Media;
using Logy.Core.Graph;

namespace Logy;

/// <summary>Colours and the main branch the graph is drawn with; replaced as a whole when either changes.</summary>
public sealed class GraphStyle
{
    public GraphStyle(IReadOnlyList<string> colors, string mainBranch)
    {
        Brushes = colors.Select(App.Brush).ToArray();
        Pens = Brushes.Select(b =>
        {
            var p = new Pen(b, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            p.Freeze();
            return p;
        }).ToArray();
        Hex = colors.ToArray();
        MainBranch = mainBranch;
    }

    public SolidColorBrush[] Brushes { get; }

    public Pen[] Pens { get; }

    public string[] Hex { get; }

    public string MainBranch { get; }

    public int Index(string colorKey) => LaneLayout.ColorIndex(colorKey, MainBranch, Brushes.Length);
}

/// <summary>Draws one row's lanes (design doc 6.4): top half, bottom half, then the commit dot.</summary>
public sealed class GraphCell : FrameworkElement
{
    public const double LaneWidth = 13;
    public const double LaneOffset = 8;
    public const double RowHeight = 22;
    private const double Centre = 11;

    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(GraphRow), typeof(GraphCell),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GraphStyleProperty = DependencyProperty.Register(
        nameof(GraphStyle), typeof(GraphStyle), typeof(GraphCell),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>True when the width cap clips lanes; draws the overflow marker at the right edge.</summary>
    public static readonly DependencyProperty ClippedProperty = DependencyProperty.Register(
        nameof(Clipped), typeof(bool), typeof(GraphCell),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush RingBrush = App.Brush("#202020");
    private static readonly Pen OverflowPen = CreateOverflowPen();

    public GraphCell()
    {
        Height = RowHeight;
        ClipToBounds = true;
        SnapsToDevicePixels = false;
    }

    public GraphRow? Row
    {
        get => (GraphRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public GraphStyle? GraphStyle
    {
        get => (GraphStyle?)GetValue(GraphStyleProperty);
        set => SetValue(GraphStyleProperty, value);
    }

    public bool Clipped
    {
        get => (bool)GetValue(ClippedProperty);
        set => SetValue(ClippedProperty, value);
    }

    public static double X(int slot) => LaneOffset + slot * LaneWidth;

    protected override void OnRender(DrawingContext dc)
    {
        var row = Row;
        var style = GraphStyle;
        if (row is null || style is null || style.Pens.Length == 0)
        {
            return;
        }

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        foreach (var s in row.Top)
        {
            DrawSegment(dc, style, s, 0, Centre);
        }

        foreach (var s in row.Bottom)
        {
            DrawSegment(dc, style, s, Centre, RowHeight);
        }

        var dot = new Point(X(row.CommitSlot), Centre);
        dc.DrawEllipse(style.Brushes[style.Index(row.CommitColorKey)], null, dot, 4.5, 4.5);
        if (row.Commit.IsHead)
        {
            dc.DrawEllipse(RingBrush, null, dot, 1.5, 1.5);
        }

        dc.Pop();

        if (Clipped)
        {
            var x = ActualWidth - 0.5;
            dc.DrawLine(OverflowPen, new Point(x, 0), new Point(x, RowHeight));
        }
    }

    private static void DrawSegment(DrawingContext dc, GraphStyle style, Segment s, double y0, double y1)
    {
        var pen = style.Pens[style.Index(s.ColorKey)];
        var a = new Point(X(s.From), y0);
        var b = new Point(X(s.To), y1);
        if (s.From == s.To)
        {
            dc.DrawLine(pen, a, b);
            return;
        }

        var mid = (y0 + y1) / 2;
        var fig = new PathFigure { StartPoint = a, IsFilled = false };
        fig.Segments.Add(new BezierSegment(new Point(a.X, mid), new Point(b.X, mid), b, true));
        var geo = new PathGeometry([fig]);
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private static Pen CreateOverflowPen()
    {
        var p = new Pen(App.Brush("#8A8A8A"), 1);
        p.Freeze();
        return p;
    }
}
