namespace FSharpQueryStudio.Fsac

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks

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
        proc.Start() |> ignore
        let client = LspClient(input = proc.StandardOutput.BaseStream, output = proc.StandardInput.BaseStream)
        proc, client

    let initializeHandshake (client: LspClient) : Task =
        task {
            let! _ =
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
                | Some(proc, client) when not proc.HasExited -> return client
                | _ ->
                    let proc, client = spawn ()
                    let handshakeTask = initializeHandshake client
                    let! winner = Task.WhenAny(handshakeTask, Task.Delay(TimeSpan.FromSeconds 15.0))

                    if winner <> handshakeTask then
                        try
                            if not proc.HasExited then
                                proc.Kill()
                        with _ ->
                            ()

                        current <- None
                        return failwith "fsautocomplete did not complete the initialize handshake within 15s"
                    else
                        try
                            do! handshakeTask
                            current <- Some(proc, client)
                            return client
                        with ex ->
                            try
                                if not proc.HasExited then
                                    proc.Kill()
                            with _ ->
                                ()

                            current <- None
                            return raise ex
            finally
                startLock.Release() |> ignore
        }

    /// Test-only: simulates a crash so `EnsureClient`'s respawn path can
    /// be exercised deterministically, without relying on a real fault.
    member _.KillForTests() : unit =
        match current with
        | Some(proc, _) when not proc.HasExited -> proc.Kill()
        | _ -> ()

    /// Best-effort graceful shutdown — never throws past this call, same
    /// convention as `Handlers.deleteAnalysisWorkDir`'s own best-effort
    /// cleanup. Wired to the Worker process's own exit in a later task.
    member _.Shutdown() : unit =
        match current with
        | Some(proc, _) ->
            try
                if not proc.HasExited then
                    proc.Kill()
            with _ ->
                ()

            current <- None
        | None -> ()
