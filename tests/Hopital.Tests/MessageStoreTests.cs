using System.Text.Json;
using Hopital.Hl7;
using Hopital.Receiver;

namespace Hopital.Tests;

public sealed class MessageStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hopital-tests", Guid.NewGuid().ToString("N"));
    private readonly MessageStore _store;

    public MessageStoreTests() => _store = new MessageStore(_root, TimeProvider.System);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private ReceivedSummary SaveSample(string name, string mode = "Accept", string? ack = "MSH|...\rMSA|AA|X\r") =>
        _store.Save(Hl7Text.Encoding.GetBytes(Hl7Text.ToWire(File.ReadAllText(Path.Combine(TestPaths.ResultSamples, name)))), "127.0.0.1:5000", mode, ack, []);

    private string FullPath(string relative) => Path.Combine(_root, relative);

    [Fact]
    public void Saves_the_message_exactly_and_a_JSON_summary_next_to_it()
    {
        var wire = Hl7Text.ToWire(File.ReadAllText(Path.Combine(TestPaths.ResultSamples, "oru-r01-image-references.hl7")));

        var summary = _store.Save(Hl7Text.Encoding.GetBytes(wire), "10.0.0.5:4000", "Accept", "MSH|x\rMSA|AA|IMG000000000000002\r", []);

        Assert.Equal(wire, File.ReadAllText(FullPath(summary.File)));
        Assert.Equal("ORU^R01^ORU_R01", summary.MessageType);
        Assert.Equal("IMG000000000000002", summary.ControlId);
        Assert.Equal("98230284", summary.PatientMrn);
        Assert.Equal("Quintero, Omar", summary.PatientName);
        Assert.Equal("ORD52625264", summary.PlacerOrderNumber);
        Assert.Equal("ACC16942728", summary.FillerOrderNumber);
        Assert.Equal("ACC16942728", summary.AccessionNumber);
        Assert.Equal("10.0.0.5:4000", summary.From);

        var json = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(FullPath(summary.File), ".json"))).RootElement;
        Assert.Equal("IMG000000000000002", json.GetProperty("controlId").GetString());
        Assert.Equal("Accept", json.GetProperty("mode").GetString());
        Assert.Contains("MSA|AA|IMG000000000000002", json.GetProperty("ack").GetString());
    }

    [Fact]
    public void Embedded_images_are_saved_as_files()
    {
        var summary = SaveSample("oru-r01-embedded-images.hl7");

        var images = summary.Attachments.Where(a => a.File is not null).ToList();
        Assert.Equal(2, images.Count);
        Assert.Equal([2, 3], images.Select(a => a.Obx));
        foreach (var image in images)
        {
            Assert.Equal("image/png", image.MediaType);
            Assert.EndsWith(".png", image.File);
            var bytes = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(FullPath(summary.File))!, image.File!));
            Assert.Equal(image.Bytes, bytes.Length);
            Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[..4]);
        }
    }

    [Fact]
    public void An_embedded_PDF_report_is_saved_and_the_document_number_recorded()
    {
        var summary = SaveSample("mdm-t02-pdf-report.hl7");

        var pdf = Assert.Single(summary.Attachments);
        Assert.Equal("application/pdf", pdf.MediaType);
        var bytes = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(FullPath(summary.File))!, pdf.File!));
        Assert.StartsWith("%PDF-", Hl7Text.Encoding.GetString(bytes[..5]));
        Assert.Equal("DOC0000000001", summary.DocumentNumber);
        Assert.Equal("ORD52625264", summary.PlacerOrderNumber);
    }

    [Fact]
    public void Referenced_images_and_links_are_recorded_not_fetched()
    {
        var summary = SaveSample("oru-r01-image-references.hl7");

        Assert.Equal(
            ["https://imaging.example/studies/ACC16942728/1.png", "https://imaging.example/viewer?accession=ACC16942728"],
            summary.Attachments.Select(a => a.Reference));
        Assert.All(summary.Attachments, a => Assert.Null(a.File));
        Assert.Equal("image/png", summary.Attachments[0].MediaType);
    }

    [Fact]
    public void Broken_embedded_data_is_reported_not_saved()
    {
        var summary = SaveSample("oru-r01-broken-image.hl7");

        Assert.Equal(2, summary.Attachments.Count);
        Assert.All(summary.Attachments, a => Assert.Null(a.File));
        Assert.Contains("does not decode", summary.Attachments[0].Problem);
        Assert.Contains("no data", summary.Attachments[1].Problem);
    }

    [Fact]
    public void A_message_without_MSH_is_still_saved_with_a_warning()
    {
        var summary = _store.Save("not hl7"u8.ToArray(), null, "Accept", null, ["Something odd"]);

        Assert.Equal("not hl7", File.ReadAllText(FullPath(summary.File)));
        Assert.Contains("_unparsed", summary.File);
        Assert.Equal(["Something odd", "No MSH segment: saved as is."], summary.Warnings);
    }

    [Fact]
    public void Recent_lists_newest_first_and_Read_gets_one_back()
    {
        var first = SaveSample("oru-r01-embedded-images.hl7");
        var second = SaveSample("mdm-t02-pdf-report.hl7");
        var third = SaveSample("oru-r01-image-references.hl7");

        var recent = _store.Recent(2);

        Assert.Equal([third.File, second.File], recent.Select(r => r.File));
        Assert.Equal(3, _store.Recent(50).Count);
        var day = first.File.Split('/')[0];
        var name = Path.GetFileNameWithoutExtension(first.File);
        var read = _store.Read(day, name)!;
        Assert.Equal(first.ControlId, read.ControlId);
        Assert.Equal(first.Attachments.Select(a => a.File), read.Attachments.Select(a => a.File));
    }

    [Fact]
    public void Recent_on_an_empty_store_is_empty()
    {
        Assert.Empty(_store.Recent(10));
    }

    [Theory]
    [InlineData("..", "x.hl7")]
    [InlineData("2026-10-26", "../x.hl7")]
    [InlineData("2026-10-26", "..")]
    [InlineData("2026-10-26", "sub/x.hl7")]
    [InlineData("2026-10-26", @"sub\x.hl7")]
    [InlineData("../2026-10-26", "x.hl7")]
    [InlineData("2026-10-26", "missing.hl7")]
    public void PathOf_refuses_anything_outside_the_store_or_missing(string day, string file)
    {
        SaveSample("oru-r01-image-references.hl7");

        Assert.Null(_store.PathOf(day, file));
    }

    [Fact]
    public void PathOf_finds_saved_messages_and_attachments()
    {
        var saved = SaveSample("oru-r01-embedded-images.hl7");
        var day = saved.File.Split('/')[0];

        Assert.NotNull(_store.PathOf(day, Path.GetFileName(saved.File)));
        Assert.NotNull(_store.PathOf(day, saved.Attachments.First(a => a.File is not null).File!));
    }

    [Fact]
    public void Messages_arriving_together_get_different_file_names()
    {
        var files = Enumerable.Range(0, 20).Select(_ => SaveSample("oru-r01-image-references.hl7").File).ToList();

        Assert.Equal(20, files.Distinct().Count());
    }
}
