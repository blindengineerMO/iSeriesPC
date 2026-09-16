using Ipc.Cl.Commands;
using Ipc.Cl.Compatibility;
using Ipc.Cl.Definitions;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Commands;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private int _commandDepth;
    private void RegisterCommandObjectCommands()
    {
        _catalog.Register("CRTCMD", CreateCommandObject);
        _catalog.Register("DSPCMD", call => {
            var key = ResolveCommandObject(CommandParser.Unquote(call.GetOption("CMD") ?? ""));
            var definition = new CommandDefinitionStore(_system.Connections).Load(key.Library, key.Name.Value);
            return new CommandResult { Message = "Command " + key, Listing = definition.Describe(key.ToString()) };
        });
    }
    private QualifiedName ResolveCommandObject(string name)
    {
        var canonical = BuiltinContract.CanonicalName(name);
        var key = QualifiedName.Parse(canonical, "*LIBL");
        foreach (var library in SearchLibraries(key.Library))
            if (_system.Objects.Exists(library, key.Name.Value, ObjectType.Command)) return new(library, key.Name.Value);
        throw new CpfException("CPF9801", "Command " + name + " not found in the job library list.");
    }
    private CommandResult DispatchCommand(CommandCall call)
    {
        if (_commandDepth >= 64) return CommandResult.Error("IPC0136: Command processing recursion limit reached.");
        _commandDepth++;
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            QualifiedName key;
            try { key = ResolveCommandObject(call.Name); }
            catch (CpfException error) when (error.MessageId == "CPF9801" && !_catalog.IsRegistered(BuiltinContract.CanonicalName(call.Name)))
            { return CommandResult.Error(BuiltinContract.Unavailable(call.Name)); }
            var definition = new CommandDefinitionStore(_system.Connections).Load(key.Library, key.Name.Value);
            if (definition.Builtin is not null && BuiltinContract.Validate(new CommandCall { Name = definition.Builtin, Keywords = call.Keywords, Positional = call.Positional }) is { } validationError) return CommandResult.Error(validationError);
            var bound = CommandBinder.Bind(definition, call);
            if (definition.Builtin is { } builtin)
                return _catalog.Execute(new CommandCall { Name = builtin, Keywords = bound.Call.Keywords });
            var arguments = CommandArgumentCodec.ProgramArguments(definition, bound, _job?.Ccsid ?? _system.Config.Ccsid);
            return ExecuteProgram(definition.ProcessingProgram!, arguments);
        }
        catch (CpfException error) { return CommandResult.Error(error.Message); }
        catch (ClParseException error) { return CommandResult.Error("IPC0005: " + error.Message); }
        catch (Ipc.Cl.Interpreter.ClCompileException error) { return CommandResult.Error(error.Message); }
        catch (Ipc.Cl.Interpreter.ClRuntimeException error) { return CommandResult.Error(error.Message, error.MessageId); }
        catch (InvalidDataException error) { return CommandResult.Error("IPC0006: " + error.Message); }
        catch (Exception error) when (error is ArgumentException or FormatException) { return CommandResult.Error("IPC0003: Invalid command or parameter."); }
        catch (Microsoft.Data.Sqlite.SqliteException) { return CommandResult.Error("IPC0201: Command catalog operation failed."); }
        finally { _commandDepth--; }
    }
    private CommandResult CreateCommandObject(CommandCall call)
    {
        string Value(string key, string fallback = "") => CommandParser.Unquote(call.GetOption(key) ?? fallback).ToUpperInvariant();
        var (library, name) = ResolveWorkObject(Value("CMD"), ObjectType.Command, creating: true);
        var (programLibrary, program) = ResolveWorkObject(Value("PGM"), ObjectType.Program);
        var keywords = new Dictionary<string, string>(call.Keywords, StringComparer.OrdinalIgnoreCase);
        if (!keywords.TryGetValue("SRCMBR", out var member) || member == "*CMD") keywords["SRCMBR"] = name;
        var source = LoadSourceMember(new CommandCall { Name = call.Name, Keywords = keywords }) ?? throw new CpfException("CPF2817", "CRTCMD requires command definition source.");
        string? help = null;
        if (call.GetOption("HLPPNLGRP") is not null && Value("HLPPNLGRP") != "*NONE")
        { var (helpLibrary, helpName) = ResolveWorkObject(Value("HLPPNLGRP"), ObjectType.PanelGroup); help = helpLibrary + "/" + helpName; }
        CommandDefinition definition;
        try { definition = new CommandDefinitionCompiler().Compile(source, programLibrary + "/" + program, Value("SRCFILE", "*SOURCE") + "(" + Value("SRCMBR", name) + ")", help, call.GetOption("HLPID") is null ? null : Value("HLPID")); }
        catch (ArgumentException error) { return CommandResult.Error(error.Message); }
        new CommandDefinitionStore(_system.Connections).Create(library, name, source, definition, _job?.UserProfile ?? "QSECOFR", DisplayReplace(call));
        return CommandResult.Ok("Command " + library + "/" + name + " compiled.");
    }
}
