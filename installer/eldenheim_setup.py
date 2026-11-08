"""Eldenheim Setup: generates the mod's data from your own Elden Ring and
installs the mod into a Valheim BepInEx plugins folder. No Python, Rust or
console needed by the player once built into an exe (installer/build.py).

The steps are the README's Setup steps, run in-process from a copy of the
tool scripts in a work folder (they write next to themselves), which is
deleted afterwards so no Elden Ring files are left lying around.
"""
import contextlib
import os
import queue
import re
import runpy
import shutil
import sys
import threading
import traceback
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
            found += [drive / "Games", drive / "SteamLibrary" / "steamapps" / "common", drive / "Steam" / "steamapps" / "common"]
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
    appdata = Path(os.environ.get("APPDATA", ""))
    for pattern in ("com.kesomannen.gale/valheim/profiles/*/BepInEx/plugins",
                    "r2modmanPlus-local/Valheim/profiles/*/BepInEx/plugins",
                    "Thunderstore Mod Manager/DataFolder/Valheim/profiles/*/BepInEx/plugins"):
        found += sorted(appdata.glob(pattern))
    return [str(p) for p in found if p.is_dir()]


def install_dir(plugins):
    """An existing Eldenheim install in this plugins folder (e.g. Gale's
    Gargantua1866-Eldenheim) is updated in place; else plugins/Eldenheim."""
    for dll in sorted(Path(plugins).glob("*/Eldenheim.dll")):
        return dll.parent
    return Path(plugins) / "Eldenheim"


def check_game(game):
    game = Path(game)
    if not (game / "eldenring.exe").exists() or not (game / "oo2core_6_win64.dll").exists():
        return "That isn't Elden Ring's Game folder (the one holding eldenring.exe)."
    if not (game / "Data0.bhd").exists():
        return "Data0.bhd is missing from the Game folder: is the game fully installed?"
    return None


def check_plugins(plugins):
    p = Path(plugins)
    if not p.is_dir():
        return "That plugins folder doesn't exist."
    if p.name.lower() != "plugins" or p.parent.name.lower() != "bepinex":
        return "Pick the BepInEx\\plugins folder (install BepInEx for Valheim first)."
    return None


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
