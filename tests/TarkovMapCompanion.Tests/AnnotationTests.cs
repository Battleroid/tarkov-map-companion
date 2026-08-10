using TarkovMapCompanion.Data;
using TarkovMapCompanion.Party;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Writing on the map, importing somebody else's writing, and sharing your own.
/// </summary>
public sealed class AnnotationTests : IDisposable
{
    private readonly string _dir;

    public AnnotationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tmc-notes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private AnnotationStore NewStore() => new(_dir);

    [Fact]
    public void ANoteSurvivesARestart()
    {
        var first = NewStore();
        Assert.NotNull(first.Add("customs", 120.5, -44.25, "Dorms"));

        var second = NewStore();
        second.Load();

        var note = Assert.Single(second.ForMap("customs"));
        Assert.Equal("Dorms", note.Text);
        Assert.Equal(120.5, note.X);
        Assert.Equal(-44.25, note.Z);
    }

    [Fact]
    public void NotesAreKeptPerMap()
    {
        var store = NewStore();

        store.Add("customs", 0, 0, "Big Red");
        store.Add("woods", 0, 0, "Sawmill");

        Assert.Single(store.ForMap("customs"));
        Assert.Single(store.ForMap("woods"));
        Assert.Empty(store.ForMap("factory"));
    }

    /// <summary>Text that would break the drawing or the file is cleaned up rather than stored.</summary>
    [Theory]
    [InlineData("  Dorms  ", "Dorms")]
    [InlineData("Two\nlines", "Two lines")]
    [InlineData("lots     of     space", "lots of space")]
    public void TextIsTidied(string input, string expected) =>
        Assert.Equal(expected, MapAnnotation.Clean(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    [InlineData(null)]
    public void EmptyTextIsRefused(string? input)
    {
        Assert.Null(MapAnnotation.Clean(input));
        Assert.Null(NewStore().Add("customs", 0, 0, input));
    }

    [Fact]
    public void OverlongTextIsClipped()
    {
        var clipped = MapAnnotation.Clean(new string('x', 500));

        Assert.NotNull(clipped);
        Assert.Equal(MapAnnotation.MaxTextLength, clipped.Length);
    }

    // ---- Files --------------------------------------------------------------

    /// <summary>
    /// A plain list of building names imports, which is the point of accepting CSV at all.
    /// </summary>
    /// <remarks>
    /// The realistic source of a few hundred labels is a spreadsheet or a wiki table, and telling
    /// somebody to hand-write JSON for that is telling them not to bother.
    /// </remarks>
    [Fact]
    public void ACsvOfBuildingNamesImports()
    {
        var path = Path.Combine(_dir, "buildings.csv");

        File.WriteAllText(path, string.Join('\n',
            "map,x,z,text",
            "customs,120.5,-44.25,Dorms",
            "customs,-10,30,Big Red",
            "woods,5,5,Sawmill"));

        var store = NewStore();
        Assert.Equal(3, store.Import(path));

        Assert.Equal(2, store.ForMap("customs").Count);
        Assert.Single(store.ForMap("woods"));
    }

    /// <summary>A label containing a comma survives, because the text is the rest of the line.</summary>
    [Fact]
    public void ACommaInTheTextSurvives()
    {
        var notes = AnnotationStore.ParseCsv("customs,1,2,Dorms, third floor");

        Assert.Equal("Dorms, third floor", Assert.Single(notes).Text);
    }

    [Theory]
    [InlineData("map,x,z,text")]
    [InlineData("# a comment")]
    [InlineData("customs,notanumber,2,Dorms")]
    [InlineData("customs,1,2")]
    [InlineData("")]
    public void MalformedCsvLinesAreSkipped(string line) => Assert.Empty(AnnotationStore.ParseCsv(line));

    [Fact]
    public void ExportedNotesImportBack()
    {
        var source = NewStore();
        source.Add("customs", 1, 2, "Dorms");
        source.Add("woods", 3, 4, "Sawmill");

        var path = Path.Combine(_dir, "shared.json");
        Assert.Equal(2, source.Export(path));

        var target = new AnnotationStore(Path.Combine(_dir, "other"));
        Assert.Equal(2, target.Import(path));

        Assert.Equal("Dorms", Assert.Single(target.ForMap("customs")).Text);
    }

    /// <summary>Importing merges, so it never costs you the notes you already had.</summary>
    [Fact]
    public void ImportingMergesRatherThanReplaces()
    {
        var store = NewStore();
        store.Add("customs", 500, 500, "Mine");

        var path = Path.Combine(_dir, "theirs.csv");
        File.WriteAllText(path, "customs,1,2,Theirs");

        store.Import(path);

        Assert.Equal(2, store.ForMap("customs").Count);
        Assert.Contains(store.ForMap("customs"), a => a.Text == "Mine");
    }

    /// <summary>The same file twice does not double up.</summary>
    [Fact]
    public void ImportingTwiceIsHarmless()
    {
        var path = Path.Combine(_dir, "buildings.csv");
        File.WriteAllText(path, "customs,120.5,-44.25,Dorms");

        var store = NewStore();

        Assert.Equal(1, store.Import(path));
        Assert.Equal(0, store.Import(path));
        Assert.Single(store.ForMap("customs"));
    }

    // ---- Sharing ------------------------------------------------------------

    /// <summary>
    /// A teammate's notes never reach the file on disk.
    /// </summary>
    /// <remarks>
    /// The distinction the whole author field exists for. Saved, they would still be on the map
    /// weeks later with nothing to say where they came from or why they cannot be deleted.
    /// </remarks>
    [Fact]
    public void SharedNotesAreNotSaved()
    {
        var store = NewStore();
        store.Add("customs", 1, 2, "Mine");

        store.SetShared("Teammate", [new MapAnnotation { Map = "customs", X = 9, Z = 9, Text = "Theirs" }]);

        Assert.Equal(2, store.ForMap("customs").Count);

        var reloaded = NewStore();
        reloaded.Load();

        Assert.Equal("Mine", Assert.Single(reloaded.ForMap("customs")).Text);
    }

    [Fact]
    public void SharedNotesAreReplacedWholesale()
    {
        var store = NewStore();

        store.SetShared("Teammate", [new MapAnnotation { Map = "customs", X = 1, Z = 1, Text = "First" }]);
        store.SetShared("Teammate", [new MapAnnotation { Map = "customs", X = 2, Z = 2, Text = "Second" }]);

        var note = Assert.Single(store.ForMap("customs"));
        Assert.Equal("Second", note.Text);
        Assert.Equal("Teammate", note.Author);
    }

    /// <summary>An empty set from somebody withdraws what they last shared.</summary>
    [Fact]
    public void SharingNothingWithdrawsWhatWasShared()
    {
        var store = NewStore();

        store.SetShared("Teammate", [new MapAnnotation { Map = "customs", X = 1, Z = 1, Text = "Theirs" }]);
        Assert.Single(store.ForMap("customs"));

        store.SetShared("Teammate", []);
        Assert.Empty(store.ForMap("customs"));
    }

    [Fact]
    public void ClearingSharedLeavesYourOwn()
    {
        var store = NewStore();

        store.Add("customs", 1, 2, "Mine");
        store.SetShared("Teammate", [new MapAnnotation { Map = "customs", X = 9, Z = 9, Text = "Theirs" }]);

        store.ClearShared();

        Assert.Equal("Mine", Assert.Single(store.ForMap("customs")).Text);
        Assert.Single(store.Own);
    }

    // ---- Protocol -----------------------------------------------------------

    /// <summary>Notes round-trip on the wire with the rest of the message intact.</summary>
    [Fact]
    public async Task AnnotationsRoundTripOnTheWire()
    {
        var key = PartyProtocol.DeriveKey(SessionCode.NewSecret());
        using var stream = new MemoryStream();

        await PartyProtocol.WriteAsync(stream, key, new PartyMessage
        {
            Kind = PartyMessageKind.Annotations,
            Annotations =
            [
                new SharedAnnotation { Name = "Casey", Map = "customs", X = 1.5, Z = -2.5, Text = "Dorms" },
            ],
        });

        stream.Position = 0;
        var read = await PartyProtocol.ReadAsync(stream, key);

        Assert.Equal(PartyMessageKind.Annotations, read!.Kind);

        var note = Assert.Single(read.Annotations!);
        Assert.Equal("Dorms", note.Text);
        Assert.Equal(1.5, note.X);
    }

    /// <summary>
    /// The host attributes notes from the connection, so nobody can write under another name.
    /// </summary>
    [Fact]
    public void SharedNotesAreAttributedByTheHost()
    {
        using var session = new PartySession();

        session.ApplyAnnotations(
            [
                new SharedAnnotation { Name = "Casey", Map = "customs", X = 1, Z = 1, Text = "Mine" },
                new SharedAnnotation { Name = "Other", Map = "customs", X = 2, Z = 2, Text = "Theirs" },
            ],
            session.CurrentGeneration);

        Assert.Equal(2, session.Annotations.Count);
        Assert.Contains(session.Annotations, a => a.Name == "Casey" && a.Text == "Mine");
        Assert.Contains(session.Annotations, a => a.Name == "Other" && a.Text == "Theirs");
    }

    /// <summary>
    /// Adding a message kind does not break a build that has never heard of it.
    /// </summary>
    /// <remarks>
    /// The reason this change did not need the salt moved, unlike heartbeats: an older client skips
    /// what it does not understand and carries on, rather than being dropped for not answering. The
    /// framing is left to the real writer; what is under test is the decoding.
    /// </remarks>
    [Fact]
    public void AKindFromALaterBuildDecodesAsUnknown()
    {
        // Through the context the protocol actually uses. A plain Deserialize is case-sensitive
        // and would match none of these names, leaving every field at its default -- which looks
        // exactly like a passing test and proves nothing.
        var message = System.Text.Json.JsonSerializer.Deserialize(
            """{"kind":"SomethingFromNextYear","version":99}""",
            PartyJsonContext.Default.PartyMessage);

        Assert.NotNull(message);
        Assert.Equal(PartyMessageKind.Unknown, message.Kind);
        Assert.Equal(99, message.Version);
    }

    /// <summary>Annotations decode from a message written by a build that also sent other fields.</summary>
    [Fact]
    public void AnnotationsDecodeAlongsideEverythingElse()
    {
        var message = System.Text.Json.JsonSerializer.Deserialize(
            """
            {
              "kind": "AllAnnotations",
              "version": 4,
              "annotations": [ { "name": "Casey", "map": "customs", "x": 1, "z": 2, "text": "Dorms" } ],
              "somethingElseEntirely": 12
            }
            """,
            PartyJsonContext.Default.PartyMessage);

        Assert.NotNull(message);
        Assert.Equal(PartyMessageKind.AllAnnotations, message.Kind);
        Assert.Equal("Dorms", Assert.Single(message.Annotations!).Text);
    }
}
