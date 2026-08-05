module AssemblyInfo

open Xunit

// FsiSession.create mutates process-global state (current directory,
// Console.Out/Error). Tests across different classes/modules would race
// on that state under xUnit's default cross-collection parallelism.
[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()
