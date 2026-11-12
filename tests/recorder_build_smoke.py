"""Check a Visual Studio build using disposable synthetic data, without showing UI."""
import argparse
import ctypes
import json
from pathlib import Path
import subprocess
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build", type=Path, required=True)
    parser.add_argument("--data", type=Path, required=True)
    args = parser.parse_args()
    build = args.build.resolve()
    data = args.data.resolve()
    if not (data / "recorder-ui-fixture.json").is_file():
        parser.error("Generate disposable data with prepare_recorder_ui_fixture.py first.")

    def run(executable, *arguments):
        result = subprocess.run(
            [str(build / executable), *map(str, arguments), "--data-dir", str(data)],
            capture_output=True, text=True, encoding="utf-8", timeout=30,
        )
        if result.returncode:
            raise RuntimeError(f"{executable} failed ({result.returncode}):\n{result.stdout}\n{result.stderr}")
        return result.stdout

    run("Linguacode.exe", "--smoke-test")
    result = json.loads((data / "desktop-smoke.json").read_text(encoding="utf-8-sig"))
    if not result.get("passed") or result.get("windowShown") is not False:
        raise RuntimeError("Invalid desktop smoke result.")

    def host(*arguments):
        return json.loads(run("Linguacode.AudioHost.exe", *arguments, "--json"))

    try:
        status = host("service", "start")["payload"]
        if not status.get("available"):
            raise RuntimeError("Matching audio service failed to start.")
        host("audio", "capture", "list", "1")
        capture = next(path for path in (data / "audio" / "recordings").iterdir() if path.is_dir())
        host("audio", "capture", "export", capture.name, data / ("raw-export-" + uuid.uuid4().hex))
        result.update(serviceProtocol=status["protocol"], pairedExport=True,
                      elevated=bool(ctypes.windll.shell32.IsUserAnAdmin()))
        output = json.dumps(result, indent=2)
        (data / "build-smoke.json").write_text(output, encoding="utf-8")
        print(output)
    finally:
        host("service", "stop")


if __name__ == "__main__":
    main()
