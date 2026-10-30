/// File-system permissions. The service runs as LocalSystem: whoever can change its executable, its libraries,
/// its configuration, or the folder it writes its log to, can run code as SYSTEM or point the LAN DNS elsewhere.
/// So all of those must be writable by SYSTEM, Administrators and TrustedInstaller only, and so must every
/// parent folder (renaming a parent swaps everything below it).
/// The audit functions only read permissions (check, install, service start); the others change them (install).
module DnsAutoSwitch.Security

open System
open System.IO
open System.Security.AccessControl
open System.Security.Principal

type Finding = { Path: string; Problem: string }

let private systemSid = SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)
let private administratorsSid = SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
let private trustedInstallerSid =
    SecurityIdentifier "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
/// OWNER RIGHTS: an entry for whoever owns the object. The owner is checked on its own.
let private ownerRightsSid = SecurityIdentifier "S-1-3-4"

/// The accounts that may change what the service runs.
let isTrusted (sid: SecurityIdentifier) =
    sid = systemSid || sid = administratorsSid || sid = trustedInstallerSid

// Access mask bits (winnt.h). On a folder, writeData means "add a file" and appendData "add a subfolder".
let private writeData = 0x2
let private appendData = 0x4
let private writeExtendedAttributes = 0x10
let private deleteChild = 0x40
let private delete = 0x10000
let private writeDac = 0x40000
let private writeOwner = 0x80000
let private genericAll = 0x10000000
let private genericWrite = 0x40000000

/// On a file the service runs, reads or writes: change it, delete it, or change who may.
let fileMask =
    writeData ||| appendData ||| writeExtendedAttributes ||| delete ||| writeDac ||| writeOwner ||| genericAll
    ||| genericWrite

/// On a folder holding such files: the same, plus deleting any child. Adding a file counts too: a DLL planted
/// next to the executable runs as SYSTEM.
let folderMask = fileMask ||| deleteChild

/// On a parent folder: renaming or deleting it swaps everything below. Adding entries to it is harmless.
let parentMask = deleteChild ||| delete ||| writeDac ||| writeOwner ||| genericAll

let private rightNames =
    [ writeData, "write or add files"
      appendData, "append or add folders"
      writeExtendedAttributes, "write extended attributes"
      deleteChild, "delete children"
      delete, "delete or rename"
      writeDac, "change permissions"
      writeOwner, "take ownership"
      genericAll, "full control"
      genericWrite, "write" ]

let private describe (rights: int) =
    rightNames
    |> List.filter (fun (bit, _) -> (rights &&& bit) <> 0)
    |> List.map snd
    |> String.concat ", "

let accountName (sid: SecurityIdentifier) =
    try (sid.Translate typeof<NTAccount>).Value with _ -> sid.Value

/// Problems in one security descriptor: an owner other than SYSTEM, Administrators or TrustedInstaller (an
/// owner can always rewrite the permissions), or an allow entry giving anybody else one of the rights in mask.
/// Inherit-only entries are skipped: they apply to the children, which are checked on their own.
let problems (mask: int) (security: FileSystemSecurity) : string list =
    let ownerProblem =
        match security.GetOwner typeof<SecurityIdentifier> with
        | :? SecurityIdentifier as owner when isTrusted owner -> []
        | :? SecurityIdentifier as owner -> [ $"owned by {accountName owner}, who can change its permissions" ]
        | _ -> [ "has no owner" ]
    let entryProblems =
        security.GetAccessRules(true, true, typeof<SecurityIdentifier>)
        |> Seq.cast<FileSystemAccessRule>
        |> Seq.choose (fun rule ->
            let sid = rule.IdentityReference :?> SecurityIdentifier
            let granted = int rule.FileSystemRights &&& mask
            let applies =
                rule.AccessControlType = AccessControlType.Allow
                && not (rule.PropagationFlags.HasFlag PropagationFlags.InheritOnly)
            if applies && granted <> 0 && not (isTrusted sid) && sid <> ownerRightsSid then
                Some $"{accountName sid} can {describe granted}"
            else
                None)
        |> List.ofSeq
    ownerProblem @ entryProblems

let private isLink (info: FileSystemInfo) =
    info.Attributes.HasFlag FileAttributes.ReparsePoint

let private readSecurity (info: FileSystemInfo) : FileSystemSecurity =
    let sections = AccessControlSections.Owner ||| AccessControlSections.Access
    match info with
    | :? DirectoryInfo as d -> FileSystemAclExtensions.GetAccessControl(d, sections) :> FileSystemSecurity
    | :? FileInfo as f -> FileSystemAclExtensions.GetAccessControl(f, sections) :> FileSystemSecurity
    | _ -> invalidArg (nameof info) "file or folder expected"

/// One file or folder. A link (reparse point) is reported, never followed.
let private inspect (mask: int) (info: FileSystemInfo) : Finding list =
    let finding problem = { Path = info.FullName; Problem = problem }
    try
        if not info.Exists then [ finding "does not exist" ]
        elif isLink info then [ finding "is a link (reparse point)" ]
        else problems mask (readSecurity info) |> List.map finding
    with ex ->
        [ finding $"permissions not readable ({ex.Message})" ]

/// The parent folders of a path, nearest first, up to the drive root.
let parents (path: string) : string list =
    let rec up (p: string) =
        match Path.GetDirectoryName p with
        | null
        | "" -> []
        | parent -> parent :: up parent
    up (Path.TrimEndingDirectorySeparator(Path.GetFullPath path))

/// The parent folders of folder: an ordinary user must not be able to rename or delete any of them.
let auditParents (folder: string) : Finding list =
    parents folder |> List.collect (fun p -> inspect parentMask (DirectoryInfo p))

/// Every file and folder below folder. Links are reported, not followed.
let rec private auditTree (folder: DirectoryInfo) : Finding list =
    match (try Ok(folder.GetFileSystemInfos()) with ex -> Error ex.Message) with
    | Error e -> [ { Path = folder.FullName; Problem = $"not readable ({e})" } ]
    | Ok entries ->
        entries
        |> List.ofArray
        |> List.collect (fun entry ->
            match entry with
            | :? DirectoryInfo as d when not (isLink d) -> inspect folderMask d @ auditTree d
            | _ -> inspect fileMask entry)

/// The install folder (executable, libraries, configuration), everything below it, and its parent folders.
let auditInstall (folder: string) : Finding list =
    let root = DirectoryInfo(Path.GetFullPath folder)
    let below = if root.Exists && not (isLink root) then auditTree root else []
    inspect folderMask root @ auditParents root.FullName @ below

/// The log folder, its parents and the log files. The service writes and rotates the log as SYSTEM: a user who
/// can plant a link there makes SYSTEM overwrite any file.
let auditLog (logFile: string) : Finding list =
    try
        let full = Path.GetFullPath logFile
        let folder = DirectoryInfo(Path.GetDirectoryName full)
        if not folder.Exists then
            [ { Path = folder.FullName; Problem = "does not exist (install creates the default log folder)" } ]
        else
            let files =
                [ full; full + ".1" ]
                |> List.choose (fun p ->
                    let f = FileInfo p
                    let d = DirectoryInfo p
                    if f.Exists then Some(f :> FileSystemInfo)
                    elif d.Exists then Some(d :> FileSystemInfo)
                    else None)
            inspect folderMask folder @ auditParents folder.FullName @ List.collect (inspect fileMask) files
    with ex ->
        [ { Path = logFile; Problem = $"not checkable ({ex.Message})" } ]

/// At most max findings as text lines, plus how many were left out.
let summarize (max: int) (findings: Finding list) : string list =
    [ for f in List.truncate max findings do
          yield $"{f.Path}: {f.Problem}"
      if findings.Length > max then
          yield $"... and {findings.Length - max} more" ]

// Permissions install applies, as SDDL. O:BA = owner Administrators. D:PAI = protected list: nothing is
// inherited from the parent. (A;OICI;FA;;;SY) = allow, inherited by files and subfolders, full access, SYSTEM.
// BA = Administrators, BU = Users, CO = CREATOR OWNER (whoever creates the file).
// 0x1200a9 = read & execute, 0x1200ab = read & execute + create files, 0x1301bf = modify.

/// Install folder and log folder: SYSTEM and Administrators full control, Users read & execute.
let lockedSddl = "O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)"

/// Hold and pause folder: as above, but Users may also create files in it (no subfolders), read its files, and
/// change or delete the files they created. They cannot delete or rename the folder, other people's files or
/// the log subfolder, so the folder can never be emptied and turned into a link.
let controlSddl =
    "O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;0x1200ab;;;BU)(A;OIIO;FR;;;BU)(A;OIIO;0x1301bf;;;CO)"

/// Everything below a locked folder: owner Administrators, no entries of its own, everything inherited.
let inheritSddl = "O:BAD:AI"

/// Replaces the owner and the permission list of one file or folder.
let applySddl (sddl: string) (info: FileSystemInfo) =
    let sections = AccessControlSections.Owner ||| AccessControlSections.Access
    match info with
    | :? DirectoryInfo as d ->
        let security = DirectorySecurity()
        security.SetSecurityDescriptorSddlForm(sddl, sections)
        FileSystemAclExtensions.SetAccessControl(d, security)
    | :? FileInfo as f ->
        let security = FileSecurity()
        security.SetSecurityDescriptorSddlForm(sddl, sections)
        FileSystemAclExtensions.SetAccessControl(f, security)
    | _ -> invalidArg (nameof info) "file or folder expected"

/// Links (reparse points) at or below folder, without following any.
let rec private linksIn (dir: DirectoryInfo) : string list =
    if isLink dir then
        [ dir.FullName ]
    else
        dir.GetFileSystemInfos()
        |> List.ofArray
        |> List.collect (fun entry ->
            match entry with
            | :? DirectoryInfo as d -> linksIn d
            | _ when isLink entry -> [ entry.FullName ]
            | _ -> [])

/// Changing a folder's permissions propagates them to everything below it, so a link there is refused before
/// anything is changed: whatever it points to must not get these permissions.
let private refuseLinks (dir: DirectoryInfo) : Result<unit, string> =
    match linksIn dir with
    | [] -> Ok()
    | link :: _ -> Error $"{link} is a link (reparse point): remove it, then run install again"

/// Locks a folder tree: folderSddl on the folder, childSddl on everything below. The folder goes first, so
/// nobody can add entries while the children are being reset. Links are refused, never followed.
let lockTree (folderSddl: string) (childSddl: string) (folder: string) : Result<unit, string> =
    let rec reset (dir: DirectoryInfo) =
        for entry in dir.GetFileSystemInfos() do
            // Checked again: one could have appeared before the folder was locked.
            if isLink entry then
                failwith $"{entry.FullName} is a link (reparse point): remove it, then run install again"
            applySddl childSddl entry
            match entry with
            | :? DirectoryInfo as d -> reset d
            | _ -> ()
    try
        let root = DirectoryInfo(Path.GetFullPath folder)
        refuseLinks root
        |> Result.map (fun () ->
            applySddl folderSddl root
            reset root)
    with ex ->
        Error ex.Message

/// Creates a folder with the given permissions, or applies them to an existing one. The folder must end up a
/// plain folder owned by SYSTEM, Administrators or TrustedInstaller: one an ordinary user created (even in the
/// moment between the check and the creation) could hide links, so it is refused rather than taken over.
let prepareFolder (sddl: string) (folder: string) : Result<unit, string> =
    try
        let dir = DirectoryInfo(Path.GetFullPath folder)
        if not dir.Exists && not (File.Exists dir.FullName) then
            let security = DirectorySecurity()
            security.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Owner ||| AccessControlSections.Access)
            // Created with its permissions in one call: it never has the parent's, not even for a moment.
            FileSystemAclExtensions.Create(dir, security)
            dir.Refresh()
        if File.Exists dir.FullName then
            Error $"{dir.FullName} is a file: delete it, then run install again"
        elif isLink dir then
            Error $"{dir.FullName} is a link (reparse point): remove it, then run install again"
        else
            match (readSecurity dir).GetOwner typeof<SecurityIdentifier> with
            | :? SecurityIdentifier as owner when isTrusted owner ->
                refuseLinks dir |> Result.map (fun () -> applySddl sddl dir)
            | :? SecurityIdentifier as owner ->
                Error $"{dir.FullName} belongs to {accountName owner}: delete it, then run install again"
            | _ -> Error $"{dir.FullName} has no owner: delete it, then run install again"
    with ex ->
        Error ex.Message
