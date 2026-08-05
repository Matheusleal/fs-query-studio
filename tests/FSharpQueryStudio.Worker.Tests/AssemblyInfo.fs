module AssemblyInfo

open Xunit

// Handlers.handleEvaluate and CancellationSignal mutate process-global
// state (current directory, Console.Out/Error, the cancellation flag).
// Tests across different classes/modules would race on that state under
// xUnit's default cross-collection parallelism - see the matching note in
// FSharpQueryStudio.Execution.Tests/AssemblyInfo.fs.
[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()
