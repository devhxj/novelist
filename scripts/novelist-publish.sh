#!/bin/bash
set -euo pipefail

RID="${1:-}"
CONFIGURATION="${CONFIGURATION:-Release}"
PUBLISH_DIR="${PUBLISH_DIR:-build/bin/novelist}"
PROJECT="src/Novelist.App/Novelist.App.csproj"

case "$PUBLISH_DIR" in
    ""|"."|".."|"/")
        echo "Refusing to publish to unsafe PUBLISH_DIR: '$PUBLISH_DIR'" >&2
        exit 1
        ;;
esac

if [ ! -f "$PROJECT" ]; then
    echo "Project file is missing: $PROJECT" >&2
    exit 1
fi

PUBLISH_PARENT="$(dirname "$PUBLISH_DIR")"
PUBLISH_BASE="$(basename "$PUBLISH_DIR")"
STAGING_DIR="$PUBLISH_PARENT/.${PUBLISH_BASE}.staging.$$"

rm -rf "$STAGING_DIR"
mkdir -p "$STAGING_DIR"
cleanup() {
    rm -rf "$STAGING_DIR"
}
trap cleanup EXIT

publish_args=(publish "$PROJECT" -c "$CONFIGURATION" -o "$STAGING_DIR" -p:PublishSingleFile=false -p:UseAppHost=true)
if [ "${NO_RESTORE:-}" = "1" ]; then
    publish_args+=(--no-restore)
fi

if [ -n "$RID" ]; then
    publish_args+=(-r "$RID" --self-contained true)
else
    publish_args+=(--self-contained false)
fi

dotnet "${publish_args[@]}"

if [ ! -f frontend/dist/index.html ]; then
    echo "frontend/dist/index.html is missing; run npm --prefix frontend run build first." >&2
    exit 1
fi
mkdir -p "$STAGING_DIR/frontend"
cp -a frontend/dist "$STAGING_DIR/frontend/"

if [ -d build/runtime/git ]; then
    mkdir -p "$STAGING_DIR/runtime"
    cp -a build/runtime/git "$STAGING_DIR/runtime/"
fi

if [ -d build/runtime/sqlite-vec ]; then
    cp -a build/runtime/sqlite-vec "$STAGING_DIR/sqlite-vec"
fi

if [ -f "$STAGING_DIR/Novelist.App.exe" ] && [ ! -f "$STAGING_DIR/novelist.exe" ]; then
    cp "$STAGING_DIR/Novelist.App.exe" "$STAGING_DIR/novelist.exe"
fi

if [ -f "$STAGING_DIR/Novelist.App" ] && [ ! -f "$STAGING_DIR/novelist" ]; then
    cp "$STAGING_DIR/Novelist.App" "$STAGING_DIR/novelist"
    chmod +x "$STAGING_DIR/novelist"
fi

if [ -n "$RID" ] && [ ! -f "$STAGING_DIR/novelist" ] && [ ! -f "$STAGING_DIR/novelist.exe" ]; then
    echo "Publish completed but no novelist executable alias was found in $STAGING_DIR." >&2
    exit 1
fi

find "$STAGING_DIR" -maxdepth 2 -type f \( -name "novelist" -o -name "novelist.exe" -o -name "Novelist.App" \) -exec chmod +x {} \; 2>/dev/null || true

rm -rf "$PUBLISH_DIR"
mv "$STAGING_DIR" "$PUBLISH_DIR"
trap - EXIT

echo "Novelist publish output -> $PUBLISH_DIR"
