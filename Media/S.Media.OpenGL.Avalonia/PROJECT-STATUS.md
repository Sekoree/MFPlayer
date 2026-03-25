# S.Media.OpenGL.Avalonia Project Status

Last updated: 2026-03-25

## Scope

- `Media/S.Media.OpenGL.Avalonia/S.Media.OpenGL.Avalonia.csproj`
- exercised by `Media/S.Media.OpenGL.Tests/S.Media.OpenGL.Tests.csproj`

## Current Stage

- Module: In Progress
- Validation: Through `S.Media.OpenGL.Tests`

## Implemented Highlights

- Avalonia host control and adapter integration are implemented.
- Clone behaviors delegate to `S.Media.OpenGL` engine policy rather than local policy duplication.
- HUD and diagnostics projection paths are present.

## Current Considerations

- Keep adapter-only boundaries (no decode/session ownership in adapter layer).
- Maintain contract parity with core OpenGL engine and shared error semantics.
- Continue lifecycle behavior checks for clone-parent transitions.

## Related Docs

- `Media/S.Media.OpenGL.Avalonia/API-outline.md`
- `Media/S.Media.OpenGL/PROJECT-STATUS.md`
- `Doc/refactor-considerations-log.md`
