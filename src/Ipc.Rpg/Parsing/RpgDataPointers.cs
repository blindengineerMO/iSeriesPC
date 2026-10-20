using System.Text.RegularExpressions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static string? BasedDeclaration(RpgProgram program, string name, IReadOnlyList<DeclarationKeyword> options, int line, bool storage)
    {
        var based = options.SingleOrDefault(k => k.Name == "BASED");
        if (based is null) return null;
        if (!storage || based.Arguments is null || !Regex.IsMatch(based.Arguments, @"\A[A-Za-z_][A-Za-z0-9_]*(?:(?:\.[A-Za-z_][A-Za-z0-9_]*)|(?:\([^:]+\)))*\z", RegexOptions.CultureInvariant) ||
            options.Any(k => k.Name is "INZ" or "CTDATA" or "TEMPLATE" or "STATIC" or "IMPORT" or "EXPORT" or "NULLIND" or "EXT" or "EXTNAME" or "PSDS"))
            throw new RpgCompileException(line, "BASED requires a standalone variable/structure and a basing pointer name, without initialization or separate storage ownership.");
        return based.Arguments.ToUpperInvariant();
    }
    private static void ValidateBasedDeclarations(RpgProgram program)
    {
        var declarations = program.Fields.Where(f => !program.GlobalFields.Contains(f) && f.BasedOn is not null).Select(f => (Name: f.BasedOn!, Line: f.DeclarationLine))
            .Concat(program.DataStructures.Where(d => !program.GlobalStructures.Contains(d) && d.BasedOn is not null).Select(d => (Name: d.BasedOn!, Line: d.DeclarationLine))).ToArray();
        foreach (var declaration in declarations)
        {
            if (RpgWorkstationBindings.Name(declaration.Name) && (program.FindField(declaration.Name) is null || program.GlobalFields.Contains(program.FindField(declaration.Name)!)) && program.FindDs(declaration.Name) is null && program.FindDsElement(declaration.Name) is null)
                DeclareFreeScalar(program, declaration.Name, "POINTER", declaration.Line);
            if (program.IsNativeProcedure && program.FindField(declaration.Name) is { } global && program.GlobalFields.Contains(global))
                DeclareFreeScalar(program, declaration.Name, "POINTER", declaration.Line);
            var expression = RpgExpressionParser.Parse(declaration.Name); var root = AddressRoot(expression);
            if (program.FindField(root) is not { Source: not RpgFieldSource.Constant } && program.FindDs(root) is null && program.FindDsElement(root) is null)
                throw new RpgCompileException(declaration.Line, "BASED requires declared pointer storage, not a function expression.");
            if (ExactActualType(program, expression) is not { Kind: RpgFieldKind.DataPointer, IsArray: false, IsDataStructure: false })
                throw new RpgCompileException(declaration.Line, "BASED requires a scalar basing pointer variable.");
        }
        foreach (var field in program.Fields.Where(f => f.BasedOn is not null))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { field.Name }; var target = field.BasedOn;
            while (target is not null)
            { if (!seen.Add(target)) throw new RpgCompileException(field.DeclarationLine, "Circular BASED pointer definitions."); target = program.FindField(target)?.BasedOn; }
        }
        foreach (var ds in program.DataStructures.Where(d => d.BasedOn is not null))
            if (DataElements(ds).Any(e => e.HasInitialValue || e.NullCapable)) throw new RpgCompileException(ds.DeclarationLine, "Based structures cannot initialize subfields or declare independent null indicators.");
    }
    private static void EnsureAddressableFields(RpgProgram program)
    {
        var texts = program.MainStatements.SelectMany(s => new[] { s.Factor1, s.Factor2, s.Result, s.Value }.OfType<string>().Select(t => (Text: t, Line: s.LineNumber)))
            .Concat(program.DataStructures.Where(d => !program.GlobalStructures.Contains(d)).SelectMany(PointerElements).Where(e => e.InitialValue is not null).Select(e => (Text: e.InitialValue!, Line: e.DeclarationLine))).ToArray();
        foreach (var text in texts)
            for (var index = 0; index < text.Text.Length; index++)
            {
                if (text.Text[index] == '\'') { Skip(text.Text, ref index); continue; }
                if (index + 5 > text.Text.Length || !text.Text.AsSpan(index, 5).Equals("%ADDR".AsSpan(), StringComparison.OrdinalIgnoreCase)) continue;
                var opening = index + 5; while (opening < text.Text.Length && char.IsWhiteSpace(text.Text[opening])) opening++;
                if (opening == text.Text.Length || text.Text[opening] != '(') continue;
                var start = opening + 1; var close = start; var depth = 1;
                for (; close < text.Text.Length; close++)
                {
                    if (text.Text[close] == '\'') { Skip(text.Text, ref close); continue; }
                    if (text.Text[close] == '(') depth++;
                    if (text.Text[close] == ')' && --depth == 0) break;
                }
                if (close == text.Text.Length) throw new RpgCompileException(text.Line, "Unclosed %ADDR.");
                var argument = RpgExpressionParser.Parse(text.Text[start..close]); var root = AddressRoot(argument);
                if (program.FindField(root) is { NativeScalar: false, NativeArray: false, Source: RpgFieldSource.ExternalFile } field)
                {
                    var owner = program.GlobalFields.Contains(field) ? program.GlobalProgram! : program;
                    field.NativeScalar = true; DeclareNativeStandalone(owner, field, text.Line);
                }
                index = close;
            }
        static void Skip(string text, ref int index)
        { while (++index < text.Length) if (text[index] == '\'') { if (index + 1 < text.Length && text[index + 1] == '\'') index++; else break; } }
    }
    private static void ValidateDataPointerOperations(RpgProgram program)
    {
        foreach (var statement in program.MainStatements.Where(s => s.Opcode is RpgOpcode.Dealloc or RpgOpcode.Alloc or RpgOpcode.Realloc))
            if (string.IsNullOrWhiteSpace(statement.Result) || ExactActualType(program, RpgExpressionParser.Parse(statement.Result)) is not { Kind: RpgFieldKind.DataPointer, IsArray: false })
                throw new RpgCompileException(statement.LineNumber, "Storage management requires a scalar basing pointer variable.");
        foreach (var element in program.DataStructures.Where(d => !program.GlobalStructures.Contains(d)).SelectMany(PointerElements).Where(e => e.Kind == RpgFieldKind.DataPointer && e.InitialValue is not null))
        {
            var expression = RpgExpressionParser.Parse(element.InitialValue!);
            if (expression is RpgLiteral { Value: RpgNullAddress }) continue;
            if (expression is not RpgBuiltin { Name: "ADDR", Arguments.Count: 1 } address || ExactActualType(program, address.Arguments[0]) is null)
                throw new RpgCompileException(element.DeclarationLine, "Pointer INZ requires %ADDR of declared non-based storage.");
            var root = AddressRoot(address.Arguments[0]);
            if (TemplateReference(program, address.Arguments[0])) throw new RpgCompileException(element.DeclarationLine, "TEMPLATE storage has no address.");
            if (program.FindField(root)?.BasedOn is not null || program.FindDs(root)?.BasedOn is not null)
                throw new RpgCompileException(element.DeclarationLine, "A based variable cannot initialize another pointer.");
            void KnownIndex(RpgExpr index)
            {
                var text = index switch { RpgFieldRef field => field.Name, RpgLiteral literal when RpgValues.IsNumeric(literal.Value) => RpgValues.ToText(literal.Value), _ => "" };
                _ = DeclarationInteger(program, text, element.DeclarationLine, 1, 32767);
            }
            void Index(RpgExpr selected)
            {
                if (selected is RpgDynamicIndexRef dynamic) KnownIndex(dynamic.Index);
                if (selected is RpgElementRef index) { KnownIndex(index.Index); Index(index.Target); }
                if (selected is RpgMemberRef member) Index(member.Target);
            }
            Index(address.Arguments[0]);
        }
    }
    internal static string AddressRoot(RpgExpr expression) => expression switch
    { RpgFieldRef f => f.Name, RpgDynamicIndexRef d => d.Name, RpgIndexedRef i => i.Name,
        RpgAllElementsRef a => AddressRoot(a.Target), RpgMemberRef m => AddressRoot(m.Target), RpgElementRef e => AddressRoot(e.Target), _ => "" };
}
