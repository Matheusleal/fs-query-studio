module Tests

open Xunit
open FSharpQueryStudio.Security

[<Fact>]
let ``Save then TryRead returns the saved secret`` () =
    let store = InMemorySecretStore() :> ISecretStore
    store.Save "vault://sales-db" "s3cr3t"

    Assert.Equal(Some "s3cr3t", store.TryRead "vault://sales-db")

[<Fact>]
let ``TryRead returns None for a reference that was never saved`` () =
    let store = InMemorySecretStore() :> ISecretStore

    Assert.Equal(None, store.TryRead "vault://never-registered")

[<Fact>]
let ``Save overwrites a previously saved secret for the same reference`` () =
    let store = InMemorySecretStore() :> ISecretStore
    store.Save "vault://sales-db" "old-secret"
    store.Save "vault://sales-db" "new-secret"

    Assert.Equal(Some "new-secret", store.TryRead "vault://sales-db")

[<Fact>]
let ``Delete removes a saved secret so TryRead returns None afterward`` () =
    let store = InMemorySecretStore() :> ISecretStore
    store.Save "vault://sales-db" "s3cr3t"

    store.Delete "vault://sales-db"

    Assert.Equal(None, store.TryRead "vault://sales-db")

[<Fact>]
let ``Delete is a no-op for a reference that was never saved`` () =
    let store = InMemorySecretStore() :> ISecretStore

    store.Delete "vault://never-registered" // Must not throw.
