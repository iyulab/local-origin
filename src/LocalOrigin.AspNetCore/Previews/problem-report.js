// Problem reporting, placed in front of a page's own markup: what stopped the page, in terms a person
// can be told. The host receives facts only: which kind of thing was blocked and from which host, or
// which error was thrown while loading, never anything the person typed.
//  - blocked: the content security policy refused something from another host. A script, style sheet,
//    font or module is a "library" the page needs; a form submission is a "form"; anything fetched or
//    displayed is "data". This also catches an inline module whose import comes from a CDN, which
//    raises no useful error of its own.
//  - load-error: the page's own code failed while loading.
//
// This file must stay ASCII: it is spliced into documents of any ASCII-compatible encoding.
(function () {
  "use strict";
  var boot = __LOCAL_ORIGIN_PROBLEMS__;
  var nativeFetch = window.fetch.bind(window);
  var headers = { "Content-Type": "application/json" };
  headers[boot.header] = "1";

  function report(kind, message, category, host) {
    nativeFetch(boot.endpoint, {
      method: "POST",
      credentials: "same-origin",
      keepalive: true,
      headers: headers,
      body: JSON.stringify({ tab: boot.tab, kind: kind, message: message, category: category, host: host })
    }).catch(function () { /* reporting is best-effort */ });
  }

  var reported = {};
  var reports = 0;
  function reportOnce(key, kind, message, category, host) {
    if (reported[key] || reports >= 20) return;
    reported[key] = true;
    reports++;
    report(kind, message, category, host);
  }

  var blockedUrls = {};
  document.addEventListener("securitypolicyviolation", function (event) {
    var directive = String(event.effectiveDirective || event.violatedDirective || "");
    var uri = String(event.blockedURI || "");
    if (!/^https?:/i.test(uri)) return; // inline, eval: allowed by policy, so not a loss
    blockedUrls[uri] = true;
    var host;
    try { host = new URL(uri).host; } catch (e) { return; }
    var category = /^(script|style|font|worker|manifest)-src/.test(directive) ? "library"
      : /^form-action/.test(directive) ? "form" : "data";
    reportOnce("blocked " + category + " " + host, "blocked", undefined, category, host);
  });

  var loading = true;
  function onLoadError(message) {
    if (!loading || !message) return;
    reportOnce("error " + message, "load-error", String(message).slice(0, 500));
  }
  // Capturing on window also sees resources that failed to load, which do not bubble.
  window.addEventListener("error", function (event) {
    var target = event.target;
    if (target && target !== window) {
      var url = target.src || target.href;
      if (!url) return;
      try { if (new URL(url, location.href).origin === location.origin) return; } catch (e) { return; }
      // The policy's violation event arrives after the element's error event; wait for it, so a
      // blocked library is reported once, as blocked.
      setTimeout(function () { if (!blockedUrls[url]) onLoadError("could not load " + url); }, 250);
      return;
    }
    if (!event.message) return;
    // Line numbers in the document count the injected markup too; report them as the page's file has them.
    var line = event.lineno && event.filename === location.href ? event.lineno - boot.lineOffset : event.lineno;
    onLoadError(event.message + (line > 0 ? " (line " + line + ")" : ""));
  }, true);
  window.addEventListener("unhandledrejection", function (event) {
    var reason = event.reason;
    onLoadError(reason && reason.message ? reason.message : String(reason));
  });
  window.addEventListener("load", function () { setTimeout(function () { loading = false; }, 1000); });
})();
