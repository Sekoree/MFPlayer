// Project-wide convenience imports.
// The concrete clock and error types live in their own namespaces
// (S.Media.Core.Clock, S.Media.Core.Errors) to match folder layout, but they
// are referenced throughout the core assembly — surfacing them globally keeps
// existing call sites ergonomic without a cascade of per-file `using` lines.
global using S.Media.Core.Clock;
global using S.Media.Core.Errors;

