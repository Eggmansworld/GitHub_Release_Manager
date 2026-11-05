import os
import sys
import time
import json
import csv
import hashlib
import threading
import queue
import datetime
import webbrowser

import requests
import tkinter as tk
from tkinter import ttk, messagebox, filedialog

# Optional drag-and-drop for folder if tkinterdnd2 is installed
try:
    from tkinterdnd2 import TkinterDnD, DND_FILES
    HAS_DND = True
except ImportError:
    TkinterDnD = tk.Tk
    HAS_DND = False

# Optional PDF support (reportlab)
try:
    from reportlab.lib.pagesizes import letter
    from reportlab.pdfgen import canvas
    HAS_REPORTLAB = True
except ImportError:
    HAS_REPORTLAB = False

# ==============================
# CONFIG
# ==============================

MAX_THREADS_DEFAULT = 5
RETRY_LIMIT = 3
CHUNK_SIZE = 1024 * 256  # 256 KB
USER_AGENT = "Eggman-GitHub-ReleaseManager/3.2"

if getattr(sys, "frozen", False):
    BASE_DIR = os.path.dirname(sys.executable)
else:
    BASE_DIR = os.path.dirname(os.path.abspath(__file__))

CONFIG_PATH = os.path.join(BASE_DIR, "Eggmans_GitHub_Release_Manager_config.json")
METADB_PATH = os.path.join(BASE_DIR, "Eggmans_GitHub_Release_Manager_repos.json")


# ==============================
# UTILITIES
# ==============================

def normalize_repo_folder_name(repo_key: str) -> str:
    """Convert 'owner/repo' into a safe folder like 'owner_repo'."""
    return repo_key.replace("/", "_").replace("\\", "_")

def ensure_metadata_dirs(root_outdir):
    """
    Ensure the metadata folder structure exists under the given root
    download folder and return the subfolder paths.
    """
    md_root = os.path.join(root_outdir, "_metadata")
    json_dir = os.path.join(md_root, "json")
    csv_dir = os.path.join(md_root, "csv")
    dat_dir = os.path.join(md_root, "dat")
    pdf_dir = os.path.join(md_root, "pdf")
    logs_dir = os.path.join(md_root, "logs")

    for d in (md_root, json_dir, csv_dir, dat_dir, pdf_dir, logs_dir):
        os.makedirs(d, exist_ok=True)

    return md_root, json_dir, csv_dir, dat_dir, pdf_dir, logs_dir

def migrate_legacy_metadata(root_outdir):
    """
    Move any old JSON/CSV/DAT/PDF/TXT/LOG files from the root download
    folder into the metadata subfolders.
    """
    if not root_outdir or not os.path.isdir(root_outdir):
        return

    md_root, json_dir, csv_dir, dat_dir, pdf_dir, logs_dir = ensure_metadata_dirs(root_outdir)

    for fname in os.listdir(root_outdir):
        lower = fname.lower()
        if lower.endswith(".json"):
            target_dir = json_dir
        elif lower.endswith(".csv"):
            target_dir = csv_dir
        elif lower.endswith(".dat"):
            target_dir = dat_dir
        elif lower.endswith(".pdf"):
            target_dir = pdf_dir
        elif lower.endswith(".txt") or lower.endswith(".log"):
            target_dir = logs_dir
        else:
            continue

        old_path = os.path.join(root_outdir, fname)
        if not os.path.isfile(old_path):
            continue

        new_path = os.path.join(target_dir, fname)
        try:
            os.replace(old_path, new_path)
        except OSError:
            pass

def api_get(url, token=None):
    headers = {"User-Agent": USER_AGENT}
    if token:
        headers["Authorization"] = f"token {token}"
    resp = requests.get(url, headers=headers, timeout=15)
    resp.raise_for_status()
    return resp


def get_all_releases(owner, repo, token=None):
    releases = []
    page = 1
    while True:
        url = f"https://api.github.com/repos/{owner}/{repo}/releases?per_page=100&page={page}"
        resp = api_get(url, token=token)
        batch = resp.json()
        if not batch:
            break
        releases.extend(batch)
        page += 1
    return releases


def compute_sha1(path):
    h = hashlib.sha1()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def send_discord_notification(webhook_url: str, content: str):
    if not webhook_url:
        return
    try:
        data = {"content": content}
        requests.post(webhook_url, json=data, timeout=10)
    except Exception:
        # fail silently
        pass


def generate_pdf_summary(repo_key, records, outdir):
    ts = datetime.datetime.now(datetime.UTC).strftime("%Y-%m-%d_%H_%M_%SZ")
    safe_repo = normalize_repo_folder_name(repo_key)

    # Place PDF/TXT inside metadata subfolders
    _, _, _, _, pdf_dir, logs_dir = ensure_metadata_dirs(outdir)
    pdf_path = os.path.join(pdf_dir, f"{safe_repo}_summary_{ts}.pdf")
    txtfallback = os.path.join(logs_dir, f"{safe_repo}_summary_{ts}.txt")

    if not HAS_REPORTLAB:
        # Fallback: TXT summary
        with open(txtfallback, "w", encoding="utf-8") as f:
            f.write(f"Summary for {repo_key} at {ts}\n\n")
            for r in records:
                f.write(
                    f"{r.get('tag','')} / {r.get('name','')} "
                    f"size={r.get('size_bytes',0)} sha1={r.get('sha1','')}\n"
                )
        return txtfallback

    c = canvas.Canvas(pdf_path, pagesize=letter)
    width, height = letter
    margin = 40
    y = height - margin

    c.setFont("Helvetica-Bold", 14)
    c.drawString(margin, y, f"GitHub Release Summary: {repo_key}")
    y -= 20
    c.setFont("Helvetica", 10)
    c.drawString(margin, y, f"Generated at {ts}")
    y -= 30

    c.setFont("Helvetica", 8)
    for r in records:
        line = (
            f"{r.get('tag','')} / {r.get('name','')} "
            f"size={r.get('size_bytes',0)} sha1={r.get('sha1','')}"
        )
        if y < margin:
            c.showPage()
            y = height - margin
            c.setFont("Helvetica", 8)
        c.drawString(margin, y, line)
        y -= 12

    c.save()
    return pdf_path

def generate_dat_for_repo(repo_key, repo_info, outdir):
    """
    Generate a RomVault-compatible XML DAT file.
    Game = top-level folder under each tag.
    Files in subfolders use relative paths.
    """
    today = datetime.datetime.now(datetime.UTC).strftime("%Y%m%d")
    safe_repo = normalize_repo_folder_name(repo_key)

    # Place DAT into metadata/dat
    _, _, _, dat_dir, _, _ = ensure_metadata_dirs(outdir)
    dat_path = os.path.join(dat_dir, f"{safe_repo}.dat")

    assets_db = repo_info.get("assets", {})

    import xml.sax.saxutils as xmlsafe

    with open(dat_path, "w", encoding="utf-8") as f:
        # Header
        f.write('<?xml version="1.0" encoding="UTF-8"?>\n')
        f.write('<datafile>\n')
        f.write('\t<header>\n')
        f.write(f'\t\t<name>{xmlsafe.escape(repo_key)}</name>\n')
        f.write('\t\t<description>retrieved from GitHub</description>\n')
        f.write(f'\t\t<version>{today}</version>\n')
        f.write('\t\t<author>Eggman</author>\n')
        f.write('\t\t<romvault/>\n')
        f.write('\t</header>\n')

        # Build mapping: game_name -> rom list
        game_map = {}

        for tag, files in assets_db.items():
            tag_folder = os.path.join(outdir, safe_repo, tag)

            for name, meta in files.items():
                full_path = meta.get("path")
                if not full_path or not os.path.isfile(full_path):
                    continue

                size = meta.get("size_bytes", 0)
                sha1 = (meta.get("sha1") or meta.get("sha256") or "").lower()

                # CRC32
                crc = meta.get("crc")
                if not crc:
                    try:
                        import zlib
                        with open(full_path, "rb") as fh:
                            data = fh.read()
                            crc = f"{zlib.crc32(data) & 0xffffffff:08x}"
                    except:
                        crc = "00000000"

                # Determine relative path
                try:
                    rel_path = os.path.relpath(full_path, tag_folder)
                except:
                    rel_path = name

                rel_path = rel_path.replace("\\", "/")
                parts = rel_path.split("/")

                # ----------------------------
                # FIXED LOGIC:
                # Game name = TAG
                # ----------------------------
                game_name = tag

                if len(parts) == 1:
                    rom_name = parts[0]                 # file in tag root
                else:
                    rom_name = "/".join(parts)          # nested path

                rom_entry = {
                    "name": rom_name,
                    "size": size,
                    "crc": crc,
                    "sha1": sha1,
                }

                game_map.setdefault(game_name, []).append(rom_entry)

        # Write out all games
        for game_name, roms in game_map.items():
            g_xml = xmlsafe.escape(game_name)
            f.write(f'\t<game name="{g_xml}">\n')
            for rom in roms:
                f.write(
                    f'\t\t<rom name="{rom["name"]}" size="{rom["size"]}" crc="{rom["crc"]}" sha1="{rom["sha1"]}"/>\n'
                )
            f.write('\t</game>\n')

        f.write('</datafile>\n')

    return dat_path


def find_orphans_for_repo(repo_key, root_outdir, repo_info):
    """Return list of paths that exist on disk but are not in assets DB."""
    repo_folder_name = normalize_repo_folder_name(repo_key)
    repo_root = os.path.join(root_outdir, repo_folder_name)
    assets_db = repo_info.get("assets", {})

    known_paths = set()
    for tag, files in assets_db.items():
        for name, info in files.items():
            p = info.get("path")
            if p:
                known_paths.add(os.path.abspath(p))

    orphans = []
    for dirpath, _, filenames in os.walk(repo_root):
        for fn in filenames:
            full = os.path.abspath(os.path.join(dirpath, fn))
            if full not in known_paths:
                orphans.append(full)
    return orphans


# ==============================
# DOWNLOAD WORKER
# ==============================

def download_asset(asset, folder, tag, result_queue, should_abort, should_pause):
    """
    Download single asset with resume + retry.
    - should_abort() -> True if HARD stop requested.
    - should_pause() -> True if PAUSE is active.
    Sends messages to result_queue:
    ("progress", tag, name, downloaded, total, speed_bytes, eta_seconds)
    ("done", tag, name, size, url, file_path, sha1 or None, success_bool, error_message or "")
    """
    url = asset["browser_download_url"]
    name = asset["name"]
    total_size = asset.get("size", 0) or 0
    file_path = os.path.join(folder, name)

    headers = {"User-Agent": USER_AGENT}
    mode = "wb"
    existing_size = 0

    # Resume logic
    if os.path.exists(file_path):
        existing_size = os.path.getsize(file_path)
        if 0 < existing_size < total_size:
            headers["Range"] = f"bytes={existing_size}-"
            mode = "ab"
        elif existing_size == total_size:
            # already complete, just hash and finish
            try:
                sha = compute_sha1(file_path)
                result_queue.put(
                    ("done", tag, name, total_size, url, file_path, sha, True, "")
                )
            except Exception as e:
                result_queue.put(
                    ("done", tag, name, total_size, url, file_path, None, False, str(e))
                )
            return
        else:
            mode = "wb"
            existing_size = 0

    retries = 0
    last_error = ""

    while retries < RETRY_LIMIT:
        if should_abort():
            last_error = "Hard stop requested"
            break
        try:
            with requests.get(url, headers=headers, stream=True, timeout=30) as r:
                if r.status_code not in (200, 206):
                    raise Exception(f"HTTP {r.status_code}")

                downloaded = existing_size
                start_time = time.time()

                with open(file_path, mode) as f:
                    for chunk in r.iter_content(chunk_size=CHUNK_SIZE):
                        if should_abort():
                            raise Exception("Hard stop requested")

                        # Pause handling
                        while should_pause() and not should_abort():
                            time.sleep(0.1)

                        if not chunk:
                            continue

                        f.write(chunk)
                        downloaded += len(chunk)
                        elapsed = max(time.time() - start_time, 0.1)
                        speed = downloaded / elapsed
                        remaining = max(total_size - downloaded, 0)
                        eta = remaining / max(speed, 1)

                        result_queue.put(
                            ("progress", tag, name, downloaded, total_size, speed, eta)
                        )

            # finished; hash
            sha = compute_sha1(file_path)
            result_queue.put(
                ("done", tag, name, total_size, url, file_path, sha, True, "")
            )
            return

        except Exception as e:
            last_error = str(e)
            retries += 1
            time.sleep(1)

    # fail case
    result_queue.put(
        ("done", tag, name, total_size, url, file_path, None, False, last_error)
    )


def worker_thread(task_queue, result_queue, stop_soft, stop_hard, pause_flag):
    while True:
        if stop_hard() or stop_soft():
            break
        item = task_queue.get()
        if item is None:
            break

        asset, folder, tag = item
        try:
            if stop_soft() or stop_hard():
                break
            download_asset(
                asset,
                folder,
                tag,
                result_queue,
                should_abort=stop_hard,
                should_pause=pause_flag,
            )
        finally:
            # no queue.join() in use, so no task_done needed
            pass


# ==============================
# GUI
# ==============================

class DownloaderGUI:
    def __init__(self, root):
        self.root = root
        self.root.title("Eggman's GitHub Release Manager")

        self.style = ttk.Style(self.root)
        self._init_theme()

        # queues / threading
        self.task_queue = queue.Queue()
        self.result_queue = queue.Queue()
        self.workers = []

        self.total_tasks = 0
        self.completed_tasks = 0
        self.downloading = False
        self.run_cancelled = False
        self.asset_records = []

        # STOP / PAUSE flags
        self.stop_soft = False
        self.stop_hard = False
        self.pause_flag = False

        # batch update
        self.batch_update_mode = False
        self.batch_repo_list = []
        self.batch_summary = []

        # Auto update
        self.auto_update_job = None

        # Flash animation for "please wait" state
        self._flash_job = None
        self._flash_visible = True

        # multi-repo DB
        self.repo_db = self._load_metadb()
        
        # Load tracked_repos.txt (if it exists)
        self._load_tracked_repo_list(os.path.join(BASE_DIR, "tracked_repos.txt"))

        # UI variables
        self.repo_var = tk.StringVar(value="evanbowman/skyland-beta")
        self.folder_var = tk.StringVar(value=os.path.join(os.getcwd(), "downloads"))
        self.max_threads_var = tk.IntVar(value=MAX_THREADS_DEFAULT)
        self.skip_existing_var = tk.BooleanVar(value=True)
        self.discord_webhook_var = tk.StringVar(value="")
        self.github_token_var = tk.StringVar(value="")
        self.auto_update_enabled_var = tk.BooleanVar(value=False)
        self.auto_update_interval_var = tk.IntVar(value=60)  # minutes
        self.progress_pct_var = tk.StringVar(value="")

        self.current_repo_key = None

        # Build UI
        self._build_menu()
        self._build_ui()
        self.root.minsize(960, 640)

        # General config
        self._load_config()
        self._refresh_repo_combo()
        self._refresh_repo_dashboard()
        self._write_tracked_repo_list()

        # Poll for worker messages
        self.root.after(200, self._update_ui_loop)
        # Schedule auto-update
        self._schedule_auto_update()

    # ---------- Theme ----------

    def _init_theme(self):
        bg = "#1e1e1e"
        fg = "#ff9900"  # bright orange
        entry_bg = "#252525"

        try:
            self.style.theme_use("clam")
        except Exception:
            pass

        self.root.configure(bg=bg)

        # Global
        self.style.configure(".", background=bg, foreground=fg)
        self.style.configure("TLabel", background=bg, foreground=fg)
        self.style.configure("TButton", padding=6, foreground=fg)

        # Entry
        self.style.configure(
            "TEntry",
            fieldbackground=entry_bg,
            foreground=fg,
            insertcolor=fg,
        )

        # Combobox (repo box)
        self.style.configure(
            "TCombobox",
            fieldbackground=entry_bg,
            background=entry_bg,
            foreground=fg,
            arrowsize=16,
        )
        
        self.style.map("TCombobox", fieldbackground=[("readonly", entry_bg)])

        # Spinbox
        self.style.configure(
            "TSpinbox",
            foreground=fg,
            fieldbackground=entry_bg,
            background=entry_bg,
        )

        # Progressbar
        self.style.configure(
            "Horizontal.TProgressbar",
            troughcolor="#333333",
            bordercolor="#333333",
            background="#ff9900",
            lightcolor="#ff9900",
            darkcolor="#ff9900",
        )

        # Treeview
        self.style.configure(
            "Treeview",
            background=entry_bg,
            fieldbackground=entry_bg,
            foreground=fg,
            rowheight=24,
        )
        self.style.map("Treeview", background=[("selected", "#444444")])

    # ---------- Meta DB ----------

    def _load_metadb(self):
        if not os.path.exists(METADB_PATH):
            return {"repos": {}}
        try:
            with open(METADB_PATH, "r", encoding="utf-8") as f:
                data = json.load(f)
            if "repos" not in data:
                data["repos"] = {}
            return data
        except Exception:
            return {"repos": {}}

    def _save_metadb(self):
        try:
            with open(METADB_PATH, "w", encoding="utf-8") as f:
                json.dump(self.repo_db, f, indent=2)
        except Exception:
            pass
            
    # ---------- Config ----------

    def _load_config(self):
        if not os.path.exists(CONFIG_PATH):
            repo_keys = sorted(self.repo_db.get("repos", {}).keys())
            if repo_keys:
                self.repo_var.set(repo_keys[0])
            return
        try:
            with open(CONFIG_PATH, "r", encoding="utf-8") as f:
                data = json.load(f)
            self.repo_var.set(data.get("repo", self.repo_var.get()))
            self.folder_var.set(data.get("root_folder", self.folder_var.get()))

            # Sweep any old JSON/CSV/DAT/PDF/TXT/LOG out of the root into metadata/
            root = self.folder_var.get().strip()
            if root:
                migrate_legacy_metadata(root)      
                
            self.max_threads_var.set(data.get("max_threads", MAX_THREADS_DEFAULT))
            self.skip_existing_var.set(bool(data.get("skip_existing", True)))
            self.discord_webhook_var.set(data.get("discord_webhook", ""))
            self.github_token_var.set(data.get("github_token", ""))
            self.auto_update_enabled_var.set(bool(data.get("auto_update_enabled", False)))
            self.auto_update_interval_var.set(int(data.get("auto_update_interval", 60)))
        except Exception:
            pass

    def _save_config(self):
        data = {
            "repo": self.repo_var.get().strip(),
            "root_folder": self.folder_var.get().strip(),
            "max_threads": int(self.max_threads_var.get()),
            "skip_existing": bool(self.skip_existing_var.get()),
            "discord_webhook": self.discord_webhook_var.get().strip(),
            "github_token": self.github_token_var.get().strip(),
            "auto_update_enabled": bool(self.auto_update_enabled_var.get()),
            "auto_update_interval": int(self.auto_update_interval_var.get()),
        }
        try:
            with open(CONFIG_PATH, "w", encoding="utf-8") as f:
                json.dump(data, f, indent=2)
        except Exception:
            pass

    # ---------- Menu ----------

    def _build_menu(self):
        menubar = tk.Menu(self.root)

        file_menu = tk.Menu(menubar, tearoff=0)
        file_menu.add_command(label="Export DB...", command=self._export_db)
        file_menu.add_separator()
        file_menu.add_command(label="Exit", command=self.root.quit)
        menubar.add_cascade(label="File", menu=file_menu)

        repos_menu = tk.Menu(menubar, tearoff=0)
        repos_menu.add_command(label="Update Selected Repo", command=self._update_selected_repo)
        repos_menu.add_command(label="Update ALL Repos", command=self._update_all_repos_clicked)
        repos_menu.add_separator()
        repos_menu.add_command(label="Open Selected Repo on GitHub", command=self._ctx_open_github)
        repos_menu.add_separator()
        repos_menu.add_command(label="Generate DAT (Selected Repo)", command=self._menu_generate_dat)
        repos_menu.add_command(label="Cleanup Orphans (Selected Repo)", command=self._menu_cleanup_orphans)
        menubar.add_cascade(label="Repos", menu=repos_menu)

        tools_menu = tk.Menu(menubar, tearoff=0)
        tools_menu.add_command(label="Generate PDF Summary (Selected Repo)", command=self._menu_generate_pdf)
        tools_menu.add_command(label="Force Hash Rescan (Selected Repo)", command=self._menu_force_hash_rescan)
        menubar.add_cascade(label="Tools", menu=tools_menu)

        help_menu = tk.Menu(menubar, tearoff=0)
        help_menu.add_command(label="About", command=self._show_about)
        menubar.add_cascade(label="Help", menu=help_menu)

        self.root.config(menu=menubar)

    # ---------- UI Layout ----------

    def _build_ui(self):
        pad = {"padx": 10, "pady": 4}

        # Top row: repo + root folder
        ttk.Label(self.root, text="GitHub repo (owner/repo):").grid(
            row=0, column=0, sticky="w", **pad
        )

        self.repo_combo = ttk.Combobox(
            self.root,
            textvariable=self.repo_var,
            width=40,
            state="normal",
            style="TEntry"      # <—— THIS IS THE FIX
        )

        self.repo_combo.grid(row=1, column=0, sticky="we", **pad)
        self.repo_combo.bind("<<ComboboxSelected>>", self._on_repo_selected)

        # Auto-convert pasted GitHub URL -> owner/repo
        def on_repo_change(*args):
            text = self.repo_var.get().strip()
            if "github.com" in text:
                try:
                    parts = text.split("github.com/")[1]
                    segments = parts.split("/")
                    if len(segments) >= 2:
                        owner = segments[0]
                        repo = segments[1]
                        self.repo_var.set(f"{owner}/{repo}")
                except Exception:
                    pass

        self.repo_var.trace_add("write", on_repo_change)

        # Add Repo button
        self.add_repo_btn = ttk.Button(
            self.root,
            text="Add Repo",
            command=self._add_repo_clicked
        )
        self.add_repo_btn.grid(row=1, column=2, padx=(0, 10), pady=4, sticky="w")

        ttk.Label(self.root, text="Root download folder:").grid(
            row=0, column=1, sticky="w", **pad
        )
        folder_frame = ttk.Frame(self.root)
        folder_frame.grid(row=1, column=1, sticky="we", **pad)

        self.folder_entry = ttk.Entry(folder_frame, textvariable=self.folder_var, width=34)
        self.folder_entry.pack(side="left", fill="x", expand=True)
        
        browse_btn = ttk.Button(folder_frame, text="Browse...", command=self._browse_folder)
        browse_btn.pack(side="left", padx=5)

        if HAS_DND:
            self.folder_entry.drop_target_register(DND_FILES)
            self.folder_entry.dnd_bind("<<Drop>>", self._on_folder_drop)

        # Options row
        options_frame = ttk.Frame(self.root)
        options_frame.grid(row=2, column=0, columnspan=3, sticky="we", **pad)

        ttk.Label(options_frame, text="Max threads:").pack(side="left")
        self.thread_spin = ttk.Spinbox(
            options_frame,
            from_=1,
            to=16,
            textvariable=self.max_threads_var,
            width=4,
            style="TSpinbox",
        )
        self.thread_spin.pack(side="left", padx=(4, 12))

        self.skip_check = ttk.Checkbutton(
            options_frame,
            text="Only download new/changed/missing assets",
            variable=self.skip_existing_var
        )
        self.skip_check.pack(side="left", padx=(0, 20))

        ttk.Label(options_frame, text="Auto-update (min):").pack(side="left")
        self.auto_spin = ttk.Spinbox(
            options_frame,
            from_=5,
            to=1440,
            textvariable=self.auto_update_interval_var,
            width=5,
            style="TSpinbox",
        )
        self.auto_spin.pack(side="left", padx=(4, 4))
        self.auto_check = ttk.Checkbutton(
            options_frame,
            text="Enable",
            variable=self.auto_update_enabled_var,
            command=self._schedule_auto_update
        )
        self.auto_check.pack(side="left", padx=(0, 10))

        # Discord webhook row
        webhook_frame = ttk.Frame(self.root)
        webhook_frame.grid(row=3, column=0, columnspan=3, sticky="we", **pad)
        ttk.Label(webhook_frame, text="Discord webhook:").pack(side="left")
        self.webhook_entry = ttk.Entry(webhook_frame, textvariable=self.discord_webhook_var, width=44)
        self.webhook_entry.pack(side="left", fill="x", expand=True, padx=(4, 16))
        ttk.Label(webhook_frame, text="GitHub token:").pack(side="left")
        self.token_entry = ttk.Entry(webhook_frame, textvariable=self.github_token_var, width=36, show="*")
        self.token_entry.pack(side="left", fill="x", expand=True, padx=(4, 0))

        # Buttons
        btn_frame = ttk.Frame(self.root)
        btn_frame.grid(row=4, column=0, columnspan=3, sticky="we", **pad)

        self.start_btn = ttk.Button(
            btn_frame, text="Update Selected Repo", command=self._update_selected_repo
        )
        self.start_btn.pack(side="left")

        self.update_all_btn = ttk.Button(
            btn_frame, text="Update ALL Repos", command=self._update_all_repos_clicked
        )
        self.update_all_btn.pack(side="left", padx=(10, 0))

        # Pause / Stop buttons
        self.pause_btn = ttk.Button(
            btn_frame, text="Pause", command=self._toggle_pause
        )
        self.pause_btn.pack(side="left", padx=(10, 0))

        self.stop_safe_btn = ttk.Button(
            btn_frame, text="Safe Stop", command=self._stop_safe
        )
        self.stop_safe_btn.pack(side="left", padx=(10, 0))

        self.stop_hard_btn = ttk.Button(
            btn_frame, text="Hard Stop", command=self._stop_hard
        )
        self.stop_hard_btn.pack(side="left", padx=(10, 0))

        # Progress / status
        progress_frame = ttk.Frame(self.root)
        progress_frame.grid(row=5, column=0, columnspan=3, sticky="we", padx=10, pady=(4, 2))
        progress_frame.grid_columnconfigure(0, weight=1)

        self.progress = ttk.Progressbar(
            progress_frame, orient="horizontal", mode="determinate"
        )
        self.progress.grid(row=0, column=0, sticky="we")

        self.progress_pct_label = ttk.Label(
            progress_frame, textvariable=self.progress_pct_var, width=10, anchor="e"
        )
        self.progress_pct_label.grid(row=0, column=1, padx=(6, 0))

        self.info_label = ttk.Label(self.root, text="")
        self.info_label.grid(row=6, column=0, columnspan=3, sticky="w", padx=10)

        self.status_label = ttk.Label(self.root, text="")
        self.status_label.grid(row=7, column=0, columnspan=3, sticky="w", padx=10, pady=(0, 6))

        # Bottom: repo dashboard + log
        bottom_frame = ttk.Frame(self.root)
        bottom_frame.grid(row=8, column=0, columnspan=3, sticky="nsew", padx=10, pady=(4, 10))

        self.root.grid_rowconfigure(8, weight=1)
        for col in range(3):
            self.root.grid_columnconfigure(col, weight=1)
        bottom_frame.grid_rowconfigure(0, weight=1)
        bottom_frame.grid_columnconfigure(0, weight=1)
        bottom_frame.grid_columnconfigure(1, weight=1)

        # Repo dashboard
        repo_frame = ttk.LabelFrame(bottom_frame, text="Tracked Repos")
        repo_frame.grid(row=0, column=0, sticky="nsew", padx=(0, 5))
        repo_frame.grid_rowconfigure(0, weight=1)
        repo_frame.grid_columnconfigure(0, weight=1)

        self.repo_tree = ttk.Treeview(
            repo_frame,
            columns=("repo", "last_checked", "assets", "last_result"),
            show="headings",
            height=8,
        )
        self.repo_tree.heading("repo", text="Repository")
        self.repo_tree.heading("last_checked", text="Last Checked (UTC)")
        self.repo_tree.heading("assets", text="Assets")
        self.repo_tree.heading("last_result", text="Last Result")
        self.repo_tree.column("repo", width=210, anchor="w")
        self.repo_tree.column("last_checked", width=140, anchor="w")
        self.repo_tree.column("assets", width=110, anchor="center")
        self.repo_tree.column("last_result", width=180, anchor="w")
        self.repo_tree.grid(row=0, column=0, sticky="nsew")

        repo_scroll = ttk.Scrollbar(repo_frame, orient="vertical", command=self.repo_tree.yview)
        self.repo_tree.configure(yscrollcommand=repo_scroll.set)
        repo_scroll.grid(row=0, column=1, sticky="ns")

        # Colour tags
        self.repo_tree.tag_configure("green",   foreground="#99ff99")   # up to date
        self.repo_tree.tag_configure("orange",  foreground="#ff9900")   # OK > 0 (changes downloaded)
        self.repo_tree.tag_configure("red",     foreground="#ff6666")   # Failed > 0
        self.repo_tree.tag_configure("default", foreground="#ffcc66")   # not yet checked

        self.repo_menu = tk.Menu(self.root, tearoff=0)
        self.repo_menu.add_command(label="Update This Repo", command=self._ctx_update_repo)
        self.repo_menu.add_command(label="Open on GitHub", command=self._ctx_open_github)
        self.repo_menu.add_separator()
        self.repo_menu.add_command(label="Open Repo Folder", command=self._ctx_open_folder)
        self.repo_menu.add_command(label="Force Hash Rescan", command=self._ctx_hash_rescan)
        self.repo_menu.add_command(label="Generate DAT File", command=self._ctx_generate_dat)
        self.repo_menu.add_command(label="Generate PDF Summary", command=self._ctx_generate_pdf)
        self.repo_menu.add_command(label="Cleanup Orphans", command=self._ctx_cleanup_orphans)
        self.repo_menu.add_command(label="Scan Local Files (Rebuild Metadata)", command=self._ctx_scan_local)
        self.repo_menu.add_separator()
        self.repo_menu.add_command(label="Remove From Tracker", command=self._ctx_remove_repo)

        self.repo_tree.bind("<Double-1>", self._on_repo_tree_double_click)
        self.repo_tree.bind("<Button-3>", self._on_repo_tree_right_click)

        # Activity log
        log_frame = ttk.LabelFrame(bottom_frame, text="Activity Log")
        log_frame.grid(row=0, column=1, sticky="nsew", padx=(5, 0))
        log_frame.grid_rowconfigure(0, weight=1)
        log_frame.grid_columnconfigure(0, weight=1)

        self.log_text = tk.Text(log_frame, height=8, wrap="none", bg="#151515", fg="#ff9900")
        self.log_text.grid(row=0, column=0, sticky="nsew")

        # Log colour tags
        self.log_text.tag_configure("log_error",   foreground="#ff6666")
        self.log_text.tag_configure("log_success",  foreground="#99ff99")
        self.log_text.tag_configure("log_warn",    foreground="#ffcc66")
        self.log_text.tag_configure("log_default", foreground="#ff9900")

        log_scroll = ttk.Scrollbar(log_frame, orient="vertical", command=self.log_text.yview)
        self.log_text.configure(yscrollcommand=log_scroll.set)
        log_scroll.grid(row=0, column=1, sticky="ns")

    # ---------- Logging ----------

    def log(self, msg):
        ts = datetime.datetime.now().strftime("%H:%M:%S")
        line = f"[{ts}] {msg}\n"

        # Pick colour tag based on content
        lo = msg.lower()
        if any(k in lo for k in ("error", "failed", "fail", "exception")):
            tag = "log_error"
        elif any(k in lo for k in ("finished", "complete", "success", "ok=", "saved", "generated", "added")):
            tag = "log_success"
        elif any(k in lo for k in ("warn", "missing", "skip", "cancel", "stop", "abort")):
            tag = "log_warn"
        else:
            tag = "log_default"

        self.log_text.insert("end", line, tag)
        self.log_text.see("end")

        # Trim log: keep at most 500 lines
        line_count = int(self.log_text.index("end-1c").split(".")[0])
        if line_count > 500:
            self.log_text.delete("1.0", f"{line_count - 400}.0")

    # ---------- Flash status ----------

    def _start_flash(self, msg):
        """Show a flashing message on info_label. Stops automatically when _stop_flash() is called."""
        self._stop_flash()  # cancel any existing flash first
        self.info_label.config(text=msg)
        self._flash_visible = True

        def _toggle():
            if self._flash_job is None:
                # Stopped — restore label to fully visible with default colour
                self.info_label.configure(foreground="#ff9900")
                return
            self._flash_visible = not self._flash_visible
            colour = "#ff9900" if self._flash_visible else "#1e1e1e"
            self.info_label.configure(foreground=colour)
            self._flash_job = self.root.after(500, _toggle)

        self._flash_job = self.root.after(500, _toggle)
        # Force the UI to actually paint the label before the blocking API call
        self.root.update_idletasks()

    def _stop_flash(self):
        """Cancel flashing and restore normal label appearance."""
        if self._flash_job is not None:
            self.root.after_cancel(self._flash_job)
            self._flash_job = None
        self.info_label.configure(foreground="#ff9900")

    # ---------- Repo dashboard ----------

    def _refresh_repo_combo(self):
        repo_keys = sorted(self.repo_db.get("repos", {}).keys())
        self.repo_combo["values"] = repo_keys

    def _refresh_repo_dashboard(self):
        for item in self.repo_tree.get_children():
            self.repo_tree.delete(item)
        for repo_key, info in self.repo_db.get("repos", {}).items():
            last_checked = info.get("last_checked") or ""
            last_result = info.get("last_result") or ""

            # Build assets summary
            assets_db = info.get("assets", {})
            total_files = sum(len(v) for v in assets_db.values())
            total_bytes = sum(
                m.get("size_bytes", 0)
                for tag_files in assets_db.values()
                for m in tag_files.values()
            )
            if total_bytes >= 1024 ** 3:
                size_str = f"{total_bytes / 1024**3:.1f} GB"
            elif total_bytes >= 1024 ** 2:
                size_str = f"{total_bytes / 1024**2:.1f} MB"
            elif total_bytes > 0:
                size_str = f"{total_bytes / 1024:.1f} KB"
            else:
                size_str = ""
            assets_str = f"{total_files} files" + (f" / {size_str}" if size_str else "") if total_files else ""

            # determine colour tag
            lr = last_result or ""
            color_tag = "default"
            if "Up to date" in lr:
                color_tag = "green"
            elif "Failed=" in lr:
                try:
                    failed_n = int(lr.split("Failed=")[1].split(",")[0].strip())
                except Exception:
                    failed_n = 1
                color_tag = "red" if failed_n > 0 else "green"
            elif "OK=" in lr:
                try:
                    ok_n = int(lr.split("OK=")[1].split(",")[0].strip())
                except Exception:
                    ok_n = 1
                color_tag = "orange" if ok_n > 0 else "green"

            self.repo_tree.insert(
                "",
                "end",
                iid=repo_key,
                values=(repo_key, last_checked, assets_str, last_result),
                tags=(color_tag,),
            )

    # ---------- Treeview interactions ----------

    def _on_repo_tree_double_click(self, event):
        item = self.repo_tree.focus()
        if not item:
            return

        self.repo_var.set(item)

        # open folder for that repo
        root_outdir = self.folder_var.get().strip()
        repo_folder = os.path.join(root_outdir, normalize_repo_folder_name(item))

        if os.path.isdir(repo_folder):
            try:
                os.startfile(repo_folder)  # Windows
            except Exception as e:
                self.log(f"Error opening folder: {e}")
        else:
            messagebox.showinfo("Folder missing", f"{repo_folder}\n\nDoes not exist.")

    def _on_repo_tree_right_click(self, event):
        item = self.repo_tree.identify_row(event.y)
        if not item:
            return
        self.repo_tree.selection_set(item)
        self.repo_var.set(item)
        self.repo_menu.post(event.x_root, event.y_root)

    # ---------- Context menu helpers ----------

    def _ctx_update_repo(self):
        repo = self.repo_var.get().strip()
        if not repo:
            return
        if self.downloading or self.batch_update_mode:
            messagebox.showwarning("Busy", "Downloads already in progress.")
            return
        self._start_flash("Processing request, please wait...")
        self._start_repo_update(repo)

    def _ctx_open_github(self):
        repo = self.repo_var.get().strip()
        if not repo:
            return
        url = f"https://github.com/{repo}/releases"
        try:
            webbrowser.open(url)
        except Exception as e:
            self.log(f"Error opening browser: {e}")

    def _ctx_open_folder(self):
        repo = self.repo_var.get().strip()
        if not repo:
            return
        root_outdir = self.folder_var.get().strip()
        folder = os.path.join(root_outdir, normalize_repo_folder_name(repo))
        if os.path.isdir(folder):
            try:
                os.startfile(folder)
            except Exception as e:
                self.log(f"Error opening folder: {e}")
        else:
            messagebox.showinfo("Missing", "Folder does not exist.")

    def _ctx_hash_rescan(self):
        self._menu_force_hash_rescan()

    def _ctx_scan_local(self):
        self._menu_scan_local_rebuild_metadata()

    def _ctx_generate_dat(self):
        self._menu_generate_dat()

    def _ctx_generate_pdf(self):
        self._menu_generate_pdf()

    def _ctx_cleanup_orphans(self):
        self._menu_cleanup_orphans()

    def _ctx_remove_repo(self):
        repo = self.repo_var.get().strip()
        if not repo:
            return
        if not messagebox.askyesno(
            "Remove Repo",
            f"Remove {repo} from tracked list?\n(This will NOT delete any files.)",
        ):
            return
        self.repo_db["repos"].pop(repo, None)
        self._save_metadb()
        self._write_tracked_repo_list()
        self._refresh_repo_combo()
        self._refresh_repo_dashboard()
        self.log(f"Removed repo: {repo}")

    # ===========================================
    # TRACKED REPO LIST HELPERS
    # ===========================================
    def _load_tracked_repo_list(self, path):
        """Load tracked_repos.txt on startup."""
        if os.path.exists(path):
            try:
                with open(path, "r", encoding="utf-8") as f:
                    for line in f:
                        repo = line.strip()
                        if repo and "/" in repo:
                            if repo not in self.repo_db.get("repos", {}):
                                self.repo_db["repos"][repo] = {
                                    "last_checked": None,
                                    "last_result": "",
                                    "assets": {},
                                }
            except Exception:
                pass

    def _write_tracked_repo_list(self):
        """Write tracked_repos.txt next to the script whenever repos change."""
        try:
            list_path = os.path.join(BASE_DIR, "tracked_repos.txt")
            with open(list_path, "w", encoding="utf-8") as f:
                for repo in sorted(self.repo_db.get("repos", {}).keys()):
                    f.write(repo + "\n")
        except Exception as e:
            self.log(f"ERROR writing tracked_repos.txt: {e}")

    # ---------- Misc handlers ----------

    def _on_repo_selected(self, event):
        pass

    def _browse_folder(self):
            path = filedialog.askdirectory(
                title="Select root download folder", initialdir=self.folder_var.get()
            )
            if path:
                self.folder_var.set(path)
                # Build metadata dirs and migrate any stray metadata files
                migrate_legacy_metadata(path)
                self._save_config()

    def _on_folder_drop(self, event):
        raw = event.data
        if " " in raw and not raw.startswith("{"):
            raw = raw.split()[0]
        raw = raw.strip("{}")
        if os.path.isdir(raw):
            self.folder_var.set(raw)
            self._save_config()

    def _export_db(self):
        if not self.repo_db.get("repos"):
            messagebox.showinfo("No data", "No repo metadata to export yet.")
            return
        out_path = filedialog.asksaveasfilename(
            title="Export metadata DB",
            defaultextension=".json",
            filetypes=[("JSON files", "*.json"), ("All files", "*.*")],
        )
        if not out_path:
            return
        try:
            with open(out_path, "w", encoding="utf-8") as f:
                json.dump(self.repo_db, f, indent=2)
            messagebox.showinfo("Exported", f"Metadata exported to:\n{out_path}")
        except Exception as e:
            messagebox.showerror("Error", f"Failed to export DB:\n{e}")

    def _show_about(self):
        messagebox.showinfo(
            "About",
            "Eggman's GitHub Release Manager\n"
            "Multi-repo incremental downloader with metadata,\n"
            "auto-update, Discord alerts, DAT/PDF export, and more.",
        )



    # ---------- Add Repo ----------

    def _add_repo_clicked(self):
        repo_text = self.repo_var.get().strip()
        if "/" not in repo_text:
            messagebox.showerror("Invalid", "Enter repo in owner/repo format.")
            return
        if "repos" not in self.repo_db:
            self.repo_db["repos"] = {}
        if repo_text not in self.repo_db["repos"]:
            self.repo_db["repos"][repo_text] = {
                "last_checked": None,
                "last_result": "",
                "assets": {},
            }
            self._save_metadb()
            self._refresh_repo_combo()
            self._refresh_repo_dashboard()
            self._write_tracked_repo_list()
            messagebox.showinfo("Added", f"Repo added:\n{repo_text}")
            self.log(f"Added repo {repo_text}")

        else:
            messagebox.showinfo("Exists", f"Repo already tracked:\n{repo_text}")

    # ---------- Auto-update ----------

    def _schedule_auto_update(self):
        if self.auto_update_job is not None:
            self.root.after_cancel(self.auto_update_job)
            self.auto_update_job = None

        self._save_config()

        if not self.auto_update_enabled_var.get():
            return

        try:
            interval_min = int(self.auto_update_interval_var.get())
        except ValueError:
            interval_min = 60
            self.auto_update_interval_var.set(interval_min)

        interval_min = max(5, interval_min)
        interval_ms = interval_min * 60 * 1000

        def auto_job():
            if not self.downloading and not self.batch_update_mode:
                self.log(f"Auto-update triggered ({interval_min} min).")
                self._start_batch_update(auto=True)
            self._schedule_auto_update()

        self.auto_update_job = self.root.after(interval_ms, auto_job)

    # ---------- Core update flows ----------

    def _update_selected_repo(self):
        if self.downloading or self.batch_update_mode:
            messagebox.showwarning("Busy", "Downloads already in progress.")
            return
        repo_text = self.repo_var.get().strip()
        if "/" not in repo_text:
            messagebox.showerror("Error", "Repository must be in the form owner/repo.")
            return
        self._start_flash("Processing request, please wait...")
        self._start_repo_update(repo_text)

    def _update_all_repos_clicked(self):
        if self.downloading or self.batch_update_mode:
            messagebox.showwarning("Busy", "Downloads already in progress.")
            return
        self._start_flash("Processing request, please wait...")
        self._start_batch_update(auto=False)

    def _start_batch_update(self, auto=False):
        repo_list = sorted(self.repo_db.get("repos", {}).keys())
        if not repo_list:
            messagebox.showinfo("No repos", "No repos are tracked yet.")
            return

        # prioritize oldest last_checked
        def sort_key(k):
            info = self.repo_db["repos"].get(k, {})
            lc = info.get("last_checked")
            if not lc:
                return datetime.datetime.min
            try:
                return datetime.datetime.fromisoformat(lc.replace("Z", ""))
            except Exception:
                return datetime.datetime.min

        repo_list.sort(key=sort_key)

        if not auto:
            ok = messagebox.askyesno(
                "Update ALL repos?",
                f"This will scan and update {len(repo_list)} repos.\n\nProceed?",
            )
            if not ok:
                self._stop_flash()
                return

        self.batch_update_mode = True
        self.batch_repo_list = list(repo_list)
        self.batch_summary = []
        self.log(f"Starting batch update for {len(self.batch_repo_list)} repos...")
        self._start_next_repo_in_batch()

    def _start_next_repo_in_batch(self):
        if not self.batch_repo_list:
            # batch done
            self.batch_update_mode = False
            self.current_repo_key = None
            self.info_label.config(text="Batch update complete.")
            self.progress["value"] = 0
            self.start_btn.config(state="normal")
            self.update_all_btn.config(state="normal")
            self._refresh_repo_dashboard()

            if self.batch_summary:
                msg = "Batch update results:\n\n" + "\n".join(self.batch_summary)
                self.log("Batch update finished.")
                messagebox.showinfo("Batch update", msg)
            return

        repo_text = self.batch_repo_list.pop(0)
        self.repo_var.set(repo_text)
        self.log(f"Batch: updating {repo_text}...")
        self._start_flash("Processing request, please wait...")
        self._start_repo_update(repo_text)

    def _start_repo_update(self, repo_text):
        root_outdir = self.folder_var.get().strip()
        if not root_outdir:
            self._stop_flash()
            messagebox.showerror("Error", "Please specify a root download folder.")
            return

        try:
            os.makedirs(root_outdir, exist_ok=True)
        except Exception as e:
            self._stop_flash()
            messagebox.showerror("Error", f"Cannot create root folder:\n{e}")
            return

        try:
            max_threads = int(self.max_threads_var.get())
            if max_threads < 1:
                max_threads = 1
        except ValueError:
            max_threads = MAX_THREADS_DEFAULT
            self.max_threads_var.set(max_threads)

        self._save_config()
        self.current_repo_key = repo_text
        self._ensure_repo_in_db(repo_text)
        self._write_tracked_repo_list()

        # Disable buttons now so the user can't queue a second update
        # while the API call is in flight.
        self.start_btn.config(state="disabled")
        self.update_all_btn.config(state="disabled")

        # Ensure the flash is visible and running before we hand off to the thread.
        self._start_flash("Processing request, please wait...")

        # Run the blocking GitHub API call in a background thread so the
        # main event loop stays alive and the flash keeps animating.
        def _fetch():
            try:
                result = self._compute_repo_tasks(repo_text)
            except Exception as e:
                result = ([], 0)
            self.root.after(0, lambda: self._on_tasks_computed(repo_text, result, max_threads))

        threading.Thread(target=_fetch, daemon=True).start()

    def _on_tasks_computed(self, repo_text, result, max_threads):
        """Called on the main thread once _compute_repo_tasks() has finished."""
        tasks, total_seen = result

        if total_seen == 0:
            self._stop_flash()
            self.start_btn.config(state="normal")
            self.update_all_btn.config(state="normal")
            self.info_label.config(text="No release assets found.")
            self.log(f"{repo_text}: no assets found.")
            if self.batch_update_mode:
                self.batch_summary.append(f"{repo_text}: no assets found")
                self._start_next_repo_in_batch()
            return

        if not tasks and self.skip_existing_var.get():
            self._stop_flash()
            self.start_btn.config(state="normal")
            self.update_all_btn.config(state="normal")
            self.info_label.config(text="Already up to date.")
            self.log(f"{repo_text}: already up to date.")
            self._update_repo_last_result(repo_text, "Up to date")
            self._update_repo_timestamp(repo_text)
            if self.batch_update_mode:
                self.batch_summary.append(f"{repo_text}: up to date")
                self._start_next_repo_in_batch()
            return

        # Ready to download
        self._stop_flash()
        self.downloading = True
        self.run_cancelled = False
        self.stop_soft = False
        self.stop_hard = False
        self.pause_flag = False
        self.pause_btn.config(text="Pause")

        self.asset_records = []
        self.total_tasks = len(tasks)
        self.completed_tasks = 0
        self.progress["maximum"] = self.total_tasks
        self.progress["value"] = 0
        self.status_label.config(text="")
        # Buttons already disabled above; they stay disabled until _finalize_downloads

        self.info_label.config(
            text=f"{repo_text}: downloading {self.total_tasks} asset(s) (seen {total_seen})..."
        )
        self.log(f"{repo_text}: starting {self.total_tasks} asset(s)...")

        # Clear result queue
        while not self.result_queue.empty():
            try:
                self.result_queue.get_nowait()
            except queue.Empty:
                break

        # Enqueue tasks + sentinels
        for t in tasks:
            self.task_queue.put(t)
        for _ in range(max_threads):
            self.task_queue.put(None)

        # Worker threads
        self.workers = []
        for _ in range(max_threads):
            t = threading.Thread(
                target=worker_thread,
                args=(
                    self.task_queue,
                    self.result_queue,
                    lambda: self.stop_soft,
                    lambda: self.stop_hard,
                    lambda: self.pause_flag,
                ),
                daemon=True,
            )
            self.workers.append(t)
            t.start()

        # mark last_checked
        self._update_repo_timestamp(repo_text)

        # Preserve existing repo entry if present
        if repo_text not in self.repo_db["repos"]:
            self.repo_db["repos"][repo_text] = {}

        repo_info = self.repo_db["repos"][repo_text]

        # Normalize required fields
        repo_info.setdefault("last_checked", None)
        repo_info.setdefault("last_result", "")
        repo_info.setdefault("assets", {})

        self._save_metadb()
        self._refresh_repo_combo()
        self._refresh_repo_dashboard()

    def _compute_repo_tasks(self, repo_text):
        """Return (tasks, total_assets_seen) for the given repo."""
        owner, repo = repo_text.split("/", 1)
        root_outdir = self.folder_var.get().strip()

        repo_folder_name = normalize_repo_folder_name(repo_text)
        repo_root = os.path.join(root_outdir, repo_folder_name)
        os.makedirs(repo_root, exist_ok=True)

        repo_info = self.repo_db["repos"].setdefault(
            repo_text, {"last_checked": None, "last_result": "", "assets": {}}
        )
        assets_db = repo_info.setdefault("assets", {})

        try:
            token = self.github_token_var.get().strip() or None
            releases = get_all_releases(owner, repo, token=token)
        except Exception as e:
            messagebox.showerror("Error", f"Failed to fetch releases for {repo_text}:\n{e}")
            self.log(f"Error fetching releases for {repo_text}: {e}")
            return [], 0

        skip_existing = self.skip_existing_var.get()
        tasks = []
        total_assets_seen = 0

        for rel in releases:
            tag = rel.get("tag_name") or rel.get("name") or "untagged"
            tag_folder = os.path.join(repo_root, tag)
            os.makedirs(tag_folder, exist_ok=True)

            # -----------------------------------------------------------
            # Write release notes inside the tag folder
            # -----------------------------------------------------------
            release_body = rel.get("body") or ""
            notes_path = os.path.join(tag_folder, "release_notes.txt")

            try:
                # If release has no description, write a blank file
                with open(notes_path, "w", encoding="utf-8") as nf:
                    nf.write(f"Release Notes for {repo_text} — {tag}\n")
                    nf.write(f"Published: {rel.get('published_at','')}\n\n")
                    nf.write("------------------------------------------------------------\n\n")
                    if release_body.strip():
                        nf.write(release_body)
                    else:
                        nf.write("(No release description provided.)\n")
                self.log(f"Saved release notes: {notes_path}")
            except Exception as e:
                self.log(f"ERROR writing release notes for {repo_text}/{tag}: {e}")


            tag_assets_db = assets_db.get(tag, {})
            for asset in rel.get("assets", []):
                total_assets_seen += 1
                name = asset["name"]
                file_path = os.path.join(tag_folder, name)
                remote_size = asset.get("size", 0) or 0
                db_entry = tag_assets_db.get(name)

                needs = False
                if not os.path.exists(file_path):
                    needs = True
                else:
                    local_size = os.path.getsize(file_path)
                    if local_size < remote_size:
                        needs = True
                    elif db_entry and db_entry.get("size_bytes") != remote_size:
                        needs = True
                    elif not db_entry:
                        needs = not skip_existing
                    else:
                        needs = not skip_existing

                if needs:
                    tasks.append((asset, tag_folder, tag))

        return tasks, total_assets_seen

    def _update_repo_last_result(self, repo_text, result_str):
        self.repo_db["repos"].setdefault(
            repo_text, {"last_checked": None, "last_result": "", "assets": {}}
        )
        self.repo_db["repos"][repo_text]["last_result"] = result_str
        self._save_metadb()
        self._refresh_repo_dashboard()

    def _ensure_repo_in_db(self, repo_text):
        """Make sure repo entry exists in repo_db with correct fields."""

        # Create root structure if missing
        if "repos" not in self.repo_db or not isinstance(self.repo_db["repos"], dict):
            self.repo_db["repos"] = {}

        # If repo missing, create empty dict (do NOT overwrite existing)
        if repo_text not in self.repo_db["repos"]:
            self.repo_db["repos"][repo_text] = {}

        # Normalize fields
        repo_info = self.repo_db["repos"][repo_text]
        repo_info.setdefault("last_checked", None)
        repo_info.setdefault("last_result", "")
        repo_info.setdefault("assets", {})

        # Save and refresh UI
        self._save_metadb()
        self._refresh_repo_combo()
        self._refresh_repo_dashboard()


    def _update_repo_timestamp(self, repo_key):
        """
        Update the last_checked timestamp for the given repo.
        """
        ts = datetime.datetime.now(datetime.UTC).strftime("%Y-%m-%d_%H_%M_%SZ")
        if "repos" not in self.repo_db:
            self.repo_db["repos"] = {}
        repo_info = self.repo_db["repos"].setdefault(
            repo_key,
            {"last_checked": None, "last_result": "", "assets": {}}
        )
        repo_info["last_checked"] = ts
        self._save_metadb()
        self._refresh_repo_dashboard()
        self.log(f"Updated last_checked for {repo_key} → {ts}")

    # ---------- STOP / PAUSE ----------

    def _toggle_pause(self):
        if not self.downloading:
            messagebox.showinfo("Idle", "Nothing is running.")
            return
        self.pause_flag = not self.pause_flag
        self.pause_btn.config(text="Resume" if self.pause_flag else "Pause")
        self.log("Pause toggled: %s" % ("ON" if self.pause_flag else "OFF"))

    def _stop_safe(self):
        if not self.downloading:
            messagebox.showinfo("Idle", "Nothing to stop.")
            return
        self.log("Safe stop requested.")
        self.stop_soft = True
        self._cancel_run_ui("Safe stop requested. Current downloads finishing; remaining skipped.")

    def _stop_hard(self):
        if not self.downloading:
            messagebox.showinfo("Idle", "Nothing to stop.")
            return
        self.log("Hard stop requested.")
        self.stop_hard = True
        self._cancel_run_ui("Hard stop requested. Downloads aborted ASAP.")

    def _cancel_run_ui(self, msg):
        self._stop_flash()
        self.downloading = False
        self.run_cancelled = True
        self.pause_flag = False
        self.pause_btn.config(text="Pause")
        self.start_btn.config(state="normal")
        self.update_all_btn.config(state="normal")
        self.info_label.config(text=msg)
        self.status_label.config(text="Run cancelled.")
        # Also cancel batch mode
        self.batch_update_mode = False
        self.batch_repo_list = []
        self.batch_summary = []

    # ---------- UI loop / message handling ----------

    def _update_ui_loop(self):
        try:
            while True:
                msg = self.result_queue.get_nowait()
                self._handle_message(msg)
        except queue.Empty:
            pass
        self.root.after(200, self._update_ui_loop)

    def _handle_message(self, msg):
        # if run was cancelled, ignore worker messages
        if self.run_cancelled or not self.downloading:
            return

        mtype = msg[0]

        if mtype == "progress":
            _, tag, name, downloaded, total, speed, eta = msg
            if total > 0:
                pct = (downloaded / total) * 100
            else:
                pct = 0.0
            speed_mb = speed / (1024 * 1024)
            eta_sec = int(eta)
            self.info_label.config(
                text=f"{self.current_repo_key} | {tag} / {name} — {pct:.1f}% — {speed_mb:.1f} MB/s — ETA {eta_sec}s"
            )
        elif mtype == "done":
            (
                _,
                tag,
                name,
                size,
                url,
                path,
                sha1,
                success,
                errmsg,
            ) = msg
            self.completed_tasks += 1
            self.progress["value"] = self.completed_tasks
            if self.total_tasks > 0:
                pct = int((self.completed_tasks / self.total_tasks) * 100)
                self.progress_pct_var.set(f"{pct}%")
            else:
                self.progress_pct_var.set("")

            rec = {
                "repo": self.current_repo_key or "",
                "tag": tag,
                "name": name,
                "size_bytes": size,
                "url": url,
                "path": path,
                "sha1": sha1,
                "success": bool(success),
                "error": errmsg,
            }
            self.asset_records.append(rec)

            if self.current_repo_key and success and sha1:
                repo_info = self.repo_db["repos"].setdefault(
                    self.current_repo_key,
                    {"last_checked": None, "last_result": "", "assets": {}},
                )
                assets_db = repo_info.setdefault("assets", {})
                tag_assets = assets_db.setdefault(tag, {})
                tag_assets[name] = {
                    "size_bytes": size,
                    "sha1": sha1,
                    "path": path,
                    "downloaded_at": datetime.datetime.now(datetime.UTC).strftime("%Y-%m-%d_%H_%M_%SZ"),
                }
                self._save_metadb()

            ok = len([r for r in self.asset_records if r["success"]])
            failed = len([r for r in self.asset_records if not r["success"]])

            self.status_label.config(
                text=f"Completed: {self.completed_tasks}/{self.total_tasks} | "
                     f"OK: {ok} | Failed: {failed}"
            )

            if self.completed_tasks >= self.total_tasks and self.downloading:
                self._finalize_downloads()

    def _finalize_downloads(self):
            self.downloading = False
            repo_text = self.current_repo_key or self.repo_var.get().strip()
            root_outdir = self.folder_var.get().strip()
            safe_repo = normalize_repo_folder_name(repo_text)

            ts = datetime.datetime.now(datetime.UTC).strftime("%Y-%m-%d_%H_%M_%SZ")

            # Ensure metadata dirs under this root and write JSON/CSV there
            _, json_dir, csv_dir, _, _, _ = ensure_metadata_dirs(root_outdir)
            json_path = os.path.join(json_dir, f"{safe_repo}_assets_{ts}.json")
            csv_path = os.path.join(csv_dir, f"{safe_repo}_assets_{ts}.csv")

            payload = {
                "generated_at_utc": ts,
                "repo": repo_text,
                "assets": self.asset_records,
            }

            ok = len([r for r in self.asset_records if r["success"]])
            failed = len([r for r in self.asset_records if not r["success"]])
            result_str = f"OK={ok}, Failed={failed}"
            self._update_repo_last_result(repo_text, result_str)
            self._update_repo_timestamp(repo_text)

            try:
                with open(json_path, "w", encoding="utf-8") as f:
                    json.dump(payload, f, indent=2)
            except Exception as e:
                self.log(f"Warning: failed to write JSON: {e}")

            try:
                with open(csv_path, "w", encoding="utf-8", newline="") as f:
                    writer = csv.writer(f)
                    writer.writerow(
                        ["repo", "tag", "name", "size_bytes", "url", "path", "sha1", "success", "error"]
                    )
                    for r in self.asset_records:
                        writer.writerow(
                            [
                                r["repo"],
                                r["tag"],
                                r["name"],
                                r["size_bytes"],
                                r["url"],
                                r["path"],
                                r["sha1"] or "",
                                "1" if r["success"] else "0",
                                r["error"] or "",
                            ]
                        )
            except Exception as e:
                self.log(f"Warning: failed to write CSV: {e}")

            self.info_label.config(text=f"{repo_text}: downloads finished.")
            self.start_btn.config(state="normal")
            self.update_all_btn.config(state="normal")
            self.pause_flag = False
            self.pause_btn.config(text="Pause")
            self.progress_pct_var.set("")
            self._refresh_repo_dashboard()

            # Discord notification (per-repo)
            webhook = self.discord_webhook_var.get().strip()
            if webhook:
                send_discord_notification(
                    webhook,
                    f"{repo_text}: update finished — OK={ok}, failed={failed}",
                )

            self.log(f"{repo_text}: update finished — OK={ok}, failed={failed}")

            if not self.batch_update_mode:
                messagebox.showinfo(
                    "Done",
                    f"{repo_text}\n\nDownloads complete.\nOK={ok}, Failed={failed}\n\n"
                    f"Metadata:\n{json_path}\n{csv_path}",
                )
            else:
                self.batch_summary.append(f"{repo_text}: {result_str}")
                # continue with next repo in batch
                self._start_next_repo_in_batch()

    # ---------- Menu Tools (PDF, DAT, Hash, Orphans) ----------

    def _get_current_repo_info(self):
        repo_key = self.repo_var.get().strip()
        if not repo_key or "/" not in repo_key:
            messagebox.showerror("Error", "Selected repo must be in owner/repo format.")
            return None, None
        info = self.repo_db["repos"].get(repo_key)
        if info is None:
            messagebox.showerror("Error", "Selected repo has no metadata yet.")
            return None, None
        return repo_key, info

    def _menu_generate_pdf(self):
        repo_key, info = self._get_current_repo_info()
        if not repo_key:
            return
        root_outdir = self.folder_var.get().strip()
        if not root_outdir:
            messagebox.showerror("Error", "Set a root download folder first.")
            return
        records = []
        assets_db = info.get("assets", {})
        for tag, files in assets_db.items():
            for name, meta in files.items():
                records.append(
                    {
                        "tag": tag,
                        "name": name,
                        "size_bytes": meta.get("size_bytes", 0),
                        "sha1": meta.get("sha1", ""),
                    }
                )
        path = generate_pdf_summary(repo_key, records, root_outdir)
        self.log(f"{repo_key}: summary generated at {path}")
        messagebox.showinfo("Summary generated", f"Summary file created:\n{path}")

    def _menu_generate_dat(self):
        repo_key, info = self._get_current_repo_info()
        if not repo_key:
            return
        root_outdir = self.folder_var.get().strip()
        if not root_outdir:
            messagebox.showerror("Error", "Set a root download folder first.")
            return
        path = generate_dat_for_repo(repo_key, info, root_outdir)
        self.log(f"{repo_key}: DAT generated at {path}")
        messagebox.showinfo("DAT generated", f"DAT file created:\n{path}")

    def _menu_cleanup_orphans(self):
        repo_key, info = self._get_current_repo_info()
        if not repo_key:
            return
        root_outdir = self.folder_var.get().strip()
        if not root_outdir:
            messagebox.showerror("Error", "Set a root download folder first.")
            return
        orphans = find_orphans_for_repo(repo_key, root_outdir, info)
        if not orphans:
            messagebox.showinfo("Orphans", "No orphaned files detected.")
            return
        msg = (
            f"Found {len(orphans)} orphaned file(s).\n"
            "They will be moved to an 'orphans' subfolder.\nProceed?"
        )
        if not messagebox.askyesno("Cleanup orphans", msg):
            return
        repo_folder_name = normalize_repo_folder_name(repo_key)
        repo_root = os.path.join(root_outdir, repo_folder_name)
        orphan_root = os.path.join(repo_root, "orphans")
        os.makedirs(orphan_root, exist_ok=True)
        for p in orphans:
            rel = os.path.relpath(p, repo_root)
            dest = os.path.join(orphan_root, rel)
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            try:
                os.replace(p, dest)
            except Exception:
                pass
        self.log(f"{repo_key}: moved {len(orphans)} orphan(s) to {orphan_root}")
        messagebox.showinfo(
            "Cleanup complete",
            f"Moved {len(orphans)} orphaned file(s) to:\n{orphan_root}",
        )

    def _menu_force_hash_rescan(self):
        repo_key, info = self._get_current_repo_info()
        if not repo_key:
            return
        if not messagebox.askyesno(
            "Force hash rescan",
            "This will recompute SHA-1 for all known assets.\nProceed?",
        ):
            return
        assets_db = info.get("assets", {})
        count = 0
        missing = 0
        for tag, files in assets_db.items():
            for name, meta in files.items():
                path = meta.get("path")
                if not path or not os.path.exists(path):
                    missing += 1
                    continue
                try:
                    sha = compute_sha1(path)
                    size = os.path.getsize(path)
                    meta["sha1"] = sha
                    meta["size_bytes"] = size
                    meta["hash_rescanned_at"] = (
                        datetime.datetime.now(datetime.UTC).strftime("%Y-%m-%d_%H_%M_%SZ")
                    )
                    count += 1
                except Exception:
                    missing += 1
        self._save_metadb()
        self._refresh_repo_dashboard()
        self.log(f"{repo_key}: hash rescan — {count} updated, {missing} missing/fail")
        messagebox.showinfo(
            "Hash rescan complete",
            f"{repo_key}:\nUpdated {count} asset(s).\nMissing/failed: {missing}",
        )

    def _menu_scan_local_rebuild_metadata(self):
        repo_key, info = self._get_current_repo_info()
        if not repo_key:
            return

        root_outdir = self.folder_var.get().strip()
        if not root_outdir:
            messagebox.showerror("Error", "Set a root download folder first.")
            return

        repo_folder_name = normalize_repo_folder_name(repo_key)
        repo_root = os.path.join(root_outdir, repo_folder_name)

        if not os.path.isdir(repo_root):
            messagebox.showerror(
                "Missing folder",
                f"Repo folder does not exist:\n{repo_root}"
            )
            return

        msg = (
            "This will scan all files under this repo's folder on disk,\n"
            "use the top-level subfolder names as tags, and rebuild metadata.\n\n"
            "Proceed?"
        )
        if not messagebox.askyesno("Scan Local Files", msg):
            return

        assets_db = info.setdefault("assets", {})
        added = 0
        updated = 0
        skipped = 0
        total = 0

        for dirpath, _, filenames in os.walk(repo_root):
            rel_dir = os.path.relpath(dirpath, repo_root)

            # Skip files directly in repo_root; we only care about tagged subfolders
            if rel_dir == ".":
                continue

            # Top-level folder under repo_root becomes the tag
            top_tag = rel_dir.split(os.sep)[0]

            # Skip "orphans" area entirely
            if top_tag.lower() == "orphans":
                continue

            tag_assets = assets_db.setdefault(top_tag, {})

            for fn in filenames:
                full_path = os.path.join(dirpath, fn)
                if not os.path.isfile(full_path):
                    continue

                total += 1

                try:
                    size = os.path.getsize(full_path)
                    # Use whichever hash function your script currently uses;
                    # if you switched to SHA1, change this to compute_sha1(...)
                    sha = compute_sha1(full_path)
                except Exception:
                    continue

                existing = tag_assets.get(fn)
                if existing:
                    same_size = existing.get("size_bytes") == size
                    same_hash = existing.get("sha1") == sha
                    same_path = os.path.abspath(existing.get("path", "")) == os.path.abspath(full_path)
                    if same_size and same_hash and same_path:
                        skipped += 1
                        continue
                    else:
                        updated += 1
                else:
                    added += 1

                tag_assets[fn] = {
                    "size_bytes": size,
                    "sha1": sha,
                    "path": full_path,
                    "indexed_at": datetime.datetime.now(datetime.UTC).strftime("%Y-%m-%d_%H_%M_%SZ"),
                }

        self._save_metadb()
        self._refresh_repo_dashboard()

        summary = (
            f"{repo_key}:\n"
            f"Scanned files: {total}\n"
            f"Added: {added}\n"
            f"Updated: {updated}\n"
            f"Unchanged (skipped): {skipped}"
        )
        self.log(f"{repo_key}: local scan complete — added={added}, updated={updated}, skipped={skipped}")
        messagebox.showinfo("Scan complete", summary)

        # === Auto-regenerate JSON/CSV metadata if missing ===
        # Determine metadata directories
        _, json_dir, csv_dir, _, _, _ = ensure_metadata_dirs(root_outdir)
        safe_repo = normalize_repo_folder_name(repo_key)

        # Expected metadata filenames
        json_expected = os.path.join(json_dir, f"{safe_repo}_assets.json")
        csv_expected  = os.path.join(csv_dir, f"{safe_repo}_assets.csv")

        # Check if missing
        json_missing = not os.path.isfile(json_expected)
        csv_missing  = not os.path.isfile(csv_expected)

        if json_missing or csv_missing:
            try:
                # Rebuild JSON + CSV using new metadata content
                payload = {
                    "repo": repo_key,
                    "last_rebuild": datetime.datetime.now(datetime.UTC).strftime("%Y-%m-%d_%H_%M_%SZ"),
                    "assets": info.get("assets", {}),
                }

                # --- Write JSON ---
                with open(json_expected, "w", encoding="utf-8") as jf:
                    json.dump(payload, jf, indent=2)

                # --- Write CSV ---
                with open(csv_expected, "w", encoding="utf-8", newline="") as cf:
                    writer = csv.writer(cf)
                    writer.writerow(["tag", "filename", "size_bytes", "sha1", "path"])
                    for tag, files in info.get("assets", {}).items():
                        for fn, meta in files.items():
                            writer.writerow([
                                tag,
                                fn,
                                meta.get("size_bytes"),
                                meta.get("sha1"),
                                meta.get("path"),
                            ])

                self.log(f"{repo_key}: metadata rebuilt (json={json_missing}, csv={csv_missing})")

            except Exception as e:
                self.log(f"{repo_key}: metadata rebuild failed: {e}")


# ==============================
# MAIN
# ==============================

def main():
    if HAS_DND:
        root = TkinterDnD.Tk()
    else:
        root = tk.Tk()
    app = DownloaderGUI(root)
    root.mainloop()


if __name__ == "__main__":
    main()
