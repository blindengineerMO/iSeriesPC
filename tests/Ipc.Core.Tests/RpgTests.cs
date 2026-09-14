using System.Text;
using Ipc.Db.Dds;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services;

namespace Ipc.Core.Tests.Rpg;

public class RpgTests
{
    private static string DLine(string name, string type, string lengthAndDecimals, string keywords = "")
    {
        var parts = lengthAndDecimals.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var length = parts.Length > 0 ? parts[0] : "";
        var decimals = parts.Length > 1 ? parts[1] : "";
        var sb = new StringBuilder(new string(' ', 100));
        sb[6] = 'D';
        WriteAt(sb, 7, name, 13);
        WriteAt(sb, 21, type, 2);
        WriteAt(sb, 38, length, 2);
        WriteAt(sb, 40, decimals, 2);
WriteAt(sb, 50, keywords, 30);
        return sb.ToString().TrimEnd() + "\n";
    }

    private static string DdsLine(string name, string type, string lengthAndDecimals, string keywords = "")
    {
        var parts = lengthAndDecimals.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var length = parts.Length > 0 ? parts[0] : "";
        var decimals = parts.Length > 1 ? parts[1] : "";
        if (type == "R")
        {
            return DdsLine('A', ' ', 'R', name, ' ', " ", " ", "");
        }

        if (type == "K")
        {
            return DdsLine('A', 'K', ' ', name, ' ', " ", " ", "");
        }

        return DdsLine('A', ' ', ' ', name, type[0], length, decimals, keywords);
    }

    private static string DdsLine(char col7, char col8, char col17, string name, char type, string length, string decimals,
        string keywords)
    {
        var sb = new StringBuilder(new string(' ', 100));
        sb[6] = col7;
        sb[7] = col8;
        sb[16] = col17;
        WriteAt(sb, 18, name, 10);
        if (type != ' ')
        {
            sb[34] = type;
            WriteAt(sb, 35, length, 4);
            WriteAt(sb, 39, decimals, 2);
        }

        WriteAt(sb, 44, keywords, 40);
        return sb.ToString() + "\n";
    }

    private static string CSpec(string conditions, string factor1, string opcode, string factor2, string result,
        string indicators = "")
    {
        var sb = new StringBuilder(new string(' ', 80));
        sb[6] = 'C';
        WriteAt(sb, 7, conditions, 10);
        WriteAt(sb, 17, factor1, 18);
        WriteAt(sb, 35, opcode, 7);
        WriteAt(sb, 42, factor2, 5);
        WriteAt(sb, 47, result, 11);
        WriteAt(sb, 64, indicators, 6);
        return sb.ToString().TrimEnd() + "\n";
    }

    private static void WriteAt(StringBuilder sb, int start, string text, int width)
    {
        for (var index = 0; index < width && index < text.Length; index++)
        {
            sb[start + index] = text[index];
        }
    }

    private static string PSpec(string name, string marker)
    {
        var sb = new StringBuilder(new string(' ', 80));
        sb[6] = 'P';
        WriteAt(sb, 7, name, 14);
        sb[23] = marker[0];
        return sb.ToString().TrimEnd() + "\n";
    }

    private static RpgProgram Compile(params string[] lines)
    {
        var source = string.Join("\n", lines.AsEnumerable().Reverse().SkipWhile(string.IsNullOrWhiteSpace).Reverse());
        return RpgCompiler.Compile("TESTPGM", "QGPL", source + "\n");
    }

    private static RpgRuntimeContext Run(IEnumerable<string> lines, params object?[] parameters)
    {
        var program = Compile(lines.ToArray());
        return new RpgInterpreter(new RpgHost()).Run(program, parameters);
    }

    private static object? Read(RpgRuntimeContext ctx, string name) => ctx.ReadValue(name);

    [Fact]
    public void Fixed_form_arithmetic_sets_indicator_results()
    {
        var ctx = Run(new[]
        {
            DLine("TOTAL", "S", "5 0"),
            CSpec("", "", "Z-ADD", "1", "TOTAL"),
            CSpec("", "", "ADD", "2", "TOTAL", "0102"),
        });

        Assert.Equal(3m, RpgValues.ToDecimal(Read(ctx, "TOTAL")));
        Assert.True(ctx.Indicators[1]);
        Assert.False(ctx.Indicators[2]);
    }

    [Fact]
    public void Fixed_form_if_else_moves_value()
    {
        var ctx = Run(new[]
        {
            DLine("A", "S", "5 0", "INZ(10)"),
            DLine("MSG", "S", "3"),
            CSpec("", "A", "IF", "GT 5", ""),
            CSpec("", "'YES'", "MOVE", "", "MSG"),
            CSpec("", "", "ELSE", "", ""),
            CSpec("", "'NO'", "MOVE", "", "MSG"),
            CSpec("", "", "ENDIF", "", ""),
        });

        Assert.Equal("YES", RpgValues.ToText(Read(ctx, "MSG")).Trim());
    }

    [Fact]
    public void Fixed_form_if_with_conditioning_indicators()
    {
        var ctx = Run(new[]
        {
            DLine("N", "S", "5 0"),
            DLine("MSG", "S", "3"),
            CSpec("N11", "'YES'", "MOVE", "", "MSG"),
            CSpec("", "11", "SETON", "", ""),
            CSpec("N11", "'NO'", "MOVE", "", "MSG"),
        });

        Assert.Equal("YES", RpgValues.ToText(Read(ctx, "MSG")).Trim());
        Assert.True(ctx.Indicators[11]);
    }

    [Fact]
    public void Fixed_form_do_loop_accumulates()
    {
        var ctx = Run(new[]
        {
            DLine("N", "S", "3 0"),
            DLine("TOTAL", "S", "5 0"),
            CSpec("", "", "Z-ADD", "0", "TOTAL"),
            CSpec("", "1", "DO", "5", "N"),
            CSpec("", "", "ADD", "1", "TOTAL"),
            CSpec("", "", "ENDDO", "", ""),
        });

        Assert.Equal(5m, RpgValues.ToDecimal(Read(ctx, "TOTAL")));
    }

    [Fact]
    public void Fixed_form_select_when_routes_to_first_match()
    {
        var ctx = Run(new[]
        {
            DLine("A", "S", "3 0", "INZ(2)"),
            DLine("MSG", "S", "3"),
            CSpec("", "", "SELECT", "", ""),
            CSpec("", "A", "WHEN", "LT 1", ""),
            CSpec("", "'LOW'", "MOVE", "", "MSG"),
            CSpec("", "A", "WHEN", "EQ 2", ""),
            CSpec("", "'TWO'", "MOVE", "", "MSG"),
            CSpec("", "", "OTHER", "", ""),
            CSpec("", "'HI'", "MOVE", "", "MSG"),
            CSpec("", "", "ENDSL", "", ""),
        });

        Assert.Equal("TWO", RpgValues.ToText(Read(ctx, "MSG")).Trim());
    }

    [Fact]
    public void Free_form_program_compiles_and_runs()
    {
        var ctx = Run(new[]
        {
            DLine("TOTAL", "S", "5 0"),
            DLine("I", "S", "3 0"),
            DLine("FLAG", "S", "3"),
            "**free",
            "  eval TOTAL = 0;",
            "  for I = 1 to 5;",
            "    eval TOTAL = TOTAL + I;",
            "  endfor;",
            "  if TOTAL > 10;",
            "    eval FLAG = 'ON';",
            "  else;",
            "    eval FLAG = 'OFF';",
            "  endif;",
            "  return;",
        });

        Assert.Equal(15m, RpgValues.ToDecimal(Read(ctx, "TOTAL")));
        Assert.Equal("ON", RpgValues.ToText(Read(ctx, "FLAG")).Trim());
    }

    [Fact]
    public void Fixed_form_subroutine_is_executed()
    {
        var ctx = Run(new[]
        {
            DLine("A", "S", "3 0", "INZ(2)"),
            DLine("B", "S", "3 0", "INZ(3)"),
            DLine("TOTAL", "S", "5 0"),
            CSpec("", "", "Z-ADD", "A", "TOTAL"),
            CSpec("", "", "EXSR", "CALC", ""),
            CSpec("", "", "BEGSR", "CALC", ""),
            CSpec("", "", "ADD", "B", "TOTAL"),
            CSpec("", "", "ENDSR", "", ""),
        });

        Assert.Equal(5m, RpgValues.ToDecimal(Read(ctx, "TOTAL")));
    }

    [Fact]
    public void Entry_plist_binds_parameters()
    {
        var ctx = Run(new[]
        {
            DLine("A", "S", "5 0"),
            DLine("B", "S", "3"),
            CSpec("", "*ENTRY", "PLIST", "", ""),
            CSpec("", "A", "PARM", "", ""),
            CSpec("", "B", "PARM", "", ""),
            CSpec("", "", "ADD", "3", "A"),
        }, 12, "HI");

        Assert.Equal(15m, RpgValues.ToDecimal(Read(ctx, "A")));
        Assert.Equal("HI", RpgValues.ToText(Read(ctx, "B")).Trim());
    }

    [Fact]
    public void Data_structure_overlay_shares_storage()
    {
        var program = Compile(
            DLine("CUSTDS", "DS", ""),
            DLine("  NAME", "", "10"),
            DLine("  ID", "", "5 0"),
            DLine("  ALIAS", "", "10", "OVERLAY(NAME)"),
            CSpec("", "'BOB'", "MOVE", "", "ALIAS"));

        var ctx = new RpgInterpreter(new RpgHost()).Run(program);
        Assert.Equal(15, program.DataStructures.First(d => d.Name == "CUSTDS").Length);
        Assert.Equal("BOB", RpgValues.ToText(Read(ctx, "NAME")).Trim());
    }

    [Fact]
    public void Mismatched_endif_throws_compile_error()
    {
        Assert.Throws<RpgCompileException>(() => Compile(
             "**free",
             "  if A > 5;",
             "    eval B = 1;",
             "  return;"));
    }

    [Fact]
    public void Bit_operations_set_target_field()
    {
        var ctx = Run(new[]
        {
            DLine("BYTES", "S", "2"),
            CSpec("", "2", "BITON", "", "BYTES"),
            CSpec("", "11", "BITON", "", "BYTES"),
        });

        var text = RpgValues.ToText(Read(ctx, "BYTES"));
        var bytes = Encoding.ASCII.GetBytes(text);
        Assert.Equal(0b01100000, bytes[0]);
        Assert.Equal(0b00100000, bytes[1]);
    }

    [Fact]
    public void Free_directive_blocks_share_program_with_fixed_statements()
    {
        var ctx = Run(new[]
        {
            DLine("A", "S", "3 0"),
            DLine("B", "S", "3 0"),
            CSpec("", "", "Z-ADD", "1", "A"),
            "/free",
            "  eval A = A + 1;",
            "  eval B = A * 10;",
            "/end-free",
            CSpec("", "", "ADD", "1", "A"),
        });

        Assert.Equal(3m, RpgValues.ToDecimal(Read(ctx, "A")));
        Assert.Equal(20m, RpgValues.ToDecimal(Read(ctx, "B")));
    }

    [Fact]
    public void Percent_builtins_len_size_subst_trim_int()
    {
        var ctx = Run(new[]
        {
            DLine("LEN", "S", "3 0"),
            DLine("SIZED", "S", "3 0"),
            DLine("SUB", "S", "10"),
            DLine("TRIM", "S", "10"),
            DLine("CONV", "S", "3 0"),
            DLine("FIELD", "S", "8"),
            "**free",
            "eval LEN = %len('ABCDEF');",
            "eval SIZED = %size(FIELD);",
            "eval SUB = %subst('HELLOWORLD':3:4);",
            "eval TRIM = %trim('  hi  ');",
            "eval CONV = %int('42');",
        });

        Assert.Equal(6m, RpgValues.ToDecimal(Read(ctx, "LEN")));
        Assert.Equal(8m, RpgValues.ToDecimal(Read(ctx, "SIZED")));
        Assert.Equal("LLOW", Assert.IsType<string>(Read(ctx, "SUB")).Trim());
        Assert.Equal("hi", Assert.IsType<string>(Read(ctx, "TRIM")).Trim());
        Assert.Equal(42m, RpgValues.ToDecimal(Read(ctx, "CONV")));
    }

    [Fact]
    public void On_error_handles_free_division_by_zero()
    {
        var ctx = Run(new[]
        {
            DLine("TOTAL", "S", "7 0"),
            "**free",
            "eval total = 5 / 0;",
            "on-error;",
            "eval total = 99;",
        });

        Assert.Equal(99m, RpgValues.ToDecimal(Read(ctx, "TOTAL")));
    }

    [Fact]
    public void On_error_without_handler_propagates_runtime_error()
    {
        Assert.Throws<RpgRuntimeException>(() => Run(new[]
        {
            DLine("TOTAL", "S", "7 0"),
            "**free",
            "eval total = 5 / 0;",
        }));
    }

    [Fact]
    public void Seton_inlr_terminates_program()
    {
        var ctx = Run(new[]
        {
            DLine("X", "S", "3 0"),
            CSpec("", "", "Z-ADD", "1", "X"),
            CSpec("", "*INLR", "SETON", "", ""),
            CSpec("", "", "Z-ADD", "2", "X"),
        });

        Assert.Equal(1m, RpgValues.ToDecimal(Read(ctx, "X")));
        Assert.True(ctx.Indicators[0]);
    }

    [Fact]
    public void Eval_inlr_assignment_terminates_program()
    {
        var ctx = Run(new[]
        {
            DLine("X", "S", "3 0"),
            "**free",
            "eval x = 1;",
            "eval *inlr = *on;",
            "eval x = 2;",
        });

        Assert.Equal(1m, RpgValues.ToDecimal(Read(ctx, "X")));
        Assert.True(ctx.Indicators[0]);
    }

    [Fact]
    public void Callp_binds_parameters_and_retrn_returns_to_caller()
    {
        var ctx = Run(new[]
        {
            DLine("RESULT", "S", "7 0"),
            "**free",
            "eval result = 0;",
            "callp addparms(5:7);",
            "eval result = 99;",
            PSpec("ADDPARMS", "B"),
            DLine("P1", "S", "7 0"),
            DLine("P2", "S", "7 0"),
            "eval result = p1 + p2;",
            "retrn;",
            PSpec("ADDPARMS", "E"),
        });

        Assert.Equal(99m, RpgValues.ToDecimal(Read(ctx, "RESULT")));
        Assert.Equal(7m, RpgValues.ToDecimal(Read(ctx, "P2")));
    }
}

public class RpgFileTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IpcSystem _system;
    private readonly SqliteFileStore _files;

    public RpgFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ipcrpg-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _system = IpcSystem.Create(_tempDir, "test.db");
        _system.Start();
        _files = new SqliteFileStore(_system.Connections, _system.Objects);
    }

    public void Dispose()
    {
        _system.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private static string DdsLine(string name, string type, string lengthAndDecimals, string keywords = "")
    {
        var parts = lengthAndDecimals.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var length = parts.Length > 0 ? parts[0] : "";
        var decimals = parts.Length > 1 ? parts[1] : "";
        if (type == "R")
        {
            return DdsLine('A', ' ', 'R', name, ' ', " ", " ", "");
        }

        if (type == "K")
        {
            return DdsLine('A', 'K', ' ', name, ' ', " ", " ", "");
        }

        return DdsLine('A', ' ', ' ', name, type[0], length, decimals, keywords);
    }

    private static string DdsLine(char col7, char col8, char col17, string name, char type, string length, string decimals,
        string keywords)
    {
        var sb = new StringBuilder(new string(' ', 100));
        sb[6] = col7;
        sb[7] = col8;
        sb[16] = col17;
        WriteAt(sb, 18, name, 10);
        if (type != ' ')
        {
            sb[34] = type;
            WriteAt(sb, 35, length, 4);
            WriteAt(sb, 39, decimals, 2);
        }

        WriteAt(sb, 44, keywords, 40);
        return sb.ToString() + "\n";
    }

    private static void WriteAt(StringBuilder sb, int start, string text, int width)
    {
        for (var index = 0; index < width && index < text.Length; index++)
        {
            sb[start + index] = text[index];
        }
    }

    private static string SalesSource =>
        DdsLine("SALES", "R", "") +
        DdsLine("ORDER#", "P", "7 0") +
        DdsLine("CUSTOMER", "A", "20") +
        DdsLine("AMOUNT", "P", "9 2") +
        DdsLine("STATUS", "A", "10") +
        DdsLine("ORDER#", "K", "");

    private static string CustSource =>
        DdsLine("CUST", "R", "") +
        DdsLine("ID", "P", "7 0") +
        DdsLine("NAME", "A", "20") +
        DdsLine("ID", "K", "");

    private static Dictionary<string, object?> Row(long order, string customer, decimal amount, string status) =>
        new()
        {
            ["ORDER#"] = order,
            ["CUSTOMER"] = customer,
            ["AMOUNT"] = amount,
            ["STATUS"] = status,
        };

    private static string CLine(string conditions, string factor1, string opcode, string factor2, string result,
        string indicators = "")
    {
        var sb = new StringBuilder(new string(' ', 80));
        sb[6] = 'C';
        WriteAt(sb, 7, conditions, 10);
        WriteAt(sb, 17, factor1, 18);
        WriteAt(sb, 35, opcode, 7);
        WriteAt(sb, 42, factor2, 5);
        WriteAt(sb, 47, result, 11);
        WriteAt(sb, 64, indicators, 6);
        return sb.ToString().TrimEnd() + "\n";
    }

    private static string DLine(string name, string type, string lengthAndDecimals, string keywords = "")
    {
        var parts = lengthAndDecimals.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var length = parts.Length > 0 ? parts[0] : "";
        var decimals = parts.Length > 1 ? parts[1] : "";
        var sb = new StringBuilder(new string(' ', 100));
        sb[6] = 'D';
        WriteAt(sb, 7, name, 13);
        WriteAt(sb, 21, type, 2);
        WriteAt(sb, 38, length, 2);
        WriteAt(sb, 40, decimals, 2);
        WriteAt(sb, 50, keywords, 30);
        return sb.ToString().TrimEnd() + "\n";
    }

    private static RpgProgram Compile(params string[] lines)
    {
        var source = string.Join("\n", lines.AsEnumerable().Reverse().SkipWhile(string.IsNullOrWhiteSpace).Reverse());
        return RpgCompiler.Compile("TESTPGM", "QGPL", source + "\n");
    }

    private static RpgRuntimeContext Run(SqliteFileStore store, RpgProgram program)
    {
        var host = new RpgHost
        {
            Files = new RpgSqliteFileAccess(store),
            LibraryResolver = (_, _) => "QGPL",
        };

        return new RpgInterpreter(host).Run(program);
    }

    private static void SeedSales(SqliteFileStore files)
    {
        var definition = new DdsCompiler().CompilePhysical("SALES", SalesSource);
        files.CreatePhysicalFile("QGPL", "SALES", definition, SalesSource, "Sales.");
        files.Insert("QGPL", "SALES", "SALES", "SALES", Row(1001, "Acme", 10.00m, "OPEN"));
        files.Insert("QGPL", "SALES", "SALES", "SALES", Row(1002, "Globex", 20.00m, "OPEN"));
        files.Insert("QGPL", "SALES", "SALES", "SALES", Row(1003, "Initech", 30.00m, "HOLD"));
    }

    [Fact]
    public void Chain_found_and_not_found_set_different_indicators()
    {
        SeedSales(_files);

        var found = Run(_files, Compile(
            DLine("ORDER#", "S", "7 0"),
            DLine("CUSTOMER", "S", "20"),
            CLine("", "", "Z-ADD", "1001", "ORDER#"),
            CLine("", "ORDER#", "CHAIN", "SALES", "", "1112"),
            CLine("", "", "EXSR", "DONE", ""),
            CLine("", "", "BEGSR", "DONE", ""),
            CLine("", "", "ENDSR", "", "")));
        Assert.True(found.Indicators[11]);
        Assert.False(found.Indicators[12]);
        Assert.Equal("Acme", RpgValues.ToText(found.ReadValue("CUSTOMER")).Trim());

        var missing = Run(_files, Compile(
            DLine("ORDER#", "S", "7 0"),
            CLine("", "", "Z-ADD", "9999", "ORDER#"),
            CLine("", "ORDER#", "CHAIN", "SALES", "", "1112")));
        Assert.False(missing.Indicators[11]);
        Assert.True(missing.Indicators[12]);
    }

    [Fact]
    public void Read_loops_over_records_until_eof()
    {
        SeedSales(_files);

        var ctx = Run(_files, Compile(
            DLine("ORDER#", "S", "7 0"),
            DLine("RESULT", "S", "7 0"),
            CLine("N12", "SALES", "READ", "", "", "1112"),
            CLine("N12", "ORDER#", "MOVE", "", "RESULT"),
            CLine("N12", "SALES", "READ", "", "", "1112"),
            CLine("N12", "ORDER#", "MOVE", "", "RESULT"),
            CLine("N12", "SALES", "READ", "", "", "1112"),
            CLine("N12", "ORDER#", "MOVE", "", "RESULT"),
            CLine("N12", "SALES", "READ", "", "", "1112"),
            CLine("N12", "ORDER#", "MOVE", "", "RESULT")));

        Assert.Equal(1003m, RpgValues.ToDecimal(ctx.ReadValue("RESULT")));
        Assert.False(ctx.Indicators[11]);
        Assert.True(ctx.Indicators[12]);
    }

    [Fact]
    public void Write_inserts_record_from_context_fields()
    {
        SeedSales(_files);

        Run(_files, Compile(
            DLine("ORDER#", "S", "7 0"),
            DLine("CUSTOMER", "S", "20"),
            DLine("AMOUNT", "S", "9 2"),
            DLine("STATUS", "S", "10"),
            CLine("", "", "Z-ADD", "2001", "ORDER#"),
            CLine("", "'Cyberdyne'", "MOVE", "", "CUSTOMER"),
            CLine("", "", "Z-ADD", "42.50", "AMOUNT"),
            CLine("", "'OPEN'", "MOVE", "", "STATUS"),
            CLine("", "SALES", "WRITE", "", "")));

        Assert.Equal(4, _files.RowCount("QGPL", "SALES", "SALES"));
        var row = _files.ReadKeyPrefix("QGPL", "SALES", "SALES", new Dictionary<string, object?> { ["ORDER#"] = 2001L })[0];
        Assert.Equal("Cyberdyne", Assert.IsType<string>(row["CUSTOMER"])!.Trim());
    }

    [Fact]
    public void Update_modifies_qualified_record_after_chain()
    {
        SeedSales(_files);

        var ctx = Run(_files, Compile(
DLine("ORDER#", "S", "7 0"),
            DLine("STATUS", "S", "10"),
            CLine("", "", "Z-ADD", "1001", "ORDER#"),
            CLine("", "ORDER#", "CHAIN", "SALES", "", "11"),
            CLine("", "'CLOSED'", "MOVE", "", "STATUS"),
            CLine("", "SALES", "UPDATE", "", "")));

        Assert.True(ctx.Indicators[11]);
        var row = _files.ReadKeyPrefix("QGPL", "SALES", "SALES", new Dictionary<string, object?> { ["ORDER#"] = 1001L })[0];
        Assert.Equal("CLOSED", Assert.IsType<string>(row["STATUS"])!.Trim());
    }

    [Fact]
    public void Delete_removes_chained_record()
    {
        SeedSales(_files);

        Run(_files, Compile(
            DLine("ORDER#", "S", "7 0"),
            CLine("", "", "Z-ADD", "1002", "ORDER#"),
            CLine("", "ORDER#", "CHAIN", "SALES", "", "11"),
            CLine("", "SALES", "DELETE", "", "")));

        Assert.Equal(2, _files.RowCount("QGPL", "SALES", "SALES"));
        Assert.Empty(_files.ReadKeyPrefix("QGPL", "SALES", "SALES", new Dictionary<string, object?> { ["ORDER#"] = 1002L }));
    }

    [Fact]
    public void Free_form_chain_and_write()
    {
        var definition = new DdsCompiler().CompilePhysical("CUST", CustSource);
        _files.CreatePhysicalFile("QGPL", "CUST", definition, CustSource, "Customers.");
        _files.Insert("QGPL", "CUST", "CUST", "CUST", new Dictionary<string, object?> { ["ID"] = 42L, ["NAME"] = "Arthur" });

        var ctx = Run(_files, Compile(
            DLine("ID", "S", "7 0"),
            DLine("NAME", "S", "20"),
            "**free",
            "  chain '42' CUST;",
            "  if *IN01;",
            "    eval ID = 43;",
            "    eval NAME = 'Ford';",
            "    write CUST;",
            "  endif;",
            "  return;"));

        Assert.True(ctx.Indicators[1]);
        Assert.Equal(2, _files.RowCount("QGPL", "CUST", "CUST"));
        var row = _files.ReadKeyPrefix("QGPL", "CUST", "CUST", new Dictionary<string, object?> { ["ID"] = 43L })[0];
        Assert.Equal("Ford", Assert.IsType<string>(row["NAME"])!.Trim());
    }
}