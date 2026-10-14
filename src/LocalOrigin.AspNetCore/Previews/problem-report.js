// Problem reporting, placed in front of a page's own markup: what stopped the page, in terms a person
// can be told. The host receives facts only: which kind of thing was blocked and from which host, or
// which error was thrown while loading, never anything the person typed.
//  - blocked: the content security policy refused something from another host. A script, style sheet,
//    font or module is a "library" the page needs; a form submission is a "form"; anything fetched or
//    displayed is "data". This also catches an inline module whose import comes from a CDN, which
//    raises no useful error of its own.
//  - load-error: the page's own code failed while loading.
//  - error: the page's own code failed after it loaded (an uncaught error or rejection, say on a click),
//    or the page reported an error itself with console.error, at any time.
// An error also says where it came from (source: uncaught, rejection, console, or resource for a file
// from another host that could not be loaded) and, when the browser gives it, where in the code: line
// and column in the page's own file, or in another file of the page's origin named by its path.
//
// This file must stay ASCII: it is spliced into documents of any ASCII-compatible encoding.
(function () {
  "use strict";
  var boot = __LOCAL_ORIGIN_PROBLEMS__;
  var nativeFetch = window.fetch.bind(window);
  var headers = { "Content-Type": "application/json" };
  headers[boot.header] = "1";

  function report(fields) {
    fields.tab = boot.tab;
    nativeFetch(boot.endpoint, {
      method: "POST",
      credentials: "same-origin",
      keepalive: true,
      headers: headers,
      body: JSON.stringify(fields)
    }).catch(function () { /* reporting is best-effort */ });
  }

  var reported = {};
  var reports = 0;
  function reportOnce(key, fields) {
    if (reported[key] || reports >= 20) return;
    reported[key] = true;
    reports++;
    report(fields);
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
    reportOnce("blocked " + category + " " + host, { kind: "blocked", category: category, host: host });
  });

  var loading = true;
  // An error thrown while the page loads stopped it from starting; one thrown later broke one thing it does.
  function onError(message, source, where) {
    if (!message) return;
    var fields = { kind: loading ? "load-error" : "error", message: String(message).slice(0, 500), source: source };
    if (where && where.line > 0) {
      fields.line = where.line;
      if (where.column > 0) fields.column = where.column;
      if (where.file) fields.file = where.file;
    }
    reportOnce("error " + message, fields);
  }
  function onLoadError(message) {
    if (loading) onError(message, "resource");
  }
  // Where a script error is, in terms of the page: its own file (the document, whatever its fragment),
  // with lines counted as that file has them; another file of the page's origin, by path; nothing for
  // a file from another host, whose positions would read as the page's.
  var page = location.href.split("#")[0];
  function position(filename, line, column) {
    if (!filename) return null;
    if (filename.split("#")[0] === page) return { line: line - boot.lineOffset, column: column };
    try {
      var url = new URL(filename, location.href);
      if (url.origin === location.origin) return { line: line, column: column, file: url.pathname };
    } catch (e) { /* not a URL: no position */ }
    return null;
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
    var at = event.lineno ? position(event.filename, event.lineno, event.colno) : null;
    var line = at ? at.line : event.lineno;
    onError(event.message + (line > 0 ? " (line " + line + ")" : ""), "uncaught", at);
  }, true);
  window.addEventListener("unhandledrejection", function (event) {
    var reason = event.reason;
    onError(reason && reason.message ? reason.message : String(reason), "rejection");
  });

  // What the page itself reports as an error. The console still gets it as before.
  function text(value) {
    if (value instanceof Error) return value.message ? value.name + ": " + value.message : value.name;
    if (typeof value === "string") return value;
    try { return JSON.stringify(value); } catch (e) { return String(value); }
  }
  var nativeError = console.error;
  console.error = function () {
    try {
      var parts = [];
      for (var i = 0; i < arguments.length; i++) parts.push(text(arguments[i]));
      var message = parts.join(" ");
      if (message) reportOnce("error " + message, { kind: "error", message: message.slice(0, 500), source: "console" });
    } catch (e) { /* reporting is best-effort */ }
    return nativeError.apply(console, arguments);
  };
  window.addEventListener("load", function () { setTimeout(function () { loading = false; }, 1000); });
})();
