# Core TDD evidence

2026-10-26, Windows x64, .NET SDK 10.0.302.

- RED: `dotnet test tests/CrewTerm.Core.Tests --no-restore --filter 'FullyQualifiedName~ProjectCatalogTests|FullyQualifiedName~NavigationTests'` exited 1. ProjectCatalog, WorkspaceNavigation and FileReferenceParser did not exist. Compiler diagnostics confirmed the missing production APIs.
- GREEN: `dotnet test tests/CrewTerm.Core.Tests --no-restore` exited 0; 9 passed, 0 failed, 0 skipped.
- Covered real disk persistence and duplicate normalization; preservation of corrupt settings; removal without deleting source directories; unsaved-buffer navigation protection; compiler path/line parsing.
- The initial RED is a missing-new-API compile failure. Additional defects must be demonstrated by runnable behavioral regression tests before fixing.
