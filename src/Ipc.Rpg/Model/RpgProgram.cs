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
}

public enum RpgFieldSource
{
    Standalone,
    Constant,
    FieldReference,
    DataStructure,
    LocalDataStructure,
    Array,
}

public sealed class RpgField
{
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
    public required string Name { get; init; }

    public int Length { get; set; }

    public int Dimension { get; set; } = 1;

    public List<RpgDsElement> Elements { get; } = new();

    public RpgDsElement? Find(string name) =>
        Elements.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class RpgSubroutine
{
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
    public required string Name { get; init; }

    public required string Library { get; init; }

    public List<RpgField> Fields { get; } = new();

    public List<RpgDataStructure> DataStructures { get; } = new();

    public List<RpgSubroutine> Subroutines { get; } = new();

    public List<RpgSubprocedure> Subprocedures { get; } = new();

    public List<RpgStatement> MainStatements { get; } = new();

    public bool IsFreeForm { get; set; }

    public List<string> FileSpecs { get; } = new();

    public RpgField? FindField(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public RpgDataStructure? FindDs(string name) =>
        DataStructures.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    public RpgDsElement? FindDsElement(string name)
    {
        foreach (var ds in DataStructures)
        {
            var element = ds.Find(name);
            if (element is not null)
            {
                return element;
            }
        }

        return null;
    }

    public RpgSubroutine? FindSubroutine(string name) =>
        Subroutines.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public RpgSubprocedure? FindSubprocedure(string name) =>
        Subprocedures.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}