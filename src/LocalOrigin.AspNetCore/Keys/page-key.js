(function () {
  "use strict";
  // Takes the page key from the address's fragment (never sent to the server), keeps it for this tab, removes it from the
  // address, and sends it as the request header's value on this page's same-origin requests that carry the header as "1".
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

  function sameOrigin(url) {
    try { return new URL(url, window.location.href).origin === window.location.origin; } catch (e) { return false; }
  }

  var nativeFetch = window.fetch;
  if (typeof nativeFetch === "function") {
    window.fetch = function (input, init) {
      var request;
      try { request = new Request(input, init); } catch (e) { return nativeFetch.apply(this, arguments); }
      if (!sameOrigin(request.url) || request.headers.get(header) !== "1") return nativeFetch.call(this, request);
      var headers = new Headers(request.headers);
      headers.set(header, key);
      return nativeFetch.call(this, new Request(request, { headers: headers }));
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
      if (this.__localOriginSameOrigin && String(name).toLowerCase() === lower && String(value) === "1") value = key;
      return setHeader.call(this, name, value);
    };
  }
})();
