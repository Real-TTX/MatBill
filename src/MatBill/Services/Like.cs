namespace MatBill.Services;

/// <summary>The pattern for a "contains" search with ILIKE: the characters that mean something to LIKE are taken literally.</summary>
public static class Like
{
    public static string Contains(string text)
        => "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
