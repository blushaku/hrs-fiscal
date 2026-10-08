namespace Hrs.Fiscal.Server;

/// <summary>The property's local time zone (Kosovo: Europe/Belgrade, CET/CEST). All dates shown and filtered use it.</summary>
public sealed class PropertyClock
{
    public PropertyClock(IConfiguration config, TimeProvider? time = null)
    {
        var id = config["Property:TimeZone"] ?? "Europe/Belgrade";
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById(id); // .NET 8 maps IANA ids on Windows too
        Time = time ?? TimeProvider.System;
    }

    public TimeZoneInfo TimeZone { get; }
    public TimeProvider Time { get; }

    public DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(Time.GetUtcNow().UtcDateTime, TimeZone);
    public DateOnly Today => DateOnly.FromDateTime(LocalNow);

    public DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone);

    public string Format(DateTime utc) => ToLocal(utc).ToString("dd.MM.yyyy HH:mm:ss");
    public string FormatShort(DateTime utc) => ToLocal(utc).ToString("dd.MM HH:mm");
}

public static class Fmt
{
    public static string Eur(long cents) => "€ " + (cents / 100m).ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);
    public static string Eur4(long priceUnits) => "€ " + (priceUnits / 10000m).ToString("#,##0.0000", System.Globalization.CultureInfo.InvariantCulture);
}
