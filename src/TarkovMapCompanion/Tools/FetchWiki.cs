using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TarkovMapCompanion.Data;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.Tools;

/// <summary>
/// Builds the bundled extract-notes file from the Escape from Tarkov wiki.
/// </summary>
/// <remarks>
/// <para>
/// Run with <c>--fetch-wiki</c> and rebuild. Reads through the MediaWiki API rather than scraping
/// rendered HTML: the API returns the source table, which is far more stable than the page layout
/// and does not trip the bot protection that blocks plain page fetches.
/// </para>
/// <para>
/// Only the structured columns are taken -- faction availability, single-use, and the short
/// Requirements cell. The free-text Notes column is deliberately skipped: it is mostly directions
/// and screenshots, it is the most clearly authored prose on the page, and none of it fits in a
/// tooltip. Wiki content is CC BY-SA and the About screen credits it.
/// </para>
/// </remarks>
public static partial class FetchWiki
{
    private const string ApiBase = "https://escapefromtarkov.fandom.com/api.php";
    private const string DefaultOutput = "src/TarkovMapCompanion/Data/Snapshots/extract-notes.json";

    /// <summary>Map normalized name to wiki page title.</summary>
    private static readonly Dictionary<string, string> WikiPages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["customs"] = "Customs",
        ["factory"] = "Factory",
        ["woods"] = "Woods",
        ["shoreline"] = "Shoreline",
        ["interchange"] = "Interchange",
        ["reserve"] = "Reserve",
        ["the-lab"] = "The_Lab",
        ["lighthouse"] = "Lighthouse",
        ["streets-of-tarkov"] = "Streets_of_Tarkov",
        ["ground-zero"] = "Ground_Zero",
        ["the-labyrinth"] = "The_Labyrinth",
        ["terminal"] = "Terminal",
        ["icebreaker"] = "Icebreaker",
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var output = args.Length > 0 ? args[0] : DefaultOutput;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.Add("User-Agent", "TarkovMapCompanion/1.0 (map helper; contact via GitHub)");

        var catalog = MapCatalog.LoadEmbedded();
        var store = new MapDataStore(new AppSettings { AllowNetwork = false });
        store.LoadLocal();

        var document = new ExtractNotesDocument { FetchedAt = DateTimeOffset.UtcNow };

        var totalNotes = 0;
        var totalConditional = 0;

        foreach (var map in catalog.Maps)
        {
            if (!WikiPages.TryGetValue(map.NormalizedName, out var page))
            {
                Console.WriteLine($"{map.NormalizedName,-20} no wiki page mapped");
                continue;
            }

            List<ExtractNote> notes;
            try
            {
                notes = await ScrapeAsync(http, page).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{map.NormalizedName,-20} FAILED: {ex.Message}");
                continue;
            }

            if (notes.Count == 0)
            {
                Console.WriteLine($"{map.NormalizedName,-20} no extraction table found");
                continue;
            }

            document.Maps[map.NormalizedName] = notes;
            totalNotes += notes.Count;

            var conditional = notes.Count(n => n.AlwaysAvailable == false);
            totalConditional += conditional;

            // Coverage against the extracts we actually draw: an unmatched name is a note that
            // will never be shown, which is worth knowing about now rather than in the UI.
            var matched = 0;
            var unmatched = new List<string>();

            if (store.ForMap(map.NormalizedName) is { Extracts: { } extracts })
            {
                var wikiKeys = notes.Select(n => ExtractNotesStore.Key(n.Name)).ToHashSet(StringComparer.Ordinal);

                foreach (var extract in extracts)
                {
                    var name = store.Translate(extract.Name);
                    if (wikiKeys.Contains(ExtractNotesStore.Key(name)))
                        matched++;
                    else
                        unmatched.Add(name);
                }
            }

            Console.WriteLine(
                $"{map.NormalizedName,-20} {notes.Count,3} notes, {conditional,2} conditional, " +
                $"{matched} matched" +
                (unmatched.Count > 0 ? $", unmatched: {string.Join(", ", unmatched.Take(6))}" : ""));
        }

        ExtractNotesStore.Save(document, output);

        Console.WriteLine();
        Console.WriteLine($"{totalNotes} extracts, {totalConditional} of them conditional");
        Console.WriteLine($"wrote {Path.GetFullPath(output)}");
        Console.WriteLine("Rebuild to embed.");
        return 0;
    }

    // ---- Scraping -----------------------------------------------------------

    private static async Task<List<ExtractNote>> ScrapeAsync(HttpClient http, string page)
    {
        var sectionIndex = await FindExtractionSectionAsync(http, page).ConfigureAwait(false);
        if (sectionIndex is null)
            return [];

        var url = $"{ApiBase}?action=parse&page={Uri.EscapeDataString(page)}" +
                  $"&prop=wikitext&format=json&section={sectionIndex}";

        var json = await http.GetStringAsync(url).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var wikitext = document.RootElement
            .GetProperty("parse").GetProperty("wikitext").GetProperty("*").GetString() ?? "";

        return ParseTable(wikitext);
    }

    private static async Task<int?> FindExtractionSectionAsync(HttpClient http, string page)
    {
        var url = $"{ApiBase}?action=parse&page={Uri.EscapeDataString(page)}&prop=sections&format=json";
        var json = await http.GetStringAsync(url).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("parse", out var parse))
            return null;

        foreach (var section in parse.GetProperty("sections").EnumerateArray())
        {
            var line = section.GetProperty("line").GetString() ?? "";

            // "Extractions" on most maps, "Extraction" on Terminal.
            if (line.StartsWith("Extraction", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(section.GetProperty("index").GetString(), out var index))
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses the extraction wikitable into notes, resolving columns by header name rather than
    /// by position, because column order is not identical on every map.
    /// </summary>
    private static List<ExtractNote> ParseTable(string wikitext)
    {
        var notes = new List<ExtractNote>();

        var tableStart = wikitext.IndexOf("{|", StringComparison.Ordinal);
        if (tableStart < 0)
            return notes;

        var lines = wikitext[tableStart..].Split('\n');

        List<string>? headers = null;
        var row = new List<string>();
        var cell = new StringBuilder();
        var inHeaderRow = true;

        void FlushCell()
        {
            if (cell.Length > 0)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
        }

        void FlushRow()
        {
            FlushCell();

            if (row.Count == 0)
                return;

            if (inHeaderRow && headers is null)
            {
                headers = row.Select(h => Clean(h).ToLowerInvariant()).ToList();
            }
            else if (headers is not null)
            {
                var note = BuildNote(headers, row);
                if (note is not null)
                    notes.Add(note);
            }

            row = [];
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("{|", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("|}", StringComparison.Ordinal))
            {
                FlushRow();
                break;
            }

            if (line.StartsWith("|-", StringComparison.Ordinal))
            {
                FlushRow();
                inHeaderRow = false;
                continue;
            }

            if (line.StartsWith('!') || line.StartsWith('|'))
            {
                FlushCell();

                var content = line[1..];

                // "! style=\"text-align: left;\" |Administration Gate" -- drop the cell attributes.
                var pipe = content.IndexOf('|');
                if (pipe >= 0 && LooksLikeCellAttributes(content[..pipe]))
                    content = content[(pipe + 1)..];

                cell.Append(content);
            }
            else
            {
                // Continuation of the current cell.
                cell.Append('\n').Append(line);
            }
        }

        FlushRow();
        return notes;
    }

    /// <summary>
    /// True for the <c>style="..."</c> / <c>colspan=2</c> prefix that precedes a cell's content.
    /// Distinguishing it from real content matters: a genuine value can contain a pipe too.
    /// </summary>
    private static bool LooksLikeCellAttributes(string prefix) =>
        prefix.Contains('=') && !prefix.Contains("[[", StringComparison.Ordinal);

    private static ExtractNote? BuildNote(List<string> headers, List<string> cells)
    {
        string? Column(params string[] names)
        {
            foreach (var name in names)
            {
                var index = headers.FindIndex(h => h.Contains(name, StringComparison.Ordinal));
                if (index >= 0 && index < cells.Count)
                    return cells[index];
            }

            return null;
        }

        var name = Clean(Column("name") ?? "");
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var availabilityRaw = Column("always available") ?? Column("always") ?? "";
        var (alwaysAvailable, availability) = ParseAvailability(availabilityRaw);

        var requirement = Condense(Clean(Column("requirements") ?? Column("requirement") ?? ""));

        return new ExtractNote
        {
            Name = name,
            AlwaysAvailable = alwaysAvailable,
            Availability = availability,
            SingleUse = ParseTick(Clean(Column("single-use") ?? Column("single use") ?? "")),
            Requirement = string.IsNullOrWhiteSpace(requirement) ? null : requirement,
        };
    }

    /// <summary>
    /// The availability cell is usually a single tick or cross, but splits by faction on shared
    /// extracts, e.g. "PMC: no, Scav: yes" for co-op doors.
    /// </summary>
    private static (bool? Always, string? PerFaction) ParseAvailability(string raw)
    {
        var text = Clean(raw);

        if (text.Contains("PMC", StringComparison.OrdinalIgnoreCase)
            && text.Contains("Scav", StringComparison.OrdinalIgnoreCase))
        {
            var readable = text.Replace("✔", "yes").Replace("✘", "no");
            return (false, Condense(readable));
        }

        return (ParseTick(text), null);
    }

    private static bool? ParseTick(string text) => text switch
    {
        _ when text.Contains('✔') => true,
        _ when text.Contains('✘') => false,
        _ => null,
    };

    // ---- Wikitext cleaning --------------------------------------------------

    [GeneratedRegex(@"\[\[(?:File|Image):[^\[\]]*(?:\[\[[^\[\]]*\]\][^\[\]]*)*\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex FileLinkRegex();

    [GeneratedRegex(@"\[\[([^\[\]\|]+)\|([^\[\]]+)\]\]")]
    private static partial Regex PipedLinkRegex();

    [GeneratedRegex(@"\[\[([^\[\]]+)\]\]")]
    private static partial Regex PlainLinkRegex();

    [GeneratedRegex(@"\{\{[^{}]*\}\}")]
    private static partial Regex TemplateRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Paid extracts state their price only as an image, e.g.
    /// <c>[[File:5000 Roubles.png|link=Roubles]] per player</c>. Stripping images first would
    /// reduce that to "per player", losing the one number that matters.
    /// </summary>
    [GeneratedRegex(@"\[\[(?:File|Image):\s*([\d,\.]+)\s*(Roubles|Dollars|Euros)[^\[\]]*\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex CurrencyImageRegex();

    private static string Clean(string wikitext)
    {
        var text = wikitext;

        // Line breaks first, so they survive tag stripping as separators.
        text = text.Replace("<br/>", "\n").Replace("<br />", "\n").Replace("<br>", "\n");

        // Rescue prices out of currency images before the general image strip.
        text = CurrencyImageRegex().Replace(text, m => $"{m.Groups[1].Value} {m.Groups[2].Value}");

        // Everything else: images carry no information we can show in a tooltip, and their
        // captions are not the cell's meaning.
        text = FileLinkRegex().Replace(text, "");

        for (var i = 0; i < 3; i++)
            text = TemplateRegex().Replace(text, "");

        text = PipedLinkRegex().Replace(text, "$2");
        text = PlainLinkRegex().Replace(text, "$1");

        text = HtmlTagRegex().Replace(text, "");

        text = text.Replace("'''", "").Replace("''", "");
        text = System.Net.WebUtility.HtmlDecode(text);

        text = WhitespaceRegex().Replace(text, " ");

        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l != "-");

        return string.Join("\n", lines).Trim();
    }

    /// <summary>
    /// Squashes a requirement cell into one short line. These are read in a tooltip mid-raid, so
    /// a wall of text is worse than no text; the wiki link is there for the full version.
    /// </summary>
    private static string Condense(string text)
    {
        const int maxLength = 190;

        var joined = string.Join(" · ", text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // Stray connectives left behind when an image between two items is removed.
            .Where(l => l is not ("+" or "-" or "&" or "and"))
            .Select(l => l.TrimEnd('.')));

        joined = WhitespaceRegex().Replace(joined, " ").Trim();

        if (joined.Length <= maxLength)
            return joined;

        var cut = joined.LastIndexOf(' ', maxLength - 1);
        return joined[..(cut > 60 ? cut : maxLength - 1)].TrimEnd(' ', ',', '·') + "…";
    }
}
