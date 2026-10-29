namespace FSharpQueryStudio.Connectors

open System
open System.Data.Common
open System.Text.RegularExpressions
open FSharpQueryStudio.Security

/// Generic utility that substitutes `template`'s `{password}` token from `secretStore`,
/// keyed by `secretReference`. Returns `template` unchanged when there is no
/// reference to resolve, or the reference has nothing stored under it.
module ConnectionStringSecrets =
    let resolve (secretStore: ISecretStore) (secretReference: string option) (template: string) : string =
        match secretReference with
        | None -> template
        | Some reference ->
            match secretStore.TryRead reference with
            | Some secret when not (String.IsNullOrEmpty secret) ->
                if template.IndexOf("{password}", StringComparison.OrdinalIgnoreCase) >= 0 then
                    Regex.Replace(template, "\{password\}", secret, RegexOptions.IgnoreCase)
                elif template.Contains("://") then
                    try
                        let uri = Uri(template)
                        let userInfo = uri.UserInfo
                        if String.IsNullOrEmpty userInfo then
                            template
                        elif userInfo.Contains(":") then
                            let user = userInfo.Substring(0, userInfo.IndexOf(':'))
                            template.Replace($"{userInfo}@", $"{user}:{Uri.EscapeDataString(secret)}@")
                        else
                            template.Replace($"{userInfo}@", $"{userInfo}:{Uri.EscapeDataString(secret)}@")
                    with _ ->
                        template
                else
                    try
                        let builder = DbConnectionStringBuilder()
                        builder.ConnectionString <- template
                        builder["Password"] <- secret
                        builder.ConnectionString
                    with _ ->
                        let trimmed = template.TrimEnd(';')
                        $"{trimmed};Password={secret}"
            | _ -> template
