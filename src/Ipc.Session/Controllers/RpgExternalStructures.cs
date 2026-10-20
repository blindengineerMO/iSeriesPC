using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Dsp;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private RpgExternalLayout DescribeRpgExternal(RpgExternalRequest request)
    {
        var overridden = _job is null ? null : EnvironmentForJob().Resolve(request.File.Split('/')[^1]);
        var (library, name) = overridden is null ? ResolveWorkObject(request.File, ObjectType.File) : (overridden.Library, overridden.Name);
        var definition = _files.GetDefinition(library, name);
        if (definition is not null)
        {
            var format = request.Format is null ? definition.PrimaryFormat : definition.Formats.SingleOrDefault(f => f.Name == request.Format)
                ?? throw new CpfException("CPF4131", "EXTNAME references an unavailable database record format.");
            var selected = new FileDefinition { Name = definition.Name, Attribute = definition.Attribute, Formats = [format] };
            var layout = RpgDiskLayouts.Describe(new("EXTERNAL", library + "/" + name, "*EXTDESC", format.Fields.Any(f => f.Sequence > 0), true, [], Output: true), library, name, selected);
            var fields = layout.Fields.Select(f => new RpgExternalField(f.External, f.Kind, f.Length, f.Decimals, f.StorageLength, f.Position,
                f.KeySequence, DefaultValue: format.Find(f.External)?.DefaultValue?.Value, CurrentDatetimeDefault: format.Find(f.External)?.CurrentDatetimeDefault == true, NullCapable: f.NullCapable)).ToArray();
            return new(library + "/" + name, format.Name, "DISK", layout.RecordLength, fields);
        }
        var panel = new DisplayFileStore(_system.Connections, ResolveDisplayMessage).Load(library, name);
        var record = request.Format is null ? panel.Records.First() : panel.Records.SingleOrDefault(r => r.Name == request.Format)
            ?? throw new CpfException("CPF4131", "EXTNAME references an unavailable display record format.");
        var subfile = record.Keywords.Any(k => k.Name == "SFL");
        var offset = 0; var result = new List<RpgExternalField>();
        foreach (var field in record.Fields.Where(f => !f.Constant))
        {
            if (field.Keywords.Any(k => k.Name == "SFLMSGKEY")) throw new CpfException("CPF4131", "EXTNAME opaque message keys require a remaining external layout contract.");
            var kind = field.Type switch { PanelFieldType.Numeric => RpgFieldKind.Zoned, PanelFieldType.Date => RpgFieldKind.Date,
                PanelFieldType.Time => RpgFieldKind.Time, PanelFieldType.Timestamp => RpgFieldKind.Timestamp, _ => RpgFieldKind.Character };
            var length = kind switch { RpgFieldKind.Date => 10, RpgFieldKind.Time => 8, RpgFieldKind.Timestamp => 26, _ => field.Length };
            result.Add(new(field.Name, kind, length, field.Decimals, length, offset + 1,
                Input: subfile || field.Usage is PanelFieldUsage.Input or PanelFieldUsage.Both,
                Output: subfile || field.Usage is PanelFieldUsage.Output or PanelFieldUsage.Both or PanelFieldUsage.Hidden,
                DefaultValue: field.Keywords.FirstOrDefault(k => k.Name == "DFT")?.Arguments.FirstOrDefault()));
            offset += length;
        }
        return new(library + "/" + name, record.Name, "WORKSTN", offset, result);
    }
}
