using Mp3PlayerSync.Core;

namespace Mp3PlayerSync.Tests;

public class CsvReaderTests
{
    [Test]
    public async Task Quoted_fields_can_contain_commas_quotes_and_line_breaks()
    {
        var records = CsvReader.Parse("a,\"b, \"\"c\"\"\",\"line1\r\nline2\"\r\nx,,z\n");

        await Assert.That(records).IsEquivalentTo(new[]
        {
            new[] { "a", "b, \"c\"", "line1\r\nline2" },
            new[] { "x", "", "z" },
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Leading_byte_order_mark_is_ignored()
    {
        var records = CsvReader.Parse("\uFEFFTrack URI,Track Name\nspotify:track:1,Song\n");

        await Assert.That(records[0][0]).IsEqualTo("Track URI");
    }

    [Test]
    public async Task Blank_lines_produce_no_records()
    {
        await Assert.That(CsvReader.Parse("a\n\n\r\nb\n\n")).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Quoted_empty_field_on_its_own_line_is_a_record()
    {
        await Assert.That(CsvReader.Parse("\"\"\n")).IsEquivalentTo(new[] { new[] { "" } }, CollectionOrdering.Matching);
    }
}
