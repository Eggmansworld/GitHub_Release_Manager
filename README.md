![AI-Assisted](https://img.shields.io/badge/AI--assisted-human--reviewed-blue)
## A Note on AI-Assisted Development

This project was built with AI as a code design and development tool. That
means an AI model helped draft, structure, and troubleshoot portions of the
code — but every line went through a human (me) for review, testing, and final
approval. The AI proposes; I dispose. Nothing here shipped without my sign-off,
and I take responsibility for what's in the repository.

This disclaimer isn't here to sell you on AI-assisted coding or argue the point.
It's here so you know exactly how this project was made. If the use of AI in the
development process is a dealbreaker for you, that's a completely valid stance —
this just isn't the project for you. No hard feelings; feel free to move along.

---

# Eggman's GitHub Release Manager

A multi-repo GitHub release downloader with metadata tracking, incremental updates, hash verification, auto-update scheduling, Discord notifications, and export utilities including RomVault DATs, PDF/TXT summaries, and CSV/JSON metadata. Built with Python + Tkinter. Supports optional drag-and-drop and PDF generation.

![Application Screenshot](https://github.com/user-attachments/assets/ec60b770-87ca-49e9-a079-607c6138e764)

---

## General Features

- Track multiple GitHub repositories at once
- **Incremental downloading** — only missing or changed assets are fetched, with resume support via HTTP Range requests
- Multi-threaded downloads with adjustable thread count
- **GitHub API token support** — anonymous access is capped at 60 requests/hour; a token raises this to 5,000/hour
- Export a tracked repo's file structure and hash info to a **RomVault-compatible XML DAT file** (the primary reason this app exists — for routine preservation and collection management via RomVault)
- Full **SHA-1 hashing** for integrity checks; **CRC32** is also computed when generating DAT files
- **Auto-update mode** — update all repos on a configurable timer (minimum 5 minutes)
- Per-repo and batch updates
- Automatic folder organization — one folder per repo, release tags become subfolders
- Metadata stored as JSON per repo, with additional CSV/JSON logs in a dedicated `_metadata` folder
- Optional **Discord webhook** notifications for update results
- Real-time **Activity Log** panel with color-coded output
- Modern dark UI theme with orange highlights

---

## Requirements

Python 3.10+ recommended (tested on 3.13).

```
pip install requests
pip install tkinterdnd2   # optional — drag-and-drop folder support
pip install reportlab     # optional — PDF summaries; falls back to TXT if missing
```

---

## Running the Program

```
python "Eggmans_GitHub_Release_Manager.py"
```

**Tips:**
- Place the script somewhere static and use the included batch files as shortcuts
- Set up a dedicated, static root download folder for your repos
- The app is fully portable — move it anywhere and it picks up where it left off

---

## Config Files

Both files are stored alongside the script and created automatically on first run.

| File | Contents |
|---|---|
| `Eggmans_GitHub_Release_Manager_config.json` | Root folder, webhook URL, API token, thread count, auto-update settings |
| `Eggmans_GitHub_Release_Manager_repos.json` | Per-repo metadata: last_checked, last_result, asset hashes, sizes, and paths |
| `tracked_repos.txt` | Plain list of tracked repos, updated automatically |

Deleting these files is safe — they regenerate after re-adding repos and rescanning or re-downloading.

---

## File Organization

```
<root_download_folder>\
    owner_repo\
        <tag1>\
        <tag2>\
        ...
        orphans\
    _metadata\
        json\
        csv\
        dat\
        pdf\
        logs\
```

- All metadata (JSON/CSV/DAT/PDF/TXT) is stored under `_metadata`, separate from downloaded files
- Legacy metadata found in the root folder is migrated automatically
- JSON/CSV files are regenerated if missing when rebuilding metadata
- All timestamps use UTC in the format `YYYY-MM-DD_HH_MM_SSZ`

---

## Dashboard Usage

### 1. Adding a Repo

Enter `owner/repo` in the GitHub repo box, or paste a full GitHub URL — it converts automatically.

```
Eggmansworld/Datfiles
https://github.com/Eggmansworld/Datfiles
```

Click **Add Repo** to add it to the Tracked Repos list.

### 2. Set a Root Download Folder

All repos download into subfolders here:

```
root_folder/owner_repo/tag/asset.ext
```

### 3. Updating a Repo

Select it in the Tracked Repos list and click **Update Selected Repo**, or right-click and choose **Update This Repo**.

### 4. Updating All Repos

Click **Update ALL Repos**. Repos are processed in oldest-checked-first order.

### 5. Treeview Controls

| Action | Result |
|---|---|
| Double-click | Open repo folder in Explorer |
| Right-click | Context menu (see below) |

**Right-click context menu:**
- Update This Repo
- Open on GitHub
- Open Repo Folder
- Force Hash Rescan
- Generate DAT File
- Generate PDF Summary
- Cleanup Orphans
- Scan Local Files (Rebuild Metadata)
- Remove From Tracker

### 6. Tracked Repos Dashboard Colors

| Color | Meaning |
|---|---|
| Green | Up to date |
| Orange | Assets downloaded (changes found) |
| Red | One or more failed downloads |
| Yellow | Not yet checked |

---

## Controls & Buttons

| Button | Action |
|---|---|
| **Update Selected Repo** | Fetch and download assets for the selected repo |
| **Update ALL Repos** | Batch update all tracked repos |
| **Pause / Resume** | Suspend and resume active downloads |
| **Safe Stop** | Finish current downloads, skip remaining |
| **Hard Stop** | Abort all threads immediately |

---

## Export & Maintenance Tools

### Generate DAT
Produces a RomVault-compatible XML `.dat` file. Game name = release tag. Handles nested files via relative paths. CRC32 and SHA-1 included per file.

### Generate PDF Summary
Requires `reportlab`. Falls back to a `.txt` file if not installed.

### Export DB
Saves the repos JSON database to a location of your choice for backup.

### Orphan Cleanup
Finds files in the repo folder not listed in metadata and moves them to `owner_repo/orphans/`.

### Force Hash Rescan
Recomputes SHA-1 for all known assets and updates the metadata database.

### Scan Local Files (Rebuild Metadata)
Indexes existing files on disk using top-level subfolders as release tags. Recreates missing JSON/CSV metadata files automatically.

---

## GitHub API Token

A token is recommended if you track many repos or use auto-update frequently.

| Access | Rate Limit |
|---|---|
| Anonymous | 60 requests/hour |
| Authenticated (token) | 5,000 requests/hour |

**To generate a token:**
Go to [https://github.com/settings/tokens/new](https://github.com/settings/tokens/new) and choose **Personal access token (classic)**. You do not need to check any scopes — a zero-scope token is sufficient for public repos.

Paste the token into the **GitHub token** field in the app. It saves to config automatically.

> **Note:** If you ever add private repos, you would need the `repo` scope at that point.

---

## Discord Notifications

Enter a webhook URL in the Discord webhook field. After each update the tool sends:

```
repo_name: update finished — OK=xx, failed=yy
```

---

## Auto-Update Mode

When enabled, the tool runs a full multi-repo update on a timer:
- Minimum interval: 5 minutes
- Scans all repos, downloads updates, logs activity, and sends Discord notifications
- Runs quietly in the background

> **Warning:** Avoid excessively short update intervals. If you see rate-limit errors in the Activity Log, increase your interval. Anonymous access is 60 requests/hour — use a token if you need more headroom.

---

## Notes

- Private repos are not currently supported
- DAT exports are suitable for RomVault; compatibility with other managers may vary
- PDF support requires `reportlab`; TXT fallback is always available

---

## Licensing

Original source code, scripts, tooling, and hand-authored documentation and metadata in this repository are licensed under the **MIT License**.

Archived game data, binaries, firmware, media assets, and other third-party materials are **not** covered by the MIT License and remain the property of their respective copyright holders.

See the `LICENSE` and `NOTICE` files for full details and scope clarification.

---

## Credits

Created for the preservation community by Eggman, with Claude's help turning ideas into code.

If you improve the script, feel free to share your changes back with the community.
