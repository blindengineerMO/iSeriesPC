using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    public DatabaseRecordWriter OpenRecordWriter(string library, string name, string member, CancellationToken cancellationToken = default)
    {
        library = ResolveLibrary(library).ToUpperInvariant(); name = name.ToUpperInvariant(); member = member.ToUpperInvariant();
        cancellationToken.ThrowIfCancellationRequested();
        var definition = GetDefinition(library, name) ?? throw new CpfException("CPF9801", "Database file not found.");
        if (definition.Formats.Count != 1) throw LogicalError("Record output requires one record format.");
        EnsureMember(library, name, member);
        var sourceLibrary = definition.Logical?.SourceLibrary ?? library;
        var sourceName = definition.Logical?.SourceFile ?? name;
        var source = definition.Logical is null ? definition : GetDefinition(sourceLibrary, sourceName) ?? throw LogicalError("Missing physical file.");
        var members = definition.Logical is { } logical ? logical.Members.TryGetValue(member, out var bound) ? bound.ToArray() : throw LogicalError("Missing logical member binding.") : [member];
        if (members.Length != 1) throw LogicalError("Record output requires one bound physical member.");
        if (definition.Logical is { } view)
        {
            ValidateLogical(view);
            if (source.Logical is not null || source.Formats.Count != 1 || source.PrimaryFormat.Name != view.SourceFormat ||
                definition.PrimaryFormat.Fields.Any(f => source.PrimaryFormat.Find(f.Name) is not { } actual || actual.Type != f.Type ||
                    actual.Length != f.Length || actual.Decimals != f.Decimals || actual.Ccsid != f.Ccsid || actual.NullCapable != f.NullCapable || actual.VariableLength != f.VariableLength))
                throw LogicalError("Physical format changed; recompile the logical file.");
        }
        var original = CursorFingerprint(definition); var originalSource = CursorFingerprint(source);
        var owner = OperationIdentity.Current;
        var lifetime = new OutputCancellation(cancellationToken, _factory.ShutdownToken);
        var resources = new List<IDisposable> { lifetime }; var locks = new JobLockStore(_factory);
        void Owner()
        {
            var caller = OperationIdentity.Current;
            if (caller?.Job != owner?.Job || !string.Equals(caller?.Principal, owner?.Principal, StringComparison.OrdinalIgnoreCase))
                throw new CpfException("CPF9802", "Record output belongs to another execution context.");
            if (owner?.Job is { } job) new JobDataAreaStore(_factory).RequireOwner(job);
        }
        void Validate()
        {
            lifetime.Token.ThrowIfCancellationRequested(); Owner();
            RequireUnenlisted(sourceLibrary, sourceName, "Declared record output");
            Authorize(library, name, AuthorityBit.Add); Authorize(sourceLibrary, sourceName, AuthorityBit.Add);
            if (GetDefinition(library, name) is not { } live || CursorFingerprint(live) != original ||
                GetDefinition(sourceLibrary, sourceName) is not { } liveSource || CursorFingerprint(liveSource) != originalSource)
                throw LogicalError("File definition changed while record output was open.");
            EnsureMember(library, name, member); EnsureMember(sourceLibrary, sourceName, members[0]);
        }
        try
        {
            Validate();
            if (owner?.Job is { } job)
            {
                var scope = Guid.NewGuid().ToString("N");
                resources.Add(locks.AcquireWithLifetime(job, new(library, name, ObjectType.File), JobLockMode.SharedUpdate,
                    TimeSpan.Zero, lifetime.Token, "OutputOpen", scope, requiredAuthority: AuthorityBit.ObjectOperate | AuthorityBit.Add));
                if (sourceLibrary != library || sourceName != name)
                    resources.Add(locks.AcquireWithLifetime(job, new(sourceLibrary, sourceName, ObjectType.File), JobLockMode.SharedUpdate,
                        TimeSpan.Zero, lifetime.Token, "OutputOpen", scope, requiredAuthority: AuthorityBit.ObjectOperate | AuthorityBit.Add));
            }
            var writer = new DatabaseRecordWriter(Validate, values =>
            {
                using var operation = locks.EnterRecordWrite(lifetime.Token);
                InsertCore(library, name, member, definition.PrimaryFormat.Name, values, lifetime.Token, Validate);
            }, replacement => { Owner(); lifetime.Rebind(replacement); }, resources);
            resources.Add(_factory.RegisterRuntimeResource(writer));
            return writer;
        }
        catch { foreach (var resource in resources.AsEnumerable().Reverse()) resource.Dispose(); throw; }
    }
    private sealed class OutputCancellation : IDisposable
    {
        private readonly CancellationToken _shutdown;
        private CancellationTokenSource _linked;
        internal CancellationToken Token => _linked.Token;
        internal OutputCancellation(CancellationToken token, CancellationToken shutdown)
        { _shutdown = shutdown; _linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown); }
        internal void Rebind(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); _shutdown.ThrowIfCancellationRequested();
            var next = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown); _linked.Dispose(); _linked = next;
        }
        public void Dispose() => _linked.Dispose();
    }
}
