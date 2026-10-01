# local-origin

> Give a local web page its own origin — serve it, keep what it saves, and try it out before it is trusted.

**Status: first code in place, no release yet (`0.1.0` not published).** This document fixes purpose,
scope and the principles the implementation must honor. The public surface is not settled; treat
everything below as subject to change until a first `0.x` package is published.

---

## Why

Desktop applications increasingly hold web pages that nobody deployed: a page a model just wrote, an
HTML file a person dropped in, a small tool saved next to the data it works on. These pages need to
*run* — and the moment they run, three questions have to be answered by the application that holds
them:

1. **Where does the page live?** Opened from `file://`, a page gets an opaque or shared origin: its
   storage collides with every other local file, `fetch` of its own data fails, and nothing isolates it
   from the next page. Served from the application's own HTTP endpoint, it becomes same-origin with that
   application's API. The browser isolates at scheme + host + port and nowhere else, so a page needs an
   origin of its own.
2. **Where does what it saves go?** A page's writes — `localStorage`, a file it puts back — have to
   survive a crash, a reload and an update of the page itself, and they have to land somewhere the
   application can find, back up and move.
3. **Does it work before anyone relies on it?** A page that has just changed should be opened once
   against a copy of the real state, with its writes kept away from the real data and its errors
   collected, before it replaces the version people use.

Every application that holds local pages ends up writing this layer, and the details — port stability,
header sets, durable write paths, cross-site refusal — are exactly where a hand-rolled version goes
wrong. local-origin is that layer, once.

## What local-origin is

- **An origin allocator.** Each page (or group of pages the host treats as one scope) gets a stable
  origin. Two strategies, chosen by the host:
  - *subdomain per scope on one port* — `http://<id>.localhost:<port>/`, cheap and numerous, for hosts
    whose browser engine resolves `*.localhost` itself;
  - *port per scope* — `http://127.0.0.1:<port>/`, for hosts that must be reachable from any client or
    resolver, or later from another machine.

  An origin is remembered across restarts, because a page's stored data is keyed by its origin: an
  origin that moves silently loses the page's data.
- **A static server with a security profile.** Serves a scope's files with a header set decided once:
  a Content Security Policy chosen by the host, `Cross-Origin-Resource-Policy: same-origin`, refusal of
  cross-site requests (`Sec-Fetch-Site`), `frame-ancestors`, and no referrer. Binds to loopback unless
  the host deliberately opens it.
- **A durable write sink.** One store under each scope, written through a journal and atomic file
  replacement, so an acknowledged write is on disk and a crash never leaves a half-written file. Pages
  reach it through one or both of two write channels, enabled per scope:
  - *file channel* — the page `PUT`s a file to its own address, confined to its own scope;
  - *storage channel* — an opt-in script mirrors `localStorage` to the store, for pages that were never
    written for any host and only know `localStorage`.
- **Preview origins.** A throwaway origin for a candidate version of a page: it reads the current data,
  its writes are acknowledged and discarded, and its load errors and policy violations are collected
  into a report the host reads afterwards. How the preview is driven — a headless browser, an off-screen
  view — is the host's choice; local-origin supplies the origin and the report.

## What local-origin is not

- **Not a sandbox for untrusted code inside a host page.** Pages run as ordinary pages on their own
  origin. Rendering generated UI inside the host's own window, behind a capability bridge, is
  [vivarium](https://github.com/iyulab/vivarium)'s job.
- **Not a change lifecycle.** Proposing, approving, applying and rolling back versions of an application
  belongs to the host, or to [vivarium-stage](https://github.com/iyulab/vivarium-stage). A preview
  origin is a place such a lifecycle can run its simulation; it decides nothing.
- **Not an authoring tool.** It never calls a model and never edits a page.
- **Not a web framework.** It hosts pages it is pointed at; routing, templating and application logic
  stay with the page or the host.

## Fixed principles

These are the anchors. An implementation that violates one of them is not local-origin.

1. **The page is not modified on disk.** Whatever a host chooses to inject is added to the served copy
   only; the stored bytes are the page's own.
2. **One origin, one scope.** No route of the host application and no other scope's files are reachable
   from a page's origin.
3. **An acknowledged write is durable.** A write is acknowledged only after it is on disk; a write
   resent after a lost acknowledgement is applied once.
4. **Data outlives the page.** Replacing or reverting a page's code never discards the data it wrote;
   data written by a version that is left behind is kept aside, not merged and not deleted. A page still
   running replaced code cannot write into the data the new code owns: its late writes are refused and
   handed to the host, never silently dropped.
5. **Loopback by default.** Reaching an origin from beyond the machine is an explicit host decision,
   never a default.
6. **No knowledge of its consumers.** Nothing here names or special-cases an application that uses it.
   A need that only one consumer has stays in that consumer.

## Packages

| Package | Contents |
|---|---|
| `LocalOrigin` | Core with no dependencies: origin allocation and persistence, the durable store, the preview report model. |
| `LocalOrigin.AspNetCore` | Hosting on ASP.NET Core: the security header profile, document injection, the write channels, preview origins. |

Target: .NET 10. Nullable reference types and warnings-as-errors are on.

### What exists so far

`LocalOrigin`

- `Origins` — `IOriginStrategy` (a scope's origin; the scope a request names, from its host name and local
  port), `SubdomainOrigins`, `PortOrigins` (`Bind`/`Unbind` a scope to the port its listener got),
  `ScopeName` (DNS-label names), `IPortMemory` / `PortMemoryFile`, `RememberedPort.StartAsync` (start on the
  remembered port, retry briefly, otherwise move and report the move).
- `Storage` — `KeyValueStore` (journal flushed before acknowledgement, atomic snapshots, previous snapshots
  kept, unreadable files set aside and reported; snapshot format identifier is a host option),
  `DurableFile` (atomic replace, shared reads, set aside).
- `Previews` — `PreviewOrigins<T>` (throwaway scope names, lifetime, bound), `PreviewReport`.
- `Files` — `ScopeFolder` (a scope's folder and the one way a request path becomes a file in it: dot segments,
  backslashes, colons, trailing dots and spaces, device names and links on the way all name nothing).

`LocalOrigin.AspNetCore`

- `OriginSecurityProfile` — the header set of every response, and refusal of requests other sites (sibling
  origins included) make, navigation excepted.
- `DocumentInjector` — host markup after the doctype of the served copy; stored bytes and declared charset kept.
- `Storage.StorageChannel` / `ChannelSessions` — the storage channel: the opt-in script, sessions and tabs,
  writes applied once, writes from revoked pages refused and reported, writes discarded for previews.
  Wire names and the global the host reads before closing a page are options.
- `Previews.ProblemReports` — a script that reports load errors and refused requests, and the endpoint that
  takes them in.
- `Files.FileChannel` — the file channel: `PUT` to the page's own address, confined to its folder, limited to the
  paths the host allows, atomic, size-capped; writes discarded for previews; the host told after each write.
- `OriginRequests.ScopeOf(HttpRequest)`.

Not yet: a listener per scope for `PortOrigins`, static serving of a scope's folder.

## Deliberately undecided

- How scopes are registered and looked up — today the host resolves a scope and hands the library its
  store and documents; whether the library should own a registry is open.
- The on-disk layout of a scope's store, and how the file channel and the key-value store share it.
- Whether the storage-channel script also ships as an npm package.
- Authentication for an origin opened beyond loopback.

## License

MIT — see [LICENSE](LICENSE).
