/**
 * The one file another site adds to put the chat bubble on their page.
 *
 *   <script src="https://your-host/chat/embed.js" async></script>
 *
 * Everything else is served from this host, so the embedding site takes no dependency beyond the
 * one tag and can drop it again by deleting the tag.
 *
 * The widget runs inside an iframe rather than in the host page's DOM. That is the whole design:
 * their CSS cannot reach into it and break the chat, ours cannot leak out and break their page, and
 * the turn requests are same-origin with the frame — so the API needs no CORS at all.
 *
 * An iframe cannot size itself, so the page inside posts what it needs and this applies it.
 */
(function () {
  "use strict";

  // The origin this script was served from is the origin the widget is loaded from, so a host page
  // never has to configure a URL and can never point it somewhere unintended.
  var self = document.currentScript;
  if (!self) return;

  var origin = new URL(self.src, window.location.href).origin;
  var src = origin + "/chat/widget.html";

  if (document.querySelector('iframe[data-agentcore-widget]')) return;

  var frame = document.createElement("iframe");
  frame.src = src;
  frame.title = "Chat";
  frame.setAttribute("data-agentcore-widget", "");
  frame.setAttribute("allow", "clipboard-write");

  frame.style.cssText = [
    "position:fixed",
    "bottom:0",
    "right:0",
    "width:96px",
    "height:96px",
    "border:0",
    "background:transparent",
    // Below a modal but above ordinary page content.
    "z-index:2147483000",
    "color-scheme:normal",
    // The frame is only as large as the widget currently needs, but it is still a rectangle over
    // the host page. A transition keeps the growth from looking like a glitch.
    "transition:width .18s ease,height .18s ease",
  ].join(";");

  window.addEventListener("message", function (event) {
    // Two checks, both required. The origin check rejects messages from any other frame on the
    // page; the source tag rejects unrelated messages from our own origin.
    if (event.origin !== origin) return;

    var data = event.data;
    if (!data || data.source !== "agentcore-widget" || data.type !== "resize") return;

    var width = Number(data.width);
    var height = Number(data.height);
    if (!isFinite(width) || !isFinite(height)) return;

    // Never taller or wider than the window it sits in, or the widget runs off a phone screen with
    // no way to scroll it back.
    frame.style.width = Math.min(width, window.innerWidth - 8) + "px";
    frame.style.height = Math.min(height, window.innerHeight - 8) + "px";
  });

  function mount() {
    document.body.appendChild(frame);
  }

  if (document.body) mount();
  else document.addEventListener("DOMContentLoaded", mount);
})();
