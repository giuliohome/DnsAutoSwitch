module DnsAutoSwitch.Tests.SecurityTests

open System
open System.Diagnostics
open System.IO
open System.Security.AccessControl
open System.Security.Principal
open Xunit
open DnsAutoSwitch.Security
open DnsAutoSwitch.Tests.TempFolder

let private folder (sddl: string) =
    let security = DirectorySecurity()
    security.SetSecurityDescriptorSddlForm sddl
    security

/// A local account that is not an administrator. The domain part is made up.
let private someUser = "S-1-5-21-1000-2000-3000-1001"
let private trustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
let private modify = "0x1301bf"

// In-memory permissions, copied from icacls on a Windows 11 machine.

[<Fact>]
let ``the permissions install sets have no problems`` () =
    Assert.Empty(problems folderMask (folder lockedSddl))
    Assert.Empty(problems fileMask (folder lockedSddl))

[<Fact>]
let ``a folder a user created under C:\ (C:\Tools) is flagged for its owner and for Authenticated Users`` () =
    let tools =
        $"O:{someUser}D:AI(A;OICIID;FA;;;BA)(A;OICIID;FA;;;SY)(A;OICIID;0x1200a9;;;BU)"
        + $"(A;ID;{modify};;;AU)(A;OICIIOID;{modify};;;AU)"
    let found = problems parentMask (folder tools)
    // The owner, and Authenticated Users (M); the inherit-only copy of that entry is not counted twice.
    Assert.Equal(2, found.Length)
    Assert.Contains(found, fun p -> p.StartsWith "owned by")
    Assert.Contains(found, fun p -> p.Contains "delete or rename")

[<Fact>]
let ``the drive root lets users add folders, which is fine for a parent but not for the install folder`` () =
    let root =
        $"O:{trustedInstaller}D:(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;0x1200a9;;;BU)"
        + $"(A;OICIIO;{modify};;;AU)(A;;0x4;;;AU)"
    Assert.Empty(problems parentMask (folder root))
    Assert.NotEmpty(problems folderMask (folder root))

[<Fact>]
let ``Program Files and ProgramData are fine as parents`` () =
    let programFiles =
        $"O:{trustedInstaller}D:PAI(A;;FA;;;{trustedInstaller})(A;CIIO;GA;;;{trustedInstaller})"
        + "(A;;0x1301bf;;;SY)(A;OICIIO;GA;;;SY)(A;;0x1301bf;;;BA)(A;OICIIO;GA;;;BA)"
        + "(A;;0x1200a9;;;BU)(A;OICIIO;GXGR;;;BU)(A;OICIIO;GA;;;CO)(A;;0x1200a9;;;AC)(A;OICIIO;GXGR;;;AC)"
    let programData =
        "O:SYD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICIIO;GA;;;CO)(A;OICI;0x1200a9;;;BU)(A;CI;0x116;;;BU)"
    Assert.Empty(problems parentMask (folder programFiles))
    Assert.Empty(problems parentMask (folder programData))

[<Fact>]
let ``the hold and pause folder is fine as a parent, not as a folder for the log or the executable`` () =
    Assert.Empty(problems parentMask (folder controlSddl))
    Assert.Contains(problems folderMask (folder controlSddl), fun p -> p.Contains "write or add files")

[<Fact>]
let ``a user who owns a folder is flagged even without write entries`` () =
    let owned = $"O:{someUser}D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)"
    Assert.Equal<string list>([ $"owned by {someUser}, who can change its permissions" ], problems folderMask (folder owned))

[<Fact>]
let ``deny entries are ignored, generic full control is flagged`` () =
    Assert.Empty(problems folderMask (folder "O:BAD:P(D;;FA;;;BU)(A;;FA;;;SY)"))
    Assert.NotEmpty(problems parentMask (folder "O:BAD:P(A;;GA;;;BU)"))

[<Fact>]
let ``parents go up to the drive root, nearest first`` () =
    Assert.Equal<string list>([ @"C:\Program Files"; @"C:\" ], parents @"C:\Program Files\DnsAutoSwitch\")

// On disk, in a temporary folder. The owner is the current user instead of Administrators, so no elevation.

let private rulesOf (path: string) =
    let security: FileSystemSecurity =
        if Directory.Exists path then FileSystemAclExtensions.GetAccessControl(DirectoryInfo path)
        else FileSystemAclExtensions.GetAccessControl(FileInfo path)
    security.GetAccessRules(true, true, typeof<SecurityIdentifier>) |> Seq.cast<FileSystemAccessRule> |> List.ofSeq

let private lockedForMe = $"O:{me}D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;{me})(A;OICI;0x1200a9;;;BU)"
let private inheritForMe = $"O:{me}D:AI"

[<Fact>]
let ``lockTree leaves only inherited entries below the folder, and none for Everyone`` () =
    use temp = new TempFolder()
    let a = temp.File("a.txt", "a")
    let b = temp.File(Path.Combine("sub", "b.txt"), "b")
    // An explicit entry that must go: Everyone may write b.txt.
    applySddl $"O:{me}D:(A;;FA;;;WD)" (FileInfo b)
    ok (lockTree lockedForMe inheritForMe temp.Path)
    let everyone = SecurityIdentifier(WellKnownSidType.WorldSid, null)
    for path in [ a; Path.GetDirectoryName b; b ] do
        let rules = rulesOf path
        Assert.NotEmpty rules
        Assert.All(rules, fun r -> Assert.True(r.IsInherited, $"{path}: explicit entry for {r.IdentityReference}"))
        Assert.DoesNotContain(rules, fun r -> r.IdentityReference = everyone)
    let users = SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)
    let usersOnB = rulesOf b |> List.filter (fun r -> r.IdentityReference = users)
    Assert.All(usersOnB, fun r -> Assert.Equal(FileSystemRights.ReadAndExecute ||| FileSystemRights.Synchronize, r.FileSystemRights))

[<Fact>]
let ``lockTree refuses a link inside the folder and does not touch what it points to`` () =
    use temp = new TempFolder()
    use target = new TempFolder()
    let outside = target.File("outside.txt", "x")
    let before = FileSystemAclExtensions.GetAccessControl(FileInfo outside).GetSecurityDescriptorSddlForm AccessControlSections.All
    let link = Path.Combine(temp.Path, "link")
    // A junction needs no privilege, unlike a symbolic link.
    let mklink = ProcessStartInfo("cmd.exe", UseShellExecute = false, CreateNoWindow = true)
    for arg in [ "/c"; "mklink"; "/J"; link; target.Path ] do mklink.ArgumentList.Add arg
    use p = Process.Start mklink
    p.WaitForExit()
    Assert.True(Directory.Exists link, "junction not created")
    let rootBefore = FileSystemAclExtensions.GetAccessControl(DirectoryInfo temp.Path).GetSecurityDescriptorSddlForm AccessControlSections.All
    Assert.True(Result.isError (lockTree lockedForMe inheritForMe temp.Path))
    // Refused before changing anything, the folder itself included.
    let rootAfter = FileSystemAclExtensions.GetAccessControl(DirectoryInfo temp.Path).GetSecurityDescriptorSddlForm AccessControlSections.All
    Assert.Equal(rootBefore, rootAfter)
    let after = FileSystemAclExtensions.GetAccessControl(FileInfo outside).GetSecurityDescriptorSddlForm AccessControlSections.All
    Assert.Equal(before, after)
    Assert.Contains(auditInstall temp.Path, fun f -> f.Path = link && f.Problem.Contains "link")

[<Fact>]
let ``hold and pause folder: users create, rewrite and delete their files, but cannot touch the log folder`` () =
    // Not elevated only: an administrator may do all of this.
    if not isElevated then
        use temp = new TempFolder()
        let mine (sddl: string) = sddl.Replace("O:BA", $"O:{me}")
        let control = Path.Combine(temp.Path, "DnsAutoSwitch")
        let logs = Path.Combine(control, "logs")
        // install runs elevated and can add logs after locking the folder; a plain user cannot, so logs first.
        Directory.CreateDirectory logs |> ignore
        applySddl (mine lockedSddl) (DirectoryInfo logs)
        applySddl (mine controlSddl) (DirectoryInfo control)
        let hold = Path.Combine(control, "DnsAutoSwitch.hold")
        File.WriteAllText(hold, "first")
        File.WriteAllText(hold, "second")
        File.Delete hold
        Assert.False(File.Exists hold)
        // Checked by the outcome: for folders .NET reports "access denied" as a plain IOException.
        let attempt (action: unit -> unit) =
            try action () with
            | :? UnauthorizedAccessException
            | :? IOException -> ()
        let moved = Path.Combine(control, "moved")
        let sub = Path.Combine(control, "sub")
        let log = Path.Combine(logs, "DnsAutoSwitch.log")
        attempt (fun () -> Directory.Delete logs)
        attempt (fun () -> Directory.Move(logs, moved))
        attempt (fun () -> Directory.CreateDirectory sub |> ignore)
        attempt (fun () -> File.WriteAllText(log, "planted"))
        Assert.True(Directory.Exists logs, "logs was deleted or renamed")
        Assert.False(Directory.Exists moved, "logs was renamed")
        Assert.False(Directory.Exists sub, "a subfolder was added")
        Assert.False(File.Exists log, "a file was planted in logs")
        // And install would not take over a folder an ordinary user owns.
        Assert.True(Result.isError (prepareFolder (mine controlSddl) control))

[<Fact>]
let ``prepareFolder creates the folder with its permissions, then refuses it if an ordinary user owns it`` () =
    use temp = new TempFolder()
    let path = Path.Combine(temp.Path, "DnsAutoSwitch")
    let sddl = controlSddl.Replace("O:BA", $"O:{me}")
    let result = prepareFolder sddl path
    // Created, with exactly the requested entries (nothing inherited from the temp folder). Creating a folder
    // does not set the "auto-inherited" flag (AI), which says nothing about the entries.
    let entries (security: DirectorySecurity) =
        security.GetSecurityDescriptorSddlForm(AccessControlSections.Access).Replace("D:PAI", "D:P")
    Assert.Equal(entries (folder sddl), entries (FileSystemAclExtensions.GetAccessControl(DirectoryInfo path)))
    // ...but refused, since its owner (the test account, standing in for Administrators) is not trusted.
    if not isElevated then Assert.True(Result.isError result)

[<Fact>]
let ``auditLog reports a missing log folder`` () =
    use temp = new TempFolder()
    let found = auditLog (Path.Combine(temp.Path, "missing", "DnsAutoSwitch.log"))
    Assert.Contains(found, fun f -> f.Problem.StartsWith "does not exist")
