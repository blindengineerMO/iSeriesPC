namespace Ipc.Core.Compilation;

/// <summary>Bounded RPG directive grammar, sharing source accounting with the CL extension.</summary>
public sealed class RpgSourcePreprocessor(Func<SourceDocument, string, SourceDocument>? resolve = null)
{
    public PreprocessedSource Expand(SourceDocument source, CancellationToken cancellationToken = default)
        => new SourcePreprocessor(resolve, rpg: true).Expand(source, cancellationToken);
}
