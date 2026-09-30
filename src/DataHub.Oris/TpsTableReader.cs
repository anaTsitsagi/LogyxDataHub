using System.Collections.Immutable;
using TpsParser;
using TpsParser.TypeModel;

namespace DataHub.Oris;

/// <summary>A TopSpeed record as field name (without table prefix, case-insensitive) → raw Clarion value.</summary>
public sealed class TpsRecord(int recordNumber, IReadOnlyDictionary<string, IClaObject?> fields)
{
    public int RecordNumber { get; } = recordNumber;
    public IReadOnlyDictionary<string, IClaObject?> Fields { get; } = fields;

    public string GetString(string field) =>
        Fields.TryGetValue(field, out var v) && v is IClaString s ? GeorgianText.Decode(s.ToString()) : string.Empty;

    public decimal? GetDecimal(string field)
    {
        if (!Fields.TryGetValue(field, out var v) || v is not IClaNumeric n) return null;
        var d = n.ToDecimal();
        return d.HasValue ? d.Value : null;
    }

    public long GetLong(string field) => (long)(GetDecimal(field) ?? 0);

    public DateOnly? GetDate(string field) => ClarionDate.ToDateOnly(GetLong(field));
}

/// <summary>
/// Streams records from every table in a TopSpeed (.tps) file.
/// </summary>
public static class TpsTableReader
{
    private static readonly EncodingOptions Encoding = new()
    {
        ContentEncoding = GeorgianText.RawEncoding,
        MetadataEncoding = GeorgianText.RawEncoding,
    };

    /// <exception cref="OrisFileException">The file is not TopSpeed, is encrypted, or is corrupt.</exception>
    public static IEnumerable<TpsRecord> ReadRecords(Stream stream, string fileName)
    {
        TpsFile tps;
        IReadOnlyDictionary<int, TableDefinition> tables;
        try
        {
            tps = new TpsFile(stream, Encoding, ErrorHandlingOptions.Default);
            tables = tps.GetTableDefinitions(ErrorHandlingOptions.Default);
        }
        catch (TpsParserException ex)
        {
            throw new OrisFileException(fileName, ex.Message, ex);
        }

        foreach (var (tableNumber, definition) in tables)
        {
            // TpsParser 6.0.1 throws a NullReferenceException when it expands GROUP fields itself,
            // so request only the non-group fields and flatten any groups it still returns.
            var wanted = definition.Fields
                .Where(f => f.TypeCode != FieldTypeCode.Group)
                .Select(f => (int)f.Index)
                .ToImmutableHashSet();
            var nodes = FieldValueReader.CreateFieldIteratorNodes(definition.Fields, wanted);

            foreach (var payload in tps.GetDataRecordPayloads(tableNumber, ErrorHandlingOptions.Default))
            {
                var fields = new Dictionary<string, IClaObject?>(StringComparer.OrdinalIgnoreCase);
                foreach (var value in Flatten(FieldValueReader.EnumerateValues(nodes, payload)))
                    fields[value.FieldDefinition.Name] = value.Value;

                yield return new TpsRecord(payload.RecordNumber, fields);
            }
        }
    }

    private static IEnumerable<FieldEnumerationResult> Flatten(IEnumerable<FieldEnumerationResult> values)
    {
        foreach (var value in values)
        {
            if (value.Value is ClaGroup group)
            {
                foreach (var child in Flatten(group.GetValues()))
                    yield return child;
            }
            else
            {
                yield return value;
            }
        }
    }
}

public sealed class OrisFileException(string fileName, string message, Exception? inner = null)
    : Exception($"{fileName}: {message}", inner)
{
    public string FileName { get; } = fileName;
}
