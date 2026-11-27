# Turns the `dotnet publish` output of src/Spark.Mac (release/mac-build/<rid>, see publish-mac.ps1) into Spark.app
# and zips it with Unix permissions intact. Works on any OS, no Mac needed.
#   powershell -File publish-mac.ps1   -> release/Spark-macOS-AppleSilicon.zip and release/Spark-macOS-Intel.zip
# The app is not notarized: on first launch the user clears the quarantine flag (see README-mac.txt in the zip).
import os
import shutil
import struct
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
VERSION = os.environ.get("SPARK_VERSION", "1.4.4")   # the release workflow passes the tag's version
TARGETS = [("osx-arm64", "AppleSilicon"), ("osx-x64", "Intel")]

INFO_PLIST = f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Spark</string>
    <key>CFBundleDisplayName</key><string>Spark</string>
    <key>CFBundleIdentifier</key><string>com.spark.downloader</string>
    <key>CFBundleExecutable</key><string>Spark</string>
    <key>CFBundleIconFile</key><string>spark.icns</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleVersion</key><string>{VERSION}</string>
    <key>CFBundleShortVersionString</key><string>{VERSION}</string>
    <key>CFBundleDevelopmentRegion</key><string>ru</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSPrincipalClass</key><string>NSApplication</string>
    <key>CFBundleURLTypes</key>
    <array>
        <dict>
            <key>CFBundleURLName</key><string>Magnet link</string>
            <key>CFBundleURLSchemes</key><array><string>magnet</string></array>
        </dict>
    </array>
    <key>CFBundleDocumentTypes</key>
    <array>
        <dict>
            <key>CFBundleTypeName</key><string>BitTorrent file</string>
            <key>CFBundleTypeRole</key><string>Viewer</string>
            <key>LSHandlerRank</key><string>Default</string>
            <key>LSItemContentTypes</key><array><string>org.bittorrent.torrent</string></array>
        </dict>
    </array>
    <key>UTImportedTypeDeclarations</key>
    <array>
        <dict>
            <key>UTTypeIdentifier</key><string>org.bittorrent.torrent</string>
            <key>UTTypeDescription</key><string>BitTorrent file</string>
            <key>UTTypeConformsTo</key><array><string>public.data</string></array>
            <key>UTTypeTagSpecification</key>
            <dict>
                <key>public.filename-extension</key><array><string>torrent</string></array>
                <key>public.mime-type</key><array><string>application/x-bittorrent</string></array>
            </dict>
        </dict>
    </array>
</dict>
</plist>
"""

README = """Spark для macOS
================

1. Перетащите Spark.app в папку «Программы» (Applications).
2. Spark не подписан у Apple, поэтому при первом запуске macOS его заблокирует.
   Откройте Терминал и выполните одну команду:

   xattr -cr /Applications/Spark.app && codesign --force --deep -s - /Applications/Spark.app

   После этого Spark открывается обычным двойным кликом.
3. Закрытие окна прячет Spark: он продолжает качать, значок с молнией — в строке меню.
   Выйти: Cmd+Q или значок в строке меню → «Выйти из Spark».
4. Перехват загрузок Chrome/Edge/Brave/Arc: Настройки → «Показать папку расширения»,
   затем chrome://extensions → Режим разработчика → «Загрузить распакованное» → эта папка
   (в окне выбора нажмите Cmd+Shift+G и вставьте путь
   ~/Library/Application Support/Spark/browser-extension).
"""


def has_code_signature(macho: Path) -> bool:
    """LC_CODE_SIGNATURE (0x1D) in a thin 64-bit Mach-O: Apple Silicon refuses to run unsigned code."""
    data = macho.read_bytes()
    magic, = struct.unpack_from("<I", data, 0)
    if magic != 0xFEEDFACF:
        return False
    ncmds, = struct.unpack_from("<I", data, 16)
    offset = 32
    for _ in range(ncmds):
        cmd, size = struct.unpack_from("<II", data, offset)
        if cmd == 0x1D:
            return True
        offset += size
    return False


def build(rid: str, label: str) -> Path:
    publish = ROOT / "release" / "mac-build" / rid
    if not (publish / "Spark").exists():
        sys.exit(f"{publish} has no Spark executable: run publish-mac.ps1")

    app = ROOT / "release" / "mac" / label / "Spark.app"
    shutil.rmtree(app.parent, ignore_errors=True)
    macos, resources = app / "Contents" / "MacOS", app / "Contents" / "Resources"
    shutil.copytree(publish, macos, ignore=shutil.ignore_patterns("browser-extension", "*.pdb"))
    shutil.copytree(publish / "browser-extension", resources / "browser-extension")
    shutil.copy(ROOT / "src" / "Spark.Mac" / "macos" / "spark.icns", resources / "spark.icns")
    (app / "Contents" / "Info.plist").write_text(INFO_PLIST, encoding="utf-8")
    (app / "Contents" / "PkgInfo").write_text("APPL????", encoding="ascii")

    exe = macos / "Spark"
    signed = has_code_signature(exe)
    print(f"{label}: apphost {'is' if signed else 'is NOT'} ad-hoc signed")

    archive = ROOT / "release" / f"Spark-macOS-{label}.zip"
    archive.unlink(missing_ok=True)
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr(zipinfo("README-mac.txt", 0o644), README)
        for path in sorted(app.parent.rglob("*")):
            name = path.relative_to(app.parent).as_posix()
            if path.is_dir():
                z.writestr(zipinfo(name + "/", 0o755, directory=True), b"")
            else:
                executable = path == exe or path.suffix == ".dylib"
                z.writestr(zipinfo(name, 0o755 if executable else 0o644), path.read_bytes())
    print(f"{label}: {archive} ({archive.stat().st_size // (1024 * 1024)} MB)")
    return archive


def zipinfo(name: str, mode: int, directory: bool = False) -> zipfile.ZipInfo:
    info = zipfile.ZipInfo(name, date_time=(2026, 11, 25, 0, 0, 0))
    info.create_system = 3   # Unix: Archive Utility and unzip honour the permission bits
    info.external_attr = ((0o040000 if directory else 0o100000) | mode) << 16
    if directory:
        info.external_attr |= 0x10
    info.compress_type = zipfile.ZIP_DEFLATED
    return info


if __name__ == "__main__":
    for rid, label in TARGETS:
        if len(sys.argv) > 1 and label not in sys.argv[1:]:
            continue
        build(rid, label)
