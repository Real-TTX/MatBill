namespace MatBill.Core.Billing;

/// <summary>One position as the arithmetic sees it: net unit price, quantity and the tax rate in percent (19, 7, 0).</summary>
public readonly record struct LineAmounts(decimal Quantity, decimal UnitPrice, decimal TaxRate);

/// <summary>The net amount and the tax of all positions with one tax rate.</summary>
public sealed record TaxGroup(decimal Rate, decimal Net, decimal Tax);

public sealed record InvoiceTotals(decimal Net, IReadOnlyList<TaxGroup> Taxes, decimal Tax, decimal Gross)
{
    public static InvoiceTotals Empty { get; } = new(0m, [], 0m, 0m);
}

/// <summary>
/// The arithmetic of an invoice. Prices are net; a position is quantity times unit price, rounded to cents; the tax is worked out once per tax rate on the
/// sum of its positions (the way German invoices show it: "Umsatzsteuer 19 % auf 1.500,00 €"), so the shown numbers always add up.
/// </summary>
public static class InvoiceMath
{
    public static readonly decimal[] CommonTaxRates = [19m, 7m, 0m];

    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    public static decimal LineNet(decimal quantity, decimal unitPrice) => Round(quantity * unitPrice);

    /// <summary>The totals of <paramref name="lines"/>. With <paramref name="taxExempt"/> (small business, § 19 UStG) no tax is charged at all.</summary>
    public static InvoiceTotals Totals(IEnumerable<LineAmounts> lines, bool taxExempt = false)
    {
        var groups = lines
            .GroupBy(l => taxExempt ? 0m : l.TaxRate)
            .Select(g =>
            {
                var net = g.Sum(l => LineNet(l.Quantity, l.UnitPrice));
                return new TaxGroup(g.Key, net, Round(net * g.Key / 100m));
            })
            .OrderByDescending(g => g.Rate)
            .ToList();

        var netTotal = groups.Sum(g => g.Net);
        var tax = groups.Sum(g => g.Tax);
        return new InvoiceTotals(netTotal, groups, tax, netTotal + tax);
    }

    /// <summary>The due date: the invoice date plus the days of the payment term.</summary>
    public static DateOnly DueDate(DateOnly invoiceDate, int paymentTermDays) => invoiceDate.AddDays(Math.Max(0, paymentTermDays));
}
