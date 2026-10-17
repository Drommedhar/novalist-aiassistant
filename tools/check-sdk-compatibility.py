"""Keep the supported host, pinned SDK build and release inputs aligned."""

import json
import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path


def check_checkout(sdk, reference):
    commit = subprocess.check_output(
        ["git", "-C", str(sdk), "rev-parse", "--verify", f"{reference}^{{commit}}"], text=True
    ).strip()
    head = subprocess.check_output(
        ["git", "-C", str(sdk), "rev-parse", "HEAD"], text=True
    ).strip()
    if head != commit:
        raise SystemExit("Validation must run against the pinned SDK revision")
    return head


def main():
    root = Path(__file__).resolve().parents[1]
    contract = json.loads((root / "sdk-compatibility.json").read_text(encoding="utf-8"))
    manifest = json.loads((root / "extension.json").read_text(encoding="utf-8"))
    if manifest["minHostVersion"] != contract["hostVersion"]:
        raise SystemExit("minHostVersion must match sdk-compatibility.json")
    sdk = root.parent / "novalist-official"
    head = check_checkout(sdk, contract["sdkRef"])
    for workflow in ("release.yml", "validate.yml"):
        text = (root / ".github" / "workflows" / workflow).read_text(encoding="utf-8")
        if f"ref: {contract['sdkRef']}" not in text:
            raise SystemExit(f"{workflow} must check out the pinned SDK revision")
    project = ET.parse(root / "Novalist.Extensions.AiAssistant.csproj")
    reference = project.find('.//PackageReference[@Include="Novalist.Sdk"]')
    if reference is None or reference.attrib.get("Version") != f"[{contract['sdkVersion']}]":
        raise SystemExit("SDK package version must match the compatibility contract")
    print(
        f"Compatibility: host {contract['hostVersion']}, SDK {contract['sdkVersion']} at {head}"
    )


if __name__ == "__main__":
    main()
