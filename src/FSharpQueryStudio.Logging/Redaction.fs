namespace FSharpQueryStudio.Logging

open System.Text.RegularExpressions
open Serilog.Core
open Serilog.Events

/// Scrubs known secret-shaped log properties before they reach any sink:
/// connection strings with an embedded password, and properties named
/// after well-known secret-bearing keys (Authorization headers, tokens).
module Redaction =

    [<Literal>]
    let RedactedPlaceholder = "***REDACTED***"

    let private secretKeyNames =
        set [ "authorization"; "password"; "pwd"; "token"; "secret"; "connectionstring" ]

    let private connectionStringSecretPattern =
        Regex(@"\b(password|pwd)\s*=", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)

    let isSecretShaped (propertyName: string) (value: LogEventPropertyValue) : bool =
        if secretKeyNames.Contains(propertyName.ToLowerInvariant()) then
            true
        else
            match value with
            | :? ScalarValue as sv ->
                match sv.Value with
                | :? string as s -> connectionStringSecretPattern.IsMatch(s)
                | _ -> false
            | _ -> false

    type RedactingEnricher() =
        interface ILogEventEnricher with
            member _.Enrich(logEvent: LogEvent, propertyFactory: ILogEventPropertyFactory) =
                for kvp in logEvent.Properties |> Seq.toList do
                    if isSecretShaped kvp.Key kvp.Value then
                        let redacted = propertyFactory.CreateProperty(kvp.Key, RedactedPlaceholder)
                        logEvent.AddOrUpdateProperty(redacted)
