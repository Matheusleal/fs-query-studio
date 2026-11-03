namespace FSharpQueryStudio.Connectors

open System
open System.Globalization

/// Provides column data transformation and formula evaluation for tabular sources (Excel, CSV).
module ColumnTransformation =

    let private parseArgs (inside: string) : string list =
        let args = ResizeArray<string>()
        let currentArg = System.Text.StringBuilder()
        let mutable inQuotes = false
        let mutable quoteChar = ' '

        for i = 0 to inside.Length - 1 do
            let ch = inside.[i]
            if (ch = '\'' || ch = '"') then
                if not inQuotes then
                    inQuotes <- true
                    quoteChar <- ch
                elif ch = quoteChar then
                    inQuotes <- false
                else
                    currentArg.Append(ch) |> ignore
            elif ch = ',' && not inQuotes then
                args.Add(currentArg.ToString().Trim())
                currentArg.Clear() |> ignore
            else
                currentArg.Append(ch) |> ignore
        args.Add(currentArg.ToString().Trim())
        args |> Seq.toList

    /// Applies a formula, replacement, or transformation rule to a column value.
    /// Supports:
    /// - replace('from', 'to')
    /// - chained replacements: replace('.', '').replace(',', '.')
    /// - trim
    /// - multiply(factor), divide(divisor)
    /// - decimal, double, int, int64, string
    let applyTransform (transformExpr: string) (value: obj) : obj =
        if isNull value || String.IsNullOrWhiteSpace transformExpr then value
        else
            let rawStr =
                match value with
                | :? string as s -> s
                | other -> Convert.ToString(other, CultureInfo.InvariantCulture)

            let trimmedExpr = transformExpr.Trim()

            let rec evalChain (expr: string) (current: string) : string =
                let expr = expr.Trim()
                if String.IsNullOrEmpty expr then current
                elif expr.StartsWith("replace(", StringComparison.OrdinalIgnoreCase) then
                    let closeIdx = expr.IndexOf(')')
                    if closeIdx > 0 then
                        let inside = expr.Substring(8, closeIdx - 8)
                        let parts = parseArgs inside
                        let fromStr = if parts.Length > 0 then parts.[0] else ""
                        let toStr = if parts.Length > 1 then parts.[1] else ""
                        let nextStr =
                            if String.IsNullOrEmpty fromStr then current
                            else current.Replace(fromStr, toStr)
                        let rest = expr.Substring(closeIdx + 1).TrimStart('.')
                        evalChain rest nextStr
                    else current
                elif expr.StartsWith("trim", StringComparison.OrdinalIgnoreCase) then
                    let closeIdx = expr.IndexOf(')')
                    let rest = if closeIdx > 0 then expr.Substring(closeIdx + 1).TrimStart('.') else expr.Substring(4).TrimStart('.')
                    evalChain rest (current.Trim())
                elif expr.StartsWith("multiply(", StringComparison.OrdinalIgnoreCase) then
                    let closeIdx = expr.IndexOf(')')
                    if closeIdx > 0 then
                        let factorStr = expr.Substring(9, closeIdx - 9).Trim().Trim('\'', '"')
                        match Double.TryParse(current.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture) with
                        | true, d ->
                            match Double.TryParse(factorStr, NumberStyles.Float, CultureInfo.InvariantCulture) with
                            | true, f -> (d * f).ToString(CultureInfo.InvariantCulture)
                            | false, _ -> current
                        | false, _ -> current
                    else current
                elif expr.StartsWith("divide(", StringComparison.OrdinalIgnoreCase) then
                    let closeIdx = expr.IndexOf(')')
                    if closeIdx > 0 then
                        let divisorStr = expr.Substring(7, closeIdx - 7).Trim().Trim('\'', '"')
                        match Double.TryParse(current.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture) with
                        | true, d ->
                            match Double.TryParse(divisorStr, NumberStyles.Float, CultureInfo.InvariantCulture) with
                            | true, div when div <> 0.0 -> (d / div).ToString(CultureInfo.InvariantCulture)
                            | _ -> current
                        | false, _ -> current
                    else current
                elif expr.Equals("decimal", StringComparison.OrdinalIgnoreCase) || expr.Equals("double", StringComparison.OrdinalIgnoreCase) then
                    current.Replace(",", ".")
                elif expr.Equals("toString", StringComparison.OrdinalIgnoreCase) || expr.Equals("string", StringComparison.OrdinalIgnoreCase) then
                    current
                else
                    current

            let resultStr = evalChain trimmedExpr rawStr
            box resultStr

/// Unified type inference for tabular connectors (Excel, CSV).
module TabularTypeInference =

    let cleanSample (s: string) : string =
        if isNull s then ""
        else s.Trim().Trim('"', '\'')

    let isSentinel (s: string) : bool =
        if isNull s then false
        else
            let t = s.Trim().Trim('"', '\'')
            String.Equals(t, "n/a", StringComparison.OrdinalIgnoreCase)
            || t = "-"
            || String.Equals(t, "null", StringComparison.OrdinalIgnoreCase)

    let isEmpty (s: string) : bool =
        if isNull s then true
        else
            let t = s.Trim().Trim('"', '\'')
            String.IsNullOrEmpty t

    let isMissingOrSentinel (s: string) : bool =
        isEmpty s || isSentinel s

    /// Normalizes a numeric string representation by resolving decimal and group separators.
    let normalizeNumeric (s: string) : string =
        let clean = cleanSample s
        if clean.Contains(",") && not (clean.Contains(".")) then
            clean.Replace(",", ".")
        elif clean.Contains(",") && clean.Contains(".") then
            let lastComma = clean.LastIndexOf(',')
            let lastDot = clean.LastIndexOf('.')
            if lastDot > lastComma then clean.Replace(",", "")
            else clean.Replace(".", "").Replace(",", ".")
        else
            clean

    /// Infers an F# type from sample string values, honoring explicit column type overrides if provided.
    let inferType (samples: string seq) : string =
        let sampleList = samples |> Seq.truncate 500 |> Seq.toList
        let hasSentinel = sampleList |> List.exists isSentinel
        let hasEmpty = sampleList |> List.exists isEmpty

        let validValues =
            sampleList
            |> List.map cleanSample
            |> List.filter (fun s -> not (isMissingOrSentinel s))

        if List.isEmpty validValues then
            if hasSentinel then "string option" else "string"
        else
            let baseType =
                // Check if all values are integers without any decimal part or decimal separators
                if validValues |> List.forall (fun s ->
                    not (s.Contains('.') || s.Contains(','))
                    && (match Int32.TryParse(s, NumberStyles.Integer ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                        | true, _ -> true | false, _ -> false)) then
                    "int"
                elif validValues |> List.forall (fun s ->
                    not (s.Contains('.') || s.Contains(','))
                    && (match Int64.TryParse(s, NumberStyles.Integer ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                        | true, _ -> true | false, _ -> false)) then
                    "int64"
                elif validValues |> List.forall (fun s ->
                    let norm = normalizeNumeric s
                    match Decimal.TryParse(norm, NumberStyles.Number ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                    | true, _ -> true
                    | false, _ -> false) then
                    "decimal"
                elif validValues |> List.forall (fun s ->
                    let norm = normalizeNumeric s
                    match Double.TryParse(norm, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                    | true, d -> Double.IsFinite d
                    | false, _ -> false) then
                    "double"
                elif validValues |> List.forall (fun s ->
                    match Boolean.TryParse(s) with
                    | true, _ -> true
                    | false, _ ->
                        let l = s.ToLowerInvariant()
                        l = "true" || l = "false" || l = "1" || l = "0" || l = "sim" || l = "nao" || l = "não") then
                    "bool"
                elif validValues |> List.forall (fun s ->
                    match DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None) with
                    | true, _ -> true
                    | false, _ ->
                        match DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None) with
                        | true, _ -> true
                        | false, _ -> false) then
                    "System.DateTime"
                else
                    "string"

            if (hasSentinel || hasEmpty) && not (baseType.EndsWith("option")) then
                $"{baseType} option"
            else
                baseType
