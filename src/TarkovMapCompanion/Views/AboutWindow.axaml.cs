using System.Reflection;
using Avalonia.Controls;
using TarkovMapCompanion.Data;
using TarkovMapCompanion.Localization;
using TarkovMapCompanion.Maps;

namespace TarkovMapCompanion.Views;

/// <summary>One attribution row: who made something, and what.</summary>
/// <remarks>Top level rather than nested so compiled bindings can name it as an x:DataType.</remarks>
public sealed record Credit(string Who, string What);

/// <summary>
/// Credits and provenance.
/// </summary>
/// <remarks>
/// Not decoration: the map artwork is other people's work used under their terms, so the
/// attributions here are a condition of shipping this. The authors are read from the map catalog
/// rather than hard-coded, so a new map with a new author credits itself.
/// </remarks>
public partial class AboutWindow : Window
{
    private readonly MapCatalog _catalog;

    // Parameterless ctor exists only for the XAML previewer.
    public AboutWindow() : this(MapCatalog.LoadEmbedded())
    {
    }

    public AboutWindow(MapCatalog catalog)
    {
        _catalog = catalog;

        InitializeComponent();

        VersionText.Text = Localizer.Loc("About.Version", Version());

        CreditsList.ItemsSource = BuildCredits();

        SourcesText.Text = string.Join(Environment.NewLine,
            MapCatalog.SourceUrl,
            MapDataStore.DataUrl,
            MapDataStore.TranslationsUrl,
            "https://assets.tarkov.dev/maps/ " + Localizer.Loc("About.SourceArtwork"),
            "https://escapefromtarkov.fandom.com/ " + Localizer.Loc("About.SourceExitConditions"));

        StackText.Text = Localizer.Loc("About.Stack");

        CloseButton.Click += (_, _) => Close();
        TarkovDevButton.Click += (_, _) => Open("https://tarkov.dev");
        SourceButton.Click += (_, _) => Open("https://github.com/the-hideout/tarkov-dev");
    }

    /// <summary>
    /// One line per map author, listing which maps they drew. Derived from the catalog so it can
    /// never drift out of date relative to the maps actually shipped.
    /// </summary>
    private IReadOnlyList<Credit> BuildCredits()
    {
        var byAuthor = _catalog.Maps
            .Where(m => !string.IsNullOrWhiteSpace(m.Author))
            .GroupBy(m => m.Author!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => new Credit(
                g.Key,
                Localizer.Loc(
                    "About.CreditMaps",
                    g.Count(),
                    g.Count() == 1 ? "" : "s",
                    string.Join(", ", g.Select(m => m.DisplayName).Order()))))
            .ToList();

        byAuthor.Insert(0, new Credit(Localizer.Loc("About.CreditWhoTarkovDev"), Localizer.Loc("About.CreditTarkovDev")));
        byAuthor.Insert(1, new Credit(Localizer.Loc("About.CreditWhoWiki"), Localizer.Loc("About.CreditWiki")));

        var unattributed = _catalog.Maps.Count(m => string.IsNullOrWhiteSpace(m.Author));
        if (unattributed > 0)
            byAuthor.Add(new Credit(Localizer.Loc("About.Unattributed"), Localizer.Loc("About.UnattributedCount", unattributed)));

        return byAuthor;
    }

    private static string Version()
    {
        var assembly = Assembly.GetExecutingAssembly();

        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "dev";
    }

    private static void Open(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not open {url}: {ex.Message}");
        }
    }
}
