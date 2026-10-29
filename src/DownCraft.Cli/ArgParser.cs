using System.Globalization;

namespace DownCraft.Cli;

/// <summary>
/// Minimal, dependency-free option parser. Supports <c>--name value</c>, <c>--name=value</c>,
/// <c>-n value</c> and boolean switches.
/// </summary>
internal sealed class ArgParser
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positionals = [];
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public ArgParser(string[] args, IReadOnlySet<string>? flags = null)
    {
        var flagSet = flags ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                for (var j = i + 1; j < args.Length; j++)
                {
                    _positionals.Add(args[j]);
                }

                break;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var token = arg[2..];
                var equals = token.IndexOf('=');
                if (equals > 0)
                {
                    _options[token[..equals]] = token[(equals + 1)..];
                    continue;
                }

                if (flagSet.Contains(token) || i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                {
                    _options[token] = null;
                    _flags.Add(token);
                    continue;
                }

                _options[token] = args[++i];
                continue;
            }

            if (arg.StartsWith('-') && arg.Length > 1 && !char.IsDigit(arg[1]))
            {
                var token = arg[1..];
                if (flagSet.Contains(token) || i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                {
                    _options[token] = null;
                    _flags.Add(token);
                    continue;
                }

                _options[token] = args[++i];
                continue;
            }

            _positionals.Add(arg);
        }
    }

    public IReadOnlyList<string> Positionals => _positionals;

    public string? Value(params string[] names)
    {
        foreach (var name in names)
        {
            if (_options.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    public bool Has(params string[] names) => names.Any(name => _options.ContainsKey(name));

    public int IntValue(int fallback, params string[] names)
    {
        var raw = Value(names);
        return raw is not null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    public IReadOnlyList<string> ListValue(params string[] names)
    {
        var raw = Value(names);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
