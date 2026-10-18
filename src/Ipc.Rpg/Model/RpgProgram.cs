namespace Ipc.Rpg.Model;

public enum RpgOpcode
{
    None,
    Add,
    Subtract,
    Multiply,
    Divide,
    Mvr,
    ZAdd,
    ZSub,
    Eval,
    Move,
    MoveL,
    MoveA,
    Clear,
    BitOn,
    BitOff,
    SetOn,
    SetOff,
    Test,
    Comp,
    If,
    Else,
    EndIf,
    Select,
    When,
    Other,
    EndSl,
    Do,
    DoU,
    DoW,
    For,
    EndDo,
    Iter,
    Leave,
    Goto,
    Tag,
    BegSr,
    EndSr,
    Exsr,
    Retrn,
    Return,
    Open,
    Close,
    Chain,
    Read,
    Reade,
    ReadP,
    ReadPe,
    SetLl,
    SetGt,
    Write,
    Update,
    Delete,
    Force,
    ExFmt,
    Dsply,
    Call,
    CallB,
    CallP,
    Parm,
    Plist,
    SndPgMmsg,
    SndMsg,
    RcvMsg,
    OnError,
    BegProc,
    EndProc,
    ReadC,
    Klist,
    Kfld,
    Unlock,
    Monitor,
    EndMon,
    SortA,
    XFoot,
    Lookup,
}

public enum RpgFieldKind
{
    Unknown,
    Character,
    VaryingChar,
    Zoned,
    Packed,
    Decimal,
    Binary,
    Integer,
    Float,
    Date,
    Time,
    Timestamp,
    Indicator,
    ProcPtr,
}

public enum RpgFieldSource
{
    Standalone,
    Constant,
    FieldReference,
    DataStructure,
    LocalDataStructure,
    Array,
    ExternalFile,
}

public sealed class RpgField
{
    public bool IsTemplate { get; init; }
    internal object LikeType { get; init; } = new();
    internal bool DeclaredLike { get; init; }
    public bool ReturnByParameter { get; internal set; }
    public bool ReadOnlyParameter { get; init; }
    public bool NoPassParameter { get; init; }
    public bool OmitParameter { get; init; }
    public bool ValueParameter { get; init; }
    public bool VariableSizeParameter { get; internal set; }
    public bool ExactParameter { get; internal set; }
    public bool TrimParameter { get; internal set; }
    public bool RightAdjustParameter { get; internal set; }
    public bool StaticLocal { get; init; }
    internal RpgDataStructure? ParameterStructure { get; init; }
    public bool IsTable { get; init; }
    public string? AlternateOf { get; init; }
    public bool CompileTimeArray { get; init; }
    public bool InferredCompileTimeDimension { get; init; }
    public int ElementsPerRecord { get; init; } = 1;
    public string CompileTimeFormat { get; init; } = "S";
    public bool? ArrayDescending { get; init; }
    public bool NativeArray { get; init; }
    public bool NativeScalar { get; init; }
    public int DeclarationLine { get; init; }
    public string? InitializationExpression { get; init; }
    public bool OpaqueBytes { get; set; }
    public required string Name { get; init; }

    public RpgFieldKind Kind { get; init; }

    public required int Length { get; init; }

    public int Decimals { get; init; }

    public string? InitialValue { get; init; }

    public bool IsArray { get; set; }

    public int Dimension { get; set; } = 1;

    public bool IsDataStructure { get; set; }

    public string? FromField { get; init; }

    public bool Varying { get; init; }

    public RpgFieldSource Source { get; set; } = RpgFieldSource.Standalone;
}

public sealed class RpgDsElement
{
    internal object LikeType { get; init; } = new();
    internal bool DeclaredLike { get; init; }
    public bool ReadOnlyParameter { get; init; }
    public IReadOnlyList<string> CompileTimeInitializers { get; set; } = Array.Empty<string>();
    public IReadOnlyList<int> CompileTimeLines { get; set; } = Array.Empty<int>();
    public bool? ArrayDescending { get; init; }
    public string? OverlayTarget { get; init; }
    public string? FeedbackKeyword { get; init; }
    public int DeclarationLine { get; init; }
    public bool Anonymous { get; init; }
    public string? LocalName { get; init; }
    public int StorageLength { get; init; }
    public int Stride { get; init; }
    public int Dimension { get; init; } = 1;
    public bool IsArray { get; init; }
    public bool OpaqueBytes { get; init; }
    public string? InitialValue { get; init; }
    public bool HasInitialValue { get; init; }
    public required string Name { get; init; }

    public required RpgFieldKind Kind { get; init; }

    public RpgFieldSource Source { get; set; } = RpgFieldSource.Standalone;

    public required int Length { get; init; }

    public int Decimals { get; init; }

    public int Offset { get; set; }

    public bool HasExplicitOffset { get; set; }
}

public sealed class RpgDataStructure
{
    public bool IsTemplate { get; init; }
    public RpgRecordOrigin? RecordOrigin { get; internal set; }
    internal object LikeType { get; init; } = new();
    internal object? TypeOrigin { get; set; }
    internal string? ParameterName { get; init; }
    internal bool ReadOnlyParameter { get; init; }
    public bool IsMultipleOccurrence { get; init; }
    public int Occurrences { get; init; } = 1;
    public int StorageCount => IsMultipleOccurrence ? Occurrences : Dimension;
    public bool IndicatorDataStructure { get; set; }
    public string? LikeDataStructure { get; init; }
    public bool ProgramStatus { get; init; }
    public int DeclarationLine { get; init; }
    public bool ExplicitLength { get; init; }
    public bool NativeStorage { get; init; }
    public bool Qualified { get; init; }
    public bool Anonymous { get; init; }
    public bool InitializeSubfields { get; init; }
    public bool IsArray { get; init; }
    public required string Name { get; init; }

    public int Length { get; set; }

    public int Dimension { get; set; } = 1;

    public List<RpgDsElement> Elements { get; } = new();

    public RpgDsElement? Find(string name) =>
        Elements.FirstOrDefault(e => !e.Anonymous && (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.LocalName, name, StringComparison.OrdinalIgnoreCase)));
}

public sealed class RpgSubroutine
{
    public string? Procedure { get; init; }
    public RpgStatement? Ending { get; set; }
    public required string Name { get; init; }

    public List<RpgStatement> Statements { get; } = new();
}

public sealed class RpgSubprocedure
{
    internal RpgProgram? NativeBody { get; set; }
    public required string Name { get; init; }

    public List<string> Parameters { get; } = new();

    public List<RpgStatement> Statements { get; } = new();

    public bool IsExport { get; set; }
}

public sealed class RpgCondition
{
    public required int Indicator { get; init; }

    public bool Negated { get; init; }

    public string Display => (Negated ? "N" : string.Empty) + Indicator.ToString("00");

    public override string ToString() => Display;
}

public sealed class RpgStatement
{
    public string? NativeOperation { get; set; }
    public string NativeControlLevel { get; set; } = "";
    public IReadOnlyList<string> SortFields { get; set; } = Array.Empty<string>();
    public bool? SortDescending { get; init; }
    public bool ClearAllOccurrences { get; set; }
    public bool IsFreeForm { get; init; }
    public bool NoLock { get; init; }
    public bool ErrorExtender { get; set; }
    public int Monitor { get; set; } = -1;
    public IReadOnlyList<int> ErrorStatuses { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> ErrorHandlers { get; set; } = Array.Empty<int>();
    public RpgFileSearch? FileSearch { get; set; }
    public Ipc.Core.Compilation.SourceLocation? Location { get; set; }
    public required RpgOpcode Opcode { get; init; }

    public string? Factor1 { get; set; }

    public string? Factor2 { get; set; }

    public string? Result { get; set; }
    public string? RecordDataStructure { get; set; }

    public string? Length { get; set; }

    public string? Value { get; set; }

    public string? Label { get; set; }

    public List<RpgCondition> Conditions { get; } = new();

    public int Indicator1 { get; set; }

    public int Indicator2 { get; set; }

    public int Indicator3 { get; set; }

    public int LineNumber { get; init; }

    public int Jump { get; set; } = -1;

    public int End { get; set; } = -1;
}

public sealed class RpgCallSpec
{
    public IReadOnlyList<string> Parameters { get; init; } = Array.Empty<string>();

    public required string ProgramName { get; init; }

    public string? Library { get; init; }
}

public sealed class RpgProgram
{
    internal Func<string, bool, int, RpgDataStructure?>? ForwardStructureLookup { get; set; }
    internal Func<string, RpgDataStructure?, RpgField?>? ForwardTypeLookup { get; set; }
    internal Func<int, string, object>? DeclarationIdentityLookup { get; set; }
    internal Func<string, RpgDataStructure?, RpgField?>? PreviewLayoutLookup { get; set; }
    internal Func<string, RpgField?>? PreviewConstantLookup { get; set; }
    internal RpgProgram? GlobalProgram { get; set; }
    internal string? NativeProcedureName { get; set; }
    internal bool IsNativeProcedure => NativeProcedureName is not null;
    internal RpgField? NativeReturnField { get; set; }
    internal HashSet<string> NativeProcedureNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HashSet<RpgField> GlobalFields { get; } = new();
    internal HashSet<RpgDataStructure> GlobalStructures { get; } = new();
    internal IEnumerable<RpgPrototype> VisiblePrototypes => Prototypes.Concat(GlobalProgram?.VisiblePrototypes ?? []).Distinct();
    internal Dictionary<string, IReadOnlyList<string>> NativeParameterLists { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasNativeEntryInterface { get; set; }
    internal int NativeEntryLine { get; set; }
    internal string? NativeEntryPrototype { get; set; }
    internal List<RpgCompileTimeData> CompileTimeData { get; } = new();
    internal Dictionary<string, int> DeclarationStructureLengths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Ipc.Core.Compilation.PreprocessedSource? Source { get; init; }
    public required string Name { get; init; }

    public required string Library { get; init; }

    public List<RpgField> Fields { get; } = new();

    public List<RpgDataStructure> DataStructures { get; } = new();
    public List<RpgFileInformation> FileInformation { get; } = new();
    public List<RpgFileErrorHandler> FileErrorHandlers { get; } = new();
    public List<RpgIndicatorBinding> IndicatorBindings { get; } = new();

    public List<RpgSubroutine> Subroutines { get; } = new();

    public List<RpgSubprocedure> Subprocedures { get; } = new();

    public List<RpgPrototype> Prototypes { get; } = new();

    public List<RpgStatement> MainStatements { get; } = new();

    public bool IsFreeForm { get; set; }

    public List<string> FileSpecs { get; } = new();
    public List<RpgWorkstationBinding> Workstations { get; } = new();
    public List<RpgDiskBinding> DiskFiles { get; } = new();
    internal List<RpgDiskBinding> DeclarationDiskFiles { get; } = new();
    internal Func<IReadOnlyList<RpgDiskBinding>>? RecordLayoutsLookup { get; set; }
    internal Dictionary<string, RpgDataStructure> RecordStructures { get; } = new(StringComparer.Ordinal);

    public RpgField? FindField(string name) =>
        (GlobalFields.Count == 0 ? null : Fields.FirstOrDefault(f => !GlobalFields.Contains(f) && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))) ??
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public RpgDataStructure? FindDs(string name) =>
        FindField(name) is { IsDataStructure: false } field && !GlobalFields.Contains(field) ? null :
        (GlobalStructures.Count == 0 ? null : DataStructures.FirstOrDefault(d => !GlobalStructures.Contains(d) && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))) ??
        DataStructures.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    public RpgDsElement? FindDsElement(string name)
    {
        if (GlobalStructures.Count > 0)
            foreach (var local in DataStructures)
                if (!GlobalStructures.Contains(local) && local.Elements.FirstOrDefault(e => !e.Anonymous && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } found) return found;
        foreach (var ds in DataStructures)
        {
            if (GlobalStructures.Contains(ds) && FindField(name) is { } field && !GlobalFields.Contains(field)) continue;
            var element = ds.Elements.FirstOrDefault(e => !e.Anonymous && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (element is not null)
            {
                return element;
            }
        }

        return null;
    }

    public RpgSubroutine? FindSubroutine(string name) => FindSubroutine(name, null);
    public RpgSubroutine? FindSubroutine(string name, string? procedure) => Subroutines.FirstOrDefault(s =>
        string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Procedure, procedure, StringComparison.OrdinalIgnoreCase));

    public RpgSubprocedure? FindSubprocedure(string name) =>
        Subprocedures.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ?? GlobalProgram?.FindSubprocedure(name);

    public RpgPrototype? FindPrototype(string name) =>
        Prototypes.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ?? GlobalProgram?.FindPrototype(name);
}

public sealed class RpgPrototype
{
    public bool NativeProcedure { get; init; }
    public RpgField? NativeReturnField { get; init; }
    public bool AcceptsArgumentCount(int count) => count >= 0 && count <= Parameters.Count &&
        (NativeSignature ? NativeParameters.Count == Parameters.Count && count >= NativeParameters.FindLastIndex(p => !p.NoPassParameter) + 1 : count == Parameters.Count);
    public int DeclarationLine { get; init; }
    public bool NativeSignature { get; init; }
    public List<RpgField> NativeParameters { get; } = new();
    public required string Name { get; init; }

    public string? ExternalName { get; init; }

    public string? ReturnType { get; init; }

    public List<string> Parameters { get; } = new();

    public Dictionary<string, string> ParameterKeywords { get; } = new(StringComparer.OrdinalIgnoreCase);
}
