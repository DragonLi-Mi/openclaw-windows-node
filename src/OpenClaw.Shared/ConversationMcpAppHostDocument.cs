using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>Trusted content-only transport. The proxy, not this host, owns authored-document CSP.</summary>
public static class ConversationMcpAppHostDocument
{
    public const int MaximumPacketCharacters = ConversationMcpAppProtocol.MaximumMessageCharacters + 65536;

    public static string Build(ConversationCanvasBoundary boundary, string nonce, string title,
        string renderId, string scriptNonce, string theme)
    {
        var config = JsonSerializer.Serialize(new
        {
            nonce, title, renderId, theme, maximumPacketCharacters = MaximumPacketCharacters,
            sandboxUrl = boundary.SandboxUrl.AbsoluteUri, sandboxOrigin = boundary.SandboxOrigin
        });
        return $$"""
            <!doctype html><html><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <style nonce="{{scriptNonce}}">html,body{height:100%;margin:0}iframe{display:block;width:100%;height:100%;border:0}</style>
            </head><body><script nonce="{{scriptNonce}}">
            (() => {
              "use strict";
              const config = {{config}};
              const native = window.chrome.webview;
              const frame = document.createElement("iframe");
              frame.title = config.title;
              frame.referrerPolicy = "origin";
              frame.setAttribute("sandbox", "allow-scripts allow-same-origin allow-forms");
              frame.setAttribute("allow", "fullscreen");
              document.documentElement.style.colorScheme = config.theme;
              frame.style.colorScheme = config.theme;
              let ready = false, delivered = false, rendered = false, retiring = false;
              const send = (type, extra = {}) => native.postMessage({
                nonce: config.nonce, renderId: config.renderId, type, ...extra
              });
              native.addEventListener("message", event => {
                const data = event.data;
                if (data?.nonce !== config.nonce || data?.renderId !== config.renderId || retiring) return;
                if (data.type === "theme" && (data.mode === "light" || data.mode === "dark")) {
                  document.documentElement.style.colorScheme = data.mode;
                  frame.style.colorScheme = data.mode;
                } else if (data.type === "document" && ready && !delivered && typeof data.html === "string") {
                  delivered = true;
                  frame.contentWindow.postMessage({
                    jsonrpc: "2.0", method: "ui/notifications/sandbox-resource-ready",
                    params: { html: data.html, csp: data.csp, renderId: config.renderId }
                  }, config.sandboxOrigin);
                } else if (data.type === "rpc" && delivered && data.packet?.jsonrpc === "2.0") {
                  frame.contentWindow.postMessage(data.packet, config.sandboxOrigin);
                }
              });
              window.addEventListener("message", event => {
                if (event.source !== frame.contentWindow || event.origin !== config.sandboxOrigin || retiring) return;
                for (const port of event.ports) port.close();
                const data = event.data;
                if (data?.method === "ui/notifications/sandbox-proxy-ready" &&
                    data.params?.sandboxUrl === config.sandboxUrl) {
                  if (ready) { retiring = true; send("surfaceError"); return; }
                  ready = true;
                  send("proxyReady");
                  return;
                }
                if (data?.method === "ui/notifications/sandbox-resource-loaded" &&
                    data.params?.renderId === config.renderId && delivered && !rendered) {
                  rendered = true;
                  send("rendered");
                  return;
                }
                if (!delivered || data?.jsonrpc !== "2.0" ||
                    typeof data.method === "string" && data.method.startsWith("ui/notifications/sandbox-")) return;
                let serialized;
                try { serialized = JSON.stringify(data); } catch { return; }
                if (serialized.length > config.maximumPacketCharacters) { send("blocked"); return; }
                send("rpc", { packet: data });
              });
              window.addEventListener("pagehide", () => { retiring = true; frame.remove(); }, { once: true });
              frame.src = config.sandboxUrl;
              document.body.appendChild(frame);
            })();
            </script></body></html>
            """;
    }
}
