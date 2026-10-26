namespace CrewTerm.App;

public sealed class CrewTermCommandLine
{
    private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
    public string Command { get; private set; } = "ui";
    public List<string> Positionals { get; } = [];
    public string StateDirectory => Get("state-dir") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrewTerm");
    public string? Get(string name) => values.GetValueOrDefault(name);

    public static CrewTermCommandLine Parse(string[] args)
    {
        var options = new CrewTermCommandLine();
        var commandSet = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h" or "--version" or "--server")
            { options.Command = arg.TrimStart('-'); commandSet = true; continue; }
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Option {arg} requires a value");
                options.values[arg[2..]] = args[++i];
            }
            else if (!commandSet) { options.Command = arg.ToLowerInvariant(); commandSet = true; }
            else options.Positionals.Add(arg);
        }
        return options;
    }
}
