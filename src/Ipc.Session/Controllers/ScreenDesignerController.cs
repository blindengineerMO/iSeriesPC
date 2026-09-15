using Ipc.Cl.Commands;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Store;
using Ipc.Dsp;
using Ipc.Services.Work;
using Ipc.Terminal;

namespace Ipc.Console.Session;

public sealed partial class MenuController
{
    private ScreenDesignerSession? _designer;
    private DesignerRequest? _designerRequest;
    private SessionEvent HandleDesigner(KeyPress key)
    {
        try
        {
            using var locks = new JobLockStore(_system.Connections).EnterCommand(_job.Key, CancellationToken);
            _ = _system.Objects.GetRequired(_designerRequest!.Library, _designerRequest.SourceFile, ObjectType.File);
            if (key.Aid == AidKey.Pf1) return OpenBuiltinHelp("Screen Design Aid help", new[] {
                "Select a record and press Enter to edit its fields. F6 adds and F7 edits the selected record, field or menu option. F11 removes an item from the draft.",
                "F8 defines a window on the selected record. F9 adds a subfile and control. F10 creates or edits a menu and its options.",
                "F2 saves with a revision check. F4 previews the selected record. F5 saves and compiles a DSPF or MENU; replacement requires YES. F12 returns to the record list.",
                "F3 exits. An unsaved draft offers Save, Discard or Continue. A source conflict leaves the draft open so that its changes are not silently overwritten." });
            if (_designer!.Handle(key)) { _designer = null; _designerRequest = null; return ReturnToWorkOrMenu(); }
            return new("ScreenDesigner");
        }
        catch (Exception error) when (error is CpfException or ArgumentException or InvalidOperationException)
        { _designer = null; _designerRequest = null; return ReturnToWorkOrMenu(error.Message, error: true); }
    }
    private SessionEvent OpenDesigner(DesignerRequest request)
    {
        try
        {
        using var locks = new JobLockStore(_system.Connections).EnterCommand(_job.Key, CancellationToken);
        var files = new SqliteFileStore(_system.Connections, _system.Objects);
        var snapshot = files.ReadSourceMember(request.Library, request.SourceFile, request.Member);
        string ResolveMessage(string id, string file)
        {
            var key = QualifiedName.Parse(file, "*LIBL");
            var library = _system.SearchLibraries(_job, key.Library).FirstOrDefault(l => _system.Objects.Exists(l, key.Name.Value, ObjectType.MessageFile))
                ?? throw new CpfException("CPF2401", "Display message file not found.");
            return new Ipc.Services.Messages.MessageDescriptionStore(_system.Connections).Get(library, key.Name.Value, id);
        }
        var design = snapshot.Source.Length == 0 ? ScreenDesign.New(request.Member, ResolveMessage) : ScreenDesign.Open(request.Member, snapshot.Source, ResolveMessage);
        _designer = new(design, request.Library + "/" + request.SourceFile + "(" + request.Member + ")", request.Library,
            source => snapshot = files.SaveSourceMember(request.Library, request.SourceFile, request.Member, source, snapshot.Revision),
            target =>
            {
                if (!ObjectName.IsValid(target.Library) || !ObjectName.IsValid(target.Name)) throw new ArgumentException("Invalid target library/object.");
                var command = target.Menu ? "CRTMNU MENU" : "CRTDSPF FILE";
                var result = _execution.Execute(command + "(" + target.Library + "/" + target.Name + ") SRCFILE(" + request.Library + "/" + request.SourceFile + ") SRCMBR(" + request.Member + ") REPLACE(" + (target.Replace ? "*YES" : "*NO") + ")");
                if (result.IsError) throw new InvalidOperationException(result.Message);
            });
        _designerRequest = request; return new("ScreenDesigner");
        }
        catch (Exception error) when (error is CpfException or ArgumentException or InvalidOperationException or PanelCompileException)
        { Screens.Status(_buffer, error.Message, error: true); return new("Menu", Message: error.Message); }
    }
}
