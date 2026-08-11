module ThemeServiceTests

open System.ComponentModel
open Xunit
open FSharpQueryStudio.Ui

[<Fact>]
let ``starts in the Dark theme`` () =
    let service = ThemeService()
    Assert.Equal(Theme.Dark, service.Current)

[<Fact>]
let ``Toggle flips Dark to Light`` () =
    let service = ThemeService()
    service.Toggle()
    Assert.Equal(Theme.Light, service.Current)

[<Fact>]
let ``Toggle flips Light back to Dark`` () =
    let service = ThemeService()
    service.Toggle()
    service.Toggle()
    Assert.Equal(Theme.Dark, service.Current)

[<Fact>]
let ``PropertyChanged fires exactly once per Toggle, for Current`` () =
    let service = ThemeService()
    let mutable raisedCount = 0
    let mutable lastProperty = None
    (service :> INotifyPropertyChanged).PropertyChanged.Add(fun args ->
        raisedCount <- raisedCount + 1
        lastProperty <- Some args.PropertyName)

    service.Toggle()

    Assert.Equal(1, raisedCount)
    Assert.Equal(Some "Current", lastProperty)
