using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DownCraft.Core.Meta;

/// <summary>Platform description used to evaluate Mojang <c>rules</c>.</summary>
public sealed class PlatformContext
{
    public PlatformContext(string osName, string arch, string osVersion)
    {
        OsName = osName;
        Arch = arch;
        OsVersion = osVersion;
    }

    /// <summary>Mojang's <c>os.name</c> vocabulary: <c>windows</c>, <c>osx</c>, <c>linux</c>.</summary>
    public string OsName { get; }

    /// <summary>Either <c>x86_64</c> or <c>arm64</c> (the values used by Mojang JSON).</summary>
    public string Arch { get; }

    /// <summary>OS version string matched against rule regular expressions.</summary>
    public string OsVersion { get; }

    public bool IsWindows => OsName == "windows";

    public static PlatformContext Current => CreateDefault();

    public static PlatformContext CreateDefault()
    {
        var os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "osx"
            : "linux";
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            _ => "x86_64",
        };
        return new PlatformContext(os, arch, Environment.OSVersion.Version.ToString());
    }

    /// <summary>The launcher platform key used in Mojang's Java runtime manifest (<c>windows-x64</c>).</summary>
    public string RuntimePlatformKey => OsName switch
    {
        "windows" => Arch == "arm64" ? "windows-arm64" : Arch == "x86" ? "windows-x86" : "windows-x64",
        "osx" => Arch == "arm64" ? "mac-os-arm64" : "mac-os",
        _ => Arch == "x86" ? "linux-i386" : "linux",
    };
}

/// <summary>Evaluates Mojang rule lists (the <c>rules</c> arrays of libraries and arguments).</summary>
public static partial class RuleEvaluator
{
    /// <summary>
    /// Returns true when the rule list allows this library/argument on the platform.
    /// An empty rule list means "allowed"; otherwise the last matching rule wins and the
    /// default is deny — this mirrors the official launcher.
    /// </summary>
    public static bool IsAllowed(IEnumerable<Rule>? rules, PlatformContext platform, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rules is null)
        {
            return true;
        }

        var list = rules as IList<Rule> ?? rules.ToList();
        if (list.Count == 0)
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in list)
        {
            if (Matches(rule, platform, features))
            {
                allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
            }
        }

        return allowed;
    }

    /// <summary>True when the rule's predicate matches the platform and enabled features.</summary>
    public static bool Matches(Rule rule, PlatformContext platform, IReadOnlyDictionary<string, bool>? features)
    {
        if (rule.Os is { } os)
        {
            if (!string.IsNullOrEmpty(os.Name) && !string.Equals(os.Name, platform.OsName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(os.Arch) && !ArchMatches(os.Arch, platform.Arch))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(os.Version) && !VersionMatches(os.Version, platform.OsVersion))
            {
                return false;
            }
        }

        if (rule.Features is { Count: > 0 })
        {
            foreach (var (name, expected) in rule.Features)
            {
                var actual = features is not null && features.TryGetValue(name, out var value) && value;
                if (actual != expected)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Mojang uses <c>x86</c> for both 32-bit and 64-bit x86 (only <c>arm64</c> is special),
    /// so a rule asking for <c>x86</c> matches <c>x86_64</c> too.
    /// </summary>
    private static bool ArchMatches(string ruleArch, string platformArch)
    {
        if (string.Equals(ruleArch, platformArch, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ruleArch.Equals("x86", StringComparison.OrdinalIgnoreCase) &&
               platformArch.Equals("x86_64", StringComparison.OrdinalIgnoreCase);
    }

    private static bool VersionMatches(string pattern, string version)
    {
        try
        {
            return Regex.IsMatch(version, pattern);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}

/// <summary>Maven coordinates (<c>group:artifact:version[:classifier][@extension]</c>).</summary>
public sealed record MavenCoordinate(string Group, string Artifact, string Version, string? Classifier = null, string Extension = "jar")
{
    /// <summary>Parses a Minecraft library <c>name</c> field. Returns false for malformed values.</summary>
    public static bool TryParse(string? value, out MavenCoordinate coordinate)
    {
        coordinate = new MavenCoordinate(string.Empty, string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        var extension = "jar";
        var at = text.LastIndexOf('@');
        if (at > 0)
        {
            extension = text[(at + 1)..];
            text = text[..at];
        }

        var parts = text.Split(':');
        if (parts.Length < 3)
        {
            return false;
        }

        var classifier = parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null;
        coordinate = new MavenCoordinate(parts[0], parts[1], parts[2], classifier, extension);
        return true;
    }

    /// <summary>Path relative to a Maven repository root, using forward slashes.</summary>
    public string ToRepositoryPath()
    {
        var fileName = Classifier is null
            ? $"{Artifact}-{Version}.{Extension}"
            : $"{Artifact}-{Version}-{Classifier}.{Extension}";
        return $"{Group.Replace('.', '/')}/{Artifact}/{Version}/{fileName}";
    }

    public override string ToString() =>
        Classifier is null ? $"{Group}:{Artifact}:{Version}" : $"{Group}:{Artifact}:{Version}:{Classifier}";
}

/// <summary>JSON settings shared by every Minecraft document (tolerant of comments and BOMs).</summary>
public static class MinecraftJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new LenientDateTimeOffsetConverter() },
    };

    public static T? Deserialize<T>(byte[] utf8) => JsonSerializer.Deserialize<T>(utf8, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Serialises a version profile the way launchers expect (2-space indented, UTF-8 no BOM).</summary>
    public static string Serialize(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions(Options)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
}
