#!/bin/sh
# Builds the standalone valslöngva for Linux and Windows: one self-contained executable each,
# with the UI beside it, in dist/. The executable is also the installer: run it, open Settings,
# "Install on this computer" (or run `valslongva install` from a terminal).
set -e
cd "$(dirname "$0")"
TREB="dotnet run --no-build --project ../../compiler/src/treb --"
dotnet build -nologo -v q ../../compiler/src/treb
rm -rf Generated dist
$TREB emit ../valslongva --out Generated --host --reference Externs/Externs.csproj
for rid in linux-x64 win-x64; do
  dotnet publish -nologo -v q -c Release -r "$rid" --self-contained -o "dist/$rid"
  rm -f "dist/$rid"/*.pdb
done
( cd dist/linux-x64 && tar czf ../valslongva-linux-x64.tar.gz valslongva ui )
( cd dist/win-x64 && zip -qr ../valslongva-win-x64.zip valslongva.exe ui )
ls -la dist/*.tar.gz dist/*.zip
