namespace FSharpQueryStudio.Protocol

open System.Text.Json
open System.Text.Json.Serialization

/// Shared JSON options for every protocol frame. F#-aware (records, options,
/// unions) via FSharp.SystemTextJson; unknown members are ignored by default,
/// which is what lets an older host tolerate a newer worker's extra fields.
module Serialization =

    let options =
        let o = JsonSerializerOptions()
        o.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        o.PropertyNameCaseInsensitive <- true
        o.Converters.Add(JsonFSharpConverter())
        o
