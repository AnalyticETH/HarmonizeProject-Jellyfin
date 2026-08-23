using System;

namespace Jellyfin.Plugin.Hue.Service;

/// <summary>
/// Calculates sunrise, sunset, and civil twilight without a network dependency. The
/// implementation uses the NOAA low-precision solar-position equations with the official
/// zenith used for apparent sunrise/sunset (90.833 degrees) and civil twilight (96 degrees).
/// The requested calendar date identifies the base solar event; a configured offset may
/// intentionally move the returned local instant across midnight. The calculator returns
/// false during polar day/night when the requested event does not occur.
/// </summary>
internal static class HueSolarCalculator
{
    private const double SunriseSunsetZenithDegrees = 90.833;
    private const double CivilTwilightZenithDegrees = 96.0;

    internal static bool TryGetEventUtc(
        DateTime utcCalculationDate,
        double latitude,
        double longitude,
        bool sunrise,
        out DateTime eventUtc)
    {
        return TryGetEventUtc(
            utcCalculationDate,
            latitude,
            longitude,
            sunrise,
            SunriseSunsetZenithDegrees,
            out eventUtc);
    }

    internal static bool TryGetCivilTwilightUtc(
        DateTime utcCalculationDate,
        double latitude,
        double longitude,
        bool dawn,
        out DateTime eventUtc)
    {
        return TryGetEventUtc(
            utcCalculationDate,
            latitude,
            longitude,
            dawn,
            CivilTwilightZenithDegrees,
            out eventUtc);
    }

    private static bool TryGetEventUtc(
        DateTime utcCalculationDate,
        double latitude,
        double longitude,
        bool sunrise,
        double zenithDegrees,
        out DateTime eventUtc)
    {
        eventUtc = default;
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            latitude < -90 || latitude > 90 || longitude < -180 || longitude > 180)
        {
            return false;
        }

        var date = DateTime.SpecifyKind(utcCalculationDate.Date, DateTimeKind.Utc);
        var dayOfYear = date.DayOfYear;
        var longitudeHour = longitude / 15.0;
        var approximateTime = dayOfYear + ((sunrise ? 6.0 : 18.0) - longitudeHour) / 24.0;
        var meanAnomaly = (0.9856 * approximateTime) - 3.289;
        var trueLongitude = NormalizeDegrees(
            meanAnomaly +
            (1.916 * SinDegrees(meanAnomaly)) +
            (0.020 * SinDegrees(2 * meanAnomaly)) +
            282.634);

        var rightAscension = NormalizeDegrees(ToDegrees(Math.Atan(0.91764 * Math.Tan(ToRadians(trueLongitude)))));
        var longitudeQuadrant = Math.Floor(trueLongitude / 90.0) * 90.0;
        var rightAscensionQuadrant = Math.Floor(rightAscension / 90.0) * 90.0;
        rightAscension = (rightAscension + longitudeQuadrant - rightAscensionQuadrant) / 15.0;

        var sineDeclination = 0.39782 * SinDegrees(trueLongitude);
        var cosineDeclination = Math.Cos(Math.Asin(sineDeclination));
        var cosineHourAngle =
            (Math.Cos(ToRadians(zenithDegrees)) -
             (sineDeclination * SinDegrees(latitude))) /
            (cosineDeclination * Math.Cos(ToRadians(latitude)));

        // Above/below the horizon for the entire day. At the exact poles the
        // denominator can become zero; treating it as no event is the safe result.
        if (!double.IsFinite(cosineHourAngle) || cosineHourAngle > 1 || cosineHourAngle < -1)
        {
            return false;
        }

        var hourAngle = ToDegrees(Math.Acos(Math.Clamp(cosineHourAngle, -1, 1)));
        if (sunrise)
            hourAngle = 360.0 - hourAngle;
        hourAngle /= 15.0;

        var localMeanTime = hourAngle + rightAscension - (0.06571 * approximateTime) - 6.622;
        var universalTime = NormalizeHours(localMeanTime - longitudeHour);
        eventUtc = date.AddHours(universalTime);
        return true;
    }

    internal static bool TryGetEventLocal(
        DateTime localDate,
        TimeZoneInfo timeZone,
        double latitude,
        double longitude,
        bool sunrise,
        int offsetMinutes,
        out DateTime eventLocal,
        out DateTime eventUtc)
    {
        return TryGetEventLocal(
            localDate,
            timeZone,
            latitude,
            longitude,
            sunrise,
            offsetMinutes,
            SunriseSunsetZenithDegrees,
            out eventLocal,
            out eventUtc);
    }

    internal static bool TryGetCivilTwilightLocal(
        DateTime localDate,
        TimeZoneInfo timeZone,
        double latitude,
        double longitude,
        bool dawn,
        int offsetMinutes,
        out DateTime eventLocal,
        out DateTime eventUtc)
    {
        return TryGetEventLocal(
            localDate,
            timeZone,
            latitude,
            longitude,
            dawn,
            offsetMinutes,
            CivilTwilightZenithDegrees,
            out eventLocal,
            out eventUtc);
    }

    private static bool TryGetEventLocal(
        DateTime localDate,
        TimeZoneInfo timeZone,
        double latitude,
        double longitude,
        bool sunrise,
        int offsetMinutes,
        double zenithDegrees,
        out DateTime eventLocal,
        out DateTime eventUtc)
    {
        eventLocal = default;
        eventUtc = default;
        if (timeZone == null || !double.IsFinite(latitude) || !double.IsFinite(longitude))
            return false;

        // Use the UTC date containing local noon as the equation's day anchor. This
        // handles longitudes near the international date line without shifting sunrise
        // or sunset to the neighboring local calendar date.
        DateTime utcNoon;
        try
        {
            utcNoon = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(localDate.Date.AddHours(12), DateTimeKind.Unspecified),
                timeZone);
        }
        catch (ArgumentException)
        {
            return false;
        }

        // Sunrise and sunset can fall on opposite UTC dates from the local calendar
        // date (for example, New York sunset is after midnight UTC). Try the neighboring
        // UTC anchors and keep only the base event that resolves to the requested local
        // date before applying the user offset. The offset is allowed to cross local
        // midnight because the schedule date belongs to the unshifted solar event.
        for (var dayOffset = -1; dayOffset <= 1; dayOffset++)
        {
            if (!TryGetEventUtc(
                    utcNoon.Date.AddDays(dayOffset),
                    latitude,
                    longitude,
                    sunrise,
                    zenithDegrees,
                    out var calculatedUtc))
                continue;

            var baseEventUtc = DateTime.SpecifyKind(calculatedUtc, DateTimeKind.Utc);
            var baseEventLocal = DateTime.SpecifyKind(
                TimeZoneInfo.ConvertTimeFromUtc(baseEventUtc, timeZone),
                DateTimeKind.Unspecified);
            if (baseEventLocal.Date != localDate.Date)
                continue;

            var candidateUtc = DateTime.SpecifyKind(
                baseEventUtc.AddMinutes(offsetMinutes),
                DateTimeKind.Utc);
            var candidateLocal = DateTime.SpecifyKind(
                TimeZoneInfo.ConvertTimeFromUtc(candidateUtc, timeZone),
                DateTimeKind.Unspecified);
            eventUtc = candidateUtc;
            eventLocal = new DateTime(
                candidateLocal.Year,
                candidateLocal.Month,
                candidateLocal.Day,
                candidateLocal.Hour,
                candidateLocal.Minute,
                0,
                DateTimeKind.Unspecified);
            return true;
        }

        return false;
    }

    private static double SinDegrees(double degrees) => Math.Sin(ToRadians(degrees));

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double ToDegrees(double radians) => radians * 180.0 / Math.PI;

    private static double NormalizeDegrees(double degrees)
    {
        var normalized = degrees % 360.0;
        return normalized < 0 ? normalized + 360.0 : normalized;
    }

    private static double NormalizeHours(double hours)
    {
        var normalized = hours % 24.0;
        return normalized < 0 ? normalized + 24.0 : normalized;
    }
}
