using McDownload.Core.Meta;

namespace McDownload.Tests;

public class RuleEvaluatorTests
{
    private static readonly PlatformContext Windows = new("windows", "x86_64", "10.0.19045");
    private static readonly PlatformContext MacArm = new("osx", "arm64", "14.5");
    private static readonly PlatformContext Linux = new("linux", "x86_64", "6.5.0");

    private static Rule MakeRule(
        string action = "allow",
        string? osName = null,
        string? arch = null,
        string? version = null,
        Dictionary<string, bool>? features = null)
    {
        var os = osName is null && arch is null && version is null
            ? null
            : new OsRule { Name = osName, Arch = arch, Version = version };

        return new Rule { Action = action, Os = os, Features = features };
    }

    [Fact]
    public void NullOrEmptyRuleListIsAllowed()
    {
        Assert.True(RuleEvaluator.IsAllowed(null, Windows));
        Assert.True(RuleEvaluator.IsAllowed(Array.Empty<Rule>(), Windows));
        Assert.True(RuleEvaluator.IsAllowed(Array.Empty<Rule>(), MacArm, new Dictionary<string, bool>()));
    }

    [Fact]
    public void AllowWindowsRuleOnlyMatchesWindows()
    {
        Rule[] rules = [MakeRule(osName: "windows")];

        Assert.True(RuleEvaluator.IsAllowed(rules, Windows));
        Assert.False(RuleEvaluator.IsAllowed(rules, MacArm));
        Assert.False(RuleEvaluator.IsAllowed(rules, Linux));
    }

    [Fact]
    public void AllowAllThenDisallowOsxAllowsEveryOtherPlatform()
    {
        Rule[] rules = [MakeRule(), MakeRule("disallow", osName: "osx")];

        Assert.True(RuleEvaluator.IsAllowed(rules, Windows));
        Assert.True(RuleEvaluator.IsAllowed(rules, Linux));
        Assert.False(RuleEvaluator.IsAllowed(rules, MacArm));
    }

    [Fact]
    public void RuleListWithOnlyADisallowDefaultsToDeny()
    {
        // No rule matches Windows, and the documented default for a non-empty list is deny.
        Rule[] rules = [MakeRule("disallow", osName: "osx")];

        Assert.False(RuleEvaluator.IsAllowed(rules, Windows));
    }

    [Fact]
    public void LastMatchingRuleWins()
    {
        Rule[] denyWindowsLast = [MakeRule(), MakeRule("disallow", osName: "windows")];
        Assert.True(RuleEvaluator.IsAllowed(denyWindowsLast, MacArm));
        Assert.False(RuleEvaluator.IsAllowed(denyWindowsLast, Windows));

        Rule[] allowWindowsLast = [MakeRule("disallow", osName: "windows"), MakeRule("allow", osName: "windows")];
        Assert.True(RuleEvaluator.IsAllowed(allowWindowsLast, Windows));
        Assert.False(RuleEvaluator.IsAllowed(allowWindowsLast, MacArm));
    }

    [Fact]
    public void RuleActionAndOsNameAreCaseInsensitive()
    {
        Rule[] rules =
        [
            new Rule { Action = "ALLOW", Os = new OsRule { Name = "Windows" } },
        ];

        Assert.True(RuleEvaluator.IsAllowed(rules, Windows));
        Assert.False(RuleEvaluator.IsAllowed(rules, new PlatformContext("linux", "x86_64", "6.5.0")));
    }

    [Fact]
    public void FeatureRuleRequiresTheFeatureToBeEnabled()
    {
        Rule[] rules =
        [
            MakeRule(features: new Dictionary<string, bool> { ["has_custom_resolution"] = true }),
        ];

        Assert.True(RuleEvaluator.IsAllowed(rules, Windows, new Dictionary<string, bool> { ["has_custom_resolution"] = true }));
        Assert.False(RuleEvaluator.IsAllowed(rules, Windows, new Dictionary<string, bool> { ["has_custom_resolution"] = false }));
        Assert.False(RuleEvaluator.IsAllowed(rules, Windows, new Dictionary<string, bool>()));
        Assert.False(RuleEvaluator.IsAllowed(rules, Windows));
    }

    [Fact]
    public void FeatureRuleExpectingFalseMatchesAMissingFeature()
    {
        Rule[] rules =
        [
            MakeRule(features: new Dictionary<string, bool> { ["demo_mode"] = false }),
        ];

        Assert.True(RuleEvaluator.IsAllowed(rules, Windows));
        Assert.True(RuleEvaluator.IsAllowed(rules, Windows, new Dictionary<string, bool> { ["demo_mode"] = false }));
        Assert.False(RuleEvaluator.IsAllowed(rules, Windows, new Dictionary<string, bool> { ["demo_mode"] = true }));
    }

    [Fact]
    public void EmptyFeatureDictionaryDoesNotRestrictTheRule()
    {
        Rule[] rules = [MakeRule(features: new Dictionary<string, bool>())];

        Assert.True(RuleEvaluator.IsAllowed(rules, Windows));
        Assert.True(RuleEvaluator.IsAllowed(rules, Linux));
    }

    [Fact]
    public void OsVersionIsMatchedAsARegularExpression()
    {
        Rule[] windows10 = [MakeRule(osName: "windows", version: "^10\\.")];

        Assert.True(RuleEvaluator.IsAllowed(windows10, Windows));
        Assert.False(RuleEvaluator.IsAllowed(windows10, new PlatformContext("windows", "x86_64", "6.1.7601")));
        Assert.False(RuleEvaluator.IsAllowed(windows10, MacArm));

        Rule[] windows10Or11 = [MakeRule(osName: "windows", version: "^(10|11)\\.")];
        Assert.True(RuleEvaluator.IsAllowed(windows10Or11, new PlatformContext("windows", "x86_64", "11.0.22631")));
    }

    [Fact]
    public void InvalidOsVersionPatternIsTreatedAsAMatch()
    {
        // A malformed third-party regex must not hide a library; the evaluator falls back to "matches".
        Rule[] rules = [MakeRule(osName: "windows", version: "[")];

        Assert.True(RuleEvaluator.IsAllowed(rules, Windows));
    }

    [Fact]
    public void X86RuleMatchesBoth32And64BitX86()
    {
        Rule[] rules = [MakeRule(arch: "x86")];

        Assert.True(RuleEvaluator.IsAllowed(rules, Windows));
        Assert.True(RuleEvaluator.IsAllowed(rules, new PlatformContext("linux", "x86", "6.5.0")));
        Assert.True(RuleEvaluator.IsAllowed(rules, new PlatformContext("windows", "x86", "10.0.19045")));
    }

    [Fact]
    public void X86RuleDoesNotMatchArm64()
    {
        Rule[] rules = [MakeRule(arch: "x86")];

        Assert.False(RuleEvaluator.IsAllowed(rules, MacArm));
        Assert.False(RuleEvaluator.IsAllowed(rules, new PlatformContext("windows", "arm64", "10.0.22631")));
    }

    [Fact]
    public void X8664RuleDoesNotMatchA32BitPlatform()
    {
        Rule[] rules = [MakeRule(arch: "x86_64")];

        Assert.True(RuleEvaluator.IsAllowed(rules, Windows));
        Assert.False(RuleEvaluator.IsAllowed(rules, new PlatformContext("windows", "x86", "10.0.19045")));
        Assert.False(RuleEvaluator.IsAllowed(rules, MacArm));
    }

    [Fact]
    public void Arm64RuleOnlyMatchesArm64()
    {
        Rule[] rules = [MakeRule(arch: "arm64")];

        Assert.True(RuleEvaluator.IsAllowed(rules, MacArm));
        Assert.True(RuleEvaluator.IsAllowed(rules, new PlatformContext("linux", "arm64", "6.5.0")));
        Assert.False(RuleEvaluator.IsAllowed(rules, Windows));
    }

    [Fact]
    public void CombinedOsArchAndVersionPredicatesMustAllMatch()
    {
        Rule[] rules = [MakeRule(osName: "windows", arch: "x86_64", version: "^10\\.")];

        Assert.True(RuleEvaluator.IsAllowed(rules, Windows));
        Assert.False(RuleEvaluator.IsAllowed(rules, new PlatformContext("windows", "x86", "10.0.19045")));
        Assert.False(RuleEvaluator.IsAllowed(rules, new PlatformContext("windows", "x86_64", "6.1.7601")));
        Assert.False(RuleEvaluator.IsAllowed(rules, MacArm));
    }

    [Fact]
    public void ArchOnlyRuleMatchesAnyOperatingSystem()
    {
        Rule[] rules = [MakeRule(arch: "x86_64")];

        Assert.True(RuleEvaluator.IsAllowed(rules, Linux));
        Assert.True(RuleEvaluator.IsAllowed(rules, Windows));
        Assert.False(RuleEvaluator.IsAllowed(rules, MacArm));
    }

    [Fact]
    public void UnconditionalRuleIgnoresAFeatureDictionary()
    {
        Assert.True(RuleEvaluator.IsAllowed([MakeRule()], Windows, new Dictionary<string, bool> { ["anything"] = true }));
        Assert.True(RuleEvaluator.IsAllowed([MakeRule()], Linux));
    }
}
