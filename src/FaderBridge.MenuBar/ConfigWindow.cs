using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fader.Bridge.Feedback;

namespace Fader.MenuBar;

/// <summary>
/// The feedback setup window: pick the audio device, then check the input
/// channels to run feedback control on. Each checked channel is monitored (shows
/// in the spectrum, logs) and its feedback is cut. Meters show level on the
/// checked channels so you can confirm the right ones are live.
/// </summary>
public sealed class ConfigWindow : Window
{
    private static readonly IBrush Bg = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1A));
    private static readonly IBrush StripOn = new SolidColorBrush(Color.FromRgb(0x12, 0x25, 0x1C));
    private static readonly IBrush StripOff = new SolidColorBrush(Color.FromRgb(0x16, 0x19, 0x22));
    private static readonly IBrush Track = new SolidColorBrush(Color.FromRgb(0x0C, 0x0E, 0x13));
    private static readonly IBrush Meter = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
    private static readonly IBrush TextOn = new SolidColorBrush(Color.FromRgb(0xD1, 0xFA, 0xE5));
    private static readonly IBrush TextOff = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));

    private const double MeterHeight = 120;

    private readonly FeedbackController _feedback;
    private readonly ComboBox _deviceBox = new() { MinWidth = 260 };
    private readonly StackPanel _strips = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock _hint = new() { Foreground = TextOff, FontSize = 12.5, Margin = new Thickness(0, 6, 0, 8) };
    private readonly DispatcherTimer _meterTimer;

    private readonly Dictionary<int, Rectangle> _meters = new();   // physical channel -> meter fill
    private readonly Dictionary<int, float> _levels = new();       // physical channel -> 0..1
    private int[] _builtChannels = Array.Empty<int>();
    private bool _syncing;

    public ConfigWindow(FeedbackController feedback)
    {
        _feedback = feedback;

        Title = "Feedback inputs";
        Width = 680;
        Height = 340;
        Background = Bg;

        _deviceBox.SelectionChanged += OnDeviceSelected;

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(new TextBlock { Text = "Device", Foreground = TextOff, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(_deviceBox);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(header);
        root.Children.Add(_hint);
        root.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _strips,
        });
        Content = root;

        _feedback.DevicesChanged += OnDevices;
        _feedback.ChannelsChanged += OnChannels;
        _feedback.SpectrumChanged += OnSpectrum;

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _meterTimer.Tick += (_, _) => ApplyMeters();

        Opened += (_, _) => { RefreshDevices(); RebuildStrips(); _meterTimer.Start(); };
        Closed += (_, _) => Teardown();
    }

    private void OnDevices() => Dispatcher.UIThread.Post(RefreshDevices);
    private void OnChannels() => Dispatcher.UIThread.Post(RebuildStrips);

    private void OnSpectrum(FkSpectrum s)
    {
        var physical = _feedback.PhysicalForSlot(s.Channel);
        if (physical < 0) return;
        var peak = float.NegativeInfinity;
        foreach (var m in s.Magnitudes) if (m > peak) peak = m;
        _levels[physical] = Math.Clamp((peak + 80f) / 80f, 0f, 1f);   // -80..0 dB -> 0..1
    }

    private void RefreshDevices()
    {
        _syncing = true;
        _deviceBox.ItemsSource = _feedback.Devices;
        _deviceBox.SelectedItem = _feedback.CurrentDevice;
        _syncing = false;
    }

    private void OnDeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (_deviceBox.SelectedItem is string device) _feedback.SetDevice(device);
    }

    private void RebuildStrips()
    {
        var channels = _feedback.InputChannels
            .Where(c => !c.Name.StartsWith("NONE", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var indices = channels.Select(c => c.Index).ToArray();

        // Only rebuild when the channel set changes; otherwise refresh checkbox state.
        if (indices.SequenceEqual(_builtChannels))
        {
            RefreshChecks();
            return;
        }
        _builtChannels = indices;

        _strips.Children.Clear();
        _meters.Clear();

        if (channels.Length == 0)
        {
            _hint.Text = "Pick an audio device to see its input channels.";
            return;
        }
        _hint.Text = "Check the inputs to run feedback control on. Meters show level on the checked ones.";

        foreach (var (index, name) in channels)
        {
            _strips.Children.Add(BuildStrip(index, name));
        }
    }

    private Control BuildStrip(int index, string name)
    {
        var enabled = _feedback.IsInputEnabled(index);

        var check = new CheckBox { IsChecked = enabled, HorizontalAlignment = HorizontalAlignment.Center };
        check.IsCheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            _feedback.SetInputEnabled(index, check.IsChecked == true);
            UpdateStripStyle(index);
        };

        var fill = new Rectangle { Fill = Meter, Width = 14, Height = 0, VerticalAlignment = VerticalAlignment.Bottom };
        _meters[index] = fill;
        var track = new Border
        {
            Width = 16, Height = MeterHeight, Background = Track,
            CornerRadius = new CornerRadius(3), ClipToBounds = true,
            Child = fill,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var label = new TextBlock
        {
            Text = name, FontSize = 11, TextAlignment = TextAlignment.Center,
            Foreground = enabled ? TextOn : TextOff, Width = 56, TextWrapping = TextWrapping.Wrap,
        };

        var strip = new Border
        {
            Tag = index,
            Width = 62, Padding = new Thickness(4, 8, 4, 8),
            Background = enabled ? StripOn : StripOff,
            BorderBrush = new SolidColorBrush(enabled ? Color.FromRgb(0x1D, 0x9E, 0x75) : Color.FromRgb(0x23, 0x28, 0x33)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Child = new StackPanel { Spacing = 8, Children = { check, track, label } },
        };
        return strip;
    }

    private void RefreshChecks()
    {
        _syncing = true;
        foreach (var child in _strips.Children)
        {
            if (child is Border { Tag: int index, Child: StackPanel sp })
            {
                if (sp.Children[0] is CheckBox cb) cb.IsChecked = _feedback.IsInputEnabled(index);
                UpdateStripStyle(index);
            }
        }
        _syncing = false;
    }

    private void UpdateStripStyle(int index)
    {
        foreach (var child in _strips.Children)
        {
            if (child is Border { Tag: int i } border && i == index && border.Child is StackPanel sp)
            {
                var on = _feedback.IsInputEnabled(index);
                border.Background = on ? StripOn : StripOff;
                border.BorderBrush = new SolidColorBrush(on ? Color.FromRgb(0x1D, 0x9E, 0x75) : Color.FromRgb(0x23, 0x28, 0x33));
                if (sp.Children[2] is TextBlock tb) tb.Foreground = on ? TextOn : TextOff;
            }
        }
    }

    private void ApplyMeters()
    {
        foreach (var (physical, rect) in _meters)
        {
            var level = _levels.GetValueOrDefault(physical, 0f);
            if (!_feedback.IsInputEnabled(physical)) level = 0f;   // no data for unchecked
            rect.Height = level * MeterHeight;
        }
    }

    private void Teardown()
    {
        _meterTimer.Stop();
        _feedback.DevicesChanged -= OnDevices;
        _feedback.ChannelsChanged -= OnChannels;
        _feedback.SpectrumChanged -= OnSpectrum;
    }
}
