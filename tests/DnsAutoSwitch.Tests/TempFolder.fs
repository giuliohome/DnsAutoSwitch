module DnsAutoSwitch.Tests.TempFolder

open System
open System.IO
open System.Security.AccessControl
open System.Security.Principal

/// The account running the tests, as SDDL. The disk tests use it as owner instead of Administrators, so they
/// run without elevation.
let me = WindowsIdentity.GetCurrent().User.Value

/// True on an elevated prompt (and on CI runners without UAC): Administrators can do anything, so the tests
/// about what an ordinary user cannot do have nothing to check there.
let isElevated =
    use identity = WindowsIdentity.GetCurrent()
    WindowsPrincipal(identity).IsInRole WindowsBuiltInRole.Administrator

/// Gives the current user full control back, so a tree the tests locked can be deleted. As the owner it may
/// always rewrite the permission list. Links are skipped: changing them would change what they point to.
let rec private unlock (info: FileSystemInfo) =
    if not (info.Attributes.HasFlag FileAttributes.ReparsePoint) then
        try
            match info with
            | :? DirectoryInfo as d ->
                let s = DirectorySecurity()
                s.SetSecurityDescriptorSddlForm($"D:PAI(A;OICI;FA;;;{me})", AccessControlSections.Access)
                FileSystemAclExtensions.SetAccessControl(d, s)
                for child in d.GetFileSystemInfos() do unlock child
            | :? FileInfo as f ->
                let s = FileSecurity()
                s.SetSecurityDescriptorSddlForm($"D:PAI(A;;FA;;;{me})", AccessControlSections.Access)
                FileSystemAclExtensions.SetAccessControl(f, s)
            | _ -> ()
        with _ -> ()

/// A fresh folder under %TEMP%, deleted with everything in it at the end.
type TempFolder() =
    let path = Path.Combine(Path.GetTempPath(), "DnsAutoSwitch.Tests." + Guid.NewGuid().ToString "N")
    do Directory.CreateDirectory path |> ignore
    member _.Path = path
    member _.File(name: string, content: string) =
        let file = Path.Combine(path, name)
        Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
        File.WriteAllText(file, content)
        file
    interface IDisposable with
        member _.Dispose() =
            unlock (DirectoryInfo path)
            // Deleting a junction removes the link, not what it points to. A few tries: a file just deleted
            // can stay "delete pending" while an antivirus still has it open, and then the folder is not empty.
            let mutable tries = 5
            while tries > 0 && Directory.Exists path do
                try Directory.Delete(path, true) with _ -> Threading.Thread.Sleep 200
                tries <- tries - 1

/// Fails the test with the error message, which Assert.Equal on a Result would not show.
let ok (result: Result<unit, string>) =
    match result with
    | Ok() -> ()
    | Error e -> failwith e
