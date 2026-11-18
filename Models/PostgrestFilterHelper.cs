namespace C_LabTap.Models;

// PostgREST's filter syntax uses commas to separate conditions inside
// or()/and() and parentheses to group them. A raw search term that
// happens to contain one of those characters can break out of the
// single ilike condition it was meant to stay inside, turning into an
// extra filter clause instead of plain text to search for (see chat:
// "serach for more before i commit" - found while hardening the 3
// admin search boxes). Legitimate names/codes/LRNs never contain these
// characters, so stripping them costs nothing real and closes the gap.
public static class PostgrestFilterHelper
{
    public static string StripReservedChars(string value) =>
        value.Replace(",", "").Replace("(", "").Replace(")", "");
}
