using System.Globalization;
using System.Text;

namespace InvoiceFlow.Application.Matching;

/// <summary>
/// Text normalization for fuzzy matching (names, descriptions) in English and Arabic.
/// Not used for exact comparisons. Those use the values as-is.
/// </summary>
public static class MatchText
{
    private static readonly HashSet<string> LegalNoise = new(StringComparer.Ordinal)
    {
        // English legal forms
        "LLC", "L", "C", "FZE", "FZCO", "FZC", "FZLLC", "CO", "COMPANY", "LTD", "LIMITED", "INC", "PJSC", "PSC", "EST", "ESTABLISHMENT",
        // Arabic legal forms (after normalization below; dots removed → letters)
        "ذمم", "شمح", "ممح", "شمع", "شركه", "مؤسسه", "موسسه",
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "THE", "AND", "OF", "FOR", "PER", "WITH", "A", "AN", "&",
        "و", "في", "من", "الى", "على", "ال",
    };

    /// <summary>Upper-case tokens with Arabic letter variants unified, diacritics removed, legal forms dropped.</summary>
    public static IReadOnlyList<string> Tokens(string? text, bool dropLegalForms = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var normalized = NormalizeArabic(text.Normalize(NormalizationForm.FormKC));
        var tokens = new List<string>();
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length == 0)
            {
                return;
            }

            var t = sb.ToString();
            sb.Clear();
            if (StopWords.Contains(t) || (dropLegalForms && LegalNoise.Contains(t)))
            {
                return;
            }

            tokens.Add(t);
        }

        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
            else if (c is '.' or '\'' or '’')
            {
                // "L.L.C" → "LLC", "ذ.م.م" → "ذمم"
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return tokens;
    }

    /// <summary>Jaccard similarity of token sets, 0..1.</summary>
    public static double Similarity(string? a, string? b, bool dropLegalForms = false)
    {
        var ta = Tokens(a, dropLegalForms).ToHashSet(StringComparer.Ordinal);
        var tb = Tokens(b, dropLegalForms).ToHashSet(StringComparer.Ordinal);
        if (ta.Count == 0 || tb.Count == 0)
        {
            return 0;
        }

        var intersection = ta.Count(tb.Contains);
        return (double)intersection / (ta.Count + tb.Count - intersection);
    }

    /// <summary>
    /// Share of <paramref name="reference"/>'s tokens that appear in <paramref name="text"/>, 0..1.
    /// Good for "does this invoice line describe that PO line?": extra words or a second language on the
    /// invoice don't lower the score.
    /// </summary>
    public static double Containment(string? text, string? reference)
    {
        var tt = Tokens(text).ToHashSet(StringComparer.Ordinal);
        var tr = Tokens(reference).ToHashSet(StringComparer.Ordinal);
        if (tt.Count == 0 || tr.Count == 0)
        {
            return 0;
        }

        return (double)tr.Count(tt.Contains) / tr.Count;
    }

    private static string NormalizeArabic(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            // Drop harakat / tatweel.
            if (c is >= 'ً' and <= 'ٟ' or 'ٰ' or 'ـ')
            {
                continue;
            }

            sb.Append(c switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ة' => 'ه',
                'ى' => 'ي',
                'ؤ' => 'و',
                'ئ' => 'ي',
                >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
                _ => c,
            });
        }

        // Arabic definite article "ال" attached to words: strip so "الموجة" matches "موجة".
        var words = sb.ToString().Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 3 && words[i].StartsWith("ال", StringComparison.Ordinal))
            {
                words[i] = words[i][2..];
            }
        }

        return string.Join(' ', words).ToUpper(CultureInfo.InvariantCulture);
    }
}
