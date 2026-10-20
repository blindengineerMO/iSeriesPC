using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Security;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult ExecuteRemoveMember(CommandCall call)
    {
        string Required(string keyword, int position)
        {
            var option = call.GetOption(keyword);
            if (option is not null && call.Positional.Count > position)
                throw new CpfException("IPC0003", "Duplicate " + keyword + " value.");
            var value = CommandParser.Unquote(option ?? (call.Positional.Count > position ? call.Positional[position] : null)).ToUpperInvariant();
            if (value.Length == 0) throw new CpfException("IPC0003", "RMVM requires " + keyword + ".");
            return value;
        }

        var file = Required("FILE", 0);
        var selector = Required("MBR", 1);
        var generic = selector.EndsWith('*') && ObjectName.IsValid(selector[..^1]);
        if (selector != "*ALL" && !generic && !ObjectName.IsValid(selector))
            throw new CpfException("IPC0003", "MBR requires a member name, a name prefix followed by * or *ALL.");
        var (library, name) = ResolveFileReference(file);
        new ServiceAuthorization(_system.Connections).RequireObject(library, name, ObjectType.File,
            AuthorityBit.ObjectExist | AuthorityBit.ObjectOperate);
        if (selector != "*ALL" && !generic)
        {
            if (!_files.MemberExists(library, name, selector))
                return CommandResult.Error($"Member {selector} not found in file {name}.");
            _cancellationToken.ThrowIfCancellationRequested();
            _files.RemoveMember(library, name, selector);
            return CommandResult.Ok($"Member {selector} removed from file {name}.");
        }

        // Select once, then publish each removal independently. A blocked member
        // must not undo earlier successes or prevent later selected removals.
        var selected = _files.ListMembers(library, name)
            .Where(member => selector == "*ALL" || member.StartsWith(selector[..^1], StringComparison.Ordinal)).ToArray();
        if (selected.Length == 0) return CommandResult.Error($"No members match {selector} in file {name}.");
        var removed = 0;
        var errors = new List<string>();
        foreach (var member in selected)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            try { _files.RemoveMember(library, name, member); removed++; }
            catch (CpfException error) { errors.Add(member + ": " + error.Message); }
        }
        var message = $"{removed} members removed from file {name}.";
        return errors.Count == 0 ? CommandResult.Ok(message) : CommandResult.Error(
            $"CPF7301: {errors.Count} members not removed from file {name}. {message} " + string.Join(" ", errors), "CPF7301");
    }
}
