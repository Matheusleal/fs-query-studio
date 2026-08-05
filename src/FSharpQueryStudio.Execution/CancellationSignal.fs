namespace FSharpQueryStudio.Execution

/// Cooperative cancellation for the currently running evaluation — see
/// Section 9. A script observes cancellation by calling `isCancelled ()`
/// directly, not via a host-injected value: `FsiEvaluationSession`
/// type-checks scripts against *reference* assemblies, which are a
/// different identity than the *runtime* assemblies a host-constructed
/// .NET object comes from, so binding a value across that boundary
/// (`AddBoundValue`) fails even for `bool`. `FsiSession.create` instead
/// adds `--reference:` to this assembly's own already-loaded location, so
/// a script's call resolves entirely within FSI's own compilation — no
/// value ever crosses the boundary.
///
/// A single flag is enough: a worker process runs at most one evaluation
/// at a time (`Handlers.handleEvaluate` guards against a second
/// concurrent one).
module CancellationSignal =

    let mutable private cancelled = false

    let isCancelled () = cancelled

    let requestCancel () = cancelled <- true

    let reset () = cancelled <- false
