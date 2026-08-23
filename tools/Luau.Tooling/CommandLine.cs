namespace Luau.Tooling;

internal sealed class CommandLine
{
    private readonly List<string> _positionals = [];
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);

    public CommandLine(IEnumerable<string> arguments)
    {
        var values = arguments.ToArray();
        for (var index = 0; index < values.Length; index++)
        {
            var argument = values[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                _positionals.Add(argument);
                continue;
            }

            var equals = argument.IndexOf('=');
            string name;
            string value;
            if (equals >= 0)
            {
                name = argument[..equals];
                value = argument[(equals + 1)..];
            }
            else
            {
                name = argument;
                if (index + 1 < values.Length && !values[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = values[++index];
                }
                else
                {
                    value = "true";
                }
            }

            Add(name, value);
        }
    }

    public IReadOnlyList<string> Positionals => _positionals;

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.TryGetValue(name, out var values) ? values[^1] : null;

    public string Get(string name, string defaultValue) => Get(name) ?? defaultValue;

    public IReadOnlyList<string> GetMany(string name) =>
        _options.TryGetValue(name, out var values) ? values : [];

    public int GetInt(string name, int defaultValue)
    {
        var value = Get(name);
        return value is null ? defaultValue : int.TryParse(value, out var parsed)
            ? parsed
            : throw new ToolingException($"Option {name} requires an integer value.");
    }

    private void Add(string name, string value)
    {
        if (!_options.TryGetValue(name, out var values))
        {
            values = [];
            _options.Add(name, values);
        }

        values.Add(value);
    }
}
