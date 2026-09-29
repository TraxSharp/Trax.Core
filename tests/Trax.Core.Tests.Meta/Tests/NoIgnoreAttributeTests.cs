namespace Trax.Core.Tests.Meta.Tests;

/// <summary>
/// A skip is a runtime decision with a reason in the output, not an attribute that hides.
///
/// <para>Enforces <c>Trax.Docs/adr/0005-a-skipped-test-is-a-runtime-decision.md</c>.</para>
/// </summary>
[Property("adr", "Trax.Docs/adr/0005-a-skipped-test-is-a-runtime-decision.md")]
[TestFixture]
public class NoIgnoreAttributeTests
{
    // The same two patterns as Trax.Core.Testing's HygieneGuards.NoIgnoreAttribute: an [Ignore]
    // anywhere in an attribute list (qualified, suffixed, or on a later line), and a per-case
    // Ignore = or IgnoreReason = on a case or fixture attribute. Matched over the whole file.
    private static readonly Regex[] IgnorePatterns =
    [
        new(
            @"(?:\[|,)\s*(?:[\w.]+\.)?Ignore(?:Attribute)?(?=\s*(?:\(|\]|,))",
            RegexOptions.Compiled
        ),
        new(
            @"\[[^\]]*?\b(?:TestCase|TestCaseSource|TestFixture|TestFixtureSource)(?:Attribute)?\b[^\]]*?\b(?:Ignore|IgnoreReason)\s*=(?!=)",
            RegexOptions.Compiled
        ),
    ];

    [Test]
    public void TestSources_DoNotUse_IgnoreAttribute()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles.CSharp("tests"))
        {
            // self-skip; this file mentions [Ignore] in a string literal
            if (file.EndsWith("NoIgnoreAttributeTests.cs", StringComparison.Ordinal))
                continue;

            var content = File.ReadAllText(file);
            var stripped = SourceText.StripCommentsAndStrings(content);
            var lines = new SortedSet<int>();
            foreach (var pattern in IgnorePatterns)
            foreach (Match match in pattern.Matches(stripped))
                lines.Add(stripped.Take(match.Index).Count(c => c == '\n') + 1);

            foreach (var line in lines)
                offenders.Add($"{RepoRoot.Relative(file)}:{line}");
        }

        offenders
            .Should()
            .BeEmpty(
                "[Ignore] silently hides failing tests. Trax.Docs/reference/test-conventions.md > Skipping requires either "
                    + "fixing the underlying code, fixing the test premise, or using Assert.Ignore(\"reason\") "
                    + "at runtime with an explicit reachability check. Offenders:\n  "
                    + string.Join("\n  ", offenders)
            );
    }
}
