using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;
using Ipc.Services.Work;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private RpgDiskLayout DescribeRpgDisk(RpgDiskRequest request)
    {
        var overridden = _job is null ? null : EnvironmentForJob().Resolve(request.DescriptionFile.Split('/')[^1]);
        var (library, name) = overridden is null ? ResolveRpgDiskObject(request.DescriptionFile) : (overridden.Library, overridden.Name);
        var definition = _files.GetDefinition(library, name) ?? throw new CpfException("CPF9801", "Database file not found.");
        return RpgDiskLayouts.Describe(request, library, name, definition);
    }
    private RpgFileHandle OpenRpgDisk(RpgDiskBinding binding)
    {
        var runtime = binding.Request.RuntimeFile == "*EXTDESC" ? binding.Request.DescriptionFile : binding.Request.RuntimeFile;
        var overridden = _job is null ? null : EnvironmentForJob().Resolve(runtime.Split('/')[^1]);
        var target = overridden is null ? ResolveRpgDiskObject(runtime) : (overridden.Library, overridden.Name);
        var (library, name) = target;
        var member = overridden?.Member ?? _files.FirstMember(library, name);
        var cancellation = _cancellationToken;
        var recordWait = binding.Request.Update ? RpgDiskLayouts.RecordWait(
            _files.GetDefinition(library, name) ?? throw new CpfException("CPF9801", "Database file not found."), overridden?.RecordLockWaitSeconds) : (TimeSpan?)null;
        void Validate(RpgFileCursor cursor)
        {
            cancellation.ThrowIfCancellationRequested();
            var live = _files.GetDefinition(library, name) ?? throw new CpfException("CPF9801", "Database file not found.");
            if (RpgDiskLayouts.Describe(binding.Request, library, name, live).Signature != binding.Layout.Signature)
                throw new CpfException("CPF4131", "Database record format changed; recompile the RPG program.");
            RpgDiskLayouts.Validate(binding, cursor);
        }
        RpgFileCursor Create() => new(new RpgSqliteFileAccess(_files), library, name, member, cancellation, binding.Request.Keyed, binding.Request.Update, binding.Request.Delete, binding.Request.Output, binding.Request.Input, recordWait);
        if (_job is null)
        {
            var cursor = Create();
            try
            {
                Validate(cursor);
                return new(() => { Validate(cursor); return cursor; }, cursor.Dispose, rebind: token => { cursor.RebindCancellation(token); cancellation = token; });
            }
            catch { cursor.Dispose(); throw; }
        }
        var environment = EnvironmentForJob();
        // A keyed path and an arrival path must never accidentally share position.
        var key = library + "/" + name + "/" + member + "/RPGDISK/" + binding.Request.Keyed + "/" + binding.Request.Update + "/" + binding.Request.Delete + "/" + binding.Request.Output + "/" + binding.Request.Input + "/" + recordWait?.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var path = environment.OpenPath(key, overridden?.Share ?? false, JobEnvironmentScope.Job, () =>
        {
            var allocation = new JobLockStore(_system.Connections).AcquireWithLifetime(_job.Key, new(library, name, ObjectType.File),
                binding.Request.Update || binding.Request.Output ? JobLockMode.SharedUpdate : JobLockMode.SharedRead, TimeSpan.Zero, cancellation, "OpenPath", Guid.NewGuid().ToString("N"),
                requiredAuthority: binding.Request.Input ? Authorities.UseBits : AuthorityBit.ObjectOperate | AuthorityBit.Add);
            try { return new RpgJobPath(Create(), allocation); }
            catch { allocation.Dispose(); throw; }
        });
        try
        {
            path.Value.Cursor.RebindCancellation(cancellation); Validate(path.Value.Cursor);
            return new(() => { var cursor = path.Value.Cursor; Validate(cursor); return cursor; }, path.Dispose, path.Close,
                token => { path.Value.Cursor.RebindCancellation(token); cancellation = token; });
        }
        catch { path.Close(); throw; }
    }
    private (string Library, string Name) ResolveRpgDiskObject(string value)
    {
        var (library, name) = SplitQualified(value, "*LIBL");
        // Resolve identity without demanding data-read authority. The file store
        // checks object-operation authority; each open mode checks its data rights.
        if (library is "*LIBL" or "*CURLIB")
            library = SearchLibraries(library).FirstOrDefault(candidate =>
                new Ipc.Services.Sqlite.SqliteObjectStore(_system.Connections).GetForAuthorization(candidate, name, ObjectType.File) is not null)
                ?? throw new CpfException("CPF9801", "Database file not found in the job library list.");
        return (library, name);
    }
}
