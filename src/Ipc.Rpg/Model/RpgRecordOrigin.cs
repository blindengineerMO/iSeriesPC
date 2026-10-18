namespace Ipc.Rpg.Model;

public sealed record RpgRecordOrigin(string File, string Record, string Signature, string Extract, bool ImplicitInput, string? Family = null);
