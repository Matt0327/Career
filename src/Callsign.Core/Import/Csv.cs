using System.Text;

namespace Callsign.Core.Import;

/// <summary>
/// A minimal streaming RFC-4180 CSV reader: quoted fields, doubled-quote escapes, and commas
/// or newlines inside quotes. Yields each record as a string array. Enough for the OurAirports
/// files without pulling in a dependency.
/// </summary>
public static class Csv
{
    public static IEnumerable<string[]> ReadRecords(TextReader reader)
    {
        var field = new StringBuilder();
        var record = new List<string>();
        bool inQuotes = false;
        bool sawAny = false;
        int read;

        while ((read = reader.Read()) != -1)
        {
            char c = (char)read;
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                    else inQuotes = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else
            {
                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        sawAny = true;
                        break;
                    case ',':
                        record.Add(field.ToString());
                        field.Clear();
                        sawAny = true;
                        break;
                    case '\r':
                        break; // handled with the following \n
                    case '\n':
                        record.Add(field.ToString());
                        field.Clear();
                        yield return record.ToArray();
                        record.Clear();
                        sawAny = false;
                        break;
                    default:
                        field.Append(c);
                        sawAny = true;
                        break;
                }
            }
        }

        if (sawAny || field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            yield return record.ToArray();
        }
    }
}
