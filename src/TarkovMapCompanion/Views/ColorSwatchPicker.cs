using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using SkiaSharp;
using TarkovMapCompanion.Rendering;

namespace TarkovMapCompanion.Views;

/// <summary>
/// A row of colors to choose from, filled into a panel supplied by the caller.
/// </summary>
/// <remarks>
/// <para>
/// A closed set rather than a full color picker. It keeps the lightness separation MarkerPalette
/// promises for color blindness, it avoids a fourth package in a dependency graph that is pinned
/// with a warning comment about exactly that, and it makes "somebody in your squad already has that
/// one" a question the control can answer.
/// </para>
/// <para>
/// Not a custom control: it fills a plain <c>Panel</c> declared in XAML. Two instances, no styling
/// or templating needed, and nothing that has to survive a theme change.
/// </para>
/// </remarks>
public sealed class ColorSwatchPicker
{
    private readonly Panel _host;
    private readonly List<(SKColor Color, ToggleButton Button)> _swatches = [];

    private bool _updating;

    public ColorSwatchPicker(Panel host)
    {
        _host = host;
        Build();
    }

    /// <summary>Raised when the user picks a color. Not raised by <see cref="Select"/>.</summary>
    public event EventHandler<SKColor>? Picked;

    /// <summary>Ticks the swatch matching this color, if there is one.</summary>
    public void Select(SKColor color)
    {
        _updating = true;

        foreach (var (swatch, button) in _swatches)
            button.IsChecked = swatch.Red == color.Red && swatch.Green == color.Green && swatch.Blue == color.Blue;

        _updating = false;
    }

    /// <summary>
    /// Notes which colors are already spoken for, so two people do not pick the same one.
    /// </summary>
    /// <remarks>
    /// Advisory. Nobody is stopped from choosing a taken color and nobody is ever reassigned --
    /// silently moving somebody's chosen color is worse than the duplicate it fixes. This just puts
    /// the information where the decision is being made.
    /// </remarks>
    public void MarkTaken(IReadOnlyDictionary<string, string> takenByName)
    {
        foreach (var (color, button) in _swatches)
        {
            var hex = ColorCodec.ToHex(color);
            var owner = takenByName.FirstOrDefault(p =>
                string.Equals(p.Value, hex, StringComparison.OrdinalIgnoreCase)).Key;

            var name = NameOf(color);

            ToolTip.SetTip(button, owner is null
                ? $"{name}  {hex}"
                : $"{name}  {hex}   already used by {owner}");

            button.Opacity = owner is null ? 1.0 : 0.55;
        }
    }

    private static string NameOf(SKColor color) =>
        MarkerPalette.PlayerChoices.FirstOrDefault(c => c.Color == color).Name ?? "Custom";

    private void Build()
    {
        _host.Children.Clear();

        foreach (var (name, color) in MarkerPalette.PlayerChoices)
        {
            var button = new ToggleButton
            {
                Width = 30,
                Height = 24,
                Margin = new Avalonia.Thickness(0, 0, 4, 4),
                Padding = new Avalonia.Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,

                // The swatch is the label. A name next to ten of these would be a wall of text, and
                // the tooltip carries the name for anyone who wants it.
                Content = new Rectangle
                {
                    Width = 18,
                    Height = 12,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = new SolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue)),
                },

                [ToolTip.TipProperty] = $"{name}  {ColorCodec.ToHex(color)}",
            };

            var chosen = color;
            button.IsCheckedChanged += (_, _) =>
            {
                // Radio behavior without a GroupName, which would need these to share a parent that
                // nothing else uses. Unticking the current choice is not a state worth having, so a
                // click on the checked one is put straight back.
                if (_updating)
                    return;

                if (button.IsChecked != true)
                {
                    _updating = true;
                    button.IsChecked = true;
                    _updating = false;
                    return;
                }

                Select(chosen);
                Picked?.Invoke(this, chosen);
            };

            _swatches.Add((color, button));
            _host.Children.Add(button);
        }
    }
}
