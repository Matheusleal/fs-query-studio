namespace FSharpQueryStudio.Security

open System.Collections.Concurrent

/// A process-local `ISecretStore` — the fake every test suite drives
/// instead of the real OS credential store, and a non-Windows
/// development fallback until a Linux/macOS-backed store lands
/// alongside those platforms' UI shells (Section 14).
type InMemorySecretStore() =
    let secrets = ConcurrentDictionary<string, string>()

    interface ISecretStore with
        member _.Save (reference: string) (secret: string) = secrets.[reference] <- secret

        member _.TryRead(reference: string) =
            match secrets.TryGetValue reference with
            | true, secret -> Some secret
            | false, _ -> None

        member _.Delete(reference: string) = secrets.TryRemove reference |> ignore
