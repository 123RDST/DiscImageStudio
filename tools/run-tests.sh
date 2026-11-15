#!/usr/bin/env bash
# Run the architecture suites as N independent processes.
#
# Most suites are cheap; a handful (boundary fitting, ISO generation, DVD encoding)
# dominate, so the wall clock is decided by the slowest shard. Shards are independent
# processes on purpose: several suites own process-wide state (WPF host, culture,
# settings), so in-process parallelism is not safe for the whole set.
#
# Usage: tools/run-tests.sh [shards] [--no-build]
set -u

cd "$(dirname "$0")/.." || exit 1

SHARDS=${1:-6}
BUILD=1
for arg in "$@"; do
    [ "$arg" = "--no-build" ] && BUILD=0
done
case "$SHARDS" in
    ''|*[!0-9]*) SHARDS=6 ;;
esac

DOTNET=${DOTNET_ROOT:-$HOME/.dotnet}/dotnet
[ -x "$DOTNET" ] || DOTNET=dotnet
PROJECT=tests/DiscImageStudio.ArchitectureTests/DiscImageStudio.ArchitectureTests.csproj
EXE=tests/DiscImageStudio.ArchitectureTests/bin/Debug/net10.0-windows/DiscImageStudio.ArchitectureTests.exe

if [ "$BUILD" = "1" ]; then
    "$DOTNET" build "$PROJECT" -c Debug -maxcpucount >/dev/null || { echo "build failed"; exit 1; }
fi
[ -x "$EXE" ] || { echo "missing $EXE"; exit 1; }

LOGDIR=$(mktemp -d)
start=$(date +%s)
pids=()
for i in $(seq 0 $((SHARDS - 1))); do
    # Each shard gets its own settings/temp tree: the suites that persist preferences would
    # otherwise overwrite each other's language choice through %APPDATA%.
    mkdir -p "$LOGDIR/env$i/appdata" "$LOGDIR/env$i/localappdata" "$LOGDIR/env$i/temp"
    ( DIS_IMAGE_STUDIO_SKIP_UPDATE_CHECK=1 \
      APPDATA="$LOGDIR/env$i/appdata" \
      LOCALAPPDATA="$LOGDIR/env$i/localappdata" \
      TEMP="$LOGDIR/env$i/temp" TMP="$LOGDIR/env$i/temp" \
      "$EXE" --shard="$i/$SHARDS" >"$LOGDIR/shard$i.log" 2>&1; echo $? >"$LOGDIR/shard$i.exit" ) &
    pids+=($!)
done
for pid in "${pids[@]}"; do wait "$pid"; done

elapsed=$(( $(date +%s) - start ))
failed=0
for i in $(seq 0 $((SHARDS - 1))); do
    code=$(cat "$LOGDIR/shard$i.exit" 2>/dev/null || echo 1)
    summary=$(grep -a "architecture-selftest" "$LOGDIR/shard$i.log" | tail -1)
    if [ "$code" != "0" ]; then
        failed=1
        echo "--- shard $i FAILED (exit $code), last 20 lines:"
        tail -20 "$LOGDIR/shard$i.log"
    else
        echo "shard $i: ${summary:-passed}"
    fi
done

echo "architecture-selftest: $SHARDS shards in ${elapsed}s"
if [ "$failed" != "0" ]; then
    echo "logs kept in $LOGDIR"
    exit 1
fi
rm -rf "$LOGDIR"
