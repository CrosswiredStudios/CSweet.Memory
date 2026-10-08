namespace CSweet.Memory;

public sealed partial class PostgreSqlMemoryStore
{
    // Native timestamptz and Npgsql parameters retain only microseconds. The JSON
    // snapshots retain all DateTimeOffset ticks, including existing rows. Compare
    // those values in SQL so ineligible rows cannot consume a candidate limit.
    // These SQL identifiers/member names are internal constants, never request input.
    private static string TimeTicks(string payload, string member)
    {
        var timestamp = $"({payload}->>'{member}')";
        // Parse the whole second with its explicit offset, then add the decimal
        // fraction separately. Casting the original string would round its seventh
        // digit; extracting a floating-point epoch would lose precision at large dates.
        return $"(EXTRACT(EPOCH FROM regexp_replace({timestamp}, '[.][0-9]+', '')::timestamptz)::numeric * 10000000 + COALESCE(rpad(substring({timestamp} from '[.]([0-9]+)'), 7, '0')::bigint, 0))";
    }

    private static string ValidAt(string payload, string start, string end, bool includeSuperseded = false) =>
        $"({TimeTicks(payload, start)}<=@asOfTicks AND ({(includeSuperseded ? "@superseded OR " : "")}{payload}->>'{end}' IS NULL OR {TimeTicks(payload, end)}>@asOfTicks))";

    private static long EpochTicks(DateTimeOffset instant) => instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
}
