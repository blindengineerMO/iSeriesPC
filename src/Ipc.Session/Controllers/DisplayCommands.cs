using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Dsp;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private void RegisterDisplayCommands()
    {
        _catalog.Register("STRSDA", ExecuteScreenDesigner);
        _catalog.Register("CRTMNU", ExecuteSdaMenu);
        _catalog.Register("DLTMNU", call => ExecuteObjectManagement(new CommandCall { Name = "DLTOBJ", Keywords = new Dictionary<string, string> {
            ["OBJ"] = call.GetOption("MENU") ?? throw new CpfException("IPC0003", "MENU is required."), ["OBJTYPE"] = ObjectType.Menu } }));
        _catalog.Register("CRTPNLGRP", ExecutePanelGroup);
        _catalog.Register("DSPHELP", ExecutePanelGroup);
        _catalog.Register("CRTDSPF", ExecuteDisplayFile);
        _catalog.Register("RUNPNL", ExecuteDisplayFile);
        RegisterMessageDescriptionCommands();
    }
    private string ResolveDisplayMessage(string id, string file)
    {
        var (library, name) = ResolveWorkObject(file.ToUpperInvariant(), ObjectType.MessageFile);
        return new Ipc.Services.Messages.MessageDescriptionStore(_system.Connections).Get(library, name, id);
    }
    private CommandResult ExecuteDisplayFile(CommandCall call)
    {
        var create = call.Name.Equals("CRTDSPF", StringComparison.OrdinalIgnoreCase);
        var (library, name) = ResolveWorkObject(CommandParser.Unquote(call.GetOption("FILE")).ToUpperInvariant(), ObjectType.File, creating: create);
        var store = new DisplayFileStore(_system.Connections, ResolveDisplayMessage);
        if (create)
        {
            var source = LoadSourceMember(call) ?? throw new CpfException("CPF2817", "CRTDSPF requires an existing SRCFILE/SRCMBR.");
            try { store.Create(library, name, source, CommandParser.Unquote(call.GetOption("TEXT")), _job?.Ccsid ?? 37, DisplayReplace(call)); }
            catch (PanelCompileException error) { return CommandResult.Error(error.Message); }
            return CommandResult.Ok($"Display file {library}/{name} compiled.");
        }
        if (_job?.Type != JobType.Interactive) throw new CpfException("IPC0127", "RUNPNL requires an interactive terminal job.");
        var definition = store.Load(library, name);
        var record = CommandParser.Unquote(call.GetOption("RCDFMT") ?? definition.Records.First(r => !r.Keywords.Any(k => k.Name == "SFL")).Name).ToUpperInvariant();
        if (!definition.Records.Any(r => r.Name == record && !r.Keywords.Any(k => k.Name == "SFL"))) throw new CpfException("CPF4131", "Display record not found.");
        // Validate initial output before returning a panel request to the terminal controller.
        new DisplayFileSession(definition, ResolveDisplayMessage).Write(record, new Dictionary<string, object?>());
        return new CommandResult { Outcome = CommandOutcome.DisplayPanel, Panel = new(library, name, record) };
    }
    private CommandResult ExecutePanelGroup(CommandCall call)
    {
        var create = call.Name.Equals("CRTPNLGRP", StringComparison.OrdinalIgnoreCase);
        var (library, name) = ResolveWorkObject(CommandParser.Unquote(call.GetOption("PNLGRP")).ToUpperInvariant(), ObjectType.PanelGroup, creating: create);
        var store = new HelpPanelStore(_system.Connections, _system.ObjectSigning);
        if (create)
        {
            var keywords = new Dictionary<string, string>(call.Keywords, StringComparer.OrdinalIgnoreCase);
            if (!keywords.TryGetValue("SRCMBR", out var member) || member == "*PNLGRP") keywords["SRCMBR"] = name;
            var source = LoadSourceMember(new CommandCall { Name = call.Name, Keywords = keywords }) ?? throw new CpfException("CPF2817", "CRTPNLGRP requires an existing source member.");
            try { store.Create(library, name, source, CommandParser.Unquote(call.GetOption("TEXT")), _job?.Ccsid ?? 37); }
            catch (UimCompileException error) { return CommandResult.Error(error.Message); }
            return CommandResult.Ok("Panel group compiled.");
        }
        if (_job?.Type != JobType.Interactive) throw new CpfException("IPC0127", "DSPHELP requires an interactive terminal job.");
        var definition = store.Load(library, name); var module = CommandParser.Unquote(call.GetOption("MODULE") ?? definition.Modules[0].Name).ToUpperInvariant();
        if (!definition.Modules.Any(m => m.Name == module)) throw new CpfException("CPF6A00", "Help module not found.");
        return new CommandResult { Outcome = CommandOutcome.DisplayHelp, Help = new(library + "/" + name, module) };
    }

    private static bool DisplayReplace(CommandCall call) => CommandParser.Unquote(call.GetOption("REPLACE") ?? "*NO").ToUpperInvariant() switch
    { "*YES" => true, "*NO" => false, _ => throw new CpfException("IPC0003", "REPLACE must be *YES or *NO.") };
    private CommandResult ExecuteScreenDesigner(CommandCall call)
    {
        if (_job?.Type != JobType.Interactive) throw new CpfException("IPC0127", "STRSDA requires an interactive terminal job.");
        var (library, name) = ResolveWorkObject(CommandParser.Unquote(call.GetOption("SRCFILE")).ToUpperInvariant(), ObjectType.File);
        var member = CommandParser.Unquote(call.GetOption("SRCMBR") ?? name).ToUpperInvariant();
        if (!ObjectName.IsValid(member)) throw new CpfException("IPC0003", "Invalid source member name.");
        var snapshot = _files.ReadSourceMember(library, name, member);
        try { _ = snapshot.Source.Length == 0 ? ScreenDesign.New(member, ResolveDisplayMessage) : ScreenDesign.Open(member, snapshot.Source, ResolveDisplayMessage); }
        catch (PanelCompileException error) { return CommandResult.Error(error.Message); }
        return new CommandResult { Outcome = CommandOutcome.ScreenDesigner, Designer = new(library, name, member) };
    }
    private CommandResult ExecuteSdaMenu(CommandCall call)
    {
        var (library, name) = ResolveWorkObject(CommandParser.Unquote(call.GetOption("MENU")).ToUpperInvariant(), ObjectType.Menu, creating: true);
        if (!DisplayReplace(call) && _system.Objects.Exists(library, name, ObjectType.Menu)) throw new CpfException("CPF7302", "Menu already exists; specify REPLACE(*YES).");
        if (call.GetOption("JSONFILE") is { } jsonFile)
        {
            if (call.GetOption("SRCFILE") is not null || call.GetOption("SRCMBR") is not null) throw new CpfException("IPC0003", "Choose JSONFILE or an SDA source member.");
            var menu = Ipc.Core.Menu.MenuDefinition.Parse(ReadCertificateFile(CommandParser.Unquote(jsonFile), Ipc.Core.Menu.MenuDefinition.MaximumBytes));
            if (menu.Name != name || (menu.Library ?? "QSYS") != library) throw new CpfException("IPC0003", "JSON menu identity must match MENU.");
            _system.Menus.Register(menu, _job?.UserProfile ?? "QSECOFR", Ipc.Core.Menu.MenuDefinition.Serialize(menu), replace: DisplayReplace(call));
            return CommandResult.Ok("Menu imported from JSON.");
        }
        var keywords = new Dictionary<string, string>(call.Keywords, StringComparer.OrdinalIgnoreCase);
        if (!keywords.TryGetValue("SRCMBR", out var member) || member == "*MENU") keywords["SRCMBR"] = name;
        var source = LoadSourceMember(new CommandCall { Name = call.Name, Keywords = keywords }) ?? throw new CpfException("CPF2817", "CRTMNU requires an SDA source member.");
        try
        {
            var design = ScreenDesign.Open(name, source, ResolveDisplayMessage);
            _system.Menus.Register(design.CompileMenu(library, name), _job?.UserProfile ?? "QSECOFR", source, replace: DisplayReplace(call));
        }
        catch (PanelCompileException error) { return CommandResult.Error(error.Message); }
        return CommandResult.Ok("Menu compiled from SDA source.");
    }

}
