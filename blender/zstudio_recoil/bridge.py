"""One-shot file bridge. Imported assets never select or launch an executable."""
import json
import os
import subprocess
import tempfile
import base64
import io
import zipfile
import concurrent.futures
import tomllib
from pathlib import Path

VERSION = tomllib.loads(Path(__file__).with_name("blender_manifest.toml").read_text(encoding="utf-8"))["version"]
PROTOCOL = 1
MAX_JSON = 32 * 1024 * 1024


def contained(root, relative):
    root = Path(root).resolve()
    if not relative or Path(relative).is_absolute() or ":" in relative or "\0" in relative:
        raise ValueError("A RECOIL dependency must be a relative file path")
    path = (root / relative).resolve()
    if path == root or not path.is_relative_to(root):
        raise ValueError("A RECOIL dependency escapes its selected folder")
    return path


def encode_json(value):
    # Admit authored strings before JSON escaping, then cap aggregate encoded
    # chunks. Avoid constructing a second unbounded serialized graph in memory.
    pending = [value]
    while pending:
        item = pending.pop()
        if isinstance(item, str) and len(item) > MAX_JSON // 6:
            raise ValueError("A RECOIL metadata string exceeds its escaped allowance")
        if isinstance(item, dict):
            pending.extend(item.keys()); pending.extend(item.values())
        elif isinstance(item, (list, tuple)):
            pending.extend(item)
    output = io.BytesIO()
    for chunk in json.JSONEncoder(ensure_ascii=True, allow_nan=False).iterencode(value):
        if len(chunk) > MAX_JSON - output.tell():
            raise ValueError("RECOIL metadata exceeds 32 MiB")
        output.write(chunk.encode("ascii"))
    return output.getvalue()


def read_json(path):
    with open(path, "rb") as stream:
        data = stream.read(MAX_JSON + 1)
    if len(data) > MAX_JSON:
        raise ValueError("RECOIL metadata exceeds 32 MiB")
    return json.loads(data)


def write_json(path, value):
    encoded = encode_json(value)
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    with open(path, "xb") as stream:
        stream.write(encoded)


def call(executable, operation, **arguments):
    executable = Path(executable)
    if not executable.is_file() or executable.name.lower() != "zstudio.exe":
        raise ValueError("Select the installed zStudio.exe in the extension preferences")
    # The zStudio download keeps the extension of its own version beside zStudio.exe.
    if not (executable.parent / f"zStudio-Blender-extension-{VERSION}.zip").is_file():
        raise ValueError(f"Select the zStudio.exe of version {VERSION}, with zStudio-Blender-extension-{VERSION}.zip beside it, matching this RECOIL extension")
    request = json.dumps(dict(protocol=PROTOCOL, operation=operation, **arguments)).encode("utf-8")
    if len(request) > 65536:
        raise ValueError("The helper request exceeds 64 KiB")
    # No shell, no network, no detached helper, and no console window stealing focus.
    with subprocess.Popen([str(executable), "--blender-helper"], stdin=subprocess.PIPE,
                          stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                          creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)) as process:
        process.stdin.write(request); process.stdin.close()
        with concurrent.futures.ThreadPoolExecutor(max_workers=1) as reader:
            pending = reader.submit(process.stdout.read, 65537)
            try:
                response = pending.result(timeout=600)
                if len(response) > 65536:
                    raise ValueError("The zStudio helper returned an oversized response")
                code = process.wait(timeout=10)
            except BaseException:
                process.kill(); process.wait()
                raise
    try:
        result = json.loads(response)
    except (ValueError, UnicodeError) as error:
        raise ValueError("The selected zStudio does not provide the RECOIL Blender helper") from error
    if code or not result.get("ok"):
        raise ValueError(result.get("error", "zStudio could not complete the operation"))
    return result


def source_root(filepath):
    """A source project/checkout root is explicit evidence, never a filename heuristic."""
    path = Path(filepath).resolve()
    for parent in path.parents:
        if parent.name == "input" and (parent.parent / "manifest.json").is_file():
            manifest = read_json(parent.parent / "manifest.json")
            if manifest.get("format") == "zstudio-blender-checkout" and manifest.get("version") == 2:
                return parent, parent.parent / "outbox"
        if (parent / "data").is_dir() and (parent / "gamegen").is_dir():
            return parent, None
    return path.parent, None


def prepare(executable, filepath):
    root, outbox = source_root(filepath)
    parent = Path(tempfile.mkdtemp(prefix="zstudio-blender-"))
    result = call(executable, "import", root=str(root), model=Path(filepath).resolve().relative_to(root).as_posix(),
                  destination=str(parent / "session"))
    return Path(result["session"]), str(outbox) if outbox else ""


def pack_session(folder):
    """Keep the exact baseline in the .blend, so reopening never depends on a temporary cache."""
    folder = Path(folder)
    manifest = read_json(folder / "session.json")
    paths = ["session.json"] + ["baseline/" + f["path"] for f in manifest["files"]]
    total = sum(contained(folder, p).stat().st_size for p in paths)
    if total > 512 * 1024 * 1024:
        raise ValueError("The packed RECOIL baseline exceeds 512 MiB")
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in paths:
            archive.write(contained(folder, path), path)
    return base64.encodebytes(out.getvalue()).decode("ascii")


def restore_session(encoded):
    if len(encoded) > 720 * 1024 * 1024:
        raise ValueError("The packed RECOIL baseline exceeds its allowance")
    folder = Path(tempfile.mkdtemp(prefix="zstudio-blender-restored-"))
    with zipfile.ZipFile(io.BytesIO(base64.b64decode(encoded.replace("\n", ""), validate=True))) as archive:
        if len(archive.infolist()) > 4097 or sum(i.file_size for i in archive.infolist()) > 512 * 1024 * 1024:
            raise ValueError("The packed RECOIL graph exceeds its allowance")
        seen = set()
        for info in archive.infolist():
            path = contained(folder, info.filename)
            if info.filename.lower() in seen:
                raise ValueError("Invalid packed RECOIL path")
            seen.add(info.filename.lower())
            path.parent.mkdir(parents=True, exist_ok=True)
            with path.open("xb") as stream:
                stream.write(archive.read(info))
    return folder
