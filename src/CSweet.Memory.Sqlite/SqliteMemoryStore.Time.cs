using System.Globalization;

namespace CSweet.Memory;

public sealed partial class SqliteMemoryStore
{
    private static readonly string[] TimestampFormats =
    [
        "O", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss'Z'"
    ];

    // Keep legacy payloads and source fingerprints untouched. SQL eligibility must compare
    // instants even when old clients stored different offsets, and before candidate LIMITs.
    // SQLite's julianday/strftime lose sub-millisecond precision at expiry boundaries.
    // Unknown or offset-free timestamps yield SQL NULL, so temporal predicates deny them.
    private static long? ParseUtcTicks(string? value) =>
        value is not null && DateTimeOffset.TryParseExact(value, TimestampFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant.UtcTicks : null;
}
