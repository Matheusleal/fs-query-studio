namespace FSharpQueryStudio.Benchmarks

open System
open System.Diagnostics

type BenchmarkMetric = {
    Category: string
    Name: string
    Iterations: int
    TotalElapsedMs: double
    MeanElapsedMs: double
    MinElapsedMs: double
    MaxElapsedMs: double
    P95ElapsedMs: double
    UserCpuMs: double
    PrivilegedCpuMs: double
    TotalCpuMs: double
    CpuPercentage: double
    AllocatedBytes: int64
    AllocatedPerOpKb: double
    Gen0Collections: int
    Gen1Collections: int
    Gen2Collections: int
    PeakWorkingSetMb: double
}

module BenchmarkRunner =

    let private calculatePercentile (sorted: double[]) (percentile: double) : double =
        if sorted.Length = 0 then 0.0
        elif sorted.Length = 1 then sorted.[0]
        else
            let idx = int (Math.Ceiling(percentile * double sorted.Length)) - 1
            sorted.[Math.Clamp(idx, 0, sorted.Length - 1)]

    let run (category: string) (name: string) (iterations: int) (warmup: int) (action: unit -> 'T) : BenchmarkMetric =
        // Warmup runs
        for _ in 1 .. warmup do
            action () |> ignore

        // Force GC clean state before benchmark measurement
        GC.Collect()
        GC.WaitForPendingFinalizers()
        GC.Collect()

        let proc = Process.GetCurrentProcess()
        proc.Refresh()
        let startUserCpu = proc.UserProcessorTime
        let startPrivCpu = proc.PrivilegedProcessorTime
        let startAllocated = GC.GetAllocatedBytesForCurrentThread()
        let startGen0 = GC.CollectionCount(0)
        let startGen1 = GC.CollectionCount(1)
        let startGen2 = GC.CollectionCount(2)

        let sw = Stopwatch()
        let times = Array.zeroCreate<double> iterations

        for i in 0 .. iterations - 1 do
            sw.Restart()
            action () |> ignore
            sw.Stop()
            times.[i] <- sw.Elapsed.TotalMilliseconds

        proc.Refresh()
        let endUserCpu = proc.UserProcessorTime
        let endPrivCpu = proc.PrivilegedProcessorTime
        let endAllocated = GC.GetAllocatedBytesForCurrentThread()
        let endGen0 = GC.CollectionCount(0)
        let endGen1 = GC.CollectionCount(1)
        let endGen2 = GC.CollectionCount(2)

        let totalElapsed = Array.sum times
        let sorted = Array.sort times
        let userCpu = (endUserCpu - startUserCpu).TotalMilliseconds
        let privCpu = (endPrivCpu - startPrivCpu).TotalMilliseconds
        let totalCpu = userCpu + privCpu
        let cpuPct = if totalElapsed > 0.0 then Math.Min(100.0, (totalCpu / (totalElapsed * double Environment.ProcessorCount)) * 100.0) else 0.0
        let totalAlloc = Math.Max(0L, endAllocated - startAllocated)
        let allocPerOp = (double totalAlloc / double iterations) / 1024.0
        let peakWs = double proc.PeakWorkingSet64 / (1024.0 * 1024.0)

        {
            Category = category
            Name = name
            Iterations = iterations
            TotalElapsedMs = totalElapsed
            MeanElapsedMs = Array.average times
            MinElapsedMs = Array.min times
            MaxElapsedMs = Array.max times
            P95ElapsedMs = calculatePercentile sorted 0.95
            UserCpuMs = userCpu
            PrivilegedCpuMs = privCpu
            TotalCpuMs = totalCpu
            CpuPercentage = cpuPct
            AllocatedBytes = totalAlloc
            AllocatedPerOpKb = allocPerOp
            Gen0Collections = endGen0 - startGen0
            Gen1Collections = endGen1 - startGen1
            Gen2Collections = endGen2 - startGen2
            PeakWorkingSetMb = peakWs
        }
