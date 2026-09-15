using System.Globalization;

namespace Ipc.Dsp;

internal static class NumericEditWord
{
    public static bool IsSupported(string word, int digits)
    {
        if (word.Length is < 1 or > 132 || word.Any(c => c is not (' ' or '0' or '.' or ',' or '/' or '-' or '&')) || word.Count(c => c == '0') > 1) return false;
        var slots = word.Count(c => c is ' ' or '0');
        if (slots != digits && !(word[0] == '0' && slots == digits + 1)) return false;
        var minus = word.IndexOf('-');
        return minus < 0 || minus == word.Length - 1;
    }
    public static string Format(decimal number, int length, int decimals, string word)
    {
        if (!IsSupported(word, length)) throw new ArgumentException("Unsupported numeric edit word.");
        var digits = Math.Abs(number).ToString("F" + decimals, CultureInfo.InvariantCulture).Replace(".", "");
        if (digits.Length > length) throw new ArgumentException("Number exceeds edit-word precision.");
        digits = digits.PadLeft(length, '0');
        var result = word.ToCharArray(); var index = 0; var suppress = true;
        var firstIsControl = word[0] == '0' && word.Count(c => c == ' ') == length;
        for (var i = 0; i < word.Length; i++)
        {
            if (i == 0 && firstIsControl) { result[i] = ' '; suppress = false; continue; }
            if (word[i] is ' ' or '0')
            {
                var digit = digits[index++];
                if (digit != '0') suppress = false;
                result[i] = suppress ? ' ' : digit;
                if (word[i] == '0') suppress = false;
            }
            else if (word[i] == '&') result[i] = ' ';
            else if (word[i] == '-') result[i] = number < 0 ? '-' : ' ';
            else if (suppress) result[i] = ' ';
        }
        return new string(result);
    }
}
