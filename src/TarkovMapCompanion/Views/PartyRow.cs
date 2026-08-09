using Avalonia.Media;

namespace TarkovMapCompanion.Views;

/// <summary>
/// One line of the party roster.
/// </summary>
/// <remarks>
/// A view type rather than the domain <c>PartyPeer</c>: the list needs a swatch matching the color
/// that peer is drawn in, and a single phrase covering four different situations -- you, not placed
/// yet, on another map, or a position of a given age. Working that out in a binding would scatter
/// the logic across XAML.
/// </remarks>
public sealed record PartyRow(string Name, string Detail, IBrush Swatch);
