"""Eldenheim Setup: generates the mod's data from your own Elden Ring and
installs the mod into a Valheim BepInEx plugins folder. No Python, Rust or
console needed by the player once built into an exe (installer/build.py).

The steps are the README's Setup steps, run in-process from a copy of the
tool scripts in a work folder (they write next to themselves), which is
deleted afterwards so no Elden Ring files are left lying around.
"""
import contextlib
import datetime
import json
import os
import queue
import re
import runpy
import shutil
import sqlite3
import subprocess
import sys
import threading
import traceback
import uuid
import winreg
from pathlib import Path
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

# The bundle (PyInstaller) or, run from source, the repo.
BUNDLE = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parent.parent))
PAYLOAD = BUNDLE / "payload" if getattr(sys, "frozen", False) else BUNDLE
PLUGIN = BUNDLE / "payload" / "plugin" if getattr(sys, "frozen", False) else BUNDLE / "build" / "installer" / "plugin"
WORK = Path(os.environ.get("LOCALAPPDATA", Path.home())) / "EldenheimSetup" / "work"
PLUGIN_FILES = ["Eldenheim.dll", "Eldenheim.Sim.dll", "manifest.json", "icon.png", "README.md"]
DATA_FILES = ["eldenheim_data.bin", "eldenheim_anims_all.bin"]


# ---- finding the folders -------------------------------------------------

def steam_libraries():
    roots = []
    for hive, key in ((winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam"),
                      (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam")):
        with contextlib.suppress(OSError):
            with winreg.OpenKey(hive, key) as k:
                for name in ("SteamPath", "InstallPath"):
                    with contextlib.suppress(OSError):
                        roots.append(Path(winreg.QueryValueEx(k, name)[0]))
    roots.append(Path(r"C:\Program Files (x86)\Steam"))
    libs = []
    for root in roots:
        vdf = root / "steamapps" / "libraryfolders.vdf"
        found = [root]
        if vdf.exists():
            found += [Path(p.replace("\\\\", "\\")) for p in
                      re.findall(r'"path"\s+"([^"]+)"', vdf.read_text(encoding="utf-8", errors="replace"))]
        for lib in found:
            if lib.exists() and lib.resolve() not in [l.resolve() for l in libs]:
                libs.append(lib)
    return libs


def game_folders():
    """Where games may be: Steam's libraries, then common folders on every drive."""
    found = [lib / "steamapps" / "common" for lib in steam_libraries()]
    for letter in "CDEFGHIJKLMNOPQRSTUVWXYZ":
        drive = Path(letter + ":/")
        if drive.exists():
            # SteamLibrary is the name Steam suggests for a library added on another drive.
            found += [drive / "Games", drive / "SteamLibrary" / "steamapps" / "common", drive / "Steam" / "steamapps" / "common",
                      drive / "Program Files (x86)" / "Steam" / "steamapps" / "common",
                      drive / "Program Files" / "Steam" / "steamapps" / "common"]
    return [f for f in found if f.is_dir()]


def find_elden_ring():
    for folder in game_folders():
        game = folder / "ELDEN RING" / "Game"
        if (game / "eldenring.exe").exists():
            return str(game)
    return ""


def find_plugin_folders():
    """BepInEx plugins folders: Steam's Valheim, then Gale and r2modman profiles."""
    found = []
    for folder in game_folders():
        found.append(folder / "Valheim" / "BepInEx" / "plugins")
    found += [Path(path) / "BepInEx" / "plugins" for path in gale_profiles()]
    appdata = Path(os.environ.get("APPDATA", ""))
    for pattern in ("com.kesomannen.gale/valheim/profiles/*/BepInEx/plugins",
                    "r2modmanPlus-local/Valheim/profiles/*/BepInEx/plugins",
                    "Thunderstore Mod Manager/DataFolder/Valheim/profiles/*/BepInEx/plugins"):
        found += sorted(appdata.glob(pattern))
    out = []
    for p in found:
        if p.is_dir() and str(p).lower() not in [o.lower() for o in out]:
            out.append(str(p))
    return out


# ---- mod managers --------------------------------------------------------
# A mod manager lists only the mods it has recorded, so an install into one of
# its profiles is recorded the way its own "Import local mod" does.

GALE_DB = Path(os.environ.get("APPDATA", "")) / "com.kesomannen.gale" / "data.sqlite3"
MANAGER_EXES = {"gale": ["gale.exe"], "r2modman": ["r2modman.exe", "Thunderstore Mod Manager.exe"]}


def gale_profiles():
    """Gale's Valheim profile folders, from its own database (read-only)."""
    if not GALE_DB.exists():
        return []
    with contextlib.suppress(sqlite3.Error):
        with contextlib.closing(sqlite3.connect("file:%s?mode=ro" % GALE_DB.as_posix(), uri=True)) as db:
            return [row[0] for row in db.execute("select path from profiles where game_slug = 'valheim'")]
    return []


def same_path(a, b):
    return os.path.normcase(os.path.normpath(str(a))) == os.path.normcase(os.path.normpath(str(b)))


def manager_of(plugins):
    """'gale', 'r2modman' (also the Thunderstore Mod Manager) or None for a plain BepInEx install."""
    profile = Path(plugins).parent.parent
    if any(same_path(profile, p) for p in gale_profiles()):
        return "gale"
    if (profile / "mods.yml").exists() or "r2modman" in str(profile) or "Thunderstore Mod Manager" in str(profile):
        return "r2modman"
    return None


def running(exes):
    out = subprocess.run(["tasklist", "/fo", "csv", "/nh"], capture_output=True, text=True,
                         creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)).stdout.lower()
    return [e for e in exes if '"%s"' % e.lower() in out]


def install_dir(plugins):
    """An existing Eldenheim install in this plugins folder is updated in place;
    else the folder the profile's manager would use."""
    for dll in sorted(Path(plugins).glob("*/Eldenheim.dll")):
        return dll.parent
    if manager_of(plugins) == "r2modman":
        return Path(plugins) / "Gargantua1866-Eldenheim"
    return Path(plugins) / "Eldenheim"


def register_gale(plugins, dest, db_path=None):
    """Adds the mod to the Gale profile's mod list, as Gale's "Import local mod" records it."""
    db_path = db_path or GALE_DB
    manifest = json.loads((dest / "manifest.json").read_text(encoding="utf-8"))
    profile = Path(plugins).parent.parent
    with contextlib.closing(sqlite3.connect(db_path)) as db:
        with contextlib.closing(sqlite3.connect(Path(db_path).with_name("data.sqlite3.before-eldenheim"))) as copy:
            db.backup(copy)
        row = next((r for r in db.execute("select id, path, mods from profiles") if same_path(r[1], profile)), None)
        if row is None:
            raise RuntimeError("Gale has no profile at %s." % profile)
        mods = json.loads(row[2])
        old = [m for m in mods if m.get("name") == manifest["name"] and m.get("author") == manifest.get("author")]
        mods = [m for m in mods if m not in old]
        mods.append({
            "enabled": True,
            "installTime": datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00", "Z"),
            "name": manifest["name"],
            "icon": str(dest / "icon.png"),
            "author": manifest.get("author"),
            "description": manifest.get("description", ""),
            "version": manifest["version_number"],
            "dependencies": manifest.get("dependencies", []),
            "uuid": old[0]["uuid"] if old else str(uuid.uuid4()),
            "fileSize": sum(f.stat().st_size for f in dest.rglob("*") if f.is_file()),
            "readme": (dest / "README.md").read_text(encoding="utf-8"),
        })
        db.execute("update profiles set mods = ? where id = ?", (json.dumps(mods), row[0]))
        db.commit()


def register_r2modman(plugins, dest):
    """Adds the mod to the profile's mods.yml, in r2modman's ManifestV2 fields
    (the Thunderstore Mod Manager is r2modman too)."""
    manifest = json.loads((dest / "manifest.json").read_text(encoding="utf-8"))
    author = manifest.get("author", "Gargantua1866")
    full = "%s-%s" % (author, manifest["name"])
    major, minor, patch = (int(v) for v in manifest["version_number"].split("."))
    q = json.dumps  # a JSON string or list is valid YAML
    entry = "\n".join([
        "- manifestVersion: 2",
        "  name: " + q(full),
        "  authorName: " + q(author),
        "  websiteUrl: " + q(manifest.get("website_url", "")),
        "  displayName: " + q(manifest["name"]),
        "  description: " + q(manifest.get("description", "")),
        "  gameVersion: '0'",
        "  networkMode: both",
        "  packageType: other",
        "  installMode: managed",
        "  installedAtTime: %d" % int(datetime.datetime.now().timestamp() * 1000),
        "  loaders: []",
        "  dependencies: " + q(manifest.get("dependencies", [])),
        "  incompatibilities: []",
        "  optionalDependencies: []",
        "  versionNumber:",
        "    major: %d" % major,
        "    minor: %d" % minor,
        "    patch: %d" % patch,
        "  enabled: true",
        "  icon: " + q(str(dest / "icon.png")),
        "",
    ])
    mods_yml = Path(plugins).parent.parent / "mods.yml"
    text = mods_yml.read_text(encoding="utf-8") if mods_yml.exists() else ""
    if text.strip() in ("", "[]"):
        text = ""
    # Each entry starts with "- " at a line start; an older Eldenheim entry is dropped.
    blocks = [b for b in re.split(r"(?m)^(?=- )", text) if b.strip()]
    name_line = re.compile(r"""(?m)^(?:- |  )name: ["']?%s["']?\s*$""" % re.escape(full))
    keep = [b if b.endswith("\n") else b + "\n" for b in blocks if not name_line.search(b)]
    mods_yml.write_text("".join(keep) + entry, encoding="utf-8")


def check_game(game):
    game = Path(game)
    if not (game / "eldenring.exe").exists() or not (game / "oo2core_6_win64.dll").exists():
        return ("That isn't Elden Ring's Game folder. Pick the folder named Game inside ELDEN RING, the one holding "
                "eldenring.exe. With Steam it is normally:\n\n"
                "C:\\Program Files (x86)\\Steam\\steamapps\\common\\ELDEN RING\\Game\n\n"
                "or, in a Steam library on another drive, X:\\SteamLibrary\\steamapps\\common\\ELDEN RING\\Game.\n\n"
                "In Steam: right-click ELDEN RING > Manage > Browse local files, then open Game.")
    if not (game / "Data0.bhd").exists():
        return "Data0.bhd is missing from the Game folder: is the game fully installed?"
    return None


def check_plugins(plugins):
    p = Path(plugins)
    if not p.is_dir():
        return "That plugins folder doesn't exist.\n\n" + PLUGINS_HELP
    if p.name.lower() != "plugins" or p.parent.name.lower() != "bepinex":
        return "Pick a folder named plugins inside BepInEx.\n\n" + PLUGINS_HELP
    manager = manager_of(plugins)
    busy = running(MANAGER_EXES[manager]) if manager else []
    if busy:
        return "Close %s first: it keeps its mod list open and would overwrite the new entry." % " and ".join(busy)
    return None


PLUGINS_HELP = ("Valheim's BepInEx\\plugins folder is normally:\n\n"
                "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Valheim\\BepInEx\\plugins\n\n"
                "With a mod manager, pick its profile's folder instead:\n"
                "Gale: %APPDATA%\\com.kesomannen.gale\\valheim\\profiles\\<profile>\\BepInEx\\plugins\n"
                "r2modman: %APPDATA%\\r2modmanPlus-local\\Valheim\\profiles\\<profile>\\BepInEx\\plugins\n"
                "Thunderstore Mod Manager: %APPDATA%\\Thunderstore Mod Manager\\DataFolder\\Valheim\\profiles\\<profile>\\BepInEx\\plugins\n\n"
                "(Install BepInEx for Valheim first if there is none.)")


# ---- the pipeline --------------------------------------------------------

def run_script(path, *args):
    """Runs a tool script as `python path args`; its sys.exit(message) becomes an error."""
    old_argv, old_cwd, old_path = sys.argv, os.getcwd(), list(sys.path)
    sys.argv = [str(path), *args]
    sys.path.insert(0, str(path.parent))
    os.chdir(WORK)
    try:
        runpy.run_path(str(path), run_name="__main__")
    except SystemExit as e:
        if e.code not in (None, 0):
            raise RuntimeError(str(e.code)) from None
    finally:
        sys.argv, sys.path[:] = old_argv, old_path
        os.chdir(old_cwd)


def pipeline(game, plugins, log):
    log("Preparing a work folder in %s" % WORK)
    # A retry in the same window must not reuse modules a failed run imported.
    for name, mod in list(sys.modules.items()):
        if str(getattr(mod, "__file__", "") or "").startswith(str(WORK)):
            del sys.modules[name]
    shutil.rmtree(WORK, ignore_errors=True)
    skip = shutil.ignore_patterns("__pycache__", "*.pyc")
    shutil.copytree(PAYLOAD / "sandbox" / "tools", WORK / "sandbox" / "tools", ignore=skip)
    (WORK / "sandbox" / "src" / "sim").mkdir(parents=True)
    (WORK / "tools").mkdir()
    for name in ("bake_unity.py", "export_data.py", "pick_sounds.py"):
        shutil.copy2(PAYLOAD / "tools" / name, WORK / "tools" / name)
    os.environ["ER_GAME_DIR"] = game
    os.environ["ER_FILES"] = str(WORK / "er-files")
    os.environ.pop("ER_SANDBOX", None)
    tools, sandbox_tools = WORK / "tools", WORK / "sandbox" / "tools"

    steps = [
        ("Unpacking the game files", sandbox_tools / "unpack_game_files.py", []),
        ("Reading actions, animations and sounds", sandbox_tools / "setup.py", []),
        ("Baking animations for Unity", tools / "bake_unity.py", ["--all", "build/eldenheim_anims_all.bin"]),
        ("Exporting the combat data", tools / "export_data.py", ["build/eldenheim_data.bin"]),
        ("Picking the sounds", tools / "pick_sounds.py", []),
    ]
    for n, (title, script, args) in enumerate(steps, 1):
        log("\n== %d/%d  %s ==" % (n, len(steps) + 1, title), step=n)
        run_script(script, *args)

    dest = install_dir(plugins)
    log("\n== %d/%d  Installing to %s ==" % (len(steps) + 1, len(steps) + 1, dest), step=len(steps) + 1)
    dest.mkdir(parents=True, exist_ok=True)
    try:
        for name in PLUGIN_FILES:
            shutil.copy2(PLUGIN / name, dest / name)
        for name in DATA_FILES:
            shutil.copy2(WORK / "build" / name, dest / name)
        sounds_bin = WORK / "sandbox" / "assets" / "player_sounds.bin"
        if sounds_bin.exists():
            shutil.copy2(sounds_bin, dest / "player_sounds.bin")
        shutil.rmtree(dest / "sounds", ignore_errors=True)
        if (WORK / "build" / "sounds").exists():
            shutil.copytree(WORK / "build" / "sounds", dest / "sounds")
    except PermissionError as e:
        raise RuntimeError("Couldn't write %s. Close Valheim and try again." % e.filename) from None
    manager = manager_of(plugins)
    if manager:
        log("Adding Eldenheim to %s's mod list" % ("Gale" if manager == "gale" else "the mod manager"))
        (register_gale if manager == "gale" else register_r2modman)(plugins, dest)
    shutil.rmtree(WORK, ignore_errors=True)
    return dest


# ---- the window ----------------------------------------------------------

class QueueWriter:
    def __init__(self, q):
        self.q = q

    def write(self, s):
        if s:
            self.q.put(("text", s))

    def flush(self):
        pass


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("Eldenheim Setup")
        with contextlib.suppress(tk.TclError):
            self.icon = tk.PhotoImage(file=str(PLUGIN / "icon.png"))
            self.iconphoto(True, self.icon)
        self.geometry("720x520")
        self.minsize(560, 420)
        self.q = queue.Queue()

        frame = ttk.Frame(self, padding=12)
        frame.pack(fill="both", expand=True)
        frame.columnconfigure(1, weight=1)
        ttk.Label(frame, text="Eldenheim's data is made from your own copy of Elden Ring. "
                              "Nothing is downloaded and nothing of Elden Ring is shared.",
                  wraplength=680).grid(row=0, column=0, columnspan=3, sticky="w", pady=(0, 10))

        ttk.Label(frame, text="Elden Ring Game folder").grid(row=1, column=0, sticky="w")
        self.game = tk.StringVar(value=find_elden_ring())
        ttk.Entry(frame, textvariable=self.game).grid(row=1, column=1, sticky="ew", padx=6)
        ttk.Button(frame, text="Browse…", command=self.pick_game).grid(row=1, column=2)

        ttk.Label(frame, text="Valheim plugins folder").grid(row=2, column=0, sticky="w", pady=6)
        found = find_plugin_folders()
        self.plugins = tk.StringVar(value=found[0] if found else "")
        ttk.Combobox(frame, textvariable=self.plugins, values=found).grid(row=2, column=1, sticky="ew", padx=6)
        ttk.Button(frame, text="Browse…", command=self.pick_plugins).grid(row=2, column=2)

        self.go = ttk.Button(frame, text="Install", command=self.start)
        self.go.grid(row=3, column=0, columnspan=3, pady=10)
        self.bar = ttk.Progressbar(frame, maximum=6)
        self.bar.grid(row=4, column=0, columnspan=3, sticky="ew")
        self.text = tk.Text(frame, height=16, wrap="word", state="disabled")
        self.text.grid(row=5, column=0, columnspan=3, sticky="nsew", pady=(8, 0))
        frame.rowconfigure(5, weight=1)
        self.after(100, self.poll)

    def pick_game(self):
        d = filedialog.askdirectory(title="Elden Ring's Game folder (holds eldenring.exe)")
        if d:
            self.game.set(os.path.normpath(d))

    def pick_plugins(self):
        d = filedialog.askdirectory(title="Valheim's BepInEx\\plugins folder")
        if d:
            self.plugins.set(os.path.normpath(d))

    def start(self):
        game, plugins = self.game.get().strip(), self.plugins.get().strip()
        problem = check_game(game) or check_plugins(plugins)
        if problem:
            messagebox.showerror("Eldenheim Setup", problem)
            return
        self.go.state(["disabled"])
        self.bar["value"] = 0
        threading.Thread(target=self.work, args=(game, plugins), daemon=True).start()

    def work(self, game, plugins):
        def log(s, step=None):
            self.q.put(("text", s + "\n"))
            if step is not None:
                self.q.put(("step", step - 1))

        writer = QueueWriter(self.q)
        try:
            with contextlib.redirect_stdout(writer), contextlib.redirect_stderr(writer):
                dest = pipeline(game, plugins, log)
            self.q.put(("done", "Installed to %s.\n\nStart Valheim to play." % dest))
        except Exception as e:
            detail = traceback.format_exc() if not isinstance(e, RuntimeError) else ""
            self.q.put(("text", detail))
            self.q.put(("fail", str(e)))

    def poll(self):
        with contextlib.suppress(queue.Empty):
            while True:
                kind, value = self.q.get_nowait()
                if kind == "text":
                    self.text.configure(state="normal")
                    self.text.insert("end", value)
                    self.text.see("end")
                    self.text.configure(state="disabled")
                elif kind == "step":
                    self.bar["value"] = value
                else:
                    self.go.state(["!disabled"])
                    if kind == "done":
                        self.bar["value"] = self.bar["maximum"]
                        messagebox.showinfo("Eldenheim Setup", value)
                    else:
                        messagebox.showerror("Eldenheim Setup", "Setup failed:\n\n%s\n\nThe log below has the details." % value)
        self.after(100, self.poll)


if __name__ == "__main__":
    if "--cli" in sys.argv:  # headless: --cli GAME_DIR PLUGINS_DIR (for checking the build)
        if sys.stdout is None:  # the windowed exe has no console
            WORK.parent.mkdir(parents=True, exist_ok=True)
            sys.stdout = sys.stderr = open(WORK.parent / "setup.log", "w", encoding="utf-8")
        game, plugins = sys.argv[2], sys.argv[3]
        problem = check_game(game) or check_plugins(plugins)
        if problem:
            sys.exit(problem)
        print("installed to", pipeline(game, plugins, lambda s, step=None: print(s, flush=True)))
    else:
        App().mainloop()
