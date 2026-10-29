using System.Text.Json;
using System.Text.Json.Nodes;
using DownCraft.Core.Diagnostics;
using DownCraft.Core.Util;

namespace DownCraft.Core.Install;

/// <summary>
/// Maintains <c>launcher_profiles.json</c> so that HMCL, PCL2, PrismLauncher, the official
/// launcher and others show the freshly installed version.
/// </summary>
public static class LauncherProfiles
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Creates or updates the profile entry that points at <paramref name="versionId"/>.</summary>
    public static void Upsert(GamePaths paths, string versionId, string? displayName = null, string? icon = null, IReporter? reporter = null)
    {
        try
        {
            var root = ReadOrCreate(paths.LauncherProfilesPath);
            var profiles = root["profiles"] as JsonObject;
            if (profiles is null)
            {
                profiles = new JsonObject();
                root["profiles"] = profiles;
            }

            var key = SanitizeKey(versionId);
            var entry = profiles[key] as JsonObject ?? new JsonObject();
            entry["name"] = string.IsNullOrWhiteSpace(displayName) ? versionId : displayName;
            entry["type"] = "custom";
            entry["lastVersionId"] = versionId;
            if (!string.IsNullOrWhiteSpace(icon))
            {
                entry["icon"] = icon;
            }

            entry["created"] ??= DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            entry["lastUsed"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            profiles[key] = entry;

            root["version"] ??= 3;
            if (root["settings"] is not JsonObject)
            {
                root["settings"] = new JsonObject
                {
                    ["language"] = "zh_CN",
                    ["enableSnapshots"] = false,
                    ["enableAdvanced"] = false,
                    ["keepLauncherOpen"] = false,
                };
            }

            File.WriteAllText(paths.LauncherProfilesPath, root.ToJsonString(Indented), Text.Utf8);
            reporter?.Debug($"已写入启动器配置 launcher_profiles.json（{versionId}）", "profile");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            reporter?.Warn($"无法写入 launcher_profiles.json：{ex.Message}", "profile");
        }
    }

    /// <summary>Marks an existing profile as used (best effort).</summary>
    public static void Touch(GamePaths paths, string versionId)
    {
        try
        {
            if (!File.Exists(paths.LauncherProfilesPath))
            {
                return;
            }

            var root = ReadOrCreate(paths.LauncherProfilesPath);
            if (root["profiles"]?[SanitizeKey(versionId)] is JsonObject entry)
            {
                entry["lastUsed"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
                File.WriteAllText(paths.LauncherProfilesPath, root.ToJsonString(Indented), Text.Utf8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    /// <summary>Reads every profile's <c>lastVersionId</c> (used by <c>downcraft list</c>).</summary>
    public static IReadOnlyList<string> ListVersionIds(GamePaths paths)
    {
        try
        {
            if (!File.Exists(paths.LauncherProfilesPath))
            {
                return [];
            }

            var root = ReadOrCreate(paths.LauncherProfilesPath);
            if (root["profiles"] is not JsonObject profiles)
            {
                return [];
            }

            return profiles
                .Select(kv => kv.Value?["lastVersionId"]?.GetValue<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Ensures the file exists with the minimal launcher schema.</summary>
    public static void EnsureExists(GamePaths paths)
    {
        if (File.Exists(paths.LauncherProfilesPath))
        {
            return;
        }

        var root = new JsonObject
        {
            ["profiles"] = new JsonObject(),
            ["settings"] = new JsonObject
            {
                ["language"] = "zh_CN",
                ["enableSnapshots"] = false,
                ["enableAdvanced"] = false,
            },
            ["version"] = 3,
        };
        File.WriteAllText(paths.LauncherProfilesPath, root.ToJsonString(Indented), Text.Utf8);
    }

    private static JsonObject ReadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject
            {
                ["profiles"] = new JsonObject(),
                ["settings"] = new JsonObject { ["language"] = "zh_CN" },
                ["version"] = 3,
            };
        }

        try
        {
            var text = File.ReadAllText(path, Text.Utf8);
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject
            {
                ["profiles"] = new JsonObject(),
                ["settings"] = new JsonObject { ["language"] = "zh_CN" },
                ["version"] = 3,
            };
        }
    }

    private static string SanitizeKey(string versionId)
    {
        // Launcher profile keys are free-form, but keeping them readable helps users.
        var invalid = Path.GetInvalidFileNameChars();
        var chars = versionId.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
