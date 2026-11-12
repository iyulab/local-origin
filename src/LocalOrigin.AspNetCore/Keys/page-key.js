(function () {
  "use strict";
  // Takes the page key from the address's fragment (never sent to the server), keeps it for this tab, removes it from the
  // address, and adds it in the key header to this page's same-origin requests that carry the header as "1" (which stays).
  var param = "__LOCAL_ORIGIN_KEY_PARAM__";
  var header = "__LOCAL_ORIGIN_KEY_HEADER__";
  var lower = header.toLowerCase();
  var store = "local-origin-key";
  var key = null;
  try { key = window.sessionStorage.getItem(store); } catch (e) { key = null; }
  var hash = window.location.hash;
  if (hash.length > 1) {
    var parts = hash.slice(1).split("&");
    var kept = [];
    for (var i = 0; i < parts.length; i++) {
      if (parts[i].indexOf(param + "=") === 0) key = parts[i].slice(param.length + 1);
      else kept.push(parts[i]);
    }
    if (kept.length !== parts.length) {
      try { window.sessionStorage.setItem(store, key); } catch (e) { /* the key lasts for this document only */ }
      var rest = kept.join("&");
      try {
        window.history.replaceState(window.history.state, "",
          window.location.pathname + window.location.search + (rest ? "#" + rest : ""));
      } catch (e) { /* the address keeps the fragment */ }
    }
  }
  if (!key) return;

  var keyHeader = "__LOCAL_ORIGIN_KEY_HEADER__-Key";

  function sameOrigin(url) {
    try { return new URL(url, window.location.href).origin === window.location.origin; } catch (e) { return false; }
  }

  // The headers a fetch would send: init's when it gives any, else the Request's own. Nothing is built from the input, so
  // a request this script leaves alone - and its body - reaches the network exactly as the page made it.
  function headersOf(input, init) {
    if (init && init.headers !== undefined) return new Headers(init.headers);
    if (typeof Request === "function" && input instanceof Request) return new Headers(input.headers);
    return new Headers();
  }

  var nativeFetch = window.fetch;
  if (typeof nativeFetch === "function") {
    window.fetch = function (input, init) {
      var headers;
      var url;
      try {
        headers = headersOf(input, init);
        url = typeof Request === "function" && input instanceof Request ? input.url : String(input);
      } catch (e) { return nativeFetch.apply(this, arguments); }
      if (!sameOrigin(url) || headers.get(header) !== "1" || headers.has(keyHeader)) return nativeFetch.apply(this, arguments);
      headers.set(keyHeader, key);
      var keyed = {};
      if (init) for (var name in init) keyed[name] = init[name];
      keyed.headers = headers;
      return nativeFetch.call(this, input, keyed);
    };
  }

  var xhr = window.XMLHttpRequest && window.XMLHttpRequest.prototype;
  if (xhr) {
    var open = xhr.open;
    var setHeader = xhr.setRequestHeader;
    xhr.open = function (method, url) {
      this.__localOriginSameOrigin = sameOrigin(url);
      return open.apply(this, arguments);
    };
    xhr.setRequestHeader = function (name, value) {
      var result = setHeader.apply(this, arguments);
      if (this.__localOriginSameOrigin && String(name).toLowerCase() === lower && String(value) === "1") setHeader.call(this, keyHeader, key);
      return result;
    };
  }
})();
