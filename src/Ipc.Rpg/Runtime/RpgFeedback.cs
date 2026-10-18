using System.Globalization;
using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Text;
using Ipc.Rpg.Model;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private readonly Dictionary<string, byte[]> _fileFeedback = new(StringComparer.OrdinalIgnoreCase);
    private byte[]? _programFeedback;
    private RpgStatement? _feedbackStatement;
    private string _feedbackRoutine = "*INIT";
    private string? _feedbackCallTarget;
    internal int ParameterCount { get; set; }

    private void InitializeFeedback()
    {
        if (GlobalContext is not null)
        {
            foreach (var pair in GlobalContext._fileFeedback) _fileFeedback.Add(pair.Key, pair.Value);
            _programFeedback = GlobalContext._programFeedback; return;
        }
        foreach (var name in _program.FileSpecs)
        {
            var binding = _program.FileInformation.SingleOrDefault(f => f.File == name);
            var bytes = binding is null ? Enumerable.Repeat(NativeBlank, 80).ToArray() : _nativeStructures[binding.Structure];
            _fileFeedback.Add(name, bytes);
            FeedbackText(bytes, 0, 8, name); FeedbackText(bytes, 8, 2, "00"); FeedbackNumber(bytes, 10, 5, 0);
        }
        var psds = _program.DataStructures.SingleOrDefault(d => d.ProgramStatus && !d.IsTemplate && !_program.GlobalStructures.Contains(d));
        if (psds is null) return;
        _programFeedback = _nativeStructures[psds.Name];
        FeedbackText(_programFeedback, 0, 10, _program.Name);
        FeedbackNumber(_programFeedback, 10, 5, 0); FeedbackNumber(_programFeedback, 15, 5, 0);
        FeedbackText(_programFeedback, 80, 10, _program.Library);
        FeedbackText(_programFeedback, 243, 10, _host.Job?.Name ?? "");
        FeedbackText(_programFeedback, 253, 10, _host.Job?.User ?? "");
        FeedbackNumber(_programFeedback, 263, 6, _host.Job?.Number ?? 0);
        FeedbackText(_programFeedback, 333, 10, _program.Name); FeedbackText(_programFeedback, 343, 10, _program.Name);
        FeedbackText(_programFeedback, 357, 10, _host.CurrentUser ?? _host.Job?.User ?? "");
    }

    internal void BeginFeedbackCall(int parameterCount)
    {
        if (parameterCount is < 0 or > 999) throw new RpgRuntimeException("RPG entry parameter count exceeds 999.") { StatusCode = 103 };
        ParameterCount = parameterCount; _feedbackStatement = null; _feedbackRoutine = "*INIT"; _feedbackCallTarget = null;
        if (!_program.IsNativeProcedure && _programFeedback is { } bytes) FeedbackNumber(bytes, 36, 3, parameterCount);
    }
    internal void SetFeedbackOperation(RpgStatement statement, string routine, bool preserveCallTarget = false)
    {
        if (!preserveCallTarget || !ReferenceEquals(_feedbackStatement, statement)) _feedbackCallTarget = null;
        _feedbackStatement = statement; _feedbackRoutine = routine;
    }
    internal void SetFeedbackCallTarget(string name) => _feedbackCallTarget = name;

    private void FeedbackText(byte[] bytes, int offset, int width, string text)
    {
        if (offset >= bytes.Length) return;
        var target = bytes.AsSpan(offset, Math.Min(width, bytes.Length - offset)); target.Fill(NativeBlank);
        // Diagnostic text must not replace the error being reported with an encoding error.
        var encoding = (Encoding)CodePage.FromCcsid(NativeCcsid).Clone(); encoding.EncoderFallback = EncoderFallback.ReplacementFallback;
        try { encoding.GetEncoder().Convert(text.AsSpan(), target, true, out _, out _, out _); }
        catch (ArgumentException) { target.Fill(NativeBlank); } // Even one character may exceed a partial PSDS field.
    }
    private void FeedbackNumber(byte[] bytes, int offset, int width, int value)
    {
        var digits = value.ToString("D" + width, CultureInfo.InvariantCulture);
        // Zoned decimal is a numeric representation, independent of the job's character CCSID.
        for (var i = 0; i < width && offset + i < bytes.Length; i++) bytes[offset + i] = (byte)(0xF0 | (digits[i] - '0'));
    }

    private void RefreshFeedbackFlags(string file)
    {
        if (!_fileFeedback.TryGetValue(file, out var bytes)) return;
        var open = DiskState.Files.ContainsKey(file) || WorkstationState.Files.ContainsKey(file);
        FeedbackText(bytes, 8, 1, open ? "1" : "0");
        FeedbackText(bytes, 9, 1, _fileStatus.GetValueOrDefault(file)?.EndOfFile == true ? "1" : "0");
    }
    private void RecordFileFeedback(string file, int status)
    {
        if (!_fileFeedback.TryGetValue(file, out var bytes)) return;
        FeedbackNumber(bytes, 10, 5, status); RefreshFeedbackFlags(file);
        var statement = _feedbackStatement;
        var opcode = statement?.Opcode switch
        {
            null => "OPEN", RpgOpcode.ReadPe => "REDPE", RpgOpcode.Unlock => "UNLCK", RpgOpcode.Update => "UPDAT",
            RpgOpcode.Delete => "DELET", _ => statement.Opcode.ToString().ToUpperInvariant()
        };
        var disk = _program.DiskFiles.SingleOrDefault(f => f.Request.Name == file);
        var workstation = _program.Workstations.SingleOrDefault(f => f.Request.Name == file);
        var operand = statement is null ? file : statement.Factor2 ?? statement.Factor1 ?? file;
        var recordOperand = operand.ToUpperInvariant();
        var isRecord = disk?.Layout.Record == recordOperand || workstation?.Layout.Records.Any(r => r.Name == recordOperand) == true;
        FeedbackText(bytes, 15, 6, opcode.PadRight(5)[..5] + (statement is null ? "I" : isRecord ? "R" : "F"));
        FeedbackText(bytes, 21, 8, _feedbackRoutine);
        FeedbackText(bytes, 29, 8, (statement?.LineNumber ?? 0).ToString("D8", CultureInfo.InvariantCulture));
        if (statement is not null && statement.Opcode is not (RpgOpcode.Open or RpgOpcode.Close or RpgOpcode.Unlock))
        {
            var record = disk?.Layout.ExternalRecord ?? workstation?.Layout.Records.FirstOrDefault(r => r.Name == recordOperand)?.External;
            if (record is not null) FeedbackText(bytes, 37, 8, record);
        }
        FeedbackText(bytes, 45, 7, "");
    }

    internal void RecordFeedbackError(RpgRuntimeException error)
    {
        if (error.FeedbackRecorded) return;
        error.FeedbackRecorded = true;
        var file = error.FileError ? error.StatusFile : null;
        var id = (error.ServiceError as CpfException)?.MessageId ?? "";
        if (file is not null && _fileFeedback.TryGetValue(file, out var feedback))
        {
            FeedbackText(feedback, 45, 7, id);
            if (_programFeedback is { } program)
            {
                FeedbackText(program, 174, 10, file); FeedbackText(program, 200, 8, file);
                if (program.Length > 208) feedback.AsSpan(10, Math.Min(35, program.Length - 208)).CopyTo(program.AsSpan(208));
            }
            return;
        }
        if (_programFeedback is not { } bytes) return;
        // File status has its own INFDS. PSDS *STATUS records program errors only.
        if (error.FileError) return;
        var previous = bytes.Length >= 15 ? bytes.AsSpan(10, 5).ToArray() : null;
        if (previous is not null && bytes.Length > 15) previous.AsSpan(0, Math.Min(5, bytes.Length - 15)).CopyTo(bytes.AsSpan(15));
        FeedbackNumber(bytes, 10, 5, error.StatusCode ?? 9999);
        FeedbackText(bytes, 20, 8, (_feedbackStatement?.LineNumber ?? 0).ToString("D8", CultureInfo.InvariantCulture));
        var routine = _feedbackRoutine;
        if (_feedbackStatement?.Opcode is RpgOpcode.Call or RpgOpcode.CallB or RpgOpcode.CallP)
        {
            var target = _feedbackCallTarget ?? _feedbackStatement.Factor1 ?? _feedbackStatement.Value ?? routine;
            var arguments = target.IndexOf('('); if (arguments >= 0) target = target[..arguments];
            routine = target.Trim().Trim('\'').Split('/')[^1];
        }
        FeedbackText(bytes, 28, 8, routine);
        FeedbackText(bytes, 39, 3, id.Length == 7 ? id[..3] : "");
        FeedbackText(bytes, 42, 4, id.Length == 7 ? id[3..] : "");
        FeedbackText(bytes, 90, 80, error.Message ?? "");
    }
}
