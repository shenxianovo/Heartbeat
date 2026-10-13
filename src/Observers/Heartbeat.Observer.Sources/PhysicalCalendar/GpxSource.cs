using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;

namespace Heartbeat.Observers.Sources.PhysicalCalendar;

/// <summary>GPX 1.1 waypoint and track import; ordinal identities are scoped to the explicit capture, not persistent GPS fix IDs.</summary>
public sealed class GpxSource : IRecordSource
{
    private static readonly XNamespace Namespace = "http://www.topografix.com/GPX/1/1";
    public string Name => "gpx-xml";
    public static SourceSchema TrackSchema { get; } = new(Guid.Parse("019a0000-0300-7000-8000-000000000002"),
        "source-gpx-tracks", "GPX captured track", SourceFields.WithIdentity(new
        { captureKey = SourceFields.String(), ordinal = SourceFields.Integer(), name = SourceFields.String(nullable: true), description = SourceFields.String(nullable: true) }));
    public static SourceSchema SegmentSchema { get; } = new(Guid.Parse("019a0000-0300-7000-8000-000000000003"),
        "source-gpx-segments", "GPX captured segment", SourceFields.WithIdentity(new
        { trackId = SourceFields.Reference(), ordinal = SourceFields.Integer() }));
    public static SourceSchema PointSchema { get; } = new(Guid.Parse("019a0000-0300-7000-8000-000000000004"),
        "source-gpx-points", "GPX captured point", SourceFields.WithIdentity(new
        {
            segmentId = SourceFields.Reference(required: false), captureKey = SourceFields.String(), ordinal = SourceFields.Integer(), pointType = SourceFields.String(),
            latitude = SourceFields.Number(), longitude = SourceFields.Number(), elevation = SourceFields.Number(nullable: true),
            exactTime = SourceFields.String(nullable: true), name = SourceFields.String(nullable: true),
            fix = SourceFields.String(nullable: true), satellites = SourceFields.Integer(nullable: true),
            hdop = SourceFields.Number(nullable: true), vdop = SourceFields.Number(nullable: true), pdop = SourceFields.Number(nullable: true)
        }));

    public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceReadContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.CaptureKey);
        var bytes = await SourceJson.ReadBytesAsync(context.Input, cancellationToken);
        XDocument document;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = SourceJson.MaximumInputBytes };
        try
        {
            using (var probe = XmlReader.Create(new MemoryStream(bytes), settings))
                while (probe.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (probe.Depth > 32) throw new InvalidDataException("GPX XML nesting limit exceeded.");
                }
            using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException error) { throw new InvalidDataException("Invalid or unsafe GPX XML.", error); }
        var root = document.Root;
        if (root?.Name != Namespace + "gpx" || (string?)root.Attribute("version") != "1.1" || string.IsNullOrWhiteSpace((string?)root.Attribute("creator")))
            throw new InvalidDataException("Expected GPX 1.1 with creator.");
        if (root.Elements(Namespace + "rte").Any()) throw new InvalidDataException("GPX routes are not supported by this bounded importer.");
        var records = new List<SourceRecord>();
        var waypointIndex = 0;
        foreach (var point in root.Elements(Namespace + "wpt"))
            Add(Point(point, context, "waypoint", ++waypointIndex, null, null));
        var trackIndex = 0;
        foreach (var track in root.Elements(Namespace + "trk"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trackKey = Key(context, "track", ++trackIndex);
            Add(new("gpx-track", trackKey, trackKey, TrackSchema,
                SourceFields.Of(new { captureKey = context.CaptureKey, ordinal = trackIndex, name = Text(track, "name"), description = Text(track, "desc") }),
                new Dictionary<string, SourceReference>()));
            var segmentIndex = 0;
            foreach (var segment in track.Elements(Namespace + "trkseg"))
            {
                var segmentKey = Key(context, "segment", trackIndex, ++segmentIndex);
                Add(new("gpx-segment", segmentKey, segmentKey, SegmentSchema, SourceFields.Of(new { ordinal = segmentIndex }),
                    new Dictionary<string, SourceReference> { ["trackId"] = new("gpx-track", trackKey) }));
                var pointIndex = 0;
                foreach (var point in segment.Elements(Namespace + "trkpt"))
                    Add(Point(point, context, "trackpoint", ++pointIndex, trackIndex, (segmentIndex, segmentKey)));
            }
        }
        foreach (var record in records) { cancellationToken.ThrowIfCancellationRequested(); yield return record; }
        void Add(SourceRecord record)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (records.Count >= 10000) throw new InvalidDataException("GPX record limit exceeded.");
            records.Add(record);
        }
    }

    private static SourceRecord Point(XElement point, SourceReadContext context, string type, int index, int? track, (int Index, string Key)? segment)
    {
        var lat = Coordinate(point, "lat", -90, 90, false);
        var lon = Coordinate(point, "lon", -180, 180, true);
        var exactTime = Text(point, "time");
        DateTimeOffset? instant = null;
        if (exactTime is not null)
        {
            try
            {
                // XSD dateTime permits an absent zone. Validate it but do not substitute the importing machine's zone.
                var conversionTime = TruncateFraction(exactTime);
                _ = XmlConvert.ToDateTime(conversionTime, XmlDateTimeSerializationMode.RoundtripKind);
                if (exactTime.EndsWith('Z') || exactTime.Length >= 6 && exactTime[^3] == ':' && exactTime[^6] is '+' or '-')
                    instant = XmlConvert.ToDateTimeOffset(conversionTime);
            }
            catch (FormatException error) { throw new InvalidDataException("Invalid GPX time.", error); }
        }
        var fix = Text(point, "fix");
        if (fix is not null && fix is not ("none" or "2d" or "3d" or "dgps" or "pps")) throw new InvalidDataException("Invalid GPX fix enum.");
        var satText = Text(point, "sat");
        int? satellites = null;
        if (satText is not null)
        {
            if (!int.TryParse(satText, NumberStyles.None, CultureInfo.InvariantCulture, out var count)) throw new InvalidDataException("Invalid GPX satellite count.");
            satellites = count;
        }
        var key = Key(context, type, track, segment?.Index, index);
        var references = new Dictionary<string, SourceReference>();
        if (segment is { } parent) references.Add("segmentId", new("gpx-segment", parent.Key));
        return new("gpx-point", key, key, PointSchema, SourceFields.Of(new
        {
            captureKey = context.CaptureKey, ordinal = index, pointType = type, latitude = lat, longitude = lon,
            elevation = Number(point, "ele"), exactTime, name = Text(point, "name"), fix, satellites,
            hdop = Number(point, "hdop", true), vdop = Number(point, "vdop", true), pdop = Number(point, "pdop", true)
        }), references, instant, instant);
    }
    // Do not let XML's sub-tick rounding carry .999999999 into the next second.
    // The original string remains evidence; conversion truncates toward the beginning of its second.
    private static string TruncateFraction(string value)
    {
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0) return value;
        var end = dot + 1;
        while (end < value.Length && char.IsAsciiDigit(value[end])) end++;
        return end - dot - 1 > 7 ? value[..(dot + 8)] + value[end..] : value;
    }
    private static decimal Coordinate(XElement point, string name, decimal min, decimal max, bool exclusiveMax)
    {
        var text = (string?)point.Attribute(name) ?? throw new InvalidDataException($"GPX point lacks {name}.");
        var value = Decimal(text);
        if (value < min || value > max || exclusiveMax && value == max) throw new InvalidDataException("GPX coordinate out of range.");
        return value;
    }
    private static string? Text(XElement element, string name)
    {
        var values = element.Elements(Namespace + name).ToArray();
        if (values.Length > 1 || values.Any(v => v.HasElements)) throw new InvalidDataException($"Ambiguous GPX {name}.");
        var text = values.SingleOrDefault()?.Value;
        if (text?.Length > 262144) throw new InvalidDataException("GPX field exceeds limit.");
        return text;
    }
    private static decimal? Number(XElement point, string name, bool nonnegative = false)
    {
        var text = Text(point, name);
        if (text is null) return null;
        var value = Decimal(text);
        if (nonnegative && value < 0) throw new InvalidDataException("GPX dilution must be nonnegative.");
        return value;
    }
    private static decimal Decimal(string text)
    {
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
            CultureInfo.InvariantCulture, out var value)) throw new InvalidDataException("Invalid GPX decimal.");
        return value;
    }
    private static string Key(SourceReadContext context, string type, params int?[] ordinals) =>
        ICalendarSource.Key(context.Scope, context.CaptureKey, type, string.Join('/', ordinals.Select(i => i?.ToString(CultureInfo.InvariantCulture) ?? "-")));
}
