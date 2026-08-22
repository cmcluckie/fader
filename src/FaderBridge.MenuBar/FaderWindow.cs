using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fader.Bridge.Feedback;

namespace Fader.MenuBar;

/// <summary>
/// The Fader window: Show and Setup as two screens over one engine, a single toggle
/// apart.
///
/// They are deliberately different products sharing data. Show is glanceable and
/// thumb-sized for the set; Setup is dense and calm for soundcheck. Both are driven
/// by one 30 ms timer - meters, LEDs and switch travel all advance together, so N
/// animated controls cost one timer rather than N.
/// </summary>
public sealed class FaderWindow : Window
{
    private readonly FeedbackController _feedback;
    private readonly ShowView _show;
    private readonly SetupView _setup;
    private readonly DispatcherTimer _frame;

    private readonly Border _showTab;
    private readonly Border _setupTab;
    private readonly TextBlock _showLabel;
    private readonly TextBlock _setupLabel;

    private bool _showing = true;

    public FaderWindow(FeedbackController feedback, System.Net.IPAddress console)
    {
        _feedback = feedback;

        Title = "Fader";
        Width = 980;
        Height = 660;
        MinWidth = 720;
        MinHeight = 520;
        Background = Tokens.Ground;

        _show = new ShowView(feedback);
        _setup = new SetupView(feedback);
        _setup.SetConsoleAddress(console);

        (_showTab, _showLabel) = Tab("Show");
        (_setupTab, _setupLabel) = Tab("Setup");
        _showTab.PointerPressed += (_, _) => SetMode(true);
        _setupTab.PointerPressed += (_, _) => SetMode(false);

        var toggle = new Border
        {
            Background = Tokens.Ground,
            BorderBrush = Tokens.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Pill,
            Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.Stack(Orientation.Horizontal, 0, _showTab, _setupTab),
        };

        var brand = Ui.Text("FADER", 15, Tokens.Ink, FontWeight.Black);
        brand.FontFamily = Tokens.Display;
        brand.LetterSpacing = 3.4;
        brand.VerticalAlignment = VerticalAlignment.Center;

        var bar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Height = 56,
            Margin = new Thickness(20, 0),
        };
        Grid.SetColumn(brand, 0);
        Grid.SetColumn(toggle, 2);
        bar.Children.Add(brand);
        bar.Children.Add(toggle);

        var barWrap = new Border
        {
            BorderBrush = Tokens.LineSoft,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = bar,
        };

        var body = new Panel();
        body.Children.Add(_show);
        body.Children.Add(_setup);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(barWrap, Dock.Top);
        root.Children.Add(barWrap);
        root.Children.Add(body);
        Content = root;

        _feedback.StatusChanged += OnStatus;

        _frame = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };   // ~33 fps
        _frame.Tick += (_, _) => Frame();

        SetMode(true);
        Opened += (_, _) => _frame.Start();
        Closed += (_, _) => Teardown();
    }

    private void OnStatus(FkStatus s) => Dispatcher.UIThread.Post(() => _show.SetCpu(s.CpuLoad));

    private void Frame()
    {
        if (_showing) _show.Tick();
        else _setup.Tick();
    }

    private (Border, TextBlock) Tab(string text)
    {
        var label = Ui.Text(text.ToUpperInvariant(), 11.5, Tokens.InkFaint, FontWeight.Bold);
        label.FontFamily = Tokens.Display;
        label.LetterSpacing = 1.6;
        var border = new Border
        {
            CornerRadius = Tokens.Pill,
            Padding = new Thickness(15, 6),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = label,
        };
        return (border, label);
    }

    /// <summary>Switch screens. Only one view ticks, so the hidden one costs nothing.</summary>
    public void SetMode(bool show)
    {
        _showing = show;
        _show.IsVisible = show;
        _setup.IsVisible = !show;

        _showTab.Background = show ? Tokens.Accent : Brushes.Transparent;
        _showLabel.Foreground = show ? Tokens.Ground : Tokens.InkFaint;
        _setupTab.Background = show ? Brushes.Transparent : Tokens.InkDim;
        _setupLabel.Foreground = show ? Tokens.InkFaint : Tokens.Ground;
    }

    private void Teardown()
    {
        _frame.Stop();
        _feedback.StatusChanged -= OnStatus;
        _show.Teardown();
        _setup.Teardown();
    }
}
