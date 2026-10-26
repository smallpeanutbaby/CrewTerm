using CrewTerm.Automation;

namespace CrewTerm.Automation.Tests;

public sealed class AgentCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "crewterm-agents-" + Guid.NewGuid().ToString("N"));
    public AgentCatalogTests() => Directory.CreateDirectory(root);

    [Fact]
    public void LocatorFindsWindowsCommandShimsInPathsWithSpaces()
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "my tools")).FullName;
        var command = Path.Combine(bin, "example.cmd");
        File.WriteAllText(command, "@echo off\r\n");
        var found = ExecutableLocator.Find("example", bin, ".EXE;.CMD");
        Assert.Equal(command, found, ignoreCase: OperatingSystem.IsWindows());
        Assert.Null(ExecutableLocator.Find("missing-agent", bin, ".EXE;.CMD"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocatorPrefersNativeShimWhenExtensionlessScriptAlsoExists(bool absolutePath)
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "npm tools")).FullName;
        var unixShim = Path.Combine(bin, "example");
        var windowsShim = unixShim + ".cmd";
        File.WriteAllText(unixShim, "#!/bin/sh\n");
        File.WriteAllText(windowsShim, "@echo off\r\n");

        var found = ExecutableLocator.Find(absolutePath ? unixShim : "example", bin, ".EXE;.CMD");

        Assert.Equal(OperatingSystem.IsWindows() ? windowsShim : unixShim, found,
            ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void CustomProfilePersistsUpdatesAndRemoval()
    {
        var catalog = new AgentCatalog(root);
        catalog.Upsert(new("custom", "Custom agent", "custom-cli", "--mode first"));
        catalog.Upsert(new("custom", "Custom agent", "custom-cli", "--mode revised"));
        var loaded = new AgentCatalog(root);
        var profile = Assert.Single(loaded.Profiles, p => p.Id == "custom");
        Assert.Equal("--mode revised", profile.Arguments);
        Assert.False(profile.IsBuiltIn);
        Assert.True(loaded.Remove("custom"));
        Assert.DoesNotContain(new AgentCatalog(root).Profiles, p => p.Id == "custom");
    }

    [Fact]
    public void BuiltinProfilesAreNotDeletedFromCatalog()
    {
        var catalog = new AgentCatalog(root);
        Assert.False(catalog.Remove("shell"));
        Assert.Contains(catalog.Discover(), a => a.Profile.Id == "shell" && a.IsAvailable);
    }

    [Fact]
    public void InvalidProfileDoesNotReplaceExistingData()
    {
        var catalog = new AgentCatalog(root);
        catalog.Upsert(new("kept", "Kept", "my-command"));
        Assert.Throws<ArgumentException>(() => catalog.Upsert(new("bad id", "Bad", " ")));
        Assert.Contains(new AgentCatalog(root).Profiles, p => p.Id == "kept");
    }

    [Fact]
    public void CorruptProfilesAreReportedAndPreserved()
    {
        var path = Path.Combine(root, "agent-profiles.json");
        File.WriteAllText(path, "[not json");
        Assert.Throws<InvalidDataException>(() => new AgentCatalog(root));
        Assert.Equal("[not json", File.ReadAllText(path));
    }

    [Fact]
    public void ExecutableWithSpacesIsQuotedForLaunch()
    {
        var profile = new AgentProfile("custom", "Custom", "C:\\my tools\\agent.exe", "--interactive");
        Assert.Equal("\"C:\\my tools\\agent.exe\" --interactive", profile.CommandLine);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
