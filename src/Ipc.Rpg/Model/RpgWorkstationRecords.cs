namespace Ipc.Rpg.Model;

internal static class RpgWorkstationRecords
{
    internal static bool Input(RpgDisplayRecord record, RpgDisplayField field) => record.Subfile || field.Input;
    internal static bool Output(RpgDisplayRecord record, RpgDisplayField field) => record.Subfile || field.Output;
    internal static IReadOnlyList<RpgDisplayField> Fields(RpgDisplayRecord record, string extract) => record.Fields
        .Where(f => extract == "*ALL" || extract == "*INPUT" && Input(record, f) || extract == "*OUTPUT" && Output(record, f)).ToArray();
    internal static RpgFieldKind Kind(RpgDisplayField field) => field.Kind == RpgFieldKind.Packed ? RpgFieldKind.Zoned : field.Kind;
    internal static int Length(RpgDisplayRecord record, string extract) => Fields(record, extract).Sum(f => f.Length);
    internal static string Family(RpgWorkstationBinding file, RpgDisplayRecord record) => "WORKSTN:" + RpgWorkstationBindings.FamilyKey(file) + ":" + record.External;
}
