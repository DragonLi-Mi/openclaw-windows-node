namespace OpenClawTray.Chat;

internal enum ChatConversationContentAdmissionKind { Accepted, Queued, Unconfirmed, Rejected, Stale }

/// <summary>Actual parent custody, not confirmation, focus, or a successful callback invocation.</summary>
internal sealed record ChatConversationContentAdmission(
    ChatConversationContentAdmissionKind Kind, string? Error = null, bool OwnsPayload = false)
{
    internal static ChatConversationContentAdmission Accepted { get; } = new(ChatConversationContentAdmissionKind.Accepted, OwnsPayload: true);
    internal static ChatConversationContentAdmission Queued { get; } = new(ChatConversationContentAdmissionKind.Queued, OwnsPayload: true);
    internal static ChatConversationContentAdmission Unconfirmed { get; } = new(ChatConversationContentAdmissionKind.Unconfirmed);
    internal static ChatConversationContentAdmission Rejected { get; } = new(ChatConversationContentAdmissionKind.Rejected);
    internal static ChatConversationContentAdmission Stale { get; } = new(ChatConversationContentAdmissionKind.Stale);
    internal bool HasCustody => OwnsPayload &&
        Kind is ChatConversationContentAdmissionKind.Accepted or ChatConversationContentAdmissionKind.Queued;

    internal ChatConversationContentAdmission AfterOwnerChange() => HasCustody ||
        Kind == ChatConversationContentAdmissionKind.Unconfirmed
            ? this with { Kind = ChatConversationContentAdmissionKind.Unconfirmed }
            : this with { Kind = ChatConversationContentAdmissionKind.Stale };
}
