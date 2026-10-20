using System.Text.Json.Nodes;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Services.Commands;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private sealed record CopyAuthority(string Holder, bool IsList, AuthorityBit Bits);
    private sealed record FileCopyCreation(AuthorityBit Public, bool ListPublic, string SourceOwner, string? Description,
        string Owner, IReadOnlyList<CopyAuthority> Authorities);

    private static FileDefinition CopyFileDefinition(FileDefinition source, string name)
    {
        var json = JsonNode.Parse(source.ToJson())!;
        json["name"] = name; json["attribute"] = source.Attribute == FileAttribute.Source ? "Source" : "Physical";
        json["logical"] = null; json["sqlView"] = null; json["sqlIndex"] = false; json["sqlTable"] = null;
        if (source.Attribute == FileAttribute.Logical) json["memberSize"] = JsonNode.Parse("{\"unlimited\":true}");
        if (source.SqlTable is not null || source.SqlView is not null || source.SqlIndex)
            foreach (var format in json["formats"]!.AsArray())
                foreach (var field in format!["fields"]!.AsArray())
                { field!["defaultValue"] = null; field["currentDatetimeDefault"] = false; }
        return FileDefinition.FromJson(json.ToJsonString());
    }

    private static string CopyMember(SqliteConnection connection, SqliteTransaction transaction, QualifiedName file, string member,
        string? newFileFirst = null, string? sourceMember = null)
    {
        member = member.ToUpperInvariant();
        if (member == "*FROMMBR" && sourceMember is not null) member = sourceMember;
        if (member == "*FIRST")
        {
            if (newFileFirst is not null) return newFileFirst;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE' ORDER BY created,mbr LIMIT 1";
            command.Parameters.AddWithValue("$lib",file.Library); command.Parameters.AddWithValue("$name",file.Name.Value);
            return command.ExecuteScalar() as string ?? throw new CpfException("CPF2817","File has no members.");
        }
        if (!ObjectName.IsValid(member)) throw new CpfException("IPC0139","CPYF requires a member name, *FIRST or TOMBR(*FROMMBR).");
        return member;
    }

    private static void EnsureCopyMember(SqliteConnection connection, SqliteTransaction transaction, QualifiedName file, string member)
    {
        if (RecordSlots.Identity(connection,transaction,file.Library,file.Name.Value,member) is null)
            throw new CpfException("CPF2817","File member not found.");
    }

    private void RequireCopyCreation(SqliteConnection connection, SqliteTransaction transaction, QualifiedName target, bool acquireAllocation = true)
    {
        var authority = new ServiceAuthorization(_factory);
        authority.RequireBoundObject(connection,transaction,"QSYS",target.Library,AuthorityBit.ObjectOperate | AuthorityBit.Add,
            objectType:ObjectType.Library,checkLibrary:false);
        authority.RequireBoundObject(connection,transaction,"QSYS","CRTPF",AuthorityBit.ObjectOperate,objectType:ObjectType.Command);
        var command = ObjectSigningService.Read(connection,transaction,"QSYS","CRTPF",ObjectType.Command);
        if (CommandDefinitionStore.ValidatePayload(command).Builtin != "CRTPF") throw new CpfException("CPF9802","The native CRTPF adapter is unavailable.");
        if (acquireAllocation) new JobLockStore(_factory).RequireObjectMutation(target.Library,target.Name.Value,FileType);
    }

    private static IReadOnlyList<CopyAuthority> CopyAuthorities(SqliteConnection connection, SqliteTransaction transaction, QualifiedName key)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT holder,is_authl,bits FROM sys_authorities WHERE lib=$lib AND name=$name AND type='*FILE' ORDER BY holder,is_authl";
        command.Parameters.AddWithValue("$lib",key.Library); command.Parameters.AddWithValue("$name",key.Name.Value);
        var result = new List<CopyAuthority>(); using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(reader.GetString(0),reader.GetBoolean(1),(AuthorityBit)reader.GetInt64(2)));
        return result;
    }

    private void CreateCopyFile(PreparedFileCopy copy, SqliteTransaction transaction)
    {
        var creation = copy.Creation!; var target = copy.Target; var connection = copy.Connection;
        CreateFileImage(connection,transaction,target.Library,target.Name.Value,
            copy.Output.Attribute == FileAttribute.Source ? SourceAttribute : PhysicalAttribute,copy.Output,null,creation.Description,copy.TargetMember,
            publicAuthority:creation.Public,owner:creation.Owner,authorizationListPublic:creation.ListPublic);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO sys_authorities SELECT $tl,$tn,type,holder,is_authl,CASE WHEN holder=$owner AND is_authl=0 THEN $all ELSE bits END FROM sys_authorities WHERE lib=$sl AND name=$sn AND type='*FILE'";
        command.Parameters.AddWithValue("$tl",target.Library); command.Parameters.AddWithValue("$tn",target.Name.Value);
        command.Parameters.AddWithValue("$sl",copy.Source.Library); command.Parameters.AddWithValue("$sn",copy.Source.Name.Value);
        command.Parameters.AddWithValue("$owner",creation.Owner); command.Parameters.AddWithValue("$all",(long)Authorities.AllBits);
        command.ExecuteNonQuery();
        // Native CRTFILE extends a new file without an operator inquiry. Preserve
        // configured SIZE while granting enough durable capacity for this load.
        var size = copy.Output.MemberSize ?? new();
        if (copy.Count > size.Capacity)
            MemberCapacity.Grant(connection,transaction,RecordSlots.Identity(connection,transaction,target.Library,target.Name.Value,copy.TargetMember)!,
                CapacitySignature(size),copy.Count);
    }

    private static void ValidateCopyCreation(PreparedFileCopy copy, SqliteTransaction transaction)
    {
        var expected = copy.Creation!; var connection = copy.Connection;
        var source = ObjectSigningService.Read(connection,transaction,copy.Source.Library,copy.Source.Name.Value,FileType);
        if (source.Owner != expected.SourceOwner || source.PublicAuthority != expected.Public || source.UseAuthorizationListPublicAuthority != expected.ListPublic ||
            !CopyAuthorities(connection,transaction,copy.Source).SequenceEqual(expected.Authorities)) throw LogicalError("Source authorities changed during CPYF creation.");
        if (copy.TargetId is null)
        {
            using var absent = connection.CreateCommand(); absent.Transaction = transaction;
            absent.CommandText = "SELECT count(*) FROM sys_objects WHERE lib=$lib AND name=$name AND type='*FILE'";
            absent.Parameters.AddWithValue("$lib",copy.Target.Library); absent.Parameters.AddWithValue("$name",copy.Target.Name.Value);
            if (Convert.ToInt64(absent.ExecuteScalar()) != 0) throw new CpfException("CPF5805","Target file appeared during CPYF creation.");
            return;
        }
        var target = ObjectSigningService.Read(connection,transaction,copy.Target.Library,copy.Target.Name.Value,FileType);
        if (target.Owner != expected.Owner || target.PublicAuthority != expected.Public || target.UseAuthorizationListPublicAuthority != expected.ListPublic ||
            !CopyAuthorities(connection,transaction,copy.Target).SequenceEqual(expected.Authorities.Select(item =>
                !item.IsList && item.Holder == expected.Owner ? item with { Bits = Authorities.AllBits } : item))) throw LogicalError("Created authorities changed during CPYF.");
    }

    private void ValidateCopyPublisher(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (OperationIdentity.Current is not { } identity) return;
        new ServiceAuthorization(_factory).RequireCaller(connection,transaction,identity.Principal);
        if (identity.Job is not { } job) return;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT cancel_requested FROM sys_jobs WHERE number=$number AND name=$name AND user=$user AND coalesce(user_profile,user)=$owner AND execution_state='Running'";
        JobRecordTransactions.BindJob(command,job); command.Parameters.AddWithValue("$owner",identity.Principal);
        var state = command.ExecuteScalar(); if (state is null) throw new CpfException("CPF1241","Copy job is no longer active.");
        if (Convert.ToInt32(state) != 0) throw new OperationCanceledException("Copy job was canceled.");
    }
}
