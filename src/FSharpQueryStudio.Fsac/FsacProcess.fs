namespace FSharpQueryStudio.Fsac

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Serilog

/// Spawns and supervises the bundled `fsautocomplete` child process,
/// mirroring `App.WorkerClient`'s own "start on first use, detect exit,
/// respawn on the next call" ownership model one process level down: the
/// App supervises the Worker, the Worker (via this type) supervises FSAC.
/// `fsautocomplete` speaks LSP over stdio by default (no CLI mode flag) —
/// confirmed against the pinned tool version during this task's own test
/// run; if a future version needs an explicit flag, add it to `Arguments`
/// below.
type FsacProcess(fsautocompleteDllPath: string) =
    let mutable current: (Process * LspClient) option = None
    let startLock = new SemaphoreSlim(1, 1)

    let killProc (proc: Process) =
        try
            if not proc.HasExited then
                proc.Kill(entireProcessTree = true)
        with _ -> ()

    do
        AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
            match current with
            | Some(proc, _) -> killProc proc
            | None -> ()
        )

    let spawn () : Process * LspClient =
        let psi =
            ProcessStartInfo(
                FileName = "dotnet",
                Arguments = $"\"{fsautocompleteDllPath}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            )

        let proc = new Process(StartInfo = psi)

        // `fsautocomplete` logs to stderr, most heavily during script/
        // project loading (the first parse) - if nothing ever reads
        // `StandardError` and the OS pipe buffer fills (~4KB on Windows),
        // the child blocks on its next stderr write and stops responding
        // to stdin/stdout entirely, with `HasExited` still `false` so no
        // respawn ever engages. `BeginErrorReadLine`/`ErrorDataReceived` is
        // the standard non-blocking drain for this - a synchronous read
        // loop here would just move the blocking problem, not fix it.
        proc.ErrorDataReceived.Add(fun args ->
            if not (isNull args.Data) then
                Log.Debug("fsautocomplete stderr: {Line}", args.Data))

        proc.Start() |> ignore
        proc.BeginErrorReadLine()
        let client = LspClient(input = proc.StandardOutput.BaseStream, output = proc.StandardInput.BaseStream)
        proc, client

    let initializeHandshake (client: LspClient) : Task =
        task {
            let! result =
                client.SendRequest(
                    "initialize",
                    {| processId = System.Diagnostics.Process.GetCurrentProcess().Id
                       rootUri = null
                       // Declaring `textDocument.publishDiagnostics` client
                       // capability is required for real fsautocomplete
                       // 0.83.0 to push `textDocument/publishDiagnostics`
                       // notifications at all - confirmed during Task 6
                       // (FsacBridge.check, the first task to round-trip
                       // real diagnostics): with an empty `capabilities`
                       // object, FSAC ran analysis (its own
                       // `fsharp/documentAnalyzed`/`fsharp/fileParsed`
                       // notifications still fired) but never sent a single
                       // `publishDiagnostics` push, even after 45s.
                       capabilities =
                        {| textDocument =
                            {| publishDiagnostics = {| relatedInformation = true |} |} |} |}
                )

            // A protocol-level `LspFail` here - including the exact
            // `LspFail{Code=-1}` sentinel a process that's already exited
            // (or whose reader loop already died) produces - must not be
            // silently discarded: doing so previously let `EnsureClient`
            // still hand back this dead pair as if it were ready, turning
            // e.g. a missing `FsacTool` at startup into a silent no-op
            // instead of a loud, diagnosable failure. Raising here routes
            // through `EnsureClient`'s existing `with ex ->` cleanup path
            // (kill the process, reset `current` to `None`, propagate).
            match result with
            | LspOk _ -> ()
            | LspFail err -> failwith $"fsautocomplete rejected the 'initialize' handshake: [{err.Code}] {err.Message}"

            do! client.SendNotification("initialized", {| |})
        }

    /// Returns the live `LspClient` for the current `fsautocomplete`
    /// process, starting one (and completing the `initialize`/
    /// `initialized` handshake) if none is running or the previous one
    /// has exited. Serialized by `startLock` so two concurrent
    /// `analysis/*` calls arriving while nothing is running don't each
    /// spawn their own process.
    member _.EnsureClient() : Task<LspClient> =
        task {
            do! startLock.WaitAsync()

            try
                match current with
                | Some(proc, client) when not proc.HasExited && client.IsAlive -> return client
                | _ ->
                    // The process can still be alive (`not proc.HasExited`)
                    // while its `LspClient`'s reader loop has already died
                    // (`not client.IsAlive`) — e.g. an unrecoverable framing
                    // error on stdout that isn't a real process exit. That
                    // combination falls through to here same as "nothing
                    // running yet"; the now-orphaned process (if any) is
                    // killed before spawning its replacement so it isn't
                    // leaked.
                    match current with
                    | Some(staleProc, _) -> killProc staleProc
                    | _ -> ()

                    let proc, client = spawn ()
                    let handshakeTask = initializeHandshake client
                    let! winner = Task.WhenAny(handshakeTask, Task.Delay(TimeSpan.FromSeconds 30.0))

                    if winner <> handshakeTask then
                        killProc proc
                        current <- None
                        return failwith "fsautocomplete did not complete the initialize handshake within 30s"
                    else
                        try
                            do! handshakeTask
                            current <- Some(proc, client)
                            return client
                        with ex ->
                            killProc proc
                            current <- None
                            return raise ex
            finally
                startLock.Release() |> ignore
        }

    /// Test-only: simulates a crash so `EnsureClient`'s respawn path can
    /// be exercised deterministically, without relying on a real fault.
    member _.KillForTests() : unit =
        match current with
        | Some(proc, _) when not proc.HasExited ->
            try
                proc.Kill(entireProcessTree = true)
                proc.WaitForExit(5000) |> ignore
            with _ ->
                ()
        | _ -> ()

    /// Best-effort graceful shutdown — never throws past this call, same
    /// convention as `Handlers.deleteAnalysisWorkDir`'s own best-effort
    /// cleanup. Wired to the Worker process's own exit in a later task.
    member this.Shutdown() : unit =
        match current with
        | Some(proc, _) ->
            try
                if not proc.HasExited then
                    proc.Kill(entireProcessTree = true)
                    proc.WaitForExit(2000) |> ignore
            with _ ->
                ()

            current <- None
        | None -> ()

    interface IDisposable with
        member this.Dispose() = this.Shutdown()
