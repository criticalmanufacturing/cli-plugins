#!/usr/bin/env bash
set -Eeuo pipefail

release_dir="$1"
version="$2"
helper="$(pwd)/scripts/release.mjs"
archive="$(node "$helper" archive "$release_dir" "$version")"
install_dir="$(mktemp -d)"
cleanup() {
    local status=$?
    trap - EXIT
    rm -rf -- "$install_dir"
    exit "$status"
}
trap cleanup EXIT

npm install --prefix "$install_dir" "$release_dir/$archive" --registry=https://registry.npmjs.org/ --no-audit --no-fund
cd "$install_dir"
mkdir -p fixture/src
printf '%s\n' '{"packageType":"Data","contentToPack":[{"source":"src/*","target":"DeeRules","contentType":"DEE"}]}' > fixture/cmfpackage.json
cat > fixture/src/Example.cs <<'CS'
class Example
{
    void Run(System.Collections.Generic.IEnumerable<dynamic> items)
    {
        foreach (var item in items)
        {
            item.Load();
        }
    }
}
CS
node node_modules/@criticalmanufacturing/codereview/run.js --version > installed-version.txt
node node_modules/@criticalmanufacturing/codereview/run.js scan \
    --repository-root "$install_dir/fixture" --roots src --output "$install_dir/findings.json"
node "$helper" smoke "$install_dir/findings.json" "$install_dir/installed-version.txt" "$version"
