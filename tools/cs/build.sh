#!/usr/bin/env bash
# Compiles a C# console tool together with the engine sources in UnityProject/Assets/AtmosEngine,
# using the C# compiler that ships inside the Unity editor (no .NET SDK needed), against
# Unity's .NET Standard 2.1 reference - so anything that compiles here also compiles in Unity.
# The result runs on the installed .NET 8 runtime.
#
#   tools/cs/build.sh CafRms            -> tools/cs/bin/CafRms.dll
#   dotnet tools/cs/bin/CafRms.dll <file.caf>
set -euo pipefail

TOOL="${1:?usage: build.sh <ToolName>}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
# the Unity editor's Data folder: set UNITY_DATA, or the Hub's usual install folders are tried
if [ -z "${UNITY_DATA:-}" ]; then
  for d in "$HOME/Unity/Hub/Editor/6000.3.26f1/Editor/Data" "/c/Program Files/Unity/Hub/Editor/6000.3.26f1/Editor/Data"; do
    [ -d "$d" ] && UNITY_DATA="$d" && break
  done
fi
: "${UNITY_DATA:?set UNITY_DATA to the Editor/Data folder of your Unity 6000.3.26f1 editor}"
OUT="$HERE/bin"
mkdir -p "$OUT"

"$UNITY_DATA/NetCoreRuntime/dotnet.exe" "$UNITY_DATA/DotNetSdkRoslyn/csc.dll" \
  -nologo -target:exe -langversion:9 -optimize+ -unsafe -warn:4 \
  -nostdlib -reference:"$(cygpath -w "$UNITY_DATA/NetStandard/ref/2.1.0/netstandard.dll")" \
  -out:"$(cygpath -w "$OUT/$TOOL.dll")" \
  "$(cygpath -w "$HERE/$TOOL.cs")" \
  $(for f in "$ROOT"/UnityProject/Assets/AtmosEngine/*.cs; do cygpath -w "$f"; done)

cat > "$OUT/$TOOL.runtimeconfig.json" <<'EOF'
{
  "runtimeOptions": {
    "tfm": "net8.0",
    "framework": { "name": "Microsoft.NETCore.App", "version": "8.0.0" }
  }
}
EOF

cp -f "$ROOT/native/build/Release/ear_shim.dll" "$OUT/" 2>/dev/null || true
echo "built $OUT/$TOOL.dll"
