namespace MatBill.Core.Billing;

public enum RecurrenceInterval
{
    Weekly,
    Monthly,
    Quarterly,
    HalfYearly,
    Yearly,
}

/// <summary>
/// The dates of a series of invoices. Every date is counted from the start date (the anchor), never from the previous date: a series that starts on
/// 31 January runs on 28 February, 31 March, 30 April ... and does not drift to the 28th for good.
/// </summary>
public static class Recurrence
{
    /// <summary>The <paramref name="index"/>th date of the series (0 is the start date itself).</summary>
    public static DateOnly Occurrence(DateOnly anchor, RecurrenceInterval interval, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (interval == RecurrenceInterval.Weekly)
        {
            return anchor.AddDays(7 * index);
        }

        // AddMonths keeps the day where the month has it and takes the last day where it has not.
        return anchor.AddMonths(Months(interval) * index);
    }

    /// <summary>The first date of the series that is on or after <paramref name="date"/>.</summary>
    public static DateOnly OnOrAfter(DateOnly anchor, RecurrenceInterval interval, DateOnly date)
    {
        if (date <= anchor)
        {
            return anchor;
        }

        // An estimate of the index that is never too high, then step forward: at most a step or two.
        var index = interval == RecurrenceInterval.Weekly
            ? (date.DayNumber - anchor.DayNumber) / 7
            : Math.Max(0, (((date.Year - anchor.Year) * 12) + date.Month - anchor.Month - 1) / Months(interval));
        while (Occurrence(anchor, interval, index) < date)
        {
            index++;
        }

        return Occurrence(anchor, interval, index);
    }

    /// <summary>The date of the series that follows <paramref name="date"/> (strictly after it).</summary>
    public static DateOnly After(DateOnly anchor, RecurrenceInterval interval, DateOnly date) => OnOrAfter(anchor, interval, date.AddDays(1));

    /// <summary>The period an invoice of the series covers: from its date to the day before the next one.</summary>
    public static (DateOnly From, DateOnly To) Period(DateOnly anchor, RecurrenceInterval interval, DateOnly date)
        => (date, After(anchor, interval, date).AddDays(-1));

    /// <summary>The next <paramref name="count"/> dates from <paramref name="from"/> on, up to <paramref name="end"/> if there is one.</summary>
    public static IReadOnlyList<DateOnly> Upcoming(DateOnly anchor, RecurrenceInterval interval, DateOnly from, int count, DateOnly? end = null)
    {
        var result = new List<DateOnly>();
        var next = OnOrAfter(anchor, interval, from);
        while (result.Count < count && (end is null || next <= end))
        {
            result.Add(next);
            next = After(anchor, interval, next);
        }

        return result;
    }

    private static int Months(RecurrenceInterval interval) => interval switch
    {
        RecurrenceInterval.Monthly => 1,
        RecurrenceInterval.Quarterly => 3,
        RecurrenceInterval.HalfYearly => 6,
        RecurrenceInterval.Yearly => 12,
        _ => throw new ArgumentOutOfRangeException(nameof(interval)),
    };
}
