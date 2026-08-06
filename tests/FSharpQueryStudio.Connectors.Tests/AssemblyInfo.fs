module AssemblyInfo

open Xunit

// ConnectorCallLoggingTests points Serilog.Log.Logger (process-global) at
// a test-local in-memory sink. Tests across different classes/modules
// would race on that global under xUnit's default cross-collection
// parallelism - see the matching note in
// FSharpQueryStudio.Worker.Tests/AssemblyInfo.fs.
[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()
