using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Html;

namespace MatBill.Localization;

/// <summary>
/// The texts the browser needs: the attributes of &lt;html&gt; that MatUi reads for its own dialogs and messages (lang, data-dlg-*, data-msg-*),
/// and every key that starts with <c>Js.</c> as a JSON block for <c>app.js</c> (read with <c>t('Js.Offline.Title')</c>; never executed, CSP stays strict).
/// </summary>
public static class ClientTexts
{
    public static IHtmlContent RootAttributes()
    {
        var attributes = new (string Name, string Key)[]
        {
            ("data-dlg-ok", "Common.Dialog.Ok"), ("data-dlg-cancel", "Common.Dialog.Cancel"), ("data-dlg-close", "Common.Dialog.Close"),
            ("data-dlg-confirm", "Common.Dialog.Confirm"), ("data-dlg-notice", "Common.Dialog.Notice"), ("data-dlg-input", "Common.Dialog.Input"),
            ("data-msg-required", "Common.Validation.Required"), ("data-msg-type", "Common.Validation.Type"), ("data-msg-pattern", "Common.Validation.Pattern"),
            ("data-copied", "Common.Copied"),
        };
        var html = new StringBuilder($"lang=\"{Languages.Current}\" data-locale=\"{Languages.FormatCulture.Name}\"");
        foreach (var (name, key) in attributes)
        {
            html.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(Text.T(key))).Append('"');
        }

        return new HtmlString(html.ToString());
    }

    public static IHtmlContent Script()
    {
        var language = Languages.Current;
        var texts = TextCatalog.Entries.Where(e => e.Key.StartsWith("Js.", StringComparison.Ordinal))
            .ToDictionary(e => e.Key, e => TextCatalog.Find(e.Key, language) ?? e.Key, StringComparer.Ordinal);

        // "<" is written as < by the serializer, so the block cannot close itself.
        return new HtmlString($"<script type=\"application/json\" id=\"mb-texts\">{JsonSerializer.Serialize(texts)}</script>");
    }
}
