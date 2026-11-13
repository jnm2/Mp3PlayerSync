using System.Text;

namespace Mp3PlayerSync.Core;

/// <summary>
/// RFC 4180 CSV reader: quoted fields may contain commas, line breaks, and doubled quotes.
/// </summary>
public static class CsvReader
{
    public static List<string[]> Parse(string text)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var recordHasContent = false;

        var start = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    recordHasContent = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    recordHasContent = true;
                    break;
                case '\r':
                case '\n':
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    EndRecord();
                    break;
                default:
                    field.Append(c);
                    recordHasContent = true;
                    break;
            }
        }

        EndRecord();
        return records;

        void EndRecord()
        {
            if (recordHasContent)
            {
                fields.Add(field.ToString());
                records.Add(fields.ToArray());
            }

            fields.Clear();
            field.Clear();
            recordHasContent = false;
        }
    }
}
