# PALib

PALib is a low-level PortAudio interop layer using `LibraryImport` only.

## Highlights

- Native symbol names are preserved (`Pa_*`, `PaWasapi_*`, etc.).
- Core and platform extension APIs are split by namespace:
  - `PALib` (core)
  - `PALib.ALSA`
  - `PALib.ASIO`
  - `PALib.CoreAudio`
  - `PALib.DirectSound`
  - `PALib.JACK`
  - `PALib.WASAPI`
  - `PALib.WDMKS`
  - `PALib.WMME`
- Trace-level call logging is available per namespace category.
- Ultra-hot calls (`Pa_ReadStream`, `Pa_WriteStream`) are excluded from trace logging.
- Raw error codes are preserved, with helper text in `PALib.Errors.PaErrorHelpers`.
- Platform extensions return `paIncompatibleStreamHostApi` on unsupported OSes instead of throwing entry-point errors.

## Native Loading

`PALib` imports `portaudio` by default.

If your runtime requires custom probing, configure your own resolver at app startup:

```csharp
NativeLibrary.SetDllImportResolver(typeof(PALib.Native).Assembly, (name, asm, path) =>
{
    // custom probing
    return IntPtr.Zero;
});
```

Optional helper `PALib.Runtime.PortAudioLibraryResolver.Install()` adds fallback candidates:

- Linux: `portaudio`, `libportaudio.so.2`
- macOS: `portaudio`, `libportaudio.dylib`
- Windows: `portaudio`, `portaudio.dll`

## Logging

Configure logging once:

```csharp
PALib.Runtime.PALibLogging.Configure(loggerFactory);
```

Categories:

- `PALib.Core`
- `PALib.ALSA`
- `PALib.ASIO`
- `PALib.CoreAudio`
- `PALib.DirectSound`
- `PALib.JACK`
- `PALib.WASAPI`
- `PALib.WDMKS`
- `PALib.WMME`

Trace logs include argument metadata for pointers and buffers.
