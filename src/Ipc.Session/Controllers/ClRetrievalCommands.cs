using System.Globalization;
using Ipc.Cl.Commands;
using Ipc.Cl.Compatibility;
using Ipc.Cl.Definitions;
using Ipc.Cl.Interpreter;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.System;
using Ipc.Core.Work;
using Ipc.Services.Commands;
using Ipc.Services.Work;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult? RunClContextCommand(CommandCall call, ClCommandContext context)
    {
        var name = BuiltinContract.CanonicalName(call.Name.Split('/')[^1]);
        if (name is not ("RTVJOBA" or "RTVDTAARA" or "CHGDTAARA" or "CRTDTAARA" or "SNDMSG" or "RCVMSG" or "SNDRPY" or "RMVMSG" or "RTVMSG")) return null;
        var key = ResolveCommandObject(call.Name);
        var definition = new CommandDefinitionStore(_system.Connections).Load(key.Library, key.Name.Value);
        if (definition.Builtin != name) return null; // A user command keeps ordinary CPP dispatch.
        var canonical = new CommandCall { Name = name, Keywords = call.Keywords, Positional = call.Positional };
        if (BuiltinContract.Validate(canonical) is { } error) return CommandResult.Error(error);
        var bound = CommandBinder.Bind(definition, canonical).Call;
        if (_job is null) throw new CpfException("CPF1241", "A job context is required.");
        new JobDataAreaStore(_system.Connections).RequireOwner(_job.Key);
        if (name == "RTVMSG") return ExecuteMessageDescription(bound, context);
        if (name is "SNDMSG" or "RCVMSG" or "SNDRPY" or "RMVMSG") return ExecuteMessageCommand(bound, context);
        if (name == "CHGDTAARA") return ExecuteJobDataArea(bound, context);
        if (name == "CRTDTAARA") return CreateDataArea(bound, context);
        var assignments = new List<(ProgramArgument Target, object? Value)>();
        ProgramArgument Target(string keyword)
        {
            var variable = bound.GetOption(keyword)?.Trim() ?? throw new CpfException("IPC0003", keyword + " requires a CL return variable.");
            if (!variable.StartsWith('&') || CommandParser.Tokenize(variable).Count != 1) throw new CpfException("IPC0003", keyword + " requires a CL return variable.");
            return context.Variable(variable);
        }
        void Character(string keyword, string value, int minimum)
        {
            var target = Target(keyword);
            minimum = Math.Max(minimum, value.Length);
            if (target.Type != "*CHAR" || target.Length < minimum) throw new CpfException("IPC0003", keyword + " requires a CHAR variable of at least " + minimum + " bytes.");
            assignments.Add((target, target.Normalize(value)));
        }
        if (name == "RTVJOBA")
        {
            foreach (var keyword in bound.Keywords.Keys)
            {
                switch (keyword)
                {
                    case "JOB": Character(keyword, _job.Key.Name, 10); break;
                    case "USER": Character(keyword, _job.Key.User, 10); break;
                    case "CURUSER": Character(keyword, _job.UserProfile ?? _job.Key.User, 10); break;
                    case "NBR":
                        if (_job.Key.Number > 999999) throw new CpfException("IPC0003", "This job number exceeds the native six-character NBR layout.");
                        Character(keyword, _job.Key.Number.ToString("D6", CultureInfo.InvariantCulture), 6); break;
                    case "CURLIB": Character(keyword, _job.CurrentLibrary is null or "*CRTDFT" ? "*NONE" : _job.CurrentLibrary, 10); break;
                    case "USRLIBL": Character(keyword, LibraryValues(_job.LibraryList ?? ""), 2750); break;
                    case "SYSLIBL": Character(keyword, LibraryValues(_system.SystemValues.Get(SystemValueNames.SystemLibraryList).Value), 165); break;
                    case "CCSID":
                        var target = Target(keyword);
                        if (target.Type != "*DEC" || target.Length != 5 || target.Decimals != 0) throw new CpfException("IPC0003", "CCSID requires a DEC(5,0) return variable.");
                        assignments.Add((target, target.Normalize((decimal)_job.Ccsid))); break;
                }
            }
        }
        else
        {
            var found = ReadDataArea(bound, context);
            var target = Target("RTNVAR");
            if (found.Type == "*DEC")
            {
                if (found.Length > 15) throw new CpfException("IPC0003", "CL supports decimal data areas of at most 15 digits.");
                if (target.Type != "*DEC") throw new CpfException("IPC0003", "Decimal data areas require a DEC return variable.");
                assignments.Add((target, target.Normalize(decimal.Round((decimal)found.Value, target.Decimals, MidpointRounding.ToZero))));
            }
            else if (found.Value is ProgramBuffer raw && target.Type == "*CHAR")
            {
                var value = raw.Ccsid == _job.Ccsid ? raw.ToArray() : StrictJobEncoding(_job.Ccsid).GetBytes(raw.ToText());
                if (target.Length < value.Length) throw new CpfException("IPC0003", "RTNVAR is smaller than the selected data-area bytes.");
                var padded = Enumerable.Repeat(StrictJobEncoding(_job.Ccsid).GetBytes(" ")[0], target.Length).ToArray();
                value.CopyTo(padded, 0);
                assignments.Add((target, target.Normalize(new ProgramBuffer(padded, _job.Ccsid))));
            }
            else
            {
                var value = found.Value is ProgramBuffer buffer ? buffer.ToText() : (bool)found.Value ? "1" : "0";
                var length = StrictJobEncoding(_job.Ccsid).GetByteCount(value);
                if (target.Type == "*LGL" && length == 1 && value is "0" or "1") assignments.Add((target, target.Normalize(value)));
                else Character("RTNVAR", value, length);
            }
        }
        // No earlier output changes if any later output's type, length or encoding fails.
        foreach (var assignment in assignments) assignment.Target.Value = assignment.Value;
        return CommandResult.Ok();
    }
    private string LibraryValues(string list) => string.Concat(list.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(library => (_system.Libraries.LibraryExists(library) ? library : "*DELETED").PadRight(10) + " "));

    private (byte[] Data, int Start, int Length, JobDataArea Area) SelectJobDataArea(CommandCall call, Func<string, string>? resolve = null)
    {
        if (_job is null) throw new CpfException("CPF1241", "A job context is required.");
        var selection = DataAreaSelection(call, resolve);
        var area = selection.Name switch {
            "*LDA" => JobDataArea.Local, "*GDA" => JobDataArea.Group, "*PDA" => JobDataArea.Initialization,
            _ => throw new CpfException("IPC0126", "Invalid job-local data area.") };
        var data = new JobDataAreaStore(_system.Connections).Read(_job.Key, area);
        var start = selection.Start ?? 1; var length = selection.Length ?? data.Length;
        if (start < 1 || length < 1 || start - 1 >= data.Length || length > data.Length - start + 1) throw new CpfException("IPC0126", "Invalid data-area byte range.");
        return (data, start, length, area);
    }
}
