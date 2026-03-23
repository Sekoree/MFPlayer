# OSCLib

`OSCLib` is a UDP-focused Open Sound Control library with full OSC 1.0 compatibility and OSC 1.1-oriented features:

- Required tags: `i f s b T F N I t`
- Recommended/legacy optional tags: `h d S c r m [ ]`
- Address matching: `* ? [] {} //`
- Configurable decoding strictness and unknown-tag handling
- Configurable oversize packet policy (safe default drop-and-log)

## Defaults and behavior

- `OSCDecodeOptions.StrictMode`: `true` (unknown tags are rejected)
- `OSCDecodeOptions.AllowMissingTypeTagString`: `true` (compat mode for older senders)
- `OSCServerOptions.MaxPacketBytes`: `8192`
- `OSCServerOptions.OversizePolicy`: `DropAndLog`
- `OSCServerOptions.OversizeLogInterval`: `00:00:05`
- `OSCServerOptions.EnableTraceHexDump`: `false`

### Unknown tags

- In strict mode (default), unknown tags are rejected.
- In non-strict mode, set `UnknownTagByteLengthResolver` to consume unknown payload bytes.
- Set `PreserveUnknownArguments = true` to expose unknown values as `OSCUnknownArgument`.

### Address matching

- Supported operators: `*`, `?`, `[]`, `{}`, `//`.
- `//` is a multi-level wildcard that can match zero or more path segments.

### Oversize packets

- `DropAndLog`: packet is dropped and warnings are throttled by `OversizeLogInterval`.
- `Throw`: receive loop faults/stops when an oversize datagram arrives.

## Projects

- `OSC/OSCLib`: core library
- `OSC/OSCLib.Tests`: xUnit coverage for codec/matcher/UDP loopback
- `OSC/OSCLib.Smoke`: simple send/listen runner

## Quick try

```bash
dotnet test /home/seko/RiderProjects/MFPlayer/OSC/OSCLib.Tests/OSCLib.Tests.csproj

dotnet run --project /home/seko/RiderProjects/MFPlayer/OSC/OSCLib.Smoke/OSCLib.Smoke.csproj -- listen 9000

dotnet run --project /home/seko/RiderProjects/MFPlayer/OSC/OSCLib.Smoke/OSCLib.Smoke.csproj -- send 127.0.0.1 9000 /demo/ping 42
```

