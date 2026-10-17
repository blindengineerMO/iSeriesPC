using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ipc.Core.Messages;
using Ipc.Core.Text;
using Ipc.Core.Work;

namespace Ipc.Services.Messages;

public sealed record MessageReplyValue(string From, string? To = null);
public sealed record MessageReplyRange(string Lower, string Upper);
public sealed record MessageReplyRelation(string Operator, string Value);
// Length 0 selects *TYPE; -1 represents LEN(*NONE).
public sealed record MessageReplyRules(string Type = "*CHAR", int Length = 0, int Decimals = 0,
    IReadOnlyList<string>? Values = null, IReadOnlyList<MessageReplyValue>? SpecialValues = null,
    MessageReplyRange? Range = null, MessageReplyRelation? Relation = null)
{
    public MessageReplyRules Freeze() => this with {
        Values = Values is null ? null : Array.AsReadOnly(Values.ToArray()),
        SpecialValues = SpecialValues is null ? null : Array.AsReadOnly(SpecialValues.ToArray()) };

    private int Maximum(string? defaultReply) => Type switch {
        "*DEC" => 15, "*NAME" => 10,
        _ => Values?.Count > 0 || SpecialValues?.Count > 0 || Range is not null || Relation is not null || defaultReply is not null ? 32 : 132 };

    public void Validate(int ccsid, string? defaultReply)
    {
        if (Type is not ("*CHAR" or "*DEC" or "*ALPHA" or "*NAME" or "*NONE") ||
            Length < -1 || Length > Maximum(defaultReply) || (Type == "*NONE") != (Length == -1) ||
            Decimals < 0 || Decimals > 9 || (Type != "*DEC" && Decimals != 0) || Type == "*DEC" && Decimals > (Length == 0 ? 15 : Length) ||
            Values?.Count > 20 || SpecialValues?.Count > 20 ||
            (Values?.Count > 0 ? 1 : 0) + (Range is null ? 0 : 1) + (Relation is null ? 0 : 1) > 1)
            throw InvalidDefinition();
        if (Type == "*NONE")
        {
            if (Values?.Count > 0 || SpecialValues?.Count > 0 || Range is not null || Relation is not null || defaultReply is not null) throw InvalidDefinition();
            return;
        }
        try
        {
            foreach (var value in Values ?? Array.Empty<string>()) { Metadata(value, ccsid); Shape(value, ccsid, defaultReply); }
            var from = new HashSet<string>(StringComparer.Ordinal);
            foreach (var special in SpecialValues ?? Array.Empty<MessageReplyValue>())
            {
                if (special is null) throw InvalidDefinition();
                Metadata(special.From, ccsid); Metadata(special.To ?? special.From, ccsid);
                if (!from.Add(special.From.TrimEnd(' '))) throw InvalidDefinition();
                Shape(special.To ?? special.From, ccsid, defaultReply);
            }
            if (Range is { } range)
            {
                Metadata(range.Lower, ccsid); Metadata(range.Upper, ccsid);
                var lower = Shape(range.Lower, ccsid, defaultReply); var upper = Shape(range.Upper, ccsid, defaultReply);
                if (Compare(lower, upper, ccsid) > 0) throw InvalidDefinition();
            }
            if (Relation is { } relation)
            {
                if (relation.Operator is not ("*EQ" or "*NE" or "*LT" or "*LE" or "*GT" or "*GE" or "*NL" or "*NG")) throw InvalidDefinition();
                Metadata(relation.Value, ccsid); Shape(relation.Value, ccsid, defaultReply);
            }
            // Native defaults need only satisfy type and length, not VALUES/RANGE/REL.
            if (defaultReply is not null) Shape(defaultReply, ccsid, defaultReply);
        }
        catch (CpfException error) when (error.MessageId == "CPF2422") { throw InvalidDefinition(); }
    }

    public ProgramBuffer Check(ProgramBuffer reply, int ccsid, string? defaultReply)
    {
        if (Type == "*NONE") return reply;
        try
        {
            var text = reply.ToText().TrimEnd(' ');
            var special = SpecialValues?.FirstOrDefault(value => value.From.TrimEnd(' ') == text);
            text = Shape(special is null ? text : special.To ?? special.From, ccsid, defaultReply);
            if (special is null)
            {
                if (Values?.Count > 0 && !Values.Any(value => Compare(text, Shape(value, ccsid, defaultReply), ccsid) == 0)) throw InvalidReply();
                if (Range is { } range)
                {
                    var width = Math.Max(Encode(range.Lower, ccsid).Length, Encode(range.Upper, ccsid).Length);
                    if (Compare(text, Shape(range.Lower, ccsid, defaultReply), ccsid, width) < 0 ||
                        Compare(text, Shape(range.Upper, ccsid, defaultReply), ccsid, width) > 0) throw InvalidReply();
                }
                if (Relation is { } relation)
                {
                    var comparison = Compare(text, Shape(relation.Value, ccsid, defaultReply), ccsid, Encode(relation.Value, ccsid).Length);
                    var valid = relation.Operator switch {
                        "*EQ" => comparison == 0, "*NE" => comparison != 0, "*LT" => comparison < 0,
                        "*LE" or "*NG" => comparison <= 0, "*GT" => comparison > 0, _ => comparison >= 0 };
                    if (!valid) throw InvalidReply();
                }
            }
            return new(Encode(text, reply.Ccsid), reply.Ccsid);
        }
        catch (CpfException error) when (error.MessageId == "IPC0136") { throw InvalidReply(); }
    }

    private string Shape(string? value, int ccsid, string? defaultReply)
    {
        if (value is null || value.Any(char.IsControl)) throw InvalidReply();
        value = value.TrimEnd(' ');
        var length = Length == 0 ? Maximum(defaultReply) : Length;
        if (Type == "*DEC")
        {
            if (!Regex.IsMatch(value, @"\A[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)\z")) throw InvalidReply();
            var parts = value.TrimStart('+', '-').Split('.');
            var scale = Decimals;
            if (parts[0].TrimStart('0').Length > length - scale || parts.Length > 1 && parts[1].Length > scale ||
                !decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _)) throw InvalidReply();
        }
        else
        {
            if (Encode(value, ccsid).Length > length) throw InvalidReply();
            if (Type == "*ALPHA" && (value.Length == 0 || !value.All(char.IsAsciiLetter))) throw InvalidReply();
            if (Type == "*NAME")
            {
                if (value.Length == 0 || !char.IsAsciiLetter(value[0]) || !value.All(char.IsAsciiLetterOrDigit)) throw InvalidReply();
                if (value.All(char.IsAsciiLetter)) value = value.ToUpperInvariant();
            }
        }
        return value;
    }

    private int Compare(string left, string right, int ccsid, int? width = null)
    {
        if (Type == "*DEC") return decimal.Parse(left, CultureInfo.InvariantCulture).CompareTo(decimal.Parse(right, CultureInfo.InvariantCulture));
        var a = Encode(left, ccsid); var b = Encode(right, ccsid);
        var size = width is not null && Type is "*CHAR" or "*ALPHA" ? width.Value : Math.Max(a.Length, b.Length);
        var blank = Encode(" ", ccsid)[0];
        for (var index = 0; index < size; index++)
        {
            var comparison = (index < a.Length ? a[index] : blank).CompareTo(index < b.Length ? b[index] : blank);
            if (comparison != 0) return comparison;
        }
        return 0;
    }
    private static void Metadata(string? value, int ccsid)
    {
        if (value is null || value.Length > 32 || value.Any(char.IsControl) || Encode(value, ccsid).Length > 32) throw InvalidDefinition();
    }
    private static byte[] Encode(string text, int ccsid)
    {
        var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        try { return encoding.GetBytes(text); } catch (EncoderFallbackException) { throw InvalidReply(); }
    }
    private static CpfException InvalidDefinition() => new("IPC0127", "Invalid or incompatible message reply rules.");
    private static CpfException InvalidReply() => new("CPF2422", "Reply does not satisfy the message description.");
}

/// <summary>Applied under the message-file write transaction, preserving unspecified rules.</summary>
public sealed record MessageReplyUpdate(string? Type = null, int? Length = null, int? Decimals = null,
    IReadOnlyList<string>? Values = null, IReadOnlyList<MessageReplyValue>? SpecialValues = null,
    bool ReplaceRange = false, MessageReplyRange? Range = null, bool ReplaceRelation = false, MessageReplyRelation? Relation = null)
{
    public MessageReplyRules Apply(MessageReplyRules? previous)
    {
        var rules = previous ?? new();
        if (Type is not null && Type != rules.Type && Length is null)
            throw new CpfException("IPC0127", "Changing reply TYPE also requires LEN.");
        return rules with { Type = Type ?? rules.Type, Length = Length ?? rules.Length, Decimals = Decimals ?? rules.Decimals,
            Values = Values ?? rules.Values, SpecialValues = SpecialValues ?? rules.SpecialValues,
            Range = ReplaceRange ? Range : rules.Range, Relation = ReplaceRelation ? Relation : rules.Relation };
    }
}
