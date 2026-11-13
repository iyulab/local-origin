# Changelog

All notable changes to the LocalOrigin packages are listed here. Versions follow
[Semantic Versioning](https://semver.org/); before 1.0 a minor version may change behavior.

## Unreleased

## 0.6.0

### Added
- `Keys.PageKeys`: page keys — what lets a write prove it comes from a page the host opened, not from any program that can
  reach the port (a loopback port answers every account on the computer, and everything a page is served can be fetched by
  any client). A scope's key is derived from the host's secret (HMAC-SHA256), so it survives a restart while the secret is
  kept. `Address(url, scope)` puts the key in the address's fragment, which a browser never sends; `Script(header)`, placed
  in front of the page before its own code, takes the key from there, keeps it for the tab, removes it from the address
  bar, and adds it in a header of its own (`KeyHeaderOf(header)`, `<header>-Key`) to the page's same-origin `fetch` and
  `XMLHttpRequest` requests that carry the header with the value `1` — the `1` stays, so a page written for the plain header
  saves unchanged and every check of the plain header (problem reports, a preview's discard) keeps working in a keyed tab.
  A request the script does not change reaches the network as the page made it. Requests from a `Worker` and
  `navigator.sendBeacon` carry no key.
- `FileChannelOptions.Keys` and `StorageChannelOptions.Keys`: with keys, a write needs the plain header and the scope's key in
  the key header; without, the plain header is taken as before. `StorageChannel.IsFromThePage(request, scope)` checks the
  key; the scope-less `IsFromThePage(request)` throws on a channel with keys (it cannot check one); a preview's
  `DiscardAsync` still takes the plain header. The storage channel's script sends the tab's key itself, so its writes do not
  depend on the order of the scripts in front of the page.

## 0.5.0

### Added
- `StoreRecoveryEvent.LostOperations`: how many operations a repair could not bring back. A stop
  at an unreadable journal line counts the operations left unapplied (from the sequence numbers,
  so unreadable lines further on are included); a sequence gap reports its size; an incomplete
  final line reports 0. Replay still stops at the unreadable line instead of skipping it.

### Changed
- `ScopeFolder.Resolve` refuses a path segment shaped like a Windows short (8.3) name
  (`PROGRA~1`, `REPORT~1.DOC`). The file system resolves such an alias to a longer name on open,
  so a rule a host applies to the name as written would not see it. Names with a space, a longer
  base or extension, no digits after the tilde, or more than one dot are still served.
- `FileChannel` no longer creates a folder when `CreateFolders` is off. A folder removed while a
  slow request body was in flight is answered with 409, the same as the pre-check, instead of
  being recreated by the write.

### Build
- Packages are published by the organization's central NuGet publisher; the repository holds no
  NuGet key.
