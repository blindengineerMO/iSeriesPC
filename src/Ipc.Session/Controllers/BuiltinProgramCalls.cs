using System.Globalization;
using System.Text;
using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services.Work;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult ExecuteBuiltinProgram(ObjectDescriptor program, IReadOnlyList<object?> parameters)
    {
        BuiltinProgramService.Validate(program);
        using var scope = EnterProgram(program);
        _cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (parameters.Count is < 2 or > 3) throw InvalidApi("QCMDEXC requires two or three parameters.");
            var command = ApiCharacter(parameters[0]);
            var length = ApiCommandLength(parameters[1]);
            if (length < 1 || length > 32702 || length != decimal.Truncate(length) || length > command.Length)
                throw InvalidApi("QCMDEXC command length must be an integer from 1 to 32702 within the supplied buffer.");
            if (parameters.Count == 3)
            {
                var igc = ApiCharacter(parameters[2]);
                if (igc.Length < 3 || new ProgramBuffer(igc.ToArray().AsSpan(0, 3), igc.Ccsid).ToText() != "IGC")
                    throw InvalidApi("QCMDEXC IGC process control must be IGC.");
            }
            // Decode only the selected bytes. Trailing storage may contain non-text data.
            var line = new ProgramBuffer(command.ToArray().AsSpan(0, (int)length), command.Ccsid).ToText();
            if (line.Any(character => character is '\0' or '\r' or '\n')) throw InvalidApi("QCMDEXC accepts one command line.");
            var result = DispatchCommand(CommandParser.Parse(line));
            if (result.Outcome is not (CommandOutcome.Continue or CommandOutcome.Error) || result.WorkList is not null)
                return CommandResult.Error("QCMDEXC command requires an interactive screen.", "CPF0006");
            return result;
        }
        catch (ClParseException error) { return CommandResult.Error(error.Message, "CPF0006"); }
        catch (ClRuntimeException error) { return CommandResult.Error(error.Message, error.MessageId); }
        catch (CpfException error) { return CommandResult.Error(error.Message, error.MessageId); }
        catch (EncoderFallbackException) { return CommandResult.Error("QCMDEXC parameter is not representable in the job CCSID.", "CPF0006"); }
    }

    private ProgramBuffer ApiCharacter(object? parameter)
    {
        if (parameter is ProgramArgument reference)
        {
            if (reference.Type is not (null or "*CHAR" or "*RAW")) throw InvalidApi("QCMDEXC requires a character command buffer.");
            parameter = (object?)reference.ToBuffer() ?? reference.Value;
        }
        if (parameter is ProgramConstant constant)
        {
            if (constant.Type is not ("*CHAR" or "*RAW")) throw InvalidApi("QCMDEXC requires a character command buffer.");
            parameter = constant.Buffer;
        }
        var ccsid = _job?.Ccsid ?? _system.Config.Ccsid;
        if (parameter is ProgramBuffer buffer)
        {
            if (buffer.Ccsid != ccsid) throw InvalidApi("QCMDEXC command buffer must use the executing job CCSID.");
            return buffer;
        }
        // The interpreted RPG and typed command adapters supply semantic scalar inputs.
        if (parameter is not string text) throw InvalidApi("QCMDEXC requires a character command buffer.");
        var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        return new(encoding.GetBytes(text), ccsid);
    }

    private static decimal ApiCommandLength(object? parameter)
    {
        if (parameter is ProgramArgument reference)
        {
            if (reference.Type is not null && (reference.Type != "*DEC" || reference.Length != 15 || reference.Decimals != 5))
                throw InvalidApi("QCMDEXC length requires packed DEC(15,5).");
            parameter = (object?)reference.ToBuffer() ?? reference.Value;
        }
        if (parameter is ProgramConstant constant)
        {
            if (constant.Type != "*RAW" && (constant.Type != "*DEC" || constant.Length != 15 || constant.Decimals != 5))
                throw InvalidApi("QCMDEXC length requires packed DEC(15,5).");
            parameter = constant.Buffer;
        }
        if (parameter is ProgramBuffer buffer)
            return Convert.ToDecimal(ClCallArgument.Bind(new("*RAW", buffer.Length, 0, buffer, buffer), "*DEC", 15, 5).Value, CultureInfo.InvariantCulture);
        if (parameter is decimal number) return number;
        throw InvalidApi("QCMDEXC length requires packed DEC(15,5).");
    }

    private static CpfException InvalidApi(string message) => new("CPF0006", message);
}
