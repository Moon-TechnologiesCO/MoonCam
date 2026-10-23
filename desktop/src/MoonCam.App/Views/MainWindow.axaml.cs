using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MoonCam.App.ViewModels;

namespace MoonCam.App.Views;

public sealed partial class MainWindow : Window
{
    private const double ResizeBorder = 6;

    public MainWindow()
    {
        InitializeComponent();
        Preview = new PreviewRenderer(PreviewImage);

        CloseButton.Click += (_, _) => Close();
        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        ZoomButton.Click += (_, _) => ToggleZoom();
        TitleBar.PointerPressed += OnTitleBarPressed;
        PointerPressed += OnWindowEdgePressed;
        Opened += (_, _) => FitToScreen();
    }

    /// <summary>Receives decoded frames and paints them; registered as a sink by the composition root.</summary>
    public PreviewRenderer Preview { get; }

    private void ToggleZoom() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount == 2)
            ToggleZoom();
        else
            BeginMoveDrag(e);
    }

    /// <summary>Borderless windows need their own resize handles: a thin band along every edge.</summary>
    private void OnWindowEdgePressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var p = e.GetPosition(this);
        bool left = p.X < ResizeBorder, right = p.X > Bounds.Width - ResizeBorder;
        bool top = p.Y < ResizeBorder, bottom = p.Y > Bounds.Height - ResizeBorder;
        WindowEdge? edge = (top, bottom, left, right) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (true, _, _, true) => WindowEdge.NorthEast,
            (_, true, true, _) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.North,
            (_, true, _, _) => WindowEdge.South,
            (_, _, true, _) => WindowEdge.West,
            (_, _, _, true) => WindowEdge.East,
            _ => null,
        };
        if (edge is { } resizeEdge)
            BeginResizeDrag(resizeEdge, e);
    }

    /// <summary>Never open larger than the screen (laptops with display scaling have little room).</summary>
    private void FitToScreen()
    {
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen)
            return;
        var area = screen.WorkingArea;
        double maxWidth = area.Width / screen.Scaling * 0.94, maxHeight = area.Height / screen.Scaling * 0.92;
        MinWidth = Math.Min(MinWidth, maxWidth);
        MinHeight = Math.Min(MinHeight, maxHeight);
        Width = Math.Min(Width, maxWidth);
        Height = Math.Min(Height, maxHeight);
        Position = new PixelPoint(
            area.X + (int)((area.Width - Width * screen.Scaling) / 2),
            area.Y + (int)((area.Height - Height * screen.Scaling) / 2));
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
