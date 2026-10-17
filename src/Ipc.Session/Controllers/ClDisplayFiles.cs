using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Ipc.Cl.Interpreter;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Dsp;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private ClDatabaseFile DescribeClDisplayFile(ClFileRequest request, string library, string name, PanelDefinition panel)
    {
        var selected = request.RecordFormat == "*ALL" ? panel.Records.Take(99).ToArray() : request.RecordFormat.Split(' ')
            .Select(format => panel.Records.SingleOrDefault(r => r.Name == format) ?? throw new CpfException("CPF4131", "Display record format is not available.")).ToArray();
        if (selected.Length == 0) throw new CpfException("CPF4131", "No display record formats are declared.");
        var fields = new Dictionary<string, ClFileField>(StringComparer.Ordinal);
        var records = new List<ClDisplayRecord>();
        void Add(ClFileField field)
        {
            if (fields.TryGetValue(field.Name, out var previous) && previous != field)
                throw new CpfException("CPF4131", "Display field has incompatible definitions across formats: " + field.Name);
            fields[field.Name] = field;
        }
        foreach (var record in selected)
        {
            var subfile = record.Keywords.Any(k => k.Name == "SFL");
            var control = record.Keywords.FirstOrDefault(k => k.Name == "SFLCTL");
            if (subfile && !record.Keywords.Any(k => k.Name == "SFLMSGRCD") || control is not null &&
                !panel.Records.Single(r => r.Name == control.Arguments[0]).Keywords.Any(k => k.Name == "SFLMSGRCD"))
                throw new CpfException("IPC0006", "CL supports message subfiles; ordinary subfile record I/O requires RPG.");
            foreach (var field in record.Fields.Where(f => !f.Constant))
            {
                var numeric = field.Type == PanelFieldType.Numeric;
                if (numeric && field.Length <= 15 && field.Decimals > 9) throw new CpfException("IPC0006", "CL display decimals support at most nine decimal positions.");
                Add(new(field.Name, numeric && field.Length <= 15 ? "*DEC" : "*CHAR", numeric && field.Length > 15 ? field.Length / 2 + 1 : field.Length,
                    numeric && field.Length <= 15 ? field.Decimals : 0));
            }
            var keywords = panel.Keywords.Concat(record.Keywords).Concat(record.Fields.SelectMany(f => f.Keywords)).ToArray();
            var indicators = keywords.SelectMany(k => k.Conditions).Select(c => c.Indicator).Concat(record.Fields.SelectMany(f => f.Conditions).Select(c => c.Indicator))
                .Concat(ResponseIndicators(keywords)).Distinct().Order().ToArray();
            foreach (var indicator in indicators) Add(new("IN" + indicator.ToString("00", CultureInfo.InvariantCulture), "*LGL", 1, 0));
            records.Add(new(record.Name, record.Fields.Where(f => !f.Constant).Select(f => f.Name)
                .Concat(indicators.Select(n => "IN" + n.ToString("00", CultureInfo.InvariantCulture))).Distinct().ToArray(), subfile));
        }
        var ordered = fields.Values.OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
        var signature = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Fields = ordered, Records = records, FormatCount = panel.Records.Count })));
        var result = new ClDatabaseFile(library + "/" + name, records[0].Name, signature, ordered, records, panel.Records.Count);
        ClFileBindings.Validate(result); return result;
    }

    private static IEnumerable<int> ResponseIndicators(IEnumerable<PanelKeyword> keywords) => keywords
        .Select(k => k.Name is "ERRMSG" && k.Arguments.Length == 2 ? k.Arguments[1] :
            k.Name is "ERRMSGID" && k.Arguments.Length == 3 ? k.Arguments[2] :
            k.Name.StartsWith("CF", StringComparison.Ordinal) || k.Name.StartsWith("CA", StringComparison.Ordinal) || k.Name is "HELP" or "PAGEDOWN" or "PAGEUP"
                ? k.Arguments.FirstOrDefault() : null)
        .Where(a => int.TryParse(a, out var n) && n is >= 1 and <= 99).Select(a => int.Parse(a!, CultureInfo.InvariantCulture)).Distinct();

    private IClDisplayFile OpenClDisplayFile(ClFileBinding binding)
    {
        if (_programTerminal is null || _job?.Type != JobType.Interactive) throw new CpfException("CPF0861", "Display file I/O requires an interactive terminal connection.");
        var (library, name) = ResolveWorkObject(binding.Request.File, ObjectType.File);
        var created = _system.Objects.GetRequired(library, name, ObjectType.File).Created;
        var store = new DisplayFileStore(_system.Connections, ResolveDisplayMessage);
        var definition = store.Load(library, name);
        void Validate()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_system.Objects.GetRequired(library, name, ObjectType.File).Created != created)
                throw new CpfException("CPF4131", "The open display file was replaced.");
            var current = store.Load(library, name);
            var layout = DescribeClDisplayFile(binding.Request, library, name, current);
            if (layout.FormatSignature != binding.Definition.FormatSignature)
                throw new CpfException("CPF4131", "Display record format changed; recompile the CL program.");
        }
        Validate();
        var file = store.Open(library, name, EnvironmentForJob());
        return new ClDisplayHandle(definition, file, _programTerminal, new(library, name), Validate, _cancellationToken, _job.Ccsid);
    }

    private sealed class ClDisplayHandle(PanelDefinition definition, DisplayFileSession file,
        Ipc.Session.IProgramTerminal terminal, QualifiedName key, Action validate, CancellationToken cancellationToken, int ccsid) : IClDisplayFile
    {
        private string? _written;
        private bool _disposed;
        private bool[] _outputFlags = new bool[100];
        public ClFileReadResult? Transfer(ClDisplayOperation operation, string record, IReadOnlyDictionary<string, object?> values)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); validate();
            var format = definition.Records.Single(r => r.Name == record);
            var flags = new bool[100];
            for (var n = 1; n <= 99; n++)
                if (values.TryGetValue("IN" + n.ToString("00", CultureInfo.InvariantCulture), out var indicator)) flags[n] = ClExpression.Logical(indicator ?? false);
            if (operation != ClDisplayOperation.Receive)
            {
                var output = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var field in format.Fields.Where(f => !f.Constant))
                {
                    var value = values.GetValueOrDefault(field.Name);
                    if (value is ProgramBuffer buffer)
                    {
                        if (field.Type == PanelFieldType.Numeric && field.Length > 15)
                            value = ClCallArgument.Bind(new("*RAW", buffer.Length, 0, buffer, buffer), "*DEC", field.Length, field.Decimals).Value;
                        else value = field.Keywords.Any(k => k.Name == "SFLMSGKEY") ? System.Text.Encoding.Latin1.GetString(buffer.ToArray()) : buffer.ToText().TrimEnd(' ');
                    }
                    if (field.Keywords.Any(k => k.Name == "SFLPGMQ") && Convert.ToString(value, CultureInfo.InvariantCulture)?.TrimEnd() == "*")
                        throw new CpfException("IPC0006", "CL message subfiles require a program queue name; SFLPGMQ '*' is not supported by CL.");
                    output[field.BindingName] = value;
                }
                try { file.Write(record, output, flags); }
                catch (ArgumentException error) { throw new CpfException("CPF4131", error.Message); }
                _written = record;
                _outputFlags = flags;
            }
            if (operation == ClDisplayOperation.Send) return null;
            if (_written != record) throw new CpfException("IPC0139", "RCVF requires prior output of the same display record.");
            using var display = new Ipc.Session.ProgramDisplay(file, key, validate);
            var response = terminal.Read(display, cancellationToken);
            validate();
            if (!response.Accepted || response.Indicators.Length != 100) throw new CpfException("CPF4131", "Terminal returned an incomplete display record.");
            var updates = new Dictionary<string, object?>(StringComparer.Ordinal);
            var attention = response.Aid is Ipc.Terminal.AidKey.Pa1 or Ipc.Terminal.AidKey.Pa2 or Ipc.Terminal.AidKey.Pa3 or Ipc.Terminal.AidKey.Clear or Ipc.Terminal.AidKey.Print ||
                definition.Keywords.Concat(format.Keywords).Any(k => k.Name.StartsWith("CA", StringComparison.Ordinal) && k.Enabled(_outputFlags) &&
                    response.Aid == (Ipc.Terminal.AidKey)((int)Ipc.Terminal.AidKey.Pf1 + int.Parse(k.Name[2..], CultureInfo.InvariantCulture) - 1));
            foreach (var field in format.Fields.Where(f => !attention && !f.Constant && f.Usage is PanelFieldUsage.Input or PanelFieldUsage.Both))
            {
                if (!response.Values.TryGetValue(field.BindingName, out var value)) throw new CpfException("CPF4131", "Terminal omitted an input field.");
                updates[field.Name] = field.Type == PanelFieldType.Numeric && field.Length > 15
                    ? new ProgramBuffer(Ipc.Cl.Definitions.CommandArgumentCodec.Encode(new() { Keyword = "VALUE", Type = "*DEC", Length = field.Length, Decimals = field.Decimals }, value, ccsid), ccsid)
                    : CharacterValue(field, value, response.Indicators);
            }
            foreach (var number in ResponseIndicators(definition.Keywords.Concat(format.Keywords).Concat(format.Fields.SelectMany(f => f.Keywords))))
                updates["IN" + number.ToString("00", CultureInfo.InvariantCulture)] = response.Indicators[number];
            return new(updates);
        }
        private static object? CharacterValue(PanelField field, object? value, IReadOnlyList<bool> flags) => value switch
        {
            DateOnly date => date.ToString(field.Keywords.LastOrDefault(k => k.Name == "DATFMT" && k.Enabled(flags))?.Arguments[0] switch {
                "*USA" => "MM/dd/yyyy", "*EUR" => "dd.MM.yyyy", "*YMD" => "yy/MM/dd", "*DMY" => "dd/MM/yy", "*MDY" => "MM/dd/yy", _ => "yyyy-MM-dd" }, CultureInfo.InvariantCulture),
            TimeOnly time => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            DateTime stamp => stamp.ToString("yyyy-MM-dd-HH.mm.ss.ffffff", CultureInfo.InvariantCulture),
            _ => value
        };
        public void Dispose() => _disposed = true;
    }
}
