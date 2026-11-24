#!/usr/bin/env bash
set -Eeuo pipefail
plugin_dir="$1"
version="$2"
helper="$plugin_dir/scripts/package.mjs"
release_dir="$plugin_dir/release"
archive="$(node "$helper" archive "$release_dir" "$version" "${3:-}")"
install_dir="$(mktemp -d)"
trap 'rm -rf -- "$install_dir"' EXIT
npm install --prefix "$install_dir" "$release_dir/$archive" --registry=https://registry.npmjs.org/ --no-audit --no-fund
cd "$install_dir"
node node_modules/@criticalmanufacturing/audit/run.js --version > installed-version.txt
node node_modules/@criticalmanufacturing/audit/run.js --help > installed-help.txt
node "$helper" smoke installed-help.txt installed-version.txt "$version"
