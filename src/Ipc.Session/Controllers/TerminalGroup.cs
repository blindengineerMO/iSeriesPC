using Ipc.Cl.Commands;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Terminal;

namespace Ipc.Console.Session;

public sealed partial class MenuController
{
    private sealed class TerminalGroup
    {
        public MenuController Root { get; }
        public MenuController Active { get; private set; }
        private readonly InteractiveSessionStore _store;
        private readonly Dictionary<int, MenuController> _members = new();
        private readonly Dictionary<int, int> _previous = new();
        private readonly object _gate = new();
        private readonly CancellationTokenSource _lifetime;
        private readonly List<CancellationTokenRegistration> _registrations = new();
        private readonly DisplayBuffer _request = new();
        private readonly DisplayForm _selection;
        private readonly FieldEditor _editor;
        private string? _mode;
        private bool _attached;
        private bool _ended;
        public CancellationToken CancellationToken => _lifetime.Token;
        public DisplayBuffer Buffer => _mode is null ? Active.OwnBuffer : _request;
        public TerminalGroup(MenuController root)
        {
            Root = Active = root; _store = new(root._system.Connections); _members.Add(root._job.Key.Number, root);
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(root._execution.CancellationToken);
            _registrations.Add(_lifetime.Token.Register(Disconnect));
            _selection = new(_request, new[] { new InputField { Row = 23, Column = 12, Length = 30 } }); _editor = new(_selection);
        }
        private MenuController[] Snapshot() { lock (_gate) return _members.Values.ToArray(); }
        public void Disconnect()
        {
            foreach (var member in Snapshot())
            {
                member._disconnected = true;
                try { member._terminalStop.Cancel(); } catch (ObjectDisposedException) { }
            }
        }
        public void End(JobCompletion completion, string message)
        {
            if (_ended) return; _ended = true; Exception? failure = null;
            foreach (var member in Snapshot().OrderBy(m => ReferenceEquals(m, Root)))
                try { member.EndOwn(completion, message); } catch (Exception error) { failure ??= error; }
            try { if (Root._sessionToken is not null) Root._system.Security.RevokeSession(Root._sessionToken); }
            finally { foreach (var registration in _registrations) registration.Dispose(); _lifetime.Dispose(); }
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        private IDisposable Identity() => OperationIdentity.Enter(Active._profile.Name, Active._job.Key, Active._job.AuthSessionId);
        private void Attach() { if (_attached) return; _store.Attach(Active._job.Key); _attached = true; }
        public SessionEvent Handle(KeyPress key)
        {
            using var identity = Identity();
            try
            {
                if (CancellationToken.IsCancellationRequested || Active._job.AuthSessionId is { } session && !Root._system.Security.IsSessionActive(session, Active._profile.Name))
                    return new("Failed", EndSession: true);
                if (key.Aid == AidKey.SysReq)
                {
                    _ = Root._system.Menus.Get("SYSREQ", "QSYS");
                    Attach(); _mode = _mode == "System" ? null : "System"; if (_mode is not null) Paint(); return new("SystemRequest");
                }
                if (_mode is null) return Active.HandleOwn(key);
                if (_mode == "System") _ = Root._system.Menus.Get("SYSREQ", "QSYS");
                if (key.Aid is AidKey.Pf3 or AidKey.Pf12 or AidKey.Pa1) { _mode = null; return new("Menu"); }
                if (key.Aid == AidKey.Pf1)
                { Screens.Status(_request, "Select a number or group name. F6 starts a group; F11 ends it. F12 returns."); return new("SystemRequest"); }
                if (key.Aid == AidKey.Pf5) { Paint(); return new("SystemRequest"); }
                if (key.Aid == AidKey.Enter || key.Aid is AidKey.Pf6 or AidKey.Pf11)
                {
                    var value = _selection.ReadValue(0).Trim().ToUpperInvariant();
                    if (_mode == "System")
                    {
                        var source = Root._system.Menus.Get("SYSREQ", "QSYS");
                        var option = source.Find(value)
                            ?? throw new CpfException("IPC0131", "Select a listed system request option.");
                        _mode = null; return Active.Select(option, source);
                    }
                    var jobs = _store.List(Active._job.Key); var partition = jobs.Single(j => j.Job == Active._job.Key).Partition;
                    var choices = jobs.Where(j => j.Partition == partition).ToArray();
                    if (int.TryParse(value, out var index) && index >= 1 && index <= choices.Length)
                        value = choices[index - 1].GroupName ?? throw new CpfException("IPC0131", "Name this group first with CHGGRPA.");
                    return Command(new(key.Aid == AidKey.Pf11 ? "ENDGRPJOB" : "TFRGRPJOB", value));
                }
                if (key.Edit is { } edit && edit != CursorEdit.None) _editor.ApplyEdit(edit);
                else if (key.Character != '\0' && !char.IsControl(key.Character)) _editor.Apply(key.Character);
                return new("SystemRequest");
            }
            catch (Exception error) when (error is CpfException or ArgumentException or InvalidOperationException)
            {
                if (error is CpfException && error.Message.Contains("CPF9802", StringComparison.Ordinal)) _mode = null;
                Screens.Status(Buffer, error.Message, error: true); return new(_mode is null ? "Menu" : "SystemRequest", Message: error.Message);
            }
        }
        public SessionEvent Command(GroupJobRequest request)
        {
            using var identity = Identity(); Attach();
            try
            {
                var jobs = _store.List(Active._job.Key); var current = jobs.Single(j => j.Job == Active._job.Key);
                if (request.Action == "CHGGRPA")
                { _store.Rename(Active._job.Key, request.Name, request.Description); Screens.Status(Active.OwnBuffer, "Group attributes changed."); return new("Menu"); }
                if (request.Action == "TFRSECJOB")
                {
                    _ = Root._system.Menus.Get("SYSREQ", "QSYS");
                    var alternate = jobs.FirstOrDefault(j => j.Partition != current.Partition);
                    if (alternate is not null) Transfer(alternate.Job);
                    else Create("QSECOND", "QCMD", secondary: true);
                    _mode = null; return new("Menu");
                }
                if (request.Name == "*SELECT") { _mode = "Groups"; Paint(); return new("GroupJobs"); }
                if (current.GroupName is null) throw new CpfException("CPF1313", "Use CHGGRPA GRPJOB(name) before transferring group jobs.");
                var target = request.Name switch
                {
                    "*CURRENT" => current,
                    "*PRV" => _previous.TryGetValue(current.Job.Number, out var previous) ? jobs.FirstOrDefault(j => j.Job.Number == previous && j.Partition == current.Partition) : null,
                    _ => jobs.FirstOrDefault(j => j.Partition == current.Partition && j.GroupName == request.Name),
                };
                if (request.Action == "ENDGRPJOB")
                {
                    if (target is null) throw new CpfException("CPF1314", "Group job not found.");
                    var member = _members[target.Job.Number];
                    if (target.Job == Active._job.Key)
                    {
                        var next = jobs.FirstOrDefault(j => j.Job != target.Job && j.Partition == current.Partition) ?? jobs.FirstOrDefault(j => j.Job != target.Job);
                        if (next is null) return new("Menu", EndSession: true);
                        Transfer(next.Job);
                    }
                    member.EndOwn(JobCompletion.Normal, "Group job ended.");
                    lock (_gate) _members.Remove(target.Job.Number);
                    _mode = null; return new("Menu");
                }
                if (target is not null) Transfer(target.Job);
                else
                {
                    if (!ObjectName.IsValid(request.Name)) throw new CpfException("CPF1314", "Previous group job is unavailable.");
                    Create(request.Name, request.InitialProgram, secondary: false);
                }
                _mode = null; return new("Menu");
            }
            catch (Exception error) when (error is CpfException or ArgumentException or InvalidOperationException)
            { Screens.Status(Buffer, error.Message, error: true); return new("Menu", Message: error.Message); }
        }
        private void Transfer(JobKey target)
        {
            var previous = Active; _store.Transfer(previous._job.Key, target); Active = _members[target.Number];
            _previous[target.Number] = previous._job.Key.Number;
        }
        private void Create(string name, string program, bool secondary)
        {
            var parent = Active;
            var job = secondary ? Root._system.Jobs.CreateInteractive(parent._profile.Name, currentLibrary: parent._job.CurrentLibrary ?? "QGPL", libraryList: parent._job.LibraryList ?? "QGPL QUSRSYS", ccsid: parent._job.Ccsid)
                : Root._system.Jobs.CreateGroupJob(parent._job.Key, name);
            MenuController? child = null;
            try
            {
                child = new MenuController(Root._system, parent._profile, parent._profile.InitialMenu == "*SIGNOFF" ? "MAIN" : null,
                    CancellationToken, parent._job.AuthSessionId, null, job, this);
                _store.Add(parent._job.Key, job.Key, secondary ? null : name, secondary);
                lock (_gate) _members.Add(job.Key.Number, child);
                Active = child; _previous[job.Key.Number] = parent._job.Key.Number;
                _registrations.Add(child._execution.CancellationToken.Register(() => {
                    // The runtime monitor may observe normal completion before the lease detaches.
                    // An intentionally ended member must not cancel the remaining terminal family.
                    if (child._disposed) return;
                    try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
                }));
                using var identity = Identity(); child.RunInitialProgram(program);
            }
            catch
            {
                if (ReferenceEquals(Active, child))
                {
                    using var identity = Identity(); _store.Transfer(job.Key, parent._job.Key); Active = parent;
                    lock (_gate) _members.Remove(job.Key.Number);
                }
                if (child is not null) child.EndOwn(JobCompletion.Abnormal, "Group startup failed.");
                else
                {
                    using var identity = OperationIdentity.Enter(job.Key.User, job.Key);
                    Root._system.Jobs.CompleteHostedSession(job.Key, JobCompletion.Abnormal, "Group startup failed.");
                }
                throw;
            }
        }
        private void Paint()
        {
            _request.ClearScreen(); _request.MoveCursor(1, 2); _request.Write(_mode == "System" ? "System Request" : "Transfer to Group Job", DisplayAttribute.HighIntensity);
            if (_mode == "System")
            {
                var menu = Root._system.Menus.Get("SYSREQ", "QSYS"); var row = 4;
                foreach (var option in menu.Options) { _request.MoveCursor(row++, 2); _request.Write(option.Number + ". " + option.Text); }
            }
            else
            {
                var jobs = _store.List(Active._job.Key); var partition = jobs.Single(j => j.Job == Active._job.Key).Partition; var index = 0;
                foreach (var job in jobs.Where(j => j.Partition == partition))
                {
                    _request.MoveCursor(4 + index, 2);
                    var line = $"{++index,2}. {job.GroupName ?? "*UNNAMED",-10} {(job.Suspended ? "Suspended" : "Active"),-9} {job.Description}";
                    _request.Write(line[..Math.Min(78, line.Length)]);
                }
            }
            _request.MoveCursor(22, 2); _request.Write("F1=Help F3/F12=Return F5=Refresh F6=Start F11=End");
            _request.MoveCursor(23, 1); _request.Write("Selection:"); _selection.ClearAll(); _editor.ApplyEdit(CursorEdit.Home);
        }
    }
}
