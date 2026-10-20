using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static void BindDisks(RpgProgram program, IReadOnlyList<WorkstationDeclaration> declarations,
        Func<RpgDiskRequest, RpgDiskLayout>? resolver)
    {
        foreach (var declaration in declarations)
        {
            if (program.DiskFiles.Count >= 16) throw new RpgCompileException(declaration.Line, "At most 16 visible DISK files are supported.");
            var request = DiskRequest(declaration);
            try
            {
                var parent = request.LikeFile is null ? null : program.DiskFiles.SingleOrDefault(f => f.Request.Name == request.LikeFile)
                    ?? program.GlobalProgram?.DiskFiles.SingleOrDefault(f => f.Request.Name == request.LikeFile)
                    ?? throw new RpgCompileException(declaration.Line, "LIKEFILE parent metadata is unavailable.");
                RpgDiskLayout layout;
                if (parent is not null)
                {
                    var record = RpgDiskBindings.RecordName(request, request.Renames.SingleOrDefault()?.Name ?? parent.Layout.ExternalRecord);
                    layout = parent.Layout with { Record = record, Signature = RpgDiskLayout.SignatureFor(parent.Layout.ExternalRecord, record,
                        parent.Layout.RecordLength, parent.Layout.BufferLayoutVersion, parent.Layout.Fields) };
                }
                else layout = resolver?.Invoke(request) ?? throw new RpgCompileException(declaration.Line, "DISK requires a compile-time database resolver.");
                var binding = new RpgDiskBinding(request, layout, parent is null ? null : RpgDiskBindings.FamilyKey(parent)); RpgDiskBindings.Validate(binding);
                if (program.Workstations.Any(w => w.Request.Procedure == request.Procedure && (w.Request.Name == request.Name || w.Request.Name == layout.Record || w.Layout.Records.Any(r => r.Name == layout.Record || r.Name == request.Name))) ||
                    program.DiskFiles.Any(f => f.Layout.Record == layout.Record && f.Request.Procedure == request.Procedure) ||
                    program.FileSpecs.Contains(layout.Record) || declarations.Any(d => d.Name == layout.Record && d.Name != request.Name))
                    throw new RpgCompileException(declaration.Line, "Ambiguous file or record name; use RENAME.");
                foreach (var field in request.IsTemplate || request.Qualified || request.Procedure is not null ? [] : layout.Fields)
                {
                    if (SharedExternalField(program, field.Name, field.External, field.Kind, field.Length, field.Decimals))
                    {
                        if (field.NullCapable && program.NullControl == "*USRCTL") program.FindDsElement(field.Name)!.NullCapable = true;
                        continue;
                    }
                    var existing = program.FindField(field.Name);
                    if (existing is not null && field.NullCapable && program.NullControl == "*USRCTL") { existing.NullCapable = true; existing.NativeScalar = true; }

                    if (program.FindDs(field.Name) is not null || program.FindDsElement(field.Name) is not null ||
                        existing is not null && (existing.IsArray || existing.IsDataStructure || existing.Source == RpgFieldSource.Constant ||
                            existing.Kind != field.Kind || existing.Length != field.Length || existing.Decimals != field.Decimals || existing.OpaqueBytes))
                        throw new RpgCompileException(declaration.Line, "Incompatible external field declaration: " + field.Name);
                    if (existing is null) program.Fields.Add(new() { Name = field.Name, Kind = field.Kind, Length = field.Length,
                        Decimals = field.Decimals, Source = RpgFieldSource.ExternalFile, NullCapable = field.NullCapable && program.NullControl == "*USRCTL", NativeScalar = field.NullCapable && program.NullControl == "*USRCTL" });
                }
                program.DiskFiles.Add(binding); if (!request.IsTemplate) program.FileSpecs.Add(request.Name);
            }
            catch (Exception error) when (error is InvalidDataException or Ipc.Core.Messages.CpfException)
            { throw new RpgCompileException(declaration.Line, error.Message); }
        }
    }

    private static RpgDiskRequest DiskRequest(WorkstationDeclaration declaration)
    {
        var keywords = DeclarationKeywords(declaration.Keywords, declaration.Line);
        foreach (var option in keywords)
        {
            if (option.Name is not ("DISK" or "KEYED" or "USROPN" or "USAGE" or "EXTDESC" or "EXTFILE" or "RENAME" or "PREFIX" or "RECNO"))
                throw new RpgCompileException(declaration.Line, "Unsupported DISK keyword " + option.Name + ".");
            if (keywords.Count(k => k.Name == option.Name) != 1) throw new RpgCompileException(declaration.Line, "Duplicate " + option.Name + ".");
            if (option.Name is "KEYED" or "USROPN" && option.Arguments is not null ||
                option.Name == "DISK" && (keywords[0] != option || option.Arguments is not null && !option.Arguments.Equals("*EXT", StringComparison.OrdinalIgnoreCase)))
                throw new RpgCompileException(declaration.Line, "DISK input supports externally described files and argument-free KEYED/USROPN.");
            if (option.Name is not ("DISK" or "KEYED" or "USROPN") && string.IsNullOrWhiteSpace(option.Arguments))
                throw new RpgCompileException(declaration.Line, option.Name + " requires arguments.");
        }
        string? Option(string name) => keywords.SingleOrDefault(k => k.Name == name)?.Arguments;
        var usage = Option("USAGE") is { } modes ? DeclarationArguments(modes.ToUpperInvariant(), declaration.Line) : ["*INPUT"];
        if (usage.Length is < 1 or > 4 || usage.Distinct().Count() != usage.Length || usage.Any(mode => mode is not ("*INPUT" or "*UPDATE" or "*DELETE" or "*OUTPUT")))
            throw new RpgCompileException(declaration.Line, "DISK USAGE supports distinct *INPUT, *UPDATE, *DELETE and *OUTPUT values.");
        var update = usage.Contains("*UPDATE") || usage.Contains("*DELETE"); var delete = usage.Contains("*DELETE");
        string File(string keyword)
        {
            var argument = Option(keyword);
            if (argument is null) return keyword == "EXTFILE" && declaration.Template && declaration.Name.Length > 10 ? "*EXTDESC" : declaration.Name;
            if (keyword == "EXTFILE" && argument.Equals("*EXTDESC", StringComparison.OrdinalIgnoreCase)) return "*EXTDESC";
            if (!System.Text.RegularExpressions.Regex.IsMatch(argument, @"\A'(?:[^']|'')*'\z"))
                throw new RpgCompileException(declaration.Line, keyword + " requires a constant quoted file name.");
            var result = Unquote(argument);
            if (!RpgWorkstationBindings.FileName(result) || result.StartsWith("*CURLIB/", StringComparison.Ordinal))
                throw new RpgCompileException(declaration.Line, keyword + " requires an uppercase file name, optionally qualified with a library or *LIBL.");
            return result;
        }
        if (Option("EXTFILE")?.Equals("*EXTDESC", StringComparison.OrdinalIgnoreCase) == true && Option("EXTDESC") is null)
            throw new RpgCompileException(declaration.Line, "EXTFILE(*EXTDESC) requires EXTDESC.");
        if (declaration.Name.Length > 10 && (Option("EXTDESC") is null || !declaration.Template && Option("EXTFILE") is null))
            throw new RpgCompileException(declaration.Line, "Long DISK file names require EXTDESC and EXTFILE.");
        var descriptionIndex = keywords.FindIndex(k => k.Name == "EXTDESC");
        var renameIndex = keywords.FindIndex(k => k.Name == "RENAME");
        if (descriptionIndex >= 0 && renameIndex >= 0 && descriptionIndex > renameIndex)
            throw new RpgCompileException(declaration.Line, "EXTDESC must precede RENAME.");
        var renames = new List<RpgRecordRename>();
        if (Option("RENAME") is { } rename)
        {
            var parts = DeclarationArguments(rename, declaration.Line);
            if (parts.Length != 2) throw new RpgCompileException(declaration.Line, "RENAME requires external and RPG record names.");
            renames.Add(new(parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant()));
        }
        return new RpgDiskRequest(declaration.Name, File("EXTDESC"), File("EXTFILE"), keywords.Any(k => k.Name == "KEYED"),
            keywords.Any(k => k.Name == "USROPN"), renames, FilePrefix(Option("PREFIX"),declaration.Line), update, delete,
            usage.Contains("*OUTPUT"), update || usage.Contains("*INPUT"), declaration.Template, declaration.Qualified, declaration.LikeFile, Procedure: declaration.Procedure, Static: declaration.Static, Recno: Option("RECNO")?.Trim().ToUpperInvariant());
    }

    private static void ValidateDiskStatements(RpgProgram program)
    {
        if (program.DiskFiles.Count == 0)
        {
            var unsupported = program.MainStatements.FirstOrDefault(s => s.NoLock && s.Opcode != RpgOpcode.Dealloc || s.Opcode == RpgOpcode.Unlock);
            if (unsupported is not null) throw new RpgCompileException(unsupported.LineNumber, "UNLOCK and N input require a declared update DISK file.");
            var undeclared = program.MainStatements.FirstOrDefault(s => s.ErrorExtender && s.NativeOperation != "CALL" && s.Opcode is not (RpgOpcode.CallP or RpgOpcode.Dealloc or RpgOpcode.Alloc or RpgOpcode.Realloc) && !program.Workstations.Any(f =>
                f.Request.Name == (s.Factor2 ?? s.Factor1)?.ToUpperInvariant() || f.Layout.Records.Any(r => r.Name == (s.Factor2 ?? s.Factor1)?.ToUpperInvariant())));
            if (undeclared is not null) throw new RpgCompileException(undeclared.LineNumber, "E file operations require a declared DISK or WORKSTN file.");
            return;
        }
        foreach (var statement in program.MainStatements)
        {
            if (statement.Opcode is not (RpgOpcode.Open or RpgOpcode.Close or RpgOpcode.Read or RpgOpcode.ReadP or RpgOpcode.Reade or RpgOpcode.ReadPe or
                RpgOpcode.Chain or RpgOpcode.SetLl or RpgOpcode.SetGt or RpgOpcode.Write or RpgOpcode.Update or RpgOpcode.Delete or RpgOpcode.Force or RpgOpcode.Unlock)) continue;
            var name = (statement.Factor2 ?? statement.Factor1)?.Trim().ToUpperInvariant();
            if (program.FindWorkstation(name) is not null)
            {
                if (statement.NoLock || statement.Opcode == RpgOpcode.Unlock) throw new RpgCompileException(statement.LineNumber, "UNLOCK and N input require an update DISK file.");
                continue;
            }
            var binding = program.FindDisk(name);
            if (binding is null) throw new RpgCompileException(statement.LineNumber, "A declared DISK file or record name is required.");
            if (program.NullControl == "*INPUTONLY" && binding.Layout.Fields.Any(f => f.NullCapable) && statement.Opcode is RpgOpcode.Write or RpgOpcode.Update)
                throw new RpgCompileException(statement.LineNumber, "ALWNULL(*INPUTONLY) cannot write or update null-capable records.");
            if (program.NullControl == "*INPUTONLY" && binding.Layout.Fields.Any(f => f.NullCapable && f.KeySequence > 0) && (statement.Opcode is RpgOpcode.Chain or RpgOpcode.SetLl or RpgOpcode.SetGt or RpgOpcode.Reade or RpgOpcode.ReadPe || statement.Opcode == RpgOpcode.Delete && !string.IsNullOrWhiteSpace(statement.Factor1)))
                throw new RpgCompileException(statement.LineNumber, "ALWNULL(*INPUTONLY) cannot search null-capable keys.");
            if (binding.Request.IsTemplate) throw new RpgCompileException(statement.LineNumber, "A TEMPLATE file has no runtime I/O.");
            if ((binding.Request.Qualified || binding.Request.Procedure is not null) && RecordIo(statement.Opcode) && statement.RecordDataStructure is null)
                throw new RpgCompileException(statement.LineNumber, "Qualified and LIKEFILE files require result data structures.");
            if (statement.Opcode == RpgOpcode.Force || statement.Opcode == RpgOpcode.Write && !binding.Request.Output ||
                statement.Opcode is RpgOpcode.Read or RpgOpcode.ReadP or RpgOpcode.Reade or RpgOpcode.ReadPe or RpgOpcode.Chain or RpgOpcode.SetLl or RpgOpcode.SetGt && !binding.Request.Input ||
                statement.Opcode is RpgOpcode.Update or RpgOpcode.Unlock && !binding.Request.Update ||
                statement.Opcode == RpgOpcode.Delete && !binding.Request.Delete)
                throw new RpgCompileException(statement.LineNumber, "Operation is not permitted by the DISK usage declaration: " + statement.Opcode + ".");
            if (statement.Opcode == RpgOpcode.Update && name != binding.Layout.Record)
                throw new RpgCompileException(statement.LineNumber, "UPDATE of an externally described file requires its record-format name.");
            if (statement.Opcode == RpgOpcode.Write && name != binding.Layout.Record)
                throw new RpgCompileException(statement.LineNumber, "WRITE of an externally described file requires its record-format name.");
            if (statement.Opcode == RpgOpcode.Unlock && name != binding.Request.Name)
                throw new RpgCompileException(statement.LineNumber, "UNLOCK requires the DISK file name.");
            if (statement.NoLock && !binding.Request.Update)
                throw new RpgCompileException(statement.LineNumber, "The N extender requires an update DISK declaration.");
            if (statement.Opcode is RpgOpcode.Open or RpgOpcode.Close && name != binding.Request.Name)
                throw new RpgCompileException(statement.LineNumber, "OPEN/CLOSE requires the DISK file name.");
            if (statement.Opcode is RpgOpcode.Reade or RpgOpcode.ReadPe && !binding.Request.Keyed)
                throw new RpgCompileException(statement.LineNumber, "READE/READPE requires a KEYED file declaration.");
        }
    }
}
