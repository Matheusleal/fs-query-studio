namespace FSharpQueryStudio.Logging

open System
open Serilog.Context

/// Attaches a request's protocol correlation ID (Section 7) to every log
/// event emitted while the returned scope is alive, via Serilog's
/// ambient LogContext. Callers never reference the property name directly.
module Correlation =

    [<Literal>]
    let CorrelationIdPropertyName = "CorrelationId"

    let pushCorrelationId (correlationId: string) : IDisposable =
        LogContext.PushProperty(CorrelationIdPropertyName, correlationId)
