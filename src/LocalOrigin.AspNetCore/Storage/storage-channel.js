// The storage channel's script, placed in front of a page's own markup. It replaces window.localStorage
// with a synchronous in-memory map seeded from the scope's store, and forwards every change to the
// host, which journals it durably. The page's own code is not modified.
//
// Delivery: at most one request is in flight. Every operation carries a per-tab sequence number and
// stays "unacknowledged" until the host confirms it; the host ignores numbers it has already applied,
// so resending is always safe. When the page is being hidden or unloaded, every unacknowledged
// operation is resent with keepalive, so a request still in flight cannot be overtaken by a later one.
//
// This file must stay ASCII: it is spliced into documents of any ASCII-compatible encoding.
(function () {
  "use strict";
  var boot = __LOCAL_ORIGIN_BOOT__;
  var data = new Map(Object.keys(boot.items).map(function (k) { return [k, boot.items[k]]; }));
  var seq = 0;
  var unacked = [];   // sent, awaiting acknowledgement, in sequence order
  var queue = [];     // recorded, not yet sent
  var inflight = false;
  var timer = 0;
  var retryDelay = 250;
  // The page may replace fetch; the channel's own traffic uses the original.
  var nativeFetch = window.fetch.bind(window);
  var headers = { "Content-Type": "application/json" };
  headers[boot.header] = "1";

  function send(body, keepalive) {
    return nativeFetch(boot.endpoint, {
      method: "POST",
      credentials: "same-origin",
      keepalive: keepalive,
      headers: headers,
      body: JSON.stringify(body)
    });
  }

  function post(ops, keepalive) {
    return send({ tab: boot.tab, ops: ops, issued: seq }, keepalive).then(function (response) {
      if (!response.ok) throw new Error("storage request failed: " + response.status);
      return response.json();
    }).then(function (result) {
      unacked = unacked.filter(function (op) { return op.seq > result.ack; });
      return result;
    });
  }

  // Sends everything not yet acknowledged, one request at a time. On failure the operations stay
  // unacknowledged and are sent again after a growing delay.
  function pump() {
    timer = 0;
    if (inflight) return;
    var ops = unacked.concat(queue);
    if (ops.length === 0) return;
    unacked = ops;
    queue = [];
    inflight = true;
    var failed = false;
    post(ops.slice(), false).then(function () {
      retryDelay = 250;
    }, function () {
      failed = true;
      retryDelay = Math.min(retryDelay * 2, 5000);
    }).then(function () {
      inflight = false;
      if (unacked.length > 0 || queue.length > 0) schedule(failed ? retryDelay : 0);
    });
  }

  function schedule(delay) {
    if (!timer) timer = setTimeout(pump, delay);
  }

  // Once the page starts leaving, a timer may never run: the page's own pagehide/unload writes come
  // after this script's handler (registered first) and are sent at once instead.
  var leaving = false;
  window.addEventListener("pageshow", function () { leaving = false; });

  function flushOnLeave() {
    leaving = true;
    var ops = unacked.concat(queue);
    if (ops.length === 0) return;
    unacked = ops;
    queue = [];
    // Browsers cap keepalive bodies at 64 KiB. A larger final batch goes as a normal request, which
    // survives a closing window but not an ending process; the host's shutdown ordering is what
    // covers that case.
    var keepalive = JSON.stringify(ops).length < 60000;
    post(ops.slice(), keepalive).catch(function () { /* nothing left to retry with */ });
  }

  function record(op) {
    op.seq = ++seq;
    queue.push(op);
    if (leaving) {
      // Sent now, with keepalive, so it leaves before the document does, together with everything not
      // yet acknowledged: the host skips sequences it has passed, so a batch that arrived ahead of an
      // earlier one would otherwise make the earlier one's operations count as old.
      flushOnLeave();
      return;
    }
    schedule(0);
  }

  var storage = {
    getItem: function (key) { key = String(key); return data.has(key) ? data.get(key) : null; },
    setItem: function (key, value) {
      key = String(key); value = String(value);
      data.set(key, value);
      record({ op: "set", key: key, value: value });
    },
    removeItem: function (key) {
      key = String(key);
      if (!data.has(key)) return;
      data.delete(key);
      record({ op: "remove", key: key });
    },
    clear: function () {
      if (data.size === 0) return;
      data.clear();
      record({ op: "clear" });
    },
    key: function (index) {
      var keys = Array.from(data.keys());
      return index >= 0 && index < keys.length ? keys[index] : null;
    }
  };
  // configurable: a Proxy must report every non-configurable own property from ownKeys.
  Object.defineProperty(storage, "length", { get: function () { return data.size; }, configurable: true });

  // Property-style access (localStorage.foo = "bar", localStorage.foo, delete localStorage.foo,
  // Object.keys(localStorage)) behaves like the Web Storage API's named properties. Reads go through
  // the methods above, so a host layer that wraps them sees every read.
  var proxy = new Proxy(storage, {
    get: function (target, name) {
      if (name in target) return target[name];
      if (typeof name !== "string") return undefined;
      if (!data.has(name)) return null;
      return target.getItem(name);
    },
    set: function (target, name, value) { target.setItem(name, value); return true; },
    has: function (target, name) { return name in target || data.has(String(name)); },
    deleteProperty: function (target, name) { target.removeItem(name); return true; },
    ownKeys: function () { return Array.from(data.keys()); },
    getOwnPropertyDescriptor: function (target, name) {
      return data.has(String(name)) ? { value: data.get(String(name)), enumerable: true, configurable: true, writable: true } : undefined;
    }
  });

  Object.defineProperty(window, "localStorage", { value: proxy, configurable: true, enumerable: true });

  // The last sequence this page issued, sent once the page has finished leaving. Every request already
  // carries the page's current sequence, but a write made while leaving may still be in transit (or
  // lost) when the host checks; this report is what lets the host know a write it has not received
  // exists, so it can tell "all applied" from "something never arrived".
  var reporting = false;
  function reportIssued() {
    send({ tab: boot.tab, ops: [], issued: seq, left: true }, true)
      .catch(function () { /* the host counts a report that never arrives as unconfirmed */ });
  }

  // What the host reads just before it closes this page: which tab this is and the last write sequence
  // the page issued. The host then waits until that sequence is applied. arm() is called by the host at
  // the same moment: the listener it adds is registered after every listener the page's own code added
  // while it ran, so it runs after the page's own pagehide writes and reports the sequence they reached.
  // Neither writable nor configurable, so the page's own code cannot change what the host reads.
  Object.defineProperty(window, boot.handle, {
    value: Object.freeze({
      tab: boot.tab,
      issued: function () { return seq; },
      arm: function () {
        if (reporting) return;
        reporting = true;
        window.addEventListener("pagehide", reportIssued);
      }
    }),
    enumerable: false, writable: false, configurable: false
  });

  window.addEventListener("pagehide", flushOnLeave);
  document.addEventListener("visibilitychange", function () {
    if (document.visibilityState === "hidden") flushOnLeave();
  });
})();
