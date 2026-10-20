using System.Buffers;
using System.Text.Json;

namespace Ipc.Rpg.Model;

public sealed partial record RpgWorkstationBindings
{
    private sealed class BoundedMetadata : IBufferWriter<byte>
    {
        private const int Limit = 1048576;
        private readonly byte[] _bytes = new byte[Limit + 16384];
        private int _written;
        internal ReadOnlySpan<byte> Written => _bytes.AsSpan(0, _written);
        public void Advance(int count)
        { if (count < 0 || count > Limit - _written) throw Invalid(); _written += count; }
        public Memory<byte> GetMemory(int sizeHint = 0)
        { if (sizeHint < 0 || sizeHint > _bytes.Length - _written) throw Invalid(); return _bytes.AsMemory(_written); }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
    private static bool Extended(RpgWorkstationBinding file) => file.Request.IsTemplate || file.Request.Qualified || file.Request.LikeFile is not null || file.Family is not null || file.Request.IsParameter;
    public static string RecordName(RpgWorkstationRequest request, string name) => request.Qualified ? request.Name + "." + name : name;
    private static bool RecordNameValid(RpgWorkstationRequest request, string name) => request.Qualified
        ? name.StartsWith(request.Name + ".", StringComparison.Ordinal) && Name(name[(request.Name.Length + 1)..]) : Name(name);
    public static string FamilyKey(RpgWorkstationBinding file) => file.Family ?? (file.Request.Procedure is null ? "" : file.Request.Procedure + "/") + file.Request.Name + ":" + file.Layout.Signature;
    public static RpgWorkstationLayout Derive(RpgWorkstationRequest request, RpgWorkstationLayout parent)
    {
        string NameFor(string external) => RecordName(request, request.Renames.SingleOrDefault(r => r.External == external)?.Name ?? external);
        var records = parent.Records.Select(r => r with { Name = NameFor(r.External),
            ControlFor = r.ControlFor is null ? null : NameFor(parent.Records.Single(s => s.Name == r.ControlFor).External) }).ToArray();
        return parent with { Records = records, Signature = parent.IndicatorArea is { } area
            ? RpgWorkstationLayout.SignatureFor(records, area) : RpgWorkstationLayout.SignatureFor(records) };
    }
    private static void ValidateFamilies(IReadOnlyList<RpgWorkstationBinding> files)
    {
        if (files.Count > 4112 || files.GroupBy(f => f.Request.Procedure).Any(g => g.Count() > 16) || files.Select(f => f.Request.Procedure).Where(p => p is not null).Distinct().Count() > 256 ||
            files.Select(f => (f.Request.Procedure, f.Request.Name)).Distinct().Count() != files.Count) throw Invalid();
        foreach (var file in files)
        {
            if (file.Request.LikeFile is not null)
            {
                var seen = new HashSet<RpgWorkstationBinding> { file }; var parent = file;
                while (parent.Request.LikeFile is { } name)
                {
                    var local = files.SingleOrDefault(f => f.Request.Name == name && f.Request.Procedure == parent.Request.Procedure);
                    var global = files.SingleOrDefault(f => f.Request.Name == name && f.Request.Procedure is null);
                    var next = local is not null && FamilyKey(local) == file.Family ? local : global;
                    if (next is null || !seen.Add(next)) throw Invalid(); parent = next;
                }
                if (file.Family != FamilyKey(parent) ||
                    file.Request.Input != parent.Request.Input || file.Request.Output != parent.Request.Output ||
                    file.Request.DescriptionFile != parent.Request.DescriptionFile || file.Request.Prefix != parent.Request.Prefix ||
                    JsonSerializer.Serialize(file.Request.Renames, Json) != JsonSerializer.Serialize(parent.Request.Renames, Json) ||
                    JsonSerializer.Serialize(file.Layout, Json) != JsonSerializer.Serialize(Derive(file.Request, parent.Layout), Json)) throw Invalid();
            }
        }
    }
    private static IReadOnlyList<RpgWorkstationBinding> ProgramFiles(RpgProgram program) => program.Workstations.Concat(
        program.Subprocedures.Where(p => p.NativeBody is not null).SelectMany(p => p.NativeBody!.Workstations.Where(f => f.Request.Procedure == p.Name && !f.Request.IsParameter))).ToArray();
    public void Verify(RpgProgram program)
    {
        var files = ProgramFiles(program);
        if (Files.Count != files.Count) throw Invalid();
        foreach (var file in files)
        {
            var stored = Files.SingleOrDefault(f => f.Request.Name == file.Request.Name && f.Request.Procedure == file.Request.Procedure) ?? throw Invalid();
            if (JsonSerializer.Serialize(stored, Json) != JsonSerializer.Serialize(file, Json)) throw Invalid();
        }
    }
}
