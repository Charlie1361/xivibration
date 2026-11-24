#!/usr/bin/env bash
# Build the plugin, copy the zip to dist/ and regenerate repo.json.
# Bump <Version> in XIVibration/XIVibration.csproj before publishing an update,
# otherwise Dalamud won't offer it to people who already have the plugin.
set -euo pipefail
cd "$(dirname "$0")"

dotnet build XIVibration/XIVibration.csproj -c Release --no-incremental
mkdir -p dist
cp XIVibration/bin/Release/XIVibration/latest.zip dist/latest.zip

python3 - <<'PY'
import json, time
m = json.load(open("XIVibration/bin/Release/XIVibration/XIVibration.json", encoding="utf-8"))
url = "https://raw.githubusercontent.com/Charlie1361/xivibration/main/dist/latest.zip"
entry = {
    "Author": m["Author"],
    "Name": m["Name"],
    "InternalName": m["InternalName"],
    "AssemblyVersion": m["AssemblyVersion"],
    "TestingAssemblyVersion": m["AssemblyVersion"],
    "Punchline": m.get("Punchline", ""),
    "Description": m.get("Description", ""),
    "ApplicableVersion": "any",
    "RepoUrl": m.get("RepoUrl", ""),
    "IconUrl": m.get("IconUrl", ""),
    "ImageUrls": m.get("ImageUrls", []),
    "Tags": m.get("Tags", []),
    "CategoryTags": m.get("CategoryTags", []),
    "DalamudApiLevel": m["DalamudApiLevel"],
    "LoadPriority": 0,
    "IsHide": False,
    "IsTestingExclusive": False,
    "DownloadCount": 0,
    "LastUpdate": int(time.time()),
    "DownloadLinkInstall": url,
    "DownloadLinkUpdate": url,
    "DownloadLinkTesting": url,
}
json.dump([entry], open("repo.json", "w", encoding="utf-8"), indent=2, ensure_ascii=False)
open("repo.json", "a").write("\n")
print("repo.json ->", entry["InternalName"], entry["AssemblyVersion"], "API", entry["DalamudApiLevel"])
PY
