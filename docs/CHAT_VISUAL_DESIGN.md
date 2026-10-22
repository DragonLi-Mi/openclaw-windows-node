# Native chat visual system

Workspace's ChatPage uses native Reactor/WinUI controls. This
presentation follows the reading rhythm of the published 2026.9.4 web chat
(source `3a9d69db306cd7f081e06254cb89c4bcc14a7107`) without
replacing Windows chrome, native input, or gateway contracts.

## Geometry and typography

`ChatVisuals` owns the shared geometry. The reading surface is capped at 768
pixels. Transcript prose is inset 16 pixels from the aligned composer edge.
Outer gutters are 12 pixels below 640 pixels and 40 pixels above it. The native
annotated rail overlays only the unused right gutter, leaving transcript and
composer centered on the viewport, including while the rail is focused.
The avatar is omitted below 960 pixels; at wider sizes its balanced side slots
leave prose centered. Accessible sender and footer identity remain.

The writing surface uses a 20-pixel corner radius, 16-pixel editor inset, and a
112-pixel minimum height. Message and code surfaces use 12-pixel corners.
These are chat surface tokens, not replacements for standard control radii.
All layout spacing is on the 4-pixel grid.

Prose uses native body type at 14 pixels with 21-pixel line height. Headings
use 24/20/16 pixels for H1/H2/H3 and below. Top-level blocks use one uniform
16-pixel gap (the native 4-pixel-grid approximation of Web's 14 pixels), with
zero extra top/bottom block margins. List and quote interiors are unchanged.
Text/media stacks use that same gap. The transcript has a 16-pixel lead-in. Comparisons
normalize the released web header separately rather than adding fake app chrome.
Code remains literal and selectable, with native horizontal and vertical
scrolling rather than truncation. Its header exposes the supplied language and
the existing Copy action. The code body uses native Consolas at 13 pixels with
18-pixel line height, matching the web's line rhythm while compensating for
Consolas's smaller glyphs relative to the web's 12-pixel monospace face.
The language label uses the same family at 12 pixels. Max-height line stacking
allows larger or fallback glyphs to grow instead of clipping at text scales.
The frame keeps 12-pixel horizontal and 8-pixel vertical padding so the more
readable leading does not crowd compact conversations. Code text uses the
native secondary text brush for a calmer tone, with system window text in HC.

Additional shared glyph aliases live in `FluentIconCatalog`: `ChevronDown`
(E70D) and `Stop` (E71A).
Glyphs do not scale with text. Text containers keep native scaling enabled.
Chat uses dedicated `ChatSubmit` (Up, E74A) and `ChatAttach` (Add, E710)
aliases. Other Send glyph aliases are unchanged.

## Theme resources

`Themes/ChatResources.xaml` owns the following semantic aliases in Default
(Dark), Light, and HighContrast. No web colors or external fonts are imported.

| Resource | Native source |
|---|---|
| `ChatCanvasBrush` | Transparent; system window in HC |
| `ChatComposerBrush` | `CardBackgroundFillColorDefault` |
| `ChatCardBrush` | `CardBackgroundFillColorDefault` |
| `ChatStrokeBrush` | `ControlStrokeColorDefault` |
| `ChatTextBrush` | `TextFillColorPrimary` |
| `ChatSecondaryTextBrush` | `TextFillColorSecondary` |
| `ChatUserBrush` | Windows accent at low opacity; full system highlight in HC |
| `ChatPendingUserBrush` | Transparent in every theme, revealing the conversation background |
| `ChatUserTextBrush` | Primary text; system highlight text in HC |
| `ChatRichTextStyle` | Native rich-text Foreground binding to `ChatTextBrush` |
| `ChatCodeTextStyle` | Native code Foreground binding to `ChatSecondaryTextBrush` |
| `ChatCopySuccessBrush` | `SystemFillColorSuccess`; system window text in HC |
| `ChatPickerAccentBrush` | Light/dark system accent variants; system window text in HC |

HC maps surfaces, strokes and text to system window/button/highlight colors.
An HC resource simulation is not evidence that Windows HC mode was enabled.

Window layering follows WinUI Gallery: the Workspace title bar and expanded navigation
pane reveal Mica, while `NavigationViewContentBackground` supplies one content
layer (`LayerFillColorDefaultBrush` in Light/Dark, system window in HC).
The embedded chat canvas is transparent so it neither hides nor doubles that
layer, including disconnected/loading states. The composer and code cards
use the card fill above that content layer.

## Controls and states

Conversation side panels share one native host beside the existing timeline and
composer. `ChatConversationPanelState` retains only panel selection and width
for the exact Gateway record, accepted signing identity and session key/id/agent.
Changing connection epoch revokes pending work without treating the retained
layout as authorization. A session id changing from unknown to known is an
identity change, not a wildcard match.
The retained ChatPage owns the layout store across provider/client remounts;
each new mount gets a fresh operation lifetime. Closing the Workspace clears
that window's layout store. Content and edit buffers are not stored by this host.

The conversation remains mounted while panels open, close or resize. At widths
below 652 pixels, the panel fills the conversation area and the retained
conversation is collapsed, not reparented. Close or Escape from inside the panel
returns focus to its opener; wide layouts support F6 between panes. Narrow F6
switches presentation without closing the panel; **Show conversation panel**
returns to it. Escape in the composer does not close a hidden panel. The resize
grip supports pointer drag, Left/Right and Home/End. Panel transitions add no
custom animation. Content owners supply their own scrolling and loading/error
states; the host shows a connection-wait message instead of stale content.

Expanded native tool cards expose **Open details**. The conversation panel
reuses the tool renderer's selectable literal input/output, including its
existing size limits. It retains only the request identity, not a copy of tool
content. Live output updates use the current snapshot. An authoritative history
commit can reassign synthetic tool identities, so it leaves the panel open with
explicit unavailable/reopen guidance. Reopening the current card establishes a
new request. This conservative boundary does not claim continuity across history
rebuilds or render arbitrary tool HTML.

This foundation does not turn the agent Workspace page into conversation Files,
resolve a filesystem root from chat labels, or reuse the global node Canvas.
Files requires the selected session's Gateway-provided source and conflict-safe
save contract. Hosted Canvas content requires its separate isolated content
boundary; a native panel alone does not establish Canvas parity.

### Conversation Canvas content boundary

`ReactorConversationCanvas` is the content consumer for the conversation panel,
not the node-global `canvas.*` window. Integration must supply a typed descriptor,
the exact accepted conversation identity and the normal composer's acknowledged
prompt-admission callback. Visible tool text does not supply document ownership.
The Shared parser preserves ordered `type:"canvas"` blocks and structured MCP App
preview metadata rather than treating HTML or document identifiers as media.

Saved documents use the advertised `canvas.document.view` method with the captured
session key, document id and ready-connection epoch. The authenticated response
supplies HTML and the public sandbox shell URL. The content-only WebView2 uses
one ephemeral profile per surface, a trusted in-memory parent document, and the
Gateway's separate-origin `/mcp-app-sandbox` proxy. Authored HTML is delivered by
message to that proxy, whose inner document remains sandboxed. No Gateway token,
local directory mapping, host object, OS bridge or general native RPC is exposed.

The native consumer has localized loading, waiting, blocked, unsupported and
retry states. Missing effective Gateway embed policy disables scripts and shows
an explicit restriction notice; a preview's `scripts` field alone cannot grant
execution. The narrow private prompt channel requires current visible focus,
non-command text of at most 4,000 characters, no control or bidi-override
characters, at most ten prompts per minute, and the parent conversation's actual
acceptance. An optional authenticated `embedSandbox` response field supplies
the effective Gateway mode; absent or unknown values remain strict, including
on older Gateways. The field describes the active policy when the response is
produced, not instantaneous revocation of already-running document scripts.
This character rejection is intentionally stricter than the web
client. Close or owner change disposes the surface; a reconnect must refetch the
document rather than reuse old sandbox or prompt authority.
Foreground-window and focus checks are not proof of a recent user gesture. A
script in the currently focused document can request normal conversational
prompts within these bounds, as in the web client. The conversation owner still
decides admission. Repeated rejected requests coalesce into one native notice;
the notice does not move focus or repeatedly announce through a live region.
Unknown availability, a pending persisted widget receipt and read timeouts retry
automatically on the same lease, with exponential backoff capped at 30 seconds
and eight total attempts. A structured receipt's `retryAfterMs` hint can extend
the wait up to five minutes. Exhaustion shows the normal retryable error state;
definitive denials and unsupported methods do not trigger automatic requests.
Seven five-minute server-directed waits can keep the panel waiting for about
35 minutes before exhaustion. Retries are backoff-driven, not triggered by Canvas
events. `OPENCLAW_CANVAS_RETRY_DELAY_MS` can override the initial delay from
100 to 30,000 milliseconds; it does not change the attempt count or either cap.
The rate limit belongs to the captured controller lifetime; reopening creates
a new limiter. Retiring browser environments remain rooted until exit, and
normal cleanup removes only their own profile after exit. First-use cleanup
runs off the UI thread and considers only versioned GUID profile directories
whose recorded app and browser process identities have both ended. Unknown,
malformed, redirected or potentially live profiles remain untouched. A crash
before the browser identity is recorded can leave an unsweepable profile; it is
preserved rather than risking a live browser's files.

This consumer does not implement legacy hosted-URL previews. They retain an
explicit unsupported state. MCP Apps have their own protocol engine and native
content surface, but remain explicitly unavailable until the integration gates
below are satisfied. They never use the Canvas prompt channel. Root/transcript integration,
authoritative script-policy transport and current-build populated native proof
are separate completion gates, not claims established by source or unit tests.
The Shared MCP App transport separately preserves the authenticated view payload
and exposes an explicit operation allowlist. It overwrites top-level
session/view/agent routing with the captured owner, preserves tool arguments and
extension metadata as data, and never retries admitted operations automatically.
Cancellation, timeout or a connection change after a write response can leave
an unknown outcome even when the Gateway already executed the operation. The
bridge must not present that as a confirmed failure or automatically retry it.
`MCP_APP_VIEW_EXPIRED` remains a structured Gateway detail code for an expired
view, not a retryable generic error. The view HTML limit follows the existing
32 Mi UTF-16-character WebSocket envelope ceiling, rather than the separate
10 MiB UTF-8 saved-Canvas-widget limit.
This transport seam alone does not enable the native MCP App UI.

### MCP App native content integration

The content host adapts the reachable web contract at upstream
`776c8da8f06c6ed244b8ce22ea33165e4463f23b` and its ext-apps 2.0.3
`2026-01-26` protocol. `ConversationContentWebView` owns the shared audited
browser lifecycle for both content families. The saved-Canvas adapter retains
its private prompt grant; `ConversationMcpAppHostDocument` instead forwards
bounded JSON-RPC only from the current separate-origin proxy iframe. It closes
offered private ports. Native admission additionally checks exact parent URI,
nonce, render id and the canonical operation lifetime before parsing requests.
No Dashboard, settings or chat Web UI is hosted.

`ChatConversationMcpAppSession` requires initialize followed by initialized before
tool input/result notifications and view operations. Unknown versions negotiate
the supported 2026 version. Actual operator read/write scopes and authenticated
view flags gate each operation. String/integer ids are distinct; duplicate
in-flight ids are rejected without resolving the original request, unknown
response ids are dropped, and unknown methods return `-32601`. Requests are
bounded to 32 in flight and 128 resource subscriptions per render.

Messages preserve text/image/resource/resource-link content and active/new
targets. Named text remains an attachment; untitled command text is rejected.
Message, Gateway-file and link actions require the focused foreground App,
native confirmation, a post-confirmation current/focus check, and actual Root
acceptance. They share an App-specific one-in-flight, ten-per-minute limiter,
not Canvas's prompt limiter. Files are passed only to the conversation Files
owner, never an OS path opener. Plugin links are typed navigation targets for
the authoritative native discovery owner, not arbitrary native URI launches.
Unlike the web host, this native slice confirms external/plugin links as well.
Plugin URLs reject extra query fields rather than retaining standalone tickets.

Native status reports loading, initialization, unavailable, blocked, expired,
busy, rate-limited, rejected, error and unknown-outcome states. A read retry is
explicit; admitted mutations never retry. Inline/fullscreen requests require
actual native-owner acceptance, not just a changed context value. Heights clamp
to 160..1200; theme, container dimensions and display context are updated.
Ready requires both the proxy's current render receipt and protocol initialization.
Retirement immediately denies new effects, cancels confirmations/waits, removes
event subscriptions and releases owned Gateway resource subscriptions only on
the captured still-current transport epoch. Late subscribe completion gets a
second exact-scope release. This is hard retirement, not a claim of SDK graceful
save/teardown parity.

**Integration remains blocked on this foundation.** The
`ChatConversationMcpAppIntegration.AcquireContentOperation` gate deliberately
returns no operation. The current panel API lacks `expectedContentId`, and its
identity lacks the separately pending canonical UserId/AccountRevision extension.
The controller cannot substitute a run-only operation or invented account id.
After that dependency is adopted, the integration owner must call the canonical
operation API with the selected view's expected content id and preserve the
whole captured conversation identity.

The Root must then supply `IChatConversationMcpAppActions` through
`ReactorConversationCanvasProps`: actual rich-message acceptance for active/new
targets, Gateway Files acceptance, vetted external/plugin navigation,
inline/fullscreen application and focus return. Its native confirmation hook
can use `ChatConversationMcpAppConfirmationState.ConfirmAsync` and render
`ConversationMcpAppConfirmationDialog.Render` in the native Reactor tree. The
state must be observed, disposed with that Root action owner, and its pending id
must be captured for each dialog response. Callbacks must fence the captured
operation around every await and restore content focus after the dialog closes,
before returning.
No missing hook defaults to success. The Root files are not changed by this slice.

`IChatConversationMcpAppEvents` is the narrow missing delivery contract. The
central client/event owner must explicitly allocate routing for
`mcp.app.hostContextChanged` and `mcp.app.resourceUpdated`, plus relevant pending
conversation-input events. It must capture the subscription's actual owner,
connection epoch, run and view, deliver on the owning UI dispatcher, and detach
on disposal. Do not relabel old events with the current selection. The session
already consumes those typed deliveries, refreshes model context with revision
fences, and forwards only owned resource updates. No central event cases or
independent connection registry are added here.

Until account/content binding and both real hooks are supplied, selecting an
MCP App displays unavailable without a document read or WebView creation.
Pure protocol/controller tests, loopback transport tests and a native compile
do not prove that a populated App rendered or that real Root/Files/Gateway
acceptance worked. Desktop/WebView/Gateway proof and independent final review
remain separate, explicitly unfulfilled gates. Full MCP Apps parity remains open.

Queued submissions leave the textbox and remain after the current turn as normal
right-aligned user bubbles. A transparent `ChatPendingUserBrush`, subtle `ChatStrokeBrush`
outline, and slightly faded `ChatSecondaryTextBrush` distinguish them from sent messages without
reducing whole-bubble opacity. **Pending**, Copy, and Cancel appear underneath only
while the message is hovered or contains keyboard focus. The footer reserves its
space so revealing it never moves the message. Hidden actions do not accept
pointer taps, but remain keyboard-focusable. Accessible item status stays
available even when the visual footer is hidden.
Text remains selectable and attachment chips remain visible.
Cancel stays available until dispatch; failures keep their error and removal
action visible without hovering. Provider promotion replaces the pending row with the normal sent message,
without adding queue-only rows to history. This applies to Workspace's ChatPage.
See the [pending-message concept](design/reference/concepts/states/pending-chat-message.md).

Chat responses are not read aloud by default. Enabling the Text-to-speech
capability permits playback but does not enable automatic spoken replies.
Settings > Voice > "Read responses aloud" opts in to automatic playback;
manual "Read aloud" remains available independently when the capability is
enabled. Existing saved read-aloud preferences are preserved.

Welcome quick-start suggestions use borderless native buttons with a quiet gray
`ControlAltFillColorSecondaryBrush` backplate and the shared subtle toolbar
hover/pressed resources. Disabled and keyboard-focus states remain native.
Gateway dashboard management lives in a Connection settings card rather than
occupying a row above the chat surface. Settings' Chat rail action opens Workspace
without changing the selected Settings page or discarding the chat draft.

An authoritative empty conversation shows **Recent chats** instead of quick-start
suggestions when the active agent has eligible conversations. The list includes
active or token-bearing conversations, excludes the current session, background, archived and channel
sessions, and shows at most five chats by last update (session key breaks ties).
`ChatWelcomePresentation` owns this pure projection. Rows retain native button
focus/hover states and open the original session through the composer controller's
selection handoff, preserving the draft and notifying the host. No message is sent
by opening a recent chat. When none qualify, the upstream welcome suggestions appear.
Recent titles align left and truncate before a trailing last-used timestamp, matching
the web dashboard's row hierarchy. Relative ages use the existing localized time-ago resources;
the tooltip and accessible help expose the full local timestamp. Unknown times stay
empty rather than inventing activity.
A ready compose-only session can show this welcome state even when other sessions
exist; materialized sessions still wait for loaded, settled empty history.

The new-chat block is vertically centered within the content viewport: selected
agent name, new-session guidance, placement controls and composer, then recent
chats or starter prompts. It shares the 768-pixel reading width and responsive
gutters. A native ScrollViewer makes the entire block reachable at short heights
and large text scales. Existing transcripts keep the composer at the bottom.
The composer's parent and input control survive the transition to a transcript.
The selected Gateway agent's `identity.name`, then `name`, then ID supplies the
heading through the placement catalog; a session title is never used as an agent name.
The agent avatar, name and guidance are horizontally centered. The avatar uses the
same `AgentIdentityBadge` image loader, emoji and initials fallback as the Workspace
agent picker, without its adjacent labels. It is 96 pixels, or 64 pixels at viewport
widths of 768 pixels and below. The existing `agents.list` response supplies the
selected agent's image and emoji, with no new request or copied identity.

Welcome copy comes from the connected Gateway's advertised, authenticated
`controlUi.welcome.get` RPC, with `{ locale }` set to the app's UI language.
Version 1 returns `requestedLocale`, `resolvedLocale`, `hint`,
`recentChatsLabel`, and ordered `suggestions` containing `id`, `displayText`,
and `sendText`. The Gateway derives this catalog from the same welcome key list
and i18n sources as the web UI during its normal build. The native app does not
bundle a second prompt list, pin a copy revision, fetch GitHub sources, or execute
web assets. Unknown suggestion IDs and their supplied order work without a
Companion update; clicking sends the exact advertised `sendText`.

Content is fetched once per connection and discarded on disconnect. Reconnection
loads the current Gateway's content, and epoch checks reject late responses and
queued UI deliveries from the previous connection. The language is fixed for the
provider lifetime, matching the app's startup-only language override. Missing
method advertisement or an unknown-method response shows an explicit
unavailable message, not stale copied prompts. Invalid catalogs and failed
requests show a separate error with reconnect guidance. In either case the
composer and recent-chat navigation remain usable; the native localized recent
chat caption is used when a Gateway caption is unavailable. These compatibility
messages are the only welcome copy maintained in native resources.

The shared RPC requires the companion upstream Gateway change. Released Gateways
without it, including the previously tested 2026.9.9 preview, use the explicit
unavailable state until upgraded to a build containing that contract.

An authoritative empty chat also shows **Environment** and **Workspace** pickers
above the writing surface. They use the same native toolbar chrome and searchable,
single-selection flyouts as the model picker. Labels shrink at narrow widths;
native selection, disabled reasons, keyboard focus, and system theme resources
remain intact, with computer and folder glyphs on the environment/workspace triggers and their
native menu rows. These decorative glyphs use `FluentIconCatalog` and do not replace
the controls' accessible names. The workspace picker offers the agent's default workspace,
registered projects, explicit Git URLs, and Gateway-side folder browsing. It never
interprets a Gateway path as a folder on the Companion machine.

An existing session's current location is not inferred from the agent's default
workspace. Until there is explicit local selection intent, its picker labels show
**Not verified**, with no selected flyout row. Sending without changing a picker
preserves the existing session location. Explicitly choosing even the displayed
default creates placement intent and runs preparation before sending. Genuinely
new compose-only targets may show the agent defaults.

`ChatPlacementController` owns each mounted host's local selection and cancellation
scope. Discovery and selection are read-only. On first send, `GatewayChatPlacementClient`
rechecks device availability, applies the workspace to the exact empty session key,
and, for remote hosts, creates a session-owned worktree and requires an active
dispatch acknowledgment before the existing chat send path runs. Preparation
errors remain visible and preserve draft/attachments. Navigation, disconnect,
or disposal fences late results; reconnect preserves explicit selection intent.
Unconfirmed remote placement is not retried or redirected locally. Review that
session in the Gateway before another placement attempt.
Losing empty-session eligibility cancels pending preparation before subsequent RPCs;
already admitted Gateway mutations are drained, not rolled back. Interrupted
preparation shows an explicit uncertainty notice. Each mounted host retains explicit
location intent and in-flight preparation by session across navigation, so returning
cannot silently display Local/default or skip placement preflight after an interruption.
Placement also requires history committed in the current connection generation;
cached transcripts remain visible after reconnect but cannot authorize preparation.
While history is unknown, explicit location intent and the draft are retained.
When an admitted preparation publishes its own new session, that same-key
materialization waits for fresh history rather than cancelling itself. The
existing operation rechecks authority before each subsequent mutation and before
sending. Nonempty history, changed session/connection, or disposal still stop it;
waiting never replays an admitted mutation. Obsolete history-wait notices clear
when current history arrives so location discovery can resume.
If history never becomes current, the wait expires after 120 seconds
(`OPENCLAW_CHAT_MATERIALIZATION_HISTORY_TIMEOUT_MS` overrides that bound),
releases the send operation, and retains the draft and uncertainty warning.
Timeouts and other non-cancellation failures after handing a mutation to the Gateway
preserve the same uncertainty notice as interruption. This is conservative when the
transport cannot distinguish a rejection from a lost response; it does not assert
rollback or duplicate allocation. Read-only discovery failures remain separate and
are no longer displayed once the empty-chat pickers disappear. Native resources
own recovery messages in every supported locale; raw exception details stay in
diagnostics rather than being rendered as untranslated guidance.
Folder selection uses the current successful directory result independently of
an earlier preparation warning. A new browse invalidates the previous directory,
so a failed request cannot leave a stale folder selectable.
A completed `/new` request cannot override a newer session or page navigation;
the successful session remains discoverable without pulling the user back into chat.

Workspace's sidebar owns session selection; the composer has no session picker.
The footer has a leading Attach action, model/reasoning selectors, and
quiet secondary actions beside the primary Send/Stop action. Every control stays
in one bottom row: model text shrinks rather than moving the selectors above
the actions. At viewport widths of 560 pixels or less, effort uses a gauge and
chevron with a 44-pixel target, retaining the full textual automation name and
tooltip. Attach, model, effort, voice and Send remain reachable in narrow
layouts through actual-width observation rather than a compact-window mode.
The compact effort trigger has a transparent idle background and the same
4-pixel interaction corners as the other toolbar controls. Its gauge and
chevron are vertically centered within the 44-pixel target. The shared toolbar
resource overrides survive resizing between text and icon layouts without
replacing native hover, pressed, disabled or keyboard-focus behavior. Picker anchor
capture preserves existing target mount callbacks.
There is no composer overflow menu. Voice preferences remain available in
Settings > Voice, where "Read responses aloud" controls the same shared
speaker mute state. Native focus, hover and disabled states remain.

The context-usage footer shows the latest assistant request measurement, not
the sum of overlapping requests. Newer session snapshots may lower that value,
including after compaction, but require an explicit positive total. A missing
total does not make input/output billing counters authoritative because they
may omit cached context. Cached seeding retains its existing fallback behavior.
Undated duplicates preserve the known measurement timestamp only when their
effective usage fields are unchanged, so matching older history cannot rewind
the footer. Changed undated usage does not inherit a measurement timestamp.

The compact effort gauge is an explicitly requested exception to the font-icon
convention: `ChatEffortGauge` renders the published web SVG geometry with native
WinUI paths and an ellipse, not a font approximation or WebView. It preserves the
24-unit viewbox, 20-pixel display size, 2-unit round strokes, dial opacity and
needle rotation from -120 to +120 degrees according to the advertised stop index.
Off and unanchored values use the minimum angle. An advertised inherited default
may inform the gauge without becoming a persisted override. The icon remains
decorative; the button keeps the textual accessible name and tooltip.
The source is pinned to 2026.9.4 and credited in `THIRD_PARTY_NOTICES.md`.

The model picker uses the standard native flyout presenter and an `AutoSuggestBox`
above one grouped, single-selection `ListView`. Native list-item templates own
hover, pressed, selected, selected-hover, disabled and keyboard-focus visuals,
including the selection indicator. Model labels use normal body typography,
metadata uses captions, and provider group headers use secondary captions.
`NativeChatModelPicker` owns that native content through Reactor's generated
control-wrapper API; `ChatModelPicker` owns its flyout lifetime. Highlighting a
row does not change the model until activation. Search submission commits only
a single unambiguous selectable result; otherwise it moves focus into the list.
Unrelated composer rerenders preserve list navigation, and reopening restores
the configured selection. The list has a bounded native scroll viewport.

All footer pickers keep top-end placement. Effort retains its custom-content
native flyout with 12-pixel corners and explicit compact padding, applying
presenter styles through native setters on mount and update.
Effort uses a native discrete slider over the
advertised stops, a textual heading/current value, and Faster/Smarter captions.
A single advertised stop uses a labeled button. The explicit Default reset
remains separate from the slider; neither opening the flyout nor its initial
value writes a setting. A Fast-mode switch is not displayed because that
workflow is not implemented by this composer. No decorative nonfunctional
web-only controls are added.
Native virtualization, scroll navigation and list lifetime remain with the
existing timeline and scroll-controller owners. The visual refresh does not
replace their navigation algorithm.
Pending attachments use a bounded horizontal rail with 56-pixel chips, 32-pixel
removal targets and room for focus visuals instead of growing the composer for
every file. Video-specific previews are not invented by the native file rail.

On history reload, message-wide structured user attachments appear on the first
nonempty text part only, not again on later text parts from that same message.
A textual media envelope retains precedence and prevents a later structured
fallback from repeating its chips. Matching cached captions preserve internal
line breaks and restore local filenames; already-flattened legacy captions
fall back to Gateway filenames. Reloaded descriptors do not restore image bytes.

The model flyout searches and groups only the existing catalog. Provider,
context and availability descriptions use `ChatModelLabels`. Provider headings
use the published web display names (for example, `github-copilot` becomes
`GitHub`), with a readable fallback for unknown IDs. Search accepts either the
display name or the original ID; grouping and wire identities keep the original ID.
The picker contains concrete catalog models only, with no Default/reset action.
An inherited model is shown by name when advertised, otherwise the trigger says
"Model". Opening or filtering the picker never changes the existing override.
The effort picker's separate Default reset and controller authorization paths
are unchanged.
No custom-ID, favorites, Fast, temperature or other gateway-backed setting is
invented.
Unknown nonempty thinking levels remain visible verbatim and do not check
Default. Reasoning choices come only from advertised session metadata, compatible
session defaults, or an exact provider/model catalog entry with no runtime conflict,
in that order. The first profile owns the choices, including explicit empty lists.
Structured IDs, labels and order are preserved; legacy `thinkingOptions` use
the gateway's public alias normalization. A `reasoning` boolean alone cannot
infer choices or veto an advertised mandatory profile.
Repeated profiles compare by their ordered options and default, not immutable-array
storage identity, so unchanged session/default/catalog metadata does not invalidate
the composer. Unknown and explicitly empty profiles remain distinct.

Without advertised choices the current value remains visible. A saved override
can still be cleared with Default; an already-default selector is read-only.
Default sends explicit JSON null, never an effective `thinkingDefault` value.
With no override, the slider and gauge show the advertised default's position,
while only the Default row is selected. Unknown explicit overrides are not
replaced by an inherited default.
Pointer previews commit only on release. Cancellation or capture loss while
pressed restores the current advertised selection without writing an override.
The native slider owns its value during the gesture, so unrelated snapshot
refreshes do not overwrite the active preview.
Deferred capture cleanup is gesture-fenced so it cannot cancel a newer input.
An authoritative session-list row that omits `thinkingLevel` clears the cached
override, while sparse activity events preserve it. Prepared profile metadata
may survive transient omissions only for the same identity and scope, and is
invalidated on a new connection. Model changes never replay the old override.
Model/reasoning changes require a connected, idle option-editing state;
the controller rechecks it for stale open-menu actions. Session browsing is not
disabled solely because the gateway is offline.
Context descriptions avoid repeating the group name.
The repository has a product brand mark but no
reusable provider-brand catalog, so providers retain accurate text rather than
invented logos.

The Web 9.4 retained-catalog warning/retry surface is deferred: the current chat
snapshot exposes model choices but no model-refresh failure state or explicit
refresh operation. Adding that surface requires a separate provider contract,
not view-owned network I/O.

Message and code Copy buttons use the same content/identity-fenced feedback
owner. A confirmed clipboard write shows a checkmark, announces Copied, and
resets after two seconds. Known clipboard access failures are logged and show
Copy failed instead. Repeated clicks restart the reset; content changes and
unmount cancel stale timers. Legacy `ClipboardHelper.CopyText` callers keep
their existing throwing behavior.
Code-copy automation IDs use `ChatCopy_code_<instance>` with a per-mount suffix,
so even identical snippets are independently addressable. The suffix survives
feedback and content rerenders, but is not a persistent conversation identifier.
Automation should discover the button in its code frame before retaining its ID.
Message-copy IDs remain based on the existing row identity.

Attachment, queue, recording, loading, thinking and error presentations use the
same resources and bounded layouts. Markdown sanitization, disabled HTML,
blocked link navigation, inert images, list measurement, timeline keys and
scroll-follow remain with their existing owners.
