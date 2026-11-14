using Mp3PlayerSync.Core;

namespace Mp3PlayerSync.Tests;

public class CsvReaderTests
{
    [Test]
    public void Quoted_fields_can_contain_commas_quotes_and_line_breaks()
    {
        var records = CsvReader.Parse("a,\"b, \"\"c\"\"\",\"line1\r\nline2\"\r\nx,,z\n");

        records.ShouldBe(new[]
        {
            new[] { "a", "b, \"c\"", "line1\r\nline2" },
            new[] { "x", "", "z" },
        });
    }

    [Test]
    public void Leading_byte_order_mark_is_ignored()
    {
        var records = CsvReader.Parse("\uFEFFTrack URI,Track Name\nspotify:track:1,Song\n");

        records[0][0].ShouldBe("Track URI");
    }

    [Test]
    public void Blank_lines_produce_no_records()
    {
        CsvReader.Parse("a\n\n\r\nb\n\n").Count.ShouldBe(2);
    }

    [Test]
    public void Quoted_empty_field_on_its_own_line_is_a_record()
    {
        CsvReader.Parse("\"\"\n").ShouldBe(new[] { new[] { "" } });
    }
}
