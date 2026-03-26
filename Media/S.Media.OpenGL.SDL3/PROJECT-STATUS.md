# S.Media.OpenGL.SDL3 Project Status

Last updated: 2026-03-26

## Scope

- `Media/S.Media.OpenGL.SDL3/S.Media.OpenGL.SDL3.csproj`
- exercised by `Media/S.Media.OpenGL.Tests/S.Media.OpenGL.Tests.csproj`

## Current Stage

- Module: In Progress
- Validation: Through `S.Media.OpenGL.Tests`

## Implemented Highlights

- SDL3 embed/view path and adapter flow are implemented.
- `SDL3ShaderPipeline` implements full GL rendering for embedded use-case: shader compilation, VAO/VBO/texture management, upload (RGBA/BGRA + all YUV formats including 10-bit), and draw — matching the standalone rendering path.
- All 11 `VideoPixelFormat` values supported (RGBA32, BGRA32, NV12, YUV420P/422P/444P, P010LE, YUV420P10LE/422P10LE/444P10LE).
- Clone behavior delegates to `S.Media.OpenGL` engine policy.
- Embed lifecycle error contracts are implemented and tested.
- Standalone SDL windows now honor configurable `WindowFlags` and title options.
- Standalone preview can explicitly show/raise via `ShowAndBringToFront()`.

## Current Considerations

- Keep embed lifecycle ordering deterministic across parent-loss and teardown paths.
- Maintain strict adapter boundary with no backend-policy drift.
- Continue parity checks against migration expectations from legacy SDL3 pathing.

## Related Docs

- `Media/S.Media.OpenGL.SDL3/API-outline.md`
- `Media/S.Media.OpenGL/PROJECT-STATUS.md`
- `Doc/refactor-considerations-log.md`
