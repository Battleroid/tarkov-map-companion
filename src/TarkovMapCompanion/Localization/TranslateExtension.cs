using System;
using Avalonia.Markup.Xaml;

namespace TarkovMapCompanion.Localization;

/// <summary>
/// XAML markup extension that resolves a localization key at parse time, e.g.
/// <c>{loc:Tr Main.Fit}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Resolution happens once, when the XAML is parsed, which is the right shape for this app: the
/// language is chosen at startup and a change is applied on restart, so there is nothing to notify
/// and no binding is needed. The returned value is a plain string, exactly as if the literal had
/// been written inline.
/// </para>
/// <para>
/// The class is named <c>TrExtension</c> (not <c>Tr</c>) so the Avalonia XAML compiler's
/// <c>&lt;name&gt;Extension</c> convention resolves the terse <c>Tr</c> spelling used
/// throughout the views.
/// </para>
/// </remarks>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    /// <summary>The dictionary key to resolve. Unset resolves to an empty string.</summary>
    public string? Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Key is null ? string.Empty : Localizer.Loc(Key);
}
