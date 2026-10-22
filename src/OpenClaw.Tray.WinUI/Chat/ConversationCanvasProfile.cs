using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

internal sealed class ConversationCanvasProfile
{
    private const string OwnerFormat = "openclaw-conversation-canvas-profile/v1";
    private const string OwnerFileName = "companion-owner.json";
    private const int MaximumOwnerMetadataBytes = 2048;
    private int _deleted;
    private ProfileOwner? _owner;
    private sealed record ProfileOwner(string Format, int ProcessId, long StartTimeUtcTicks,
        int BrowserProcessId = 0, long BrowserStartTimeUtcTicks = 0);

    internal ConversationCanvasProfile(string localDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataRoot);
        DirectoryPath = Path.Combine(Path.GetFullPath(localDataRoot), "ConversationCanvas", Guid.NewGuid().ToString("N"));
    }

    internal string DirectoryPath { get; }

    internal void Create()
    {
        for (var parent = new DirectoryInfo(DirectoryPath); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The Canvas profile path must not traverse a redirected directory.");
        SweepStaleProfiles(Path.GetDirectoryName(DirectoryPath)!);
        Directory.CreateDirectory(DirectoryPath);
        using var process = Process.GetCurrentProcess();
        _owner = new(OwnerFormat, process.Id, process.StartTime.ToUniversalTime().Ticks);
        WriteOwner();
    }

    internal void RecordBrowser(int processId)
    {
        if (_owner is null) throw new InvalidOperationException("The Canvas profile has not been created.");
        using var process = Process.GetProcessById(processId);
        _owner = _owner with { BrowserProcessId = processId, BrowserStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks };
        WriteOwner();
    }

    private void WriteOwner() =>
        File.WriteAllText(Path.Combine(DirectoryPath, OwnerFileName), JsonSerializer.Serialize(_owner));

    internal static bool IsSweepCandidate(string directoryName, FileAttributes attributes) =>
        Guid.TryParseExact(directoryName, "N", out _) && (attributes & FileAttributes.ReparsePoint) == 0;

    /// <summary>Only versioned app-owned profiles with both recorded processes gone are eligible.</summary>
    internal static void SweepStaleProfiles(string profilesRoot)
    {
        if (!Directory.Exists(profilesRoot)) return;
        if ((File.GetAttributes(profilesRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Canvas orphan cleanup refused a redirected profile root.");
        foreach (var directory in new DirectoryInfo(profilesRoot).EnumerateDirectories())
        {
            if (!IsSweepCandidate(directory.Name, directory.Attributes)) continue;
            var marker = new FileInfo(Path.Combine(directory.FullName, OwnerFileName));
            if (!marker.Exists || marker.Length > MaximumOwnerMetadataBytes ||
                (marker.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                var owner = JsonSerializer.Deserialize<ProfileOwner>(File.ReadAllText(marker.FullName));
                if (owner is not { Format: OwnerFormat, ProcessId: > 0, StartTimeUtcTicks: > 0,
                    BrowserProcessId: > 0, BrowserStartTimeUtcTicks: > 0 } ||
                    MayBeAlive(owner.ProcessId, owner.StartTimeUtcTicks) ||
                    MayBeAlive(owner.BrowserProcessId, owner.BrowserStartTimeUtcTicks))
                    continue;
                RefuseReparsePoints(directory.FullName);
                Directory.Delete(directory.FullName, recursive: true);
            }
            catch (JsonException ex) { LogSweepFailure(ex); }
            catch (IOException ex) { LogSweepFailure(ex); }
            catch (UnauthorizedAccessException ex) { LogSweepFailure(ex); }
        }
    }

    private static bool MayBeAlive(int processId, long startTimeUtcTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks == startTimeUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; }
    }

    private static void LogSweepFailure(Exception exception) =>
        Logger.Warn($"[ConversationCanvas] Orphan profile was preserved ({exception.GetType().Name}).");

    /// <summary>Called only when no browser has started, or after this environment's browser exits.</summary>
    internal void DeleteAfterBrowserExit()
    {
        if (Interlocked.Exchange(ref _deleted, 1) != 0 || !Directory.Exists(DirectoryPath)) return;
        try
        {
            RefuseReparsePoints(DirectoryPath);
            Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (IOException ex)
        {
            Logger.Warn($"[ConversationCanvas] Isolated profile cleanup failed ({ex.GetType().Name}).");
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Warn($"[ConversationCanvas] Isolated profile cleanup failed ({ex.GetType().Name}).");
        }
    }

    private static void RefuseReparsePoints(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Canvas profile cleanup refused a redirected entry.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Canvas profile cleanup refused a redirected entry.");
            if ((attributes & FileAttributes.Directory) != 0) RefuseReparsePoints(entry);
        }
    }
}
