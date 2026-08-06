namespace FSharpQueryStudio.Security

open System
open System.Runtime.InteropServices
open System.Text

/// P/Invoke surface for the Windows Credential Manager (`advapi32.dll`).
/// Kept to the handful of calls `WindowsCredentialStore` needs — this is
/// the "platform integration layer" Section 14 carves out so the rest of
/// the codebase can stay platform-neutral against `ISecretStore` alone.
module private NativeCredentials =
    [<Literal>]
    let CRED_TYPE_GENERIC = 1u

    [<Literal>]
    let CRED_PERSIST_LOCAL_MACHINE = 2u

    [<StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
    type CREDENTIAL =
        struct
            val mutable Flags: uint32
            val mutable Type: uint32
            val mutable TargetName: nativeint
            val mutable Comment: nativeint
            val mutable LastWritten: ComTypes.FILETIME
            val mutable CredentialBlobSize: uint32
            val mutable CredentialBlob: nativeint
            val mutable Persist: uint32
            val mutable AttributeCount: uint32
            val mutable Attributes: nativeint
            val mutable TargetAlias: nativeint
            val mutable UserName: nativeint
        end

    [<DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern bool CredWriteW(CREDENTIAL& credential, uint32 flags)

    [<DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern bool CredReadW(string targetName, uint32 ``type``, uint32 flags, nativeint& credentialPtr)

    [<DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern bool CredDeleteW(string targetName, uint32 ``type``, uint32 flags)

    [<DllImport("advapi32.dll", SetLastError = true)>]
    extern void CredFree(nativeint credentialPtr)

open NativeCredentials

/// The Windows v1 `ISecretStore` — Section 14's "Windows credential
/// manager" choice. Every credential is written under the target name
/// `reference`, tagged `CRED_TYPE_GENERIC` so it never collides with
/// credentials other applications store.
type WindowsCredentialStore() =

    interface ISecretStore with
        member _.Save (reference: string) (secret: string) =
            let secretBytes = Encoding.Unicode.GetBytes(secret)
            let blobPtr = Marshal.AllocHGlobal(secretBytes.Length)

            try
                Marshal.Copy(secretBytes, 0, blobPtr, secretBytes.Length)

                let mutable credential = CREDENTIAL()
                credential.Type <- CRED_TYPE_GENERIC
                credential.TargetName <- Marshal.StringToHGlobalUni(reference)
                credential.CredentialBlobSize <- uint32 secretBytes.Length
                credential.CredentialBlob <- blobPtr
                credential.Persist <- CRED_PERSIST_LOCAL_MACHINE
                credential.UserName <- Marshal.StringToHGlobalUni("fs-query-studio")

                try
                    if not (CredWriteW(&credential, 0u)) then
                        let error = Marshal.GetLastWin32Error()
                        failwith $"CredWriteW failed for '{reference}' with Win32 error {error}"
                finally
                    Marshal.FreeHGlobal(credential.TargetName)
                    Marshal.FreeHGlobal(credential.UserName)
            finally
                Marshal.FreeHGlobal(blobPtr)

        member _.TryRead(reference: string) =
            let mutable credentialPtr = nativeint 0

            if CredReadW(reference, CRED_TYPE_GENERIC, 0u, &credentialPtr) then
                try
                    let credential =
                        Marshal.PtrToStructure<CREDENTIAL>(credentialPtr)

                    let secretBytes = Array.zeroCreate<byte> (int credential.CredentialBlobSize)
                    Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length)
                    Some(Encoding.Unicode.GetString(secretBytes))
                finally
                    CredFree credentialPtr
            else
                None

        member _.Delete(reference: string) =
            CredDeleteW(reference, CRED_TYPE_GENERIC, 0u) |> ignore
