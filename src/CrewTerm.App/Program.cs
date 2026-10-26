using System.Reflection;
using System.Text;
using System.Text.Json;
using CrewTerm.App.Server;
using CrewTerm.App.UI;
using CrewTerm.Automation;
using Terminal.Gui.App;

namespace CrewTerm.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        CrewTermCommandLine? options = null;
        try
        {
            options = CrewTermCommandLine.Parse(args);
            if (options.Command is "help" or "h") { Console.WriteLine(Help); return 0; }
            if (options.Command == "version") { Console.WriteLine("CrewTerm 0.1.0"); return 0; }
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This release requires Windows 10 1809 or later (ConPTY).");
            if (options.Command == "server")
            {
                using var host = new DaemonHost(options.StateDirectory);
                host.RunAsync().GetAwaiter().GetResult();
                return 0;
            }
            if (options.Command == "doctor")
            {
                Console.WriteLine($"CrewTerm 0.1.0 | {Environment.OSVersion}\nState: {options.StateDirectory}");
                foreach (var item in new AgentCatalog(options.StateDirectory).Discover())
                    Console.WriteLine($"{item.Profile.Name,-18} {item.ExecutablePath ?? "not installed"}");
                return 0;
            }
            EnsureDaemonAsync(options.StateDirectory).GetAwaiter().GetResult();
            if (options.Command != "ui") return ExecuteAsync(options).GetAwaiter().GetResult();
            bool inputDrained;
            using (var app = Application.Create())
            {
                app.Init();
                var window = new WorkspaceWindow(app, options);
                try { app.Run(window); }
                finally { window.Dispose(); }
                inputDrained = window.InputDrained;
            }
            if (!inputDrained)
            {
                Console.Error.WriteLine("CrewTerm: some input could not be delivered. Reconnect and inspect the terminal before retrying a command.");
                return 1;
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("CrewTerm: " + error.Message);
            if (options?.Command == "server")
            {
                try { File.AppendAllText(Path.Combine(options.StateDirectory, "server-error.log"), $"{DateTimeOffset.Now:O} {error}\n"); }
                catch (IOException) { }
            }
            return 1;
        }
    }

    internal static async Task EnsureDaemonAsync(string stateDirectory)
    {
        var client = new DaemonClient(stateDirectory);
        try { if ((await client.CallAsync(new("ping"))).Ok) return; }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
        Directory.CreateDirectory(stateDirectory);
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable");
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Assembly.GetExecutingAssembly().Location);
        arguments.Add("--server");
        arguments.Add("--state-dir");
        arguments.Add(Path.GetFullPath(stateDirectory));
        DetachedProcess.Start(processPath, arguments);
        for (var i = 0; i < 30; i++)
        {
            try { if ((await client.CallAsync(new("ping"))).Ok) return; }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await Task.Delay(100);
        }
        throw new IOException("Session server did not start. Inspect server-error.log in " + stateDirectory);
    }

    private static async Task<int> ExecuteAsync(CrewTermCommandLine options)
    {
        string Required(string key) => options.Get(key) ?? throw new ArgumentException("Required: --" + key);
        var id = options.Get("id") ?? options.Positionals.FirstOrDefault();
        var request = options.Command switch
        {
            "sessions" => new DaemonRequest("list"),
            "new" => new("create", Command: Required("command"), WorkingDirectory: options.Get("project") ?? Environment.CurrentDirectory, Name: options.Get("name")),
            "send" => new("input", SessionId: id, Text: Required("text")),
            "read" => new("screen", SessionId: id),
            "close" => new("close", SessionId: id),
            "stop" => new("stop"),
            "tasks" => new("task-list"),
            "run" => new("task-start", Command: Required("command"), WorkingDirectory: options.Get("project") ?? Environment.CurrentDirectory, Name: options.Get("name") ?? "Command"),
            "task" => new("task-get", SessionId: id),
            "cancel" => new("task-cancel", SessionId: id),
            _ => throw new ArgumentException("Unknown command: " + options.Command + ". Use --help.")
        };
        var response = await new DaemonClient(options.StateDirectory).CallAsync(request);
        if (!response.Ok) throw new InvalidOperationException(response.Error);
        Console.WriteLine(options.Command == "read" ? response.Text : JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    internal const string Help = """
        CrewTerm 0.1.0 — multi-agent terminal workspace (Windows / C#)

        crewterm [--project PATH] [--state-dir PATH]     Open workspace
        crewterm doctor                                Discover installed agent CLIs
        crewterm sessions                              List live terminals (JSON)
        crewterm new --command "claude" --project PATH  Start an embedded terminal
        crewterm send ID --text "..."                   Send literal input
        crewterm read ID                               Read terminal screen
        crewterm close ID                              Terminate a terminal
        crewterm run --command "dotnet test" --project PATH
        crewterm tasks | task ID | cancel ID            Background jobs
        crewterm stop                                  Stop daemon, terminals and jobs

        F1 Help  F2 Project  F3 Agent  F4 Files  F5 Save  F6 Git
        F7 Tasks  F8 Split  F9 Commands  F10 Detach
        Ctrl+Shift+P Commands | Ctrl+Shift+V Paste | Ctrl+Shift+C Copy screen
        Alt+1/2 Terminal focus | Alt+3 Editor | Alt+4 Files | Alt+5 Agents

        Detaching keeps sessions/tasks alive. Explicit Close/Stop terminates them.
        Agent authentication and billing belong to each installed CLI.
        """;
}
