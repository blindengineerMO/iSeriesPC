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
    public bool IsFreeForm { get; init; }
    public bool NoLock { get; init; }
    public bool ErrorExtender { get; init; }
    public int Monitor { get; set; } = -1;
    public IReadOnlyList<int> ErrorStatuses { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> ErrorHandlers { get; set; } = Array.Empty<int>();
    public RpgFileSearch? FileSearch { get; set; }
    public Ipc.Core.Compilation.SourceLocation? Location { get; set; }
    public required RpgOpcode Opcode { get; init; }

    public string? Factor1 { get; set; }

    public string? Factor2 { get; set; }

    public string? Result { get; set; }

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
    public Ipc.Core.Compilation.PreprocessedSource? Source { get; init; }
    public required string Name { get; init; }

    public required string Library { get; init; }

    public List<RpgField> Fields { get; } = new();

    public List<RpgDataStructure> DataStructures { get; } = new();
    public List<RpgFileInformation> FileInformation { get; } = new();
    public List<RpgFileErrorHandler> FileErrorHandlers { get; } = new();

    public List<RpgSubroutine> Subroutines { get; } = new();

    public List<RpgSubprocedure> Subprocedures { get; } = new();

    public List<RpgPrototype> Prototypes { get; } = new();

    public List<RpgStatement> MainStatements { get; } = new();

    public bool IsFreeForm { get; set; }

    public List<string> FileSpecs { get; } = new();
    public List<RpgWorkstationBinding> Workstations { get; } = new();
    public List<RpgDiskBinding> DiskFiles { get; } = new();

    public RpgField? FindField(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public RpgDataStructure? FindDs(string name) =>
        DataStructures.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    public RpgDsElement? FindDsElement(string name)
    {
        foreach (var ds in DataStructures)
        {
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
        Subprocedures.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public RpgPrototype? FindPrototype(string name) =>
        Prototypes.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class RpgPrototype
{
    public required string Name { get; init; }

    public string? ExternalName { get; init; }

    public string? ReturnType { get; init; }

    public List<string> Parameters { get; } = new();

    public Dictionary<string, string> ParameterKeywords { get; } = new(StringComparer.OrdinalIgnoreCase);
}
