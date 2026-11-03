namespace FSharpQueryStudio.Fsac

open System
open System.IO
open System.Text.RegularExpressions

/// Utilities for canonicalizing and synthesizing file URIs when communicating with FSAC.
module FsacUri =

    /// Normalizes document URIs by unescaping data strings, converting backslashes,
    /// and stripping single-letter drive prefixes to ensure deterministic equality comparisons.
    let canonicalizeUri (uri: string) : string =
        let decoded = Uri.UnescapeDataString(uri).Replace('\\', '/')
        Regex.Replace(decoded, "^file:///[A-Za-z]:/", "file:///", RegexOptions.IgnoreCase).ToLowerInvariant()

    /// Process-unique base directory under which synthetic FSAC-facing URIs are namespaced.
    let untitledUriRoot =
        Path.Combine(Path.GetTempPath(), "fqs-fsac-untitled-" + Guid.NewGuid().ToString("N"))

    /// Converts any document URI into a valid file:// URI accepted and parsed predictably by FSAC.
    /// Preserves existing file:// URIs, while mapping non-file URIs (such as untitled or notebook cells)
    /// to deterministic, safe synthetic paths ending with .fsx.
    let fsacUriFor (documentUri: string) : string =
        if documentUri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) then
            documentUri
        else
            let rawName =
                match documentUri.IndexOf(':') with
                | -1 -> documentUri
                | i -> documentUri.Substring(i + 1)

            let sanitized =
                String(
                    rawName.ToCharArray()
                    |> Array.map (fun c ->
                        if
                            (c >= 'A' && c <= 'Z')
                            || (c >= 'a' && c <= 'z')
                            || (c >= '0' && c <= '9')
                            || c = '.'
                            || c = '_'
                            || c = '-'
                        then
                            c
                        else
                            '_')
                )

            let safeName =
                if sanitized.EndsWith(".fsx", StringComparison.OrdinalIgnoreCase) then
                    sanitized
                else
                    sanitized + ".fsx"

            let fullPath = Path.Combine(untitledUriRoot, safeName).Replace('\\', '/')
            $"file:///{fullPath.TrimStart('/')}"

