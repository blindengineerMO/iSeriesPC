using Ipc.Core.Messages;
using Ipc.Core.Objects;

namespace Ipc.Services.Work;

public sealed record UserSpaceListTarget(UserSpaceAttributes Attributes, byte[] UserArea)
{
    public int MaximumSize => Attributes.OptimumAlignment ? UserSpaceStore.MaximumAlignedLength : UserSpaceStore.MaximumLength;
}

public sealed partial class UserSpaceStore
{
    /// <summary>List APIs implicitly extend storage. Build and publish under one write reservation, preserving the caller's user area and unused tail.</summary>
    public void ReplaceList(QualifiedName space, Func<UserSpaceListTarget, byte[]> build, CancellationToken cancellationToken = default)
    {
        ValidateName(space); space = new JobTemporaryLibrary(factory).Resolve(space, ObjectType.UserSpace);
        cancellationToken.ThrowIfCancellationRequested(); Authorize(space, Authorities.ChangeBits, true, false);
        _locks.RequireExclusiveRead(space.Library, space.Name.Value, ObjectType.UserSpace);
        using var connection = factory.Open(); using var transaction = BeginWrite(connection, cancellationToken);
        Authorize(space, Authorities.ChangeBits, true, false); var attributes = ReadAttributes(connection, transaction, space);
        using var query = Command(connection, transaction, space, "SELECT substr(data,1,64) FROM sys_user_spaces WHERE space_lib=$lib AND space_name=$name");
        var previous = (byte[])query.ExecuteScalar()!;
        var area = Enumerable.Repeat(attributes.InitialValue, 64).ToArray(); previous.CopyTo(area, 0);
        var expectedArea = area.ToArray();
        var bytes = build(new(attributes, area));
        if (bytes.Length < 192 || bytes.Length > (attributes.OptimumAlignment ? MaximumAlignedLength : MaximumLength) ||
            !bytes.AsSpan(0, 64).SequenceEqual(expectedArea)) throw new CpfException("IPC0136", "Invalid generated list or changed caller user area.");
        cancellationToken.ThrowIfCancellationRequested();
        Authorize(space, Authorities.ChangeBits, true, false);
        using var update = Command(connection, transaction, space, "UPDATE sys_user_spaces SET data=CAST($prefix||substr(data,$suffix) AS BLOB) WHERE space_lib=$lib AND space_name=$name");
        update.Parameters.AddWithValue("$prefix", bytes); update.Parameters.AddWithValue("$suffix", bytes.Length + 1);
        update.ExecuteNonQuery(); Changed(connection, transaction, space);
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
    }
}
