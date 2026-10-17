using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgBranchTests
{
    [Theory]
    [InlineData(0, "FALSE")]
    [InlineData(1, "TRUE")]
    public void Both_IF_paths_execute_their_body_once_and_resume_after_ENDIF(int flag, string expected)
    {
        var source = $"**free\ndcl-s FLAG int(10) inz({flag});\nif FLAG = 1;\ndsply 'TRUE';\nelse;\ndsply 'FALSE';\nendif;\ndsply 'AFTER';";
        var messages = new List<string>(); var host = new RpgHost { Display = messages.Add };
        new RpgInterpreter(host).Run(RpgCompiler.Compile("QGPL", "BRANCH", source));
        Assert.Equal(new[] { expected, "AFTER" }, messages);
    }
    [Theory]
    [InlineData(0, 0, "OUTER FALSE", "INNER FALSE")]
    [InlineData(0, 1, "OUTER FALSE", "INNER TRUE")]
    [InlineData(1, 0, "OUTER TRUE", "INNER FALSE")]
    [InlineData(1, 1, "OUTER TRUE", "INNER TRUE")]
    public void Nested_ELSE_blocks_preserve_the_correct_join_points(int outer, int inner, string expectedOuter, string expectedInner)
    {
        var source = $"**free\ndcl-s OUTER int(10) inz({outer});\ndcl-s INNER int(10) inz({inner});\n" +
            "if OUTER = 1;\ndsply 'OUTER TRUE';\nif INNER = 1;\ndsply 'INNER TRUE';\nelse;\ndsply 'INNER FALSE';\nendif;\n" +
            "else;\ndsply 'OUTER FALSE';\nif INNER = 1;\ndsply 'INNER TRUE';\nelse;\ndsply 'INNER FALSE';\nendif;\nendif;\ndsply 'AFTER';";
        var messages = new List<string>(); var host = new RpgHost { Display = messages.Add };
        new RpgInterpreter(host).Run(RpgCompiler.Compile("QGPL", "BRANCH", source));
        Assert.Equal(new[] { expectedOuter, expectedInner, "AFTER" }, messages);
    }
}
