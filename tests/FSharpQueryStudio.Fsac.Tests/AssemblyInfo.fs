module AssemblyInfo

open Xunit

// `FsacBridge.sharedProcess` is one shared static `FsacProcess` instance
// for this whole test assembly (mirroring the "one long-lived instance"
// shape `FsacBridge.fs`'s own doc comment describes for the real Worker
// process). `FsacBridgeCheckTests`'s "recovers after respawn" test calls
// `FsacBridge.sharedProcess.KillForTests()` on that same shared instance
// to exercise crash recovery - a real, deterministic hazard for any other
// test class with an in-flight request against the same process at that
// moment, not a hypothetical one: confirmed during Task 8
// (`FsacBridgeDefinitionTests`) by pairing that test class with
// `FsacBridgeCheckTests` and observing a reliably reproducing
// `LspFail { Code = -1; Message = "the LSP server process exited" }` -
// the exact signal `LspClient.fs` sends every pending request when its
// process dies out from under it. Under xUnit's default cross-collection
// parallelism, `FsacBridgeCheckTests`'s kill can land squarely inside
// another class's own in-flight `SendRequest`, same class of bug the
// matching `AssemblyInfo.fs` in `FSharpQueryStudio.Execution.Tests` and
// `FSharpQueryStudio.Worker.Tests` already document for their own
// process-global state. Disabling parallelization here serializes every
// test in this assembly - including the intentional *intra-test*
// concurrency `FsacBridgeHoverTests`'s "check and hover racing for the
// same documentUri" test relies on (two tasks started before either is
// awaited, within one test method) - which this leaves untouched, since
// that race is unaffected by cross-test/class scheduling.
[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()
