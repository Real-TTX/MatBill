using MatBill.Configuration;
using MatBill.Services;

namespace MatBill.Pages;

public enum EditorMode
{
    /// <summary>A draft invoice: number on issue, a date, a service date.</summary>
    Invoice,
    /// <summary>The template of a series: number and dates are set by each run.</summary>
    Template,

    /// <summary>A draft quote: a number of its own range on issue, valid for a number of days.</summary>
    Quote,
}

/// <summary>What the editable document (<c>_DocumentEditor</c>) draws: the fields as the form holds them and the frame around them (branding, number).</summary>
public sealed class DocumentEditorModel
{
    public required DocumentInput Input { get; init; }

    public required EditorMode Mode { get; init; }

    public required CompanyConfig Company { get; init; }

    public required InvoicingConfig Invoicing { get; init; }

    /// <summary>The number the invoice will get when it is issued (a preview, nothing is taken).</summary>
    public string? NumberPreview { get; init; }

    public string? LogoUrl { get; init; }

    public bool CanEditBranding { get; init; }

    /// <summary>"Entwurf", "Vorlage": the faint stamp across the page.</summary>
    public string? Stamp { get; init; }

    public bool TaxExempt => Company.SmallBusiness;

    /// <summary>The template of a series states the period of each invoice.</summary>
    public bool StatePeriod { get; init; } = true;
}

/// <summary>What the read-only document (<c>_DocumentView</c>) draws: an issued invoice, a shared one.</summary>
public sealed record DocumentViewModel(InvoiceDocument Doc, CompanyConfig Company, string? LogoUrl);

public static class Brand
{
    /// <summary>The address of the logo of the company, or null when there is none.</summary>
    public static string? LogoUrl(CompanyConfig company) => Guid.TryParse(company.LogoToken, out var token) ? $"/branding/logo/{token}" : null;
}
