namespace OpenClaw.Shared;

/// <summary>Explicit selected conversation. Neither field can fall back to the main session.</summary>
public sealed record SessionFileScope(string SessionKey, string AgentId);

public sealed record SessionFileRequest(SessionFileScope Scope, string Path, string? MessageEntryId = null);

/// <summary>Gateway-resolved file authority, not a path to open on the Companion machine.</summary>
public sealed record SessionFileSource(string WorkspaceId, string? Root, string? FileRoot);

public sealed record SessionFileWrite(
    SessionFileScope Scope, string Path, string Content, string ExpectedHash, SessionFileSource ExpectedSource);

public enum SessionFilesReadiness { Waiting, Ready, Unsupported }

public sealed record SessionFilesCapabilities(
    SessionFilesReadiness Readiness, bool List = false, bool Get = false,
    bool Set = false, bool Download = false, bool Diff = false);

public sealed record SessionFileDownload(string Filename, string MimeType, string Url, DateTimeOffset ExpiresAt);

public enum SessionDiffScope { All, Uncommitted, Commit }

public sealed record SessionDiffQuery(SessionFileScope Scope, SessionDiffScope Kind = SessionDiffScope.All,
    string? Commit = null, bool SummaryOnly = false, bool Poll = true);

public sealed record SessionDiffFile(
    string Path, string Status, int Additions, int Deletions, string? OldPath = null,
    bool Binary = false, bool Untracked = false, string? Patch = null, bool Truncated = false);

public sealed record SessionDiffCommit(string Sha, string Subject);

public sealed record SessionDiff(
    string SessionKey, SessionDiffFile[] Files, int Additions, int Deletions,
    string? Root = null, string? Branch = null, string? BaseRef = null, int? AheadCount = null,
    SessionDiffCommit[]? Commits = null, SessionDiffCommit? MergeBase = null,
    bool Truncated = false, bool Computing = false, string? UnavailableReason = null);

public static class SessionFileLimits
{
    public const int PreviewBytes = 1024 * 1024;
    public const int NativeEditorCharacters = PreviewBytes / 2 - 1;
    public const int RetainedDraftCount = 32;
    public const int RetainedDraftCharacters = RetainedDraftCount * PreviewBytes * 3;
    public const int RequestTimeoutMilliseconds = 30000;

    public static bool IsContentHash(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
