using System.Text.RegularExpressions;

namespace Mindweave.Architecture.Tests;

/// <summary>Rules that keep the evolution seams (docs/evolution.md E3, E4) from being bypassed as features pile up.</summary>
public partial class SeamTests
{
    /// <summary>
    /// The key leaves the credential store only where a request is about to be sent; everywhere else holds a
    /// reference. A new reader is a new leak surface and must be added here deliberately.
    /// </summary>
    [Fact]
    public void Only_the_request_paths_read_credentials()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "ICredentialStore.cs",
            "CredentialStores.cs",
            "MacKeychainCredentialStore.cs",
            "WindowsCredentialStore.cs",
            "GenerationRequests.cs",
            "ModelSettingsService.cs",

            // Deletes replaced model keys once nothing uses them (D2 §2.3); never reads one.
            "RetiredCredentials.cs",

            // The search service's key (phase 3, decision W14): read only for a search request or 测试.
            "WebSearchSettings.cs",

            // A third-party server's environment and header values (phase 4, decision M3): read only to start or connect it.
            "McpServerSettings.cs",

            // Every stored key, read only to scrub an exported log of it (D2 §2.2); no value leaves the process.
            "StoredKeys.cs",
        };

        Assert.Empty(SourceFiles("src").Where(f => !allowed.Contains(Path.GetFileName(f)) && File.ReadAllText(f).Contains("ICredentialStore", StringComparison.Ordinal)).Select(Path.GetFileName));
    }

    /// <summary>Features decide by declared capability, never by vendor or model name (E4).</summary>
    [Fact]
    public void Core_and_application_never_branch_on_a_vendor()
    {
        var offenders = SourceFiles(Path.Combine("src", "Mindweave.Core")).Concat(SourceFiles(Path.Combine("src", "Mindweave.Application")))
            .Where(f => VendorName().IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName);

        Assert.Empty(offenders);
    }

    /// <summary>
    /// A file panel is opened in one place, which keeps it app-modal on macOS: as a sheet it can stop taking clicks
    /// (found 2026-11-25, see FilePanels). A new one that calls the picker itself would bring that back.
    /// </summary>
    [Fact]
    public void File_panels_are_opened_in_one_place()
    {
        var offenders = SourceFiles("src")
            .Where(f => Path.GetFileName(f) != "FilePanels.cs" && FilePicker().IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName);

        Assert.Empty(offenders);
    }

    [GeneratedRegex(@"\b(Save|Open)(File|Folder)PickerAsync\b|\bStorageProvider\b")]
    private static partial Regex FilePicker();

    private static IEnumerable<string> SourceFiles(string relative) =>
        Directory.EnumerateFiles(Path.Combine(LayeringTests.RepositoryRoot(), relative), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [GeneratedRegex(@"(?i)\b(openai|anthropic|claude|deepseek|gemini|gpt-\d|qwen|moonshot|mistral)\b")]
    private static partial Regex VendorName();
}
