#!/bin/bash
set -euo pipefail

PROJECT="src/Novelist.App/Novelist.App.csproj"
VERSION_FILE="build/version/novelist.version"

if [ ! -f "$PROJECT" ]; then
    echo "Project file is missing: $PROJECT" >&2
    exit 1
fi

version="${VERSION:-}"
if [ -z "$version" ]; then
    dotnet msbuild "$PROJECT" -restore -nologo -v:minimal -t:WriteNovelistVersion -p:Configuration=Release
    version="$(tr -d '\r\n' < "$VERSION_FILE")"
fi

if [ -z "$version" ]; then
    echo "MinVer did not produce a version. Set VERSION=1.2.3 to override." >&2
    exit 1
fi

npm --prefix frontend ci
npm --prefix frontend run build

VERSION="$version" bash scripts/novelist-publish.sh win-x64

if ! command -v iscc >/dev/null 2>&1; then
    if [ -x "/c/Program Files (x86)/Inno Setup 6/ISCC.exe" ]; then
        export PATH="/c/Program Files (x86)/Inno Setup 6:$PATH"
    elif [ -x "/c/Program Files/Inno Setup 6/ISCC.exe" ]; then
        export PATH="/c/Program Files/Inno Setup 6:$PATH"
    fi
fi

if ! command -v iscc >/dev/null 2>&1; then
    echo "Inno Setup is missing. Install Inno Setup 6 and make iscc available on PATH." >&2
    exit 1
fi

export VERSION="$version"
iscc build/package/windows/setup.iss

echo "Windows installer -> build/dist/novelist-v${version}-windows-amd64.exe"
