using System.Text.Json;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static Func<RpgWorkstationRequest, RpgWorkstationLayout>? CachedWorkstationResolver(Func<RpgWorkstationRequest, RpgWorkstationLayout>? resolver)
    {
        if (resolver is null) return null;
        var cache = new Dictionary<string, RpgWorkstationLayout>(StringComparer.Ordinal);
        return request => { var key = JsonSerializer.Serialize(request); if (!cache.TryGetValue(key, out var value)) cache.Add(key, value = resolver(request)); return value; };
    }
    private static IReadOnlyList<RpgWorkstationBinding> RecordWorkstations(RpgProgram program) =>
        (program.WorkstationLayoutsLookup?.Invoke() ?? []).Concat(program.DeclarationWorkstations).GroupBy(f => f.Request.Name).Select(g => g.Last()).ToArray();
    private static RpgDataStructure WorkstationRecordTemplate(RpgProgram program, string[] parts, int line, RpgWorkstationBinding? parameter = null, bool nullMap = false)
    {
        var file = parameter ?? RecordWorkstations(program).Where(f => f.Layout.Records.Any(r => r.Name == parts[0]))
            .OrderByDescending(f => f.Request.Procedure == program.NativeProcedureName && f.Request.Procedure is not null).FirstOrDefault();
        if (file is null || !file.Layout.Records.Any(r => r.Name == parts[0])) throw new RpgCompileException(line, "LIKEREC requires a declared externally described DISK or WORKSTN record: " + parts[0] + ".");
        var record = file.Layout.Records.Single(r => r.Name == parts[0]); var extract = parts.Length == 1 ? "*INPUT" : parts[1];
        if (extract == "*KEY" || extract == "*INPUT" && !file.Request.Input || extract == "*OUTPUT" && !file.Request.Output)
            throw new RpgCompileException(line, "LIKEREC extract is incompatible with the WORKSTN record.");
        var owner = program; while (owner.GlobalProgram is { } globals) owner = globals;
        var family = RpgWorkstationRecords.Family(file, record); var key = family + ":" + extract + ":" + nullMap;
        if (owner.RecordStructures.TryGetValue(key, out var cached)) return cached;
        var length = nullMap ? RpgWorkstationRecords.Fields(record, extract).Count : RpgWorkstationRecords.Length(record, extract);
        if (length is < 1 or > 1048576) throw new RpgCompileException(line, "WORKSTN LIKEREC requires 1 through 1048576 bytes of fields.");
        var result = new RpgDataStructure { Name = "\u0001RECORD", NativeStorage = true, Qualified = true, TypeOrigin = record, Length = length,
            RecordOrigin = new(file.Request.Name, record.Name, file.Layout.Signature, extract, parts.Length == 1, family, NullMap: nullMap) };
        var offset = 0;
        foreach (var field in RpgWorkstationRecords.Fields(record, extract))
        {
            result.Elements.Add(new() { Name = field.Name, LocalName = field.Name, ExternalName = field.External, Kind = nullMap ? RpgFieldKind.Indicator : RpgWorkstationRecords.Kind(field), Length = nullMap ? 1 : field.Length,
                Decimals = nullMap ? 0 : field.Decimals, Offset = offset, StorageLength = nullMap ? 1 : field.Length, Stride = nullMap ? 1 : field.Length, LikeType = field, OpaqueBytes = !nullMap && field.Opaque });
            offset += nullMap ? 1 : field.Length;
        }
        owner.RecordStructures.Add(key, result); return result;
    }
    internal static bool WorkstationRecordMatches(RpgRecordOrigin origin, RpgWorkstationBinding file, RpgDisplayRecord record, RpgOpcode opcode) =>
        !origin.NullMap && (origin.External is not null ? ExternalWorkstationMatches(origin, file, record) : origin.Family == RpgWorkstationRecords.Family(file, record)) && (opcode == RpgOpcode.ExFmt ? origin.Extract == "*ALL" :
            opcode is RpgOpcode.Write or RpgOpcode.Update ? origin.Extract is "*OUTPUT" or "*ALL" : origin.Extract is "*INPUT" or "*ALL");
    private static RpgDisplayRecord? WorkstationResultRecord(RpgWorkstationBinding file, string name) =>
        file.Layout.Records.SingleOrDefault(r => r.Name == name) ?? (file.Request.Name == name && file.Layout.Records.Count == 1 ? file.Layout.Records[0] : null);
    private static void ValidateWorkstationRecordIo(RpgProgram program, RpgStatement statement, RpgWorkstationBinding file, string name)
    {
        var record = WorkstationResultRecord(file, name);
        var text = statement.RecordDataStructure!;
        RpgExpr expression;
        try { expression = RpgExpressionParser.Parse(text); }
        catch (RpgCompileException error) { throw new RpgCompileException(statement.LineNumber, error.Message); }
        var actual = ExactActualType(program, expression);
        var structure = actual?.ParameterStructure;
        if (record is null || structure is not { NativeStorage: true, IsTemplate: false } || actual!.IsArray || structure.RecordOrigin is not { } origin ||
            !WorkstationRecordMatches(origin, file, record, statement.Opcode) || structure.Length != RpgWorkstationRecords.Length(record, origin.Extract))
            throw new RpgCompileException(statement.LineNumber, "A WORKSTN result requires a matching LIKEREC record and extract; arrays require one element, and file-name input requires one format.");
        if (statement.Opcode is RpgOpcode.ExFmt or RpgOpcode.Read or RpgOpcode.ReadC or RpgOpcode.Chain && actual!.ReadOnlyParameter)
            throw new RpgCompileException(statement.LineNumber, "A CONST parameter cannot receive WORKSTN input.");
    }
}
