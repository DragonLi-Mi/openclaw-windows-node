using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>The only trusted web surface. Authored HTML is delivered to the Gateway's isolated proxy.</summary>
public static class ConversationCanvasHostDocument
{
    public static string ContentSecurityPolicy(ConversationCanvasBoundary boundary, string nonce) =>
        $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; " +
        $"frame-src {boundary.SandboxOrigin}; connect-src 'none'; base-uri 'none'; " +
        "form-action 'none'; object-src 'none'; frame-ancestors 'none'";

    public static string Build(ConversationCanvasBoundary boundary, string nonce, string title, string renderId,
        string? scriptNonce = null, string? theme = null)
    {
        scriptNonce ??= nonce;
        var config = JsonSerializer.Serialize(new
        {
            nonce,
            sandboxUrl = boundary.SandboxUrl.AbsoluteUri,
            sandboxOrigin = boundary.SandboxOrigin,
            title,
            renderId,
            theme = theme is "dark" ? "dark" : "light"
        });
        return $$"""
            <!doctype html>
            <html><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <style nonce="{{scriptNonce}}">html,body{height:100%;margin:0}iframe{display:block;width:100%;height:100%;border:0}</style>
            </head><body><script nonce="{{scriptNonce}}">
            (() => {
              "use strict";
              const config = {{config}};
              const native = window.chrome.webview;
              const frame = document.createElement("iframe");
              let theme = config.theme;
              document.documentElement.style.colorScheme = theme;
              frame.style.colorScheme = theme;
              frame.title = config.title;
              frame.referrerPolicy = "origin";
              frame.setAttribute("sandbox", "allow-scripts allow-same-origin allow-forms");
              frame.setAttribute("allow", "fullscreen");
              let ready = false;
              let delivered = false;
              let rendered = false;
              let promptPort;
              const send = (type, extra = {}) => native.postMessage({
                nonce: config.nonce, renderId: config.renderId, type, ...extra
              });
              const focused = () => document.hasFocus() && document.activeElement === frame &&
                frame.isConnected && frame.getClientRects().length > 0;
              const postTheme = () => frame.contentWindow?.postMessage(
                { type: "openclaw:widget-theme", mode: theme }, config.sandboxOrigin);
              native.addEventListener("message", event => {
                const data = event.data;
                if (data?.nonce !== config.nonce || data?.renderId !== config.renderId) return;
                if (data.type === "theme" && (data.mode === "light" || data.mode === "dark")) {
                  theme = data.mode;
                  document.documentElement.style.colorScheme = theme;
                  frame.style.colorScheme = theme;
                  if (ready) postTheme();
                  return;
                }
                if (data.type !== "document" || typeof data.html !== "string" || !ready || delivered) return;
                delivered = true;
                frame.contentWindow.postMessage({
                  jsonrpc: "2.0", method: "ui/notifications/sandbox-resource-ready",
                  params: { html: data.html, renderId: config.renderId, allowScripts: data.allowScripts === true }
                }, config.sandboxOrigin);
              });
              window.addEventListener("message", event => {
                if (event.source !== frame.contentWindow || event.origin !== config.sandboxOrigin) return;
                const data = event.data;
                if (data?.method === "ui/notifications/sandbox-proxy-ready" &&
                    data.params?.sandboxUrl === config.sandboxUrl) {
                  if (ready) {
                    promptPort?.close();
                    send("surfaceError");
                    return;
                  }
                  ready = true;
                  postTheme();
                  send("proxyReady");
                  return;
                }
                if (data?.method === "ui/notifications/sandbox-resource-loaded" &&
                    data.params?.renderId === config.renderId && delivered && !rendered) {
                  rendered = true;
                  postTheme();
                  send("rendered");
                  return;
                }
                if (data?.type === "openclaw:widget-bridge-port-offer") {
                  for (const port of event.ports) port.close();
                  return;
                }
                if (data?.type !== "openclaw:widget-prompt-offer") return;
                const port = event.ports[0];
                if (!port || promptPort || !delivered) {
                  for (const offered of event.ports) offered.close();
                  return;
                }
                promptPort = port;
                port.onmessage = message => {
                  if (message.data?.type === "openclaw:widget-prompt" && rendered && focused() &&
                      typeof message.data.prompt === "string")
                    send("prompt", { text: message.data.prompt });
                };
                port.start();
                port.postMessage({ type: "openclaw:widget-prompt-host-ready" });
              });
              window.addEventListener("pagehide", () => {
                promptPort?.close();
                promptPort = undefined;
                frame.remove();
              }, { once: true });
              frame.src = config.sandboxUrl;
              document.body.appendChild(frame);
            })();
            </script></body></html>
            """;
    }
}
