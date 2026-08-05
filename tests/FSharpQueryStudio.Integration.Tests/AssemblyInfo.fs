module AssemblyInfo

open Xunit

// Every test here spawns a real worker subprocess. Running them
// concurrently (xUnit's default across test classes) contends for CPU
// enough to push timing-sensitive tests - like A.9's grace-period
// cancellation - past their window under load. See the matching notes in
// Execution.Tests/Worker.Tests' AssemblyInfo.fs for the related but
// distinct process-global-state reason those two need this too.
[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()
