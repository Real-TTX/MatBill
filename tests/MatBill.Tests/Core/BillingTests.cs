using MatBill.Core.Billing;

namespace MatBill.Tests.Core;

public sealed class InvoiceMathTests
{
    [Fact]
    public void The_tax_is_worked_out_once_per_rate_on_the_sum_of_its_positions()
    {
        var totals = InvoiceMath.Totals([new(1, 1200m, 19m), new(2, 150m, 19m), new(1, 39.90m, 7m)]);
        Assert.Equal(1539.90m, totals.Net);
        Assert.Equal(2, totals.Taxes.Count);
        Assert.Equal(285.00m, totals.Taxes[0].Tax);
        Assert.Equal(2.79m, totals.Taxes[1].Tax);
        Assert.Equal(1827.69m, totals.Gross);
    }

    [Fact]
    public void A_position_is_rounded_to_cents_away_from_zero()
    {
        Assert.Equal(0.13m, InvoiceMath.LineNet(1, 0.125m));
        Assert.Equal(3.38m, InvoiceMath.LineNet(0.25m, 13.5m));
    }

    [Fact]
    public void A_small_business_charges_no_tax()
    {
        var totals = InvoiceMath.Totals([new(1, 100m, 19m), new(1, 50m, 7m)], taxExempt: true);
        Assert.Equal(150m, totals.Gross);
        Assert.Equal(0m, totals.Tax);
    }

    [Fact]
    public void The_due_date_is_the_invoice_date_plus_the_term()
        => Assert.Equal(new DateOnly(2026, 10, 31), InvoiceMath.DueDate(new DateOnly(2026, 10, 17), 14));
}

public sealed class RecurrenceTests
{
    [Fact]
    public void A_series_that_starts_on_the_31st_runs_on_the_last_day_and_comes_back_to_the_31st()
    {
        var start = new DateOnly(2026, 1, 31);
        Assert.Equal(new DateOnly(2026, 2, 28), Recurrence.Occurrence(start, RecurrenceInterval.Monthly, 1));
        Assert.Equal(new DateOnly(2026, 3, 31), Recurrence.Occurrence(start, RecurrenceInterval.Monthly, 2));
        Assert.Equal(new DateOnly(2026, 3, 31), Recurrence.After(start, RecurrenceInterval.Monthly, new DateOnly(2026, 2, 28)));
    }

    [Theory]
    [InlineData(RecurrenceInterval.Weekly, "2026-10-24")]
    [InlineData(RecurrenceInterval.Monthly, "2026-11-17")]
    [InlineData(RecurrenceInterval.Quarterly, "2027-01-17")]
    [InlineData(RecurrenceInterval.HalfYearly, "2027-04-17")]
    [InlineData(RecurrenceInterval.Yearly, "2027-10-17")]
    public void Every_interval_has_its_next_date(RecurrenceInterval interval, string expected)
        => Assert.Equal(DateOnly.Parse(expected), Recurrence.After(new DateOnly(2026, 10, 17), interval, new DateOnly(2026, 10, 17)));

    [Fact]
    public void The_next_date_on_or_after_a_day_is_found_from_the_anchor()
    {
        var start = new DateOnly(2025, 3, 15);
        Assert.Equal(new DateOnly(2026, 10, 15), Recurrence.OnOrAfter(start, RecurrenceInterval.Monthly, new DateOnly(2026, 10, 15)));
        Assert.Equal(new DateOnly(2026, 11, 15), Recurrence.OnOrAfter(start, RecurrenceInterval.Monthly, new DateOnly(2026, 10, 16)));
        Assert.Equal(start, Recurrence.OnOrAfter(start, RecurrenceInterval.Monthly, new DateOnly(2020, 1, 1)));
    }

    [Fact]
    public void The_period_ends_the_day_before_the_next_date_and_upcoming_stops_at_the_end()
    {
        var start = new DateOnly(2026, 11, 1);
        Assert.Equal((start, new DateOnly(2026, 11, 30)), Recurrence.Period(start, RecurrenceInterval.Monthly, start));
        var dates = Recurrence.Upcoming(start, RecurrenceInterval.Monthly, start, 10, new DateOnly(2027, 1, 31));
        Assert.Equal(3, dates.Count);
    }
}

public sealed class NumberPatternTests
{
    [Fact]
    public void Numbers_are_formatted_with_year_month_and_counter()
    {
        Assert.Equal("RE-2026-0042", NumberPattern.Format("RE-{JJJJ}-{NNNN}", new DateOnly(2026, 10, 17), 42));
        Assert.Equal("26/10/007", NumberPattern.Format("{JJ}/{MM}/{NNN}", new DateOnly(2026, 10, 17), 7));
        Assert.Equal("K-12345", NumberPattern.Format("K-{NNNN}", new DateOnly(2026, 1, 1), 12345));
    }

    [Fact]
    public void The_counter_starts_again_in_the_period_of_the_pattern()
    {
        var d = new DateOnly(2026, 10, 17);
        Assert.Equal("2026", NumberPattern.PeriodKey("RE-{JJJJ}-{NNNN}", d));
        Assert.Equal("2026-10", NumberPattern.PeriodKey("{JJJJ}{MM}-{NNN}", d));
        Assert.Equal("", NumberPattern.PeriodKey("K-{NNNN}", d));
    }

    [Theory]
    [InlineData("RE-{JJJJ}-{NNNN}", true)]
    [InlineData("RE-{JJJJ}", false)]
    [InlineData("{NN}-{NN}", false)]
    [InlineData("RE-{XYZ}-{NNNN}", false)]
    [InlineData("", false)]
    public void Only_patterns_with_one_counter_and_known_placeholders_are_accepted(string pattern, bool ok)
        => Assert.Equal(ok, NumberPattern.Problem(pattern) is null);
}

public sealed class AmountTests
{
    [Theory]
    [InlineData("1.200,50", 1200.50)]
    [InlineData("1200,5", 1200.5)]
    [InlineData("1 200,50 €", 1200.50)]
    [InlineData("12.5", 12.5)]
    [InlineData("1.200", 1200)]
    [InlineData("1,200.75", 1200.75)]
    [InlineData("0.125", 0.125)]
    [InlineData("-3,5", -3.5)]
    public void Amounts_are_read_as_people_type_them(string text, double expected)
    {
        Assert.True(Amount.TryParse(text, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1,2,3")]
    public void What_is_no_amount_is_refused(string text) => Assert.False(Amount.TryParse(text, out _));

    [Fact]
    public void Amounts_are_written_the_German_way()
    {
        Assert.Equal("1.200,00", Amount.Edit(1200m));
        Assert.Equal("1,5", Amount.Quantity(1.5m));
    }
}
