# Maple Central (Command-Center)

A local build tool for QA to **download, extract, patch, launch and verify** game builds from one window. It started as a way to update and launch local MapleStory builds against internal servers, and it's grown into a full pipeline: pull the latest build straight from the Google Drive Build Repo, apply it to the right build tab, launch it from the Launcher with your chosen server and debug commands, and verify the Live install against the official checksums.

Every build tab, server list and executable list is user-configurable, so new projects can be added without a code change, a Jira ticket or engineering time.

Built with **C# / WPF on .NET 8** (WebView2 for Google sign-in and OpTool, SharpCompress for .zip/.7z extraction). Mainly used for MapleStory (GMS) and MapleStory Classic (CMS).

<!-- IMAGE: Main window overview (tab strip + storage bar) -->

-------

## Contents

1. [How the tool is laid out](#layout)
2. [Build Repo](#build-repo)
3. [Launcher](#launcher)
4. [Build Tabs](#build-tabs)
5. [Server Status](#server-status)
6. [Active Clients](#active-clients)
7. [OpTool](#optool)
8. [Maintenance Version Verification](#maintenance-version-verification)
9. [Settings](#settings)
10. [Where things are stored](#storage)
11. [Experience](#experience)

-------

<a name="layout"></a>
# How the tool is laid out

The normal flow is now **Build Repo → Launcher**:

1. **Build Repo** downloads a build from Google Drive and applies it to a build tab.
2. **Launcher** picks that build tab and launches it with the server, executable, debug commands and documents you want.

Because those two tabs drive everything, the individual build tabs (GMS, CMS, Live and any you add) can be **hidden** in Settings to keep the tab strip clean. They still show up in the Launcher and Build Repo dropdowns.

Permanent tabs: **Build Repo, Launcher, Server Status, OpTool, Maintenance Version Verification, Settings.** **Active Clients** shows up on its own whenever a client launched from the tool is running.

The bar at the bottom of the window tracks free disk space and changes color as you get closer to full.

<!-- IMAGE: Tab strip with build tabs hidden -->

-------

<a name="build-repo"></a>
# Build Repo

Browses the team's Google Drive build folder (**MapleStory (GMS) › Version › Build › Full / Partial**) through a signed-in Google session and drops the result straight into a build tab.

<!-- IMAGE: Build Repo tree expanded to a build -->

**Browsing**
- Click **(+)** on a version to see its builds, and on a build to see what's inside. Folders show file counts.
- Select a build, folder or single file to download it.
- Full folders / `Full_*.zip` are tagged **New Build**; Partial folders / `Partial_*.zip` are tagged **Patch**.
- **Batching:** tick several Full/Partial folders from the same build and they download and apply in order, Full first.

**Download for tab / After download**
- Pick the target build tab, then choose what happens after the download:
  - **New Build**: extracts and applies `Full_*.zip` like the New Build button (the current build is **deleted and replaced**), then applies everything else on top as a patch.
  - **Patch**: applies everything (Full included) like the Patch button. The current build is **kept** and only matching files get overwritten.
  - **Neither checked**: just downloads the files into the Current Build directory without extracting.
- The target tab's **Version Number** is stamped with the version the build came from, so its header and Documents folder follow along.
- Archives always land in a `BuildRepoDownloads` staging folder. If a download drops, **Resume** picks it up, and files that already finished are verified and reused instead of downloaded again.

<!-- IMAGE: Download options (New Build / Patch) + progress bar -->

**Local Files** (skip Google Drive entirely)
- **Extract local archives:** add .zip/.7z files from your PC, drag to reorder, and apply them in order. Optionally tick **Start with a New Build** to wipe the folder before file #1, and set a Version Number to stamp on finish. Source files are never moved or deleted.
- **Transfer existing build content:** move everything from one build tab to another (build files, Documents, Version Number). The Target is cleared first, and the Source is left empty with a "Last version moved to…" note.

<!-- IMAGE: Local Files – Extract mode -->
<!-- IMAGE: Local Files – Transfer mode -->

-------

<a name="launcher"></a>
# Launcher

One home for every build tab's launch tools. Pick a build from the **Build** dropdown (hidden tabs are marked *(hidden)*) and you see its Current Build path, Version, and these panels:

- **Launch:** choose an executable and server, then **Launch Build**.
- **Debug Command List:** turn on **Enable Debug Command List** and Add / Import / Edit / Delete commands. The list is saved per tab and copied into the build folder as `cmd_uidebug.txt` right before launch.
- **Existing Equipment Creation:** tick items from the equipment sheet to add their create commands to the debug list (Legendary, 3 potential + 3 bonus potential lines).
- **Documents:** drag & drop reference files for the current build. Documents are grouped by version, so a new version gets its own folder.

The Launcher points at the same data as each build tab, so a change in one shows up in the other. It remembers the last build you picked.

<!-- IMAGE: Launcher with a build selected -->
<!-- IMAGE: Debug Command List + Equipment Creation -->

-------

<a name="build-tabs"></a>
# Build Tabs

Each build tab (GMS, CMS, Live Service, plus any you add) owns one build folder and still has its own controls if you'd rather work without the Build Repo:

- **New Build:** wipes the Current Build folder and extracts the selected archive.
- **Patch:** keeps the build and copies changed files on top.
- **This build has additional files to apply:** queue a base archive plus patches and they chain automatically.
- **Pushed to Live** (Live and opted-in tabs): move a verified build, with its Documents and Version, from another build tab or a manual folder into Live. This keeps the exact public build on hand for later checks in test.

<!-- IMAGE: Build tab – New Build / Patch -->
<!-- IMAGE: Pushed to Live source picker -->

-------

<a name="server-status"></a>
# Server Status

Pings every internal server and shows Up / Down / Checking for each one, grouped by environment. You can **Refresh** a single server or **Refresh All**, and add, rename or delete custom servers. Group expand state is remembered between sessions.

<!-- IMAGE: Server Status -->

-------

<a name="active-clients"></a>
# Active Clients

Shows up when you launch a client from the tool and closes itself once every tracked client has exited. Each row lists the build, PID, server and version, with:
- **Bring to Front:** shows the window, and moves it to your current screen if it's on another monitor.
- **Close:** ends that client after you confirm.

<!-- IMAGE: Active Clients -->

-------

<a name="optool"></a>
# OpTool

An embedded browser for the QA OpTool web panels. Pick an environment and hit **Go**. You sign in on OpTool's own login page. The tool never stores or fills in credentials, and the session is private, so closing the app signs you out.

<!-- IMAGE: OpTool -->

-------

<a name="maintenance-version-verification"></a>
# Maintenance Version Verification

Pulls the release-version sheet (Release Version, Internal Upload Tag, Installed On), downloads the matching `.md5` checksum from Drive and verifies the Live build folder file by file. Results show per-file pass/fail with expected MD5s and a raw log. Extra files the `.md5` doesn't list are reported for information only and never fail a run.

<!-- IMAGE: Maintenance Verification results -->

-------

<a name="settings"></a>
# Settings

Nothing takes effect until you **Save Settings**. If you switch tabs with unsaved changes, the tool asks first.

- **Application Tabs:** drag to reorder, rename, hide, or **Change Icon** on the app's tabs. Settings itself can't be hidden.
- **Build Tabs:** **+ Add Build Tab**, rename, delete, reorder (this is the dropdown order in Launcher and Build Repo), set the build folder and Version Number, and toggle visibility.
- **Available Servers:** connect or disconnect built-in servers per tab, **+ Import Built-In Servers** from GMS/CMS/Live, or **+ Add Custom Server** (name, connection type, IP, port). A custom server is checked for a connection before it saves, so dead entries don't get in.
- **Available Executables:** every .exe found directly in the tab's build folder. Tick the ones that should appear in Select Executable. Your last pick is remembered.
- **Application:** Build Repo Google Drive folder URL, Maintenance Verification sheet URL, and **Run Command Center as Administrator**, which relaunches elevated with a UAC prompt on the next start.

<!-- IMAGE: Settings – Application Tabs / Build Tabs -->
<!-- IMAGE: Settings – Servers -->
<!-- IMAGE: Settings – Executables -->

-------

<a name="storage"></a>
# Where things are stored

Everything is under `%AppData%\CommandCenter`:
- `settings.json`: tabs, servers, executables and preferences
- Per-tab debug command lists (`cmd_uidebug.txt`)
- Custom tab icons
- WebView2 data for OpTool (no logins persist there)

-------

<a name="experience"></a>
# Experience

This started as a small in-studio tool for updating and launching Dev and Live builds. Each time a manual step slowed the team down, it got folded in: hunting through Drive for the right build, unzipping Full then Partial archives in the right order, keeping old Live builds organized, retyping server IPs, and checking checksums by hand. Today a tester can go from "a new build dropped" to "logged in on the right server with my debug commands loaded" without leaving the app. Because tabs, servers and executables are all user-configurable, it can take on new projects without me touching the code.
