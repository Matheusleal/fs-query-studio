// ============================================================================
// build.fsx - Build & Release Automation Script for FSharpQueryStudio
// Usage:
//   dotnet fsi build.fsx [target]
// Targets:
//   build    : Compila backend e extensão em modo Release (padrão)
//   release  : Compila tudo e empacota a extensão VS Code (.vsix)
//   test     : Executa testes unitários do backend e da extensão
//   clean    : Limpa artefatos de build (bin, obj, dist, *.vsix)
//   all      : clean -> build -> test -> release
// ============================================================================

open System
open System.IO
open System.Diagnostics
open System.Runtime.InteropServices

// --- Configurações & Caminhos ---
let rootDir = __SOURCE_DIRECTORY__
let solutionFile = Path.Combine(rootDir, "FSharpQueryStudio.slnx")
let workerProject = Path.Combine(rootDir, "src", "FSharpQueryStudio.Worker", "FSharpQueryStudio.Worker.fsproj")
let vscodeDir = Path.Combine(rootDir, "editors", "vscode")
let workerOutDir = Path.Combine(vscodeDir, "dist", "worker")
let vsixOutputDir = Path.Combine(vscodeDir, "fs-query-studio.vsix")

let isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)

// --- Utilitários de Console & Cores ---
let printHeader (msg: string) =
    Console.ForegroundColor <- ConsoleColor.Cyan
    Console.WriteLine("\n" + String.replicate 70 "=")
    Console.WriteLine($"==> {msg}")
    Console.WriteLine(String.replicate 70 "=")
    Console.ResetColor()

let printSuccess (msg: string) =
    Console.ForegroundColor <- ConsoleColor.Green
    Console.WriteLine($"[OK] {msg}")
    Console.ResetColor()

let printError (msg: string) =
    Console.ForegroundColor <- ConsoleColor.Red
    Console.WriteLine($"[ERRO] {msg}")
    Console.ResetColor()

let printInfo (msg: string) =
    Console.ForegroundColor <- ConsoleColor.DarkGray
    Console.WriteLine($"[INFO] {msg}")
    Console.ResetColor()

// --- Execução de Processos ---
let runProcess (workingDir: string) (command: string) (args: string) =
    let fileName, arguments = 
        if isWindows && (command = "npm" || command = "npx") then
            "cmd.exe", $"/c \"{command} {args}\""
        else
            command, args

    printInfo $"Executando em '{workingDir}': {command} {args}"
    
    let psi = ProcessStartInfo(
        FileName = fileName,
        Arguments = arguments,
        WorkingDirectory = workingDir,
        UseShellExecute = false,
        RedirectStandardOutput = false,
        RedirectStandardError = false
    )
    
    use proc = Process.Start(psi)
    proc.WaitForExit()
    
    if proc.ExitCode <> 0 then
        printError $"Comando '{command} {args}' falhou com código de saída {proc.ExitCode}."
        failwith $"Falha na execução do processo (código: {proc.ExitCode})"

// --- Targets ---

let clean () =
    printHeader "LIMPANDO ARTEFATOS DE BUILD"
    
    // Limpar bin/ e obj/ dos projetos da solução (ignorando .git, .vscode-test, node_modules e .worktrees)
    let cleanDirs = 
        Directory.GetDirectories(rootDir, "bin", SearchOption.AllDirectories)
        |> Array.append (Directory.GetDirectories(rootDir, "obj", SearchOption.AllDirectories))
        |> Array.append [| workerOutDir |]
        |> Array.filter (fun d -> 
            not (d.Contains(".git") || 
                 d.Contains(".vscode-test") || 
                 d.Contains("node_modules") || 
                 d.Contains(".worktrees")))

    for dir in cleanDirs do
        if Directory.Exists(dir) then
            printInfo $"Removendo diretório: {dir}"
            try Directory.Delete(dir, true) with _ -> ()

    // Remover arquivos .vsix
    let vsixFiles = Directory.GetFiles(vscodeDir, "*.vsix")
    for f in vsixFiles do
        printInfo $"Removendo pacote: {f}"
        try File.Delete(f) with _ -> ()

    printSuccess "Limpeza concluída com sucesso!"

let buildBackend () =
    printHeader "1. COMPILANDO BACKEND (.NET - RELEASE)"
    runProcess rootDir "dotnet" $"build \"{solutionFile}\" -c Release"
    printSuccess "Backend compilado com sucesso."

let publishWorker () =
    printHeader "2. PUBLICANDO WORKER (F# WORKER - RELEASE)"
    if not (Directory.Exists(workerOutDir)) then
        Directory.CreateDirectory(workerOutDir) |> ignore
        
    runProcess rootDir "dotnet" $"publish \"{workerProject}\" -c Release -o \"{workerOutDir}\""
    printSuccess "Worker publicado em editors/vscode/dist/worker com sucesso."

let compileVscode () =
    printHeader "3. COMPILANDO EXTENSÃO VS CODE"
    runProcess vscodeDir "npm" "run compile"
    printSuccess "Extensão VS Code compilada com sucesso."

let testBackend () =
    printHeader "EXECUTANDO TESTES DO BACKEND (.NET)"
    runProcess rootDir "dotnet" $"test \"{solutionFile}\" -c Release --no-build"
    printSuccess "Testes do backend passaram com 100% de sucesso."

let testVscode () =
    printHeader "EXECUTANDO TESTES DA EXTENSÃO (VS CODE)"
    runProcess vscodeDir "npm" "test"
    printSuccess "Testes da extensão passaram com sucesso."

let packageVscode () =
    printHeader "4. EMPACOTANDO EXTENSÃO VS CODE (.VSIX)"
    runProcess vscodeDir "npm" "run package"
    
    if File.Exists(vsixOutputDir) then
        let fileInfo = FileInfo(vsixOutputDir)
        let sizeKb = double fileInfo.Length / 1024.0
        printSuccess $"Extensão empacotada com sucesso: {vsixOutputDir} ({sizeKb:F2} KB)"
    else
        failwith $"Arquivo {vsixOutputDir} não foi gerado."

// --- Target Aggregators ---

let targetBuild () =
    let sw = Stopwatch.StartNew()
    buildBackend ()
    publishWorker ()
    compileVscode ()
    sw.Stop()
    printSuccess $"Target 'build' concluído em {sw.Elapsed.TotalSeconds:F2}s"

let targetRelease () =
    let sw = Stopwatch.StartNew()
    targetBuild ()
    packageVscode ()
    sw.Stop()
    printSuccess $"Target 'release' concluído com sucesso em {sw.Elapsed.TotalSeconds:F2}s"

let targetTest () =
    let sw = Stopwatch.StartNew()
    testBackend ()
    testVscode ()
    sw.Stop()
    printSuccess $"Target 'test' concluído com sucesso em {sw.Elapsed.TotalSeconds:F2}s"

let targetAll () =
    let sw = Stopwatch.StartNew()
    clean ()
    targetBuild ()
    targetTest ()
    packageVscode ()
    sw.Stop()
    printSuccess $"Target 'all' concluído com sucesso em {sw.Elapsed.TotalSeconds:F2}s"

// --- Entry Point ---
let args = fsi.CommandLineArgs |> Array.skip 1

let target = 
    if args.Length > 0 then
        args.[0].ToLowerInvariant()
    else
        "build"

let showHelp () =
    Console.WriteLine("Uso: dotnet fsi build.fsx [target]")
    Console.WriteLine("")
    Console.WriteLine("Targets disponíveis:")
    Console.WriteLine("  build    : Compila o backend (.NET) e a extensão VS Code em modo Release (padrão)")
    Console.WriteLine("  release  : Executa o build completo e empacota o VSIX da extensão")
    Console.WriteLine("  test     : Executa todos os testes do backend e do VS Code")
    Console.WriteLine("  clean    : Limpa binários e artefatos gerados")
    Console.WriteLine("  all      : Executa clean -> build -> test -> release")

let executeTarget (tgt: string) =
    match tgt with
    | "build" -> targetBuild ()
    | "release" | "package" -> targetRelease ()
    | "test" -> targetTest ()
    | "clean" -> clean ()
    | "all" -> targetAll ()
    | "--help" | "-h" | "help" -> showHelp ()
    | unknown ->
        printError $"Target desconhecido: '{unknown}'"
        showHelp ()
        exit 1

try
    executeTarget target
with ex ->
    printError $"Falha na execução: {ex.Message}"
    exit 1
