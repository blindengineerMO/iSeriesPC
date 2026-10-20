using System.Globalization;
using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Dsp;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;
using Ipc.Services.Work;
using Ipc.Session;
using Ipc.Terminal;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private RpgWorkstationLayout DescribeRpgWorkstation(RpgWorkstationRequest request)
    {
        var (library, name) = ResolveWorkObject(request.DescriptionFile, ObjectType.File);
        return DescribeRpgWorkstation(request, library, name, new DisplayFileStore(_system.Connections, ResolveDisplayMessage).Load(library, name));
    }

    private static RpgWorkstationLayout DescribeRpgWorkstation(RpgWorkstationRequest request, string library, string name, PanelDefinition panel, string? family = null)
    {
        if (request.Renames.Any(rename => !panel.Records.Any(r => r.Name == rename.External)))
            throw new CpfException("CPF4131", "RENAME references an unavailable display record.");
        string RecordName(string external) => RpgWorkstationBindings.RecordName(request, request.Renames.SingleOrDefault(r => r.External == external)?.Name ?? external);
        var records = panel.Records.Select(record =>
        {
            var subfile = record.Keywords.Any(k => k.Name == "SFL");
            var control = record.Keywords.FirstOrDefault(k => k.Name == "SFLCTL");
            var size = subfile ? panel.Records.Single(r => r.Keywords.Any(k => k.Name == "SFLCTL" && k.Arguments[0] == record.Name))
                .Keywords.Single(k => k.Name == "SFLSIZ").Arguments[0] : "0";
            var fields = record.Fields.Where(f => !f.Constant).Select(field => new RpgDisplayField(field.Name, request.Prefix + field.Name,
                field.Type switch { PanelFieldType.Numeric => RpgFieldKind.Packed, PanelFieldType.Date => RpgFieldKind.Date,
                    PanelFieldType.Time => RpgFieldKind.Time, PanelFieldType.Timestamp => RpgFieldKind.Timestamp, _ => RpgFieldKind.Character },
                field.Type switch { PanelFieldType.Date => 10, PanelFieldType.Time => 8, PanelFieldType.Timestamp => 26, _ => field.Length }, field.Decimals,
                field.Usage is PanelFieldUsage.Input or PanelFieldUsage.Both,
                field.Usage is PanelFieldUsage.Output or PanelFieldUsage.Both or PanelFieldUsage.Hidden,
                field.Keywords.Any(k => k.Name == "SFLMSGKEY"))).ToArray();
            return new RpgDisplayRecord(record.Name, RecordName(record.Name), fields, subfile, record.Keywords.Any(k => k.Name == "SFLMSGRCD"),
                control is null ? null : RecordName(control.Arguments[0]), int.Parse(size, CultureInfo.InvariantCulture));
        }).ToArray();
        var indicatorArea = panel.Keywords.Any(k => k.Name == "INDARA");
        var signature = RpgWorkstationLayout.SignatureFor(records, indicatorArea);
        var layout = new RpgWorkstationLayout(library + "/" + name, signature, records) { IndicatorArea = indicatorArea };
        RpgWorkstationBindings.Validate(new(request, layout, family)); return layout;
    }

    private IRpgWorkstation OpenRpgWorkstation(RpgWorkstationBinding binding)
    {
        if (_programTerminal is null || _job?.Type != JobType.Interactive) throw new CpfException("CPF0861", "Display file I/O requires an interactive terminal connection.");
        var runtimeFile = binding.Request.RuntimeFile == "*EXTDESC" ? binding.Layout.DescriptionFile : binding.Request.RuntimeFile;
        var (library, name) = ResolveWorkObject(runtimeFile, ObjectType.File);
        var created = _system.Objects.GetRequired(library, name, ObjectType.File).Created;
        var allocation = new JobLockStore(_system.Connections).AcquireWithLifetime(_job.Key, new(library, name, ObjectType.File),
            JobLockMode.SharedRead, TimeSpan.Zero, _cancellationToken, "RpgDisplay", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DisplayFileStore(_system.Connections, ResolveDisplayMessage);
            void Validate()
            {
                new JobDataAreaStore(_system.Connections).RequireOwner(_job.Key);
                if (_system.Objects.GetRequired(library, name, ObjectType.File).Created != created)
                    throw new CpfException("CPF4131", "The open display file was replaced.");
                var current = DescribeRpgWorkstation(binding.Request, library, name, store.Load(library, name), binding.Family);
                var signature = binding.Layout.IndicatorArea is null ? RpgWorkstationLayout.SignatureFor(current.Records) : current.Signature;
                if (signature != binding.Layout.Signature)
                    throw new CpfException("CPF4131", "Display record format changed; recompile the RPG program.");
            }
            Validate(); var panel = store.Load(library, name); var file = store.Open(library, name, EnvironmentForJob()); Validate();
            var handle = new RpgDisplayHandle(binding, panel, file, new(library, name), Validate, allocation);
            handle.Bind(_programTerminal, _cancellationToken, _job.Ccsid); return handle;
        }
        catch { allocation.Dispose(); throw; }
    }

    private void RebindRpgWorkstation(IRpgWorkstation workstation)
    {
        if (_programTerminal is null || _job?.Type != JobType.Interactive) throw new CpfException("CPF0861", "Display file I/O requires an interactive terminal connection.");
        ((RpgDisplayHandle)workstation).Bind(_programTerminal, _cancellationToken, _job.Ccsid);
    }

    private sealed class RpgDisplayHandle(RpgWorkstationBinding binding, PanelDefinition definition, DisplayFileSession file,
        QualifiedName key, Action validate, IDisposable allocation) : IRpgWorkstation
    {
        private bool _disposed;
        private IProgramTerminal _terminal = null!;
        private CancellationToken _cancellation;
        private int _ccsid;
        private string? _written;
        private bool[] _outputFlags = new bool[100];
        private readonly Dictionary<string, int> _changedPosition = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _current = new(StringComparer.Ordinal);
        public void Bind(IProgramTerminal terminal, CancellationToken cancellation, int ccsid)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellation.ThrowIfCancellationRequested(); validate();
            _terminal = terminal; _cancellation = cancellation; _ccsid = ccsid;
        }
        public RpgWorkstationResult Transfer(RpgWorkstationOperation operation, string record, IReadOnlyDictionary<string, object?> values,
            IReadOnlyList<bool> indicators, int? relativeRecord = null, Action<RpgWorkstationResult>? prepare = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); _cancellation.ThrowIfCancellationRequested(); validate();
            if (record.Length == 0 && operation == RpgWorkstationOperation.Read) record = _written ?? "";
            var format = binding.Layout.Records.SingleOrDefault(r => r.Name == record) ?? throw new CpfException("CPF4131", "A display record format is required.");
            var external = definition.Records.Single(r => r.Name == format.External);
            try
            {
                if (operation is RpgWorkstationOperation.ReadChanged or RpgWorkstationOperation.Chain)
                {
                    if (!format.Subfile) throw new CpfException("CPF4131", "READC/CHAIN requires a subfile format.");
                    var state = file.Subfile(format.External);
                    var row = operation == RpgWorkstationOperation.ReadChanged ? state.PeekNextChanged(_changedPosition.GetValueOrDefault(record)) :
                        relativeRecord is { } rrn && state.RecordNumbers.Contains(rrn) ? state.Read(rrn) : null;
                    var input = row is null ? new Dictionary<string, object?>() : InputValues(format, external, row.Values, all: true);
                    var result = new RpgWorkstationResult(input, new Dictionary<int, bool>(), row is not null,
                        operation == RpgWorkstationOperation.ReadChanged && row is null, row?.Number);
                    prepare?.Invoke(result);
                    if (row is not null)
                    {
                        _current[record] = row.Number;
                        if (operation == RpgWorkstationOperation.ReadChanged) { state.AcknowledgeChanged(row.Number); _changedPosition[record] = row.Number; }
                    }
                    else
                    {
                        _current.Remove(record);
                        if (operation == RpgWorkstationOperation.ReadChanged) _changedPosition.Remove(record);
                    }
                    return result;
                }
                if (operation is RpgWorkstationOperation.Write or RpgWorkstationOperation.Exchange or RpgWorkstationOperation.Update)
                {
                    if (format.Subfile)
                    {
                        if (operation == RpgWorkstationOperation.Exchange) throw new CpfException("CPF4131", "EXFMT requires a non-subfile record.");
                        var output = OutputValues(format, external, values, all: true);
                        var state = file.Subfile(format.External);
                        if (operation == RpgWorkstationOperation.Update)
                        {
                            if (!_current.TryGetValue(record, out var number)) throw new CpfException("CPF4131", "UPDATE requires a successful subfile read.");
                            state.Update(number, output, indicators);
                        }
                        else state.Write(relativeRecord ?? throw new CpfException("CPF4131", "Subfile WRITE requires an RRN."), output, indicators);
                    }
                    else
                    {
                        if (operation == RpgWorkstationOperation.Update) throw new CpfException("CPF4131", "Display UPDATE requires a subfile record.");
                        file.Write(format.External, OutputValues(format, external, values), indicators);
                        _written = record; _outputFlags = indicators.ToArray();
                        if (format.ControlFor is { } data && external.Keywords.Any(k => k.Name is "SFLCLR" or "SFLINZ" && k.Enabled(indicators)))
                        { _current.Remove(data); _changedPosition.Remove(data); }
                    }
                }
                if (operation is RpgWorkstationOperation.Write or RpgWorkstationOperation.Update)
                    return new(new Dictionary<string, object?>(), new Dictionary<int, bool>());
                if (format.Subfile || _written != record) throw new CpfException("CPF4131", "Display READ requires prior output of the same non-subfile record.");
                using var display = new ProgramDisplay(file, key, () => { _cancellation.ThrowIfCancellationRequested(); validate(); });
                var response = _terminal.Read(display, _cancellation); validate();
                if (!response.Accepted || response.Indicators.Length != 100) throw new CpfException("CPF4131", "Terminal returned an incomplete display record.");
                var attention = response.Aid is AidKey.Pa1 or AidKey.Pa2 or AidKey.Pa3 or AidKey.Clear or AidKey.Print ||
                    definition.Keywords.Concat(external.Keywords).Any(k => k.Name.StartsWith("CA", StringComparison.Ordinal) && k.Enabled(_outputFlags) &&
                        response.Aid == (AidKey)((int)AidKey.Pf1 + int.Parse(k.Name[2..], CultureInfo.InvariantCulture) - 1));
                var updates = attention ? new Dictionary<string, object?>() : InputValues(format, external, response.Values);
                var flags = ResponseIndicators(definition.Keywords.Concat(external.Keywords).Concat(external.Fields.SelectMany(f => f.Keywords)))
                    .ToDictionary(n => n, n => response.Indicators[n]);
                var answer = new RpgWorkstationResult(updates, flags); prepare?.Invoke(answer);
                _changedPosition.Clear(); _current.Clear(); return answer;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or EncoderFallbackException)
            { throw new CpfException("CPF4131", error.Message); }
        }

        private Dictionary<string, object?> OutputValues(RpgDisplayRecord format, PanelRecord external, IReadOnlyDictionary<string, object?> values, bool all = false)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var field in format.Fields.Where(f => f.Output || all))
            {
                var panel = external.Fields.Single(f => f.Name == field.External); var value = values.GetValueOrDefault(field.Name);
                if (field.Opaque)
                {
                    if (value is not ProgramBuffer raw || raw.Length != 4) throw new ArgumentException("SFLMSGKEY requires four opaque bytes.");
                    value = Encoding.Latin1.GetString(raw.ToArray());
                }
                else if (field.Kind == RpgFieldKind.Character)
                {
                    var text = value is ProgramBuffer buffer ? buffer.ToText() : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                    var encoding = (Encoding)CodePage.FromCcsid(_ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
                    if (encoding.GetByteCount(text) > field.Length) throw new ArgumentException("Display character value exceeds its byte length.");
                    if (text.Any(c => !TerminalGlyph.IsSingleCell(c))) throw new ArgumentException("Display values require supported single-cell characters.");
                    value = text.TrimEnd(' ');
                }
                else if (field.Kind == RpgFieldKind.Packed)
                {
                    var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    if (decimal.Round(number, field.Decimals) != number || decimal.Abs(number).ToString("F" + field.Decimals, CultureInfo.InvariantCulture).Replace(".", "").TrimStart('0').Length > field.Length)
                        throw new ArgumentException("Display numeric value exceeds its precision.");
                    value = number;
                }
                else if (value is DateTimeOffset stamp) value = field.Kind == RpgFieldKind.Date ? DateOnly.FromDateTime(stamp.DateTime) : stamp.DateTime;
                result[panel.BindingName] = value;
            }
            return result;
        }
        private Dictionary<string, object?> InputValues(RpgDisplayRecord format, PanelRecord external, IReadOnlyDictionary<string, object?> values, bool all = false)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var field in format.Fields.Where(f => f.Input || all))
            {
                var panel = external.Fields.Single(f => f.Name == field.External);
                if (!values.TryGetValue(panel.BindingName, out var value)) throw new ArgumentException("Display input omitted a field.");
                result[field.Name] = field.Opaque && value is string raw ? new ProgramBuffer(Encoding.Latin1.GetBytes(raw), _ccsid) : value switch
                { DateOnly date => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), TimeOnly time => time.ToTimeSpan(), _ => value };
            }
            return result;
        }
        public void Dispose() { if (_disposed) return; _disposed = true; allocation.Dispose(); }
    }
}
