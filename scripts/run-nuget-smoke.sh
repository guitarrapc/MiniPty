#!/usr/bin/env bash
set -euo pipefail

version="${1:?Usage: run-nuget-smoke.sh <package-version> <nupkg-directory> [runtime-identifier]}"
feed_dir="${2:?Usage: run-nuget-smoke.sh <package-version> <nupkg-directory> [runtime-identifier]}"
rid="${3:-}"
version="${version#v}"

to_dotnet_path() {
  local dir="$1"
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -m "$dir"
    return
  fi
  # Git Bash pwd (/c/foo) is misread by NuGet as C:\c\foo on Windows.
  if [[ "$dir" =~ ^/([a-zA-Z])/(.*)$ ]]; then
    printf '%s:/%s' "$(printf '%s' "${BASH_REMATCH[1]}" | tr '[:lower:]' '[:upper:]')" "${BASH_REMATCH[2]}"
    return
  fi
  printf '%s' "$dir"
}

root_unix="$(cd "$(dirname "$0")/.." && pwd)"
feed_dir_unix="$(cd "$feed_dir" && pwd)"
root="$(to_dotnet_path "$root_unix")"
feed_dir="$(to_dotnet_path "$feed_dir_unix")"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

if [[ ! -f "$feed_dir_unix/MiniPty.${version}.nupkg" ]]; then
  echo "MiniPty.${version}.nupkg not found in $feed_dir_unix" >&2
  ls -la "$feed_dir_unix" >&2 || true
  exit 1
fi

cat > "$tmp/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="minipty-local" value="$feed_dir" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF

cat > "$tmp/NuGetSmoke.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MiniPty" Version="$version" />
    <PackageReference Include="MiniPty.Capture" Version="$version" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="$root/samples/NuGetSmoke.cs" Link="NuGetSmoke.cs" />
  </ItemGroup>
</Project>
EOF

dotnet run --project "$tmp/NuGetSmoke.csproj" -c Release --configfile "$tmp/nuget.config"

if [[ -n "$rid" ]]; then
  out="$tmp/aot-publish"
  dotnet publish "$tmp/NuGetSmoke.csproj" -c Release -r "$rid" \
    --self-contained true -p:PublishAot=true -p:StripSymbols=true -p:DebugType=None \
    -o "$out" --configfile "$tmp/nuget.config"
  exe="$out/NuGetSmoke"
  if [[ "$rid" == win-* ]]; then
    exe="$out/NuGetSmoke.exe"
  fi
  test -f "$exe"
  if [[ "$rid" == linux-* ]]; then
    test ! -f "$out/libminipty_unix.so"
    test ! -f "$out/libminipty_unix.a"
  fi
  if [[ "$rid" == osx-* ]]; then
    test ! -f "$out/libminipty_unix.dylib"
    test ! -f "$out/libminipty_unix.a"
    test -f "$out/minipty_spawn_helper"
  fi
  "$exe"
fi
