using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private readonly Dictionary<string, int> _nativeTableSelections = new(StringComparer.OrdinalIgnoreCase);

    private NativeView TableStorage(string name)
    {
        if (IsGlobalName(name)) return GlobalContext!.TableStorage(name);
        if (Program.FindField(name) is not { IsTable: true }) throw NativeFailure(105, "A table search requires a standalone table name.");
        var element = Program.FindDsElement(name) ?? throw NativeFailure(105, "Native table storage is missing.");
        var owner = Program.DataStructures.Single(d => d.Elements.Contains(element));
        return NativeMember(new(owner, _nativeStructures[owner.Name], 0, owner.Length, null, false, 1), element.LocalName ?? element.Name);
    }
    private NativeView NamedTable(RpgExpr expression, out string name)
    {
        if (expression is not RpgFieldRef field) throw NativeFailure(105, "A table search requires a table name without an index.");
        name = field.Name; return TableStorage(name);
    }
    private void SelectTables(string search, string? alternate, NativeView? alternateStorage, int found)
    {
        if (alternateStorage is not null && found > alternateStorage.Count)
            throw NativeFailure(121, "The alternate table has no corresponding element; table selections are unchanged.");
        _host.CancellationToken.ThrowIfCancellationRequested();
        if (found == 0) return;
        SelectTable(search, found);
        if (alternate is not null) SelectTable(alternate, found);
    }
    private void SelectTable(string name, int index)
    { if (IsGlobalName(name)) GlobalContext!.SelectTable(name, index); else _nativeTableSelections[name] = index; }
    private bool LookupTable(string operation, IReadOnlyList<RpgExpr> arguments)
    {
        ArrayArguments(arguments, 2, 3);
        var table = NamedTable(arguments[1], out var name); string? alternate = null;
        var alternateStorage = arguments.Count == 3 ? NamedTable(arguments[2], out alternate) : null;
        var key = SearchArrayArgument(table, arguments[0]);
        int Compare(int index)
        {
            _host.CancellationToken.ThrowIfCancellationRequested(); return CompareArrayValues(ArrayKey(NativeIndex(table, index + 1)), key);
        }
        var found = FindArrayPosition(operation[1..], 1, table.Count, table.Element!.ArrayDescending, Compare);
        SelectTables(name, alternate, alternateStorage, found);
        return found != 0;
    }
    private void FixedTableLookup(RpgStatement statement, string name)
    {
        var table = TableStorage(name);
        var alternate = statement.Result;
        var alternateStorage = alternate is null ? null : TableStorage(alternate);
        var key = FixedLookupKey(table, RpgExpressionParser.Parse(statement.Factor1!), statement.Factor1!);
        var (found, comparison) = SearchFixedLookup(statement, table, key, 1);
        SelectTables(name, alternate, alternateStorage, found);
        PublishFixedLookup(statement, found, comparison);
    }
}
