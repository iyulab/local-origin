# Changelog

All notable changes to the LocalOrigin packages are listed here. Versions follow
[Semantic Versioning](https://semver.org/); before 1.0 a minor version may change behavior.

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
