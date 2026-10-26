# Core TDD evidence

2026-10-26, Windows x64, .NET SDK 10.0.302.

- RED: `dotnet test tests/CrewTerm.Core.Tests --no-restore --filter 'FullyQualifiedName~ProjectCatalogTests|FullyQualifiedName~NavigationTests'` exited 1. ProjectCatalog, WorkspaceNavigation and FileReferenceParser did not exist. Compiler diagnostics confirmed the missing production APIs.
- GREEN: `dotnet test tests/CrewTerm.Core.Tests --no-restore` exited 0; 9 passed, 0 failed, 0 skipped.
- Covered real disk persistence and duplicate normalization; preservation of corrupt settings; removal without deleting source directories; unsaved-buffer navigation protection; compiler path/line parsing.
- The initial RED is a missing-new-API compile failure. Additional defects must be demonstrated by runnable behavioral regression tests before fixing.

## Persistence and settings regressions

- RED: `dotnet test tests/CrewTerm.Core.Tests --no-restore --filter FullyQualifiedName~Concurrent` failed two behavioral assertions: concurrent project and template saves silently overwrote the first writer instead of reporting stale state.
- GREEN: fingerprint checks plus a named cross-process mutex protect optimistic saves; `dotnet test tests/CrewTerm.Core.Tests --no-restore` passed all 16 tests.
- Additional tested behavior: persisted layout/templates/concurrency bounds, selected-line code context and invalid ranges.
- Final verification: `dotnet test CrewTerm.slnx -c Release --no-restore --logger 'trx;LogFilePrefix=crewterm-final'`, Core 16/16, no skips.
