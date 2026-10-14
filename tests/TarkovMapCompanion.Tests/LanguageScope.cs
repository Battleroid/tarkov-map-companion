using TarkovMapCompanion.Localization;
using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Pins the process-wide UI language for the lifetime of one test (or test class) and restores it
/// afterwards.
/// </summary>
/// <remarks>
/// The app ships in Simplified Chinese by default now. Display-oriented tests, though, assert
/// English strings, and they used to pass only because English happened to be the default. A test
/// that asserts one language's text should name that language rather than inherit whatever the
/// default is, so those classes hold one of these.
/// </remarks>
internal sealed class LanguageScope : IDisposable
{
    private readonly AppLanguage _previous = Localizer.Language;

    internal LanguageScope(AppLanguage language) => Localizer.SetLanguage(language);

    public void Dispose() => Localizer.SetLanguage(_previous);
}
