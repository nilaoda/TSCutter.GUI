# Release tools

## Third-party notices

`generate_third_party_notices.py` combines `THIRD_PARTY_NOTICES.md`,
`THIRD_PARTY_LICENSES.txt`, the project `LICENSE`, and the .NET runtime notices
into one UTF-8 file. Upstream text and line endings are preserved, with input BOMs
removed. `THIRD_PARTY_LICENSES.txt` stores identical texts once and lists all
corresponding components in each section heading. Sdcb's original reference code
is kept separately in `design/reference/Sdcb.FFmpeg.VideoFrameConverter.cs.txt`.

The application projects do not import or run this tool. Ordinary `dotnet build`
and `dotnet publish` work independently of the repository's license documents and
Python. The shared `release-info` job generates the TXT once and uploads it as a
`third-party-notices` artifact. Windows, Linux, and macOS jobs download the same
file before archiving their packages. On macOS, the TXT goes alongside the
executable inside `TSCutter.GUI.app/Contents/MacOS`.

All three builds use the exact SDK version selected by the shared job. .NET's
runtime license texts for these targets have the same content (Windows uses
different line endings), so the shared job reads them from the Linux runtime
pack resolved by that SDK. The consolidated dependency licenses include the
Windows static graphics libraries as well as the shared cross-platform libraries.

For local self-contained packaging, query the same project's resolved runtime pack
using the same configuration and runtime identifier as the publish. For example,
from the repository root with Python 3 and the .NET SDK installed:

```sh
dotnet publish src/TSCutter.GUI -r linux-x64 -c Release -o artifact
dotnet msbuild src/TSCutter.GUI/TSCutter.GUI.csproj -nologo \
  -target:ResolveFrameworkReferences -property:Configuration=Release \
  -property:RuntimeIdentifier=linux-x64 -property:SelfContained=true \
  -getItem:ResolvedRuntimePack > runtime-packs.json
python3 tools/generate_third_party_notices.py \
  --runtime-packs runtime-packs.json --output artifact/THIRD_PARTY_NOTICES.txt
```

This appends the resolved runtime pack's `LICENSE.TXT` and
`THIRD-PARTY-NOTICES.TXT`. Missing runtime metadata or license files fail packaging
instead of silently falling back to older texts.

For a local preview of the static license texts, without runtime notices:

```sh
python3 tools/generate_third_party_notices.py --output artifact/THIRD_PARTY_NOTICES.txt
```

The script uses only the Python standard library. Run its focused checks with:

```sh
python3 -m unittest discover -s tools -p 'test_*.py'
```
