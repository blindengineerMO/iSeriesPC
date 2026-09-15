using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Services.Security;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private void RegisterProfileCommands()
    {
        _catalog.Register("CRTUSRPRF", ExecuteProfile);
        _catalog.Register("CHGUSRPRF", ExecuteProfile);
        _catalog.Register("DLTUSRPRF", ExecuteProfile);
    }
    private CommandResult ExecuteProfile(CommandCall call)
    {
        new ServiceAuthorization(_system.Connections).RequireSpecial(SpecialAuthority.SecurityAdministrator);
        string Value(string key, string fallback = "") => CommandParser.Unquote(call.GetOption(key) ?? fallback);
        var name = Value("USRPRF").ToUpperInvariant(); if (!ObjectName.IsValid(name)) throw new CpfException("IPC0003", "A valid USRPRF name is required.");
        if (call.Name == "DLTUSRPRF") { _system.Security.Profiles.Delete(name); return CommandResult.Ok("User profile deleted."); }
        var create = call.Name == "CRTUSRPRF";
        var profile = create ? new UserProfile { Name = name } : _system.Security.Profiles.Get(name);
        if (call.GetOption("USRCLS") is not null) profile.UserClass = UserClasses.Parse(Value("USRCLS"));
        var authorities = Value("SPCAUT", create ? "*USRCLS" : "*SAME").ToUpperInvariant();
        if (authorities == "*USRCLS") profile.SpecialAuthorities = UserClasses.BaselineAuthorities(profile.UserClass);
        else if (authorities != "*SAME")
        {
            profile.SpecialAuthorities = SpecialAuthority.None;
            foreach (var part in CommandParser.Tokenize(authorities)) profile.SpecialAuthorities |= SpecialAuthorities.Parse(part);
        }
        if (call.GetOption("STATUS") is not null) profile.Status = Value("STATUS").ToUpperInvariant() switch {
            "*ENABLED" => ProfileStatus.Enabled, "*DISABLED" => ProfileStatus.Disabled, _ => throw new CpfException("IPC0003", "STATUS must be *ENABLED or *DISABLED.") };
        if (call.GetOption("TEXT") is not null) profile.Description = Value("TEXT");
        if (profile.Description?.Length > 50 || profile.Description?.Any(c => !Ipc.Terminal.TerminalGlyph.IsSingleCell(c)) == true) throw new CpfException("IPC0003", "Profile text must fit 50 terminal characters.");
        if (call.GetOption("GRPPRF") is not null)
        {
            var group = Value("GRPPRF").ToUpperInvariant();
            profile.GroupProfile = group == "*NONE" ? null : _system.Security.Profiles.Get(group).Name;
        }
        string? Initial(string key, string? existing, string none)
        {
            if (call.GetOption(key) is null) return existing;
            var value = Value(key).ToUpperInvariant(); if (value == none) return null;
            if (key == "INLMNU" && value == "*SIGNOFF") return value;
            _ = QualifiedName.Parse(value, "*LIBL"); return value;
        }
        profile.InitialProgram = Initial("INLPGM", profile.InitialProgram, "*NONE");
        profile.InitialMenu = Initial("INLMNU", profile.InitialMenu, "*MAIN");
        if (call.GetOption("CURLIB") is not null)
        {
            var library = Value("CURLIB").ToUpperInvariant();
            if (library != "*CRTDFT" && !ObjectName.IsValid(library)) throw new CpfException("IPC0003", "Invalid current library.");
            profile.InitialCurrentLibrary = library;
        }
        if (call.GetOption("CCSID") is not null)
        {
            if (!int.TryParse(Value("CCSID"), out var ccsid) || ccsid is not (37 or 500 or 1047 or 850 or 819 or 1208 or 367)) throw new CpfException("IPC0003", "Unsupported CCSID.");
            profile.Ccsid = ccsid;
        }
        var passwordMode = Value("PASSWORD", create ? "*NONE" : "*SAME").ToUpperInvariant();
        if (passwordMode is not ("*NONE" or "*SAME")) throw new CpfException("IPC0134", "Use PWDFILE for a private password file, or PASSWORD(*NONE/*SAME).");
        string? password = null;
        if (call.GetOption("PWDFILE") is not null)
        {
            if (call.GetOption("PASSWORD") is not null) throw new CpfException("IPC0003", "Specify one password operation.");
            password = System.Text.Encoding.UTF8.GetString(ReadCertificateFile(Value("PWDFILE"), 4096, privateOnly: true)).TrimEnd('\r', '\n');
        }
        _system.Security.Profiles.SaveSettings(profile, create, password, removePassword: password is null && passwordMode == "*NONE");
        return CommandResult.Ok(create ? "User profile created." : "User profile changed.");
    }
}
