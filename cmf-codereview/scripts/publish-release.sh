#!/usr/bin/env bash
set -Eeuo pipefail

if [[ "${CODE_REVIEW_PUBLISH_ENABLED:-false}" != true ]]; then
    echo "Publishing is blocked until the RepoFlow upload limit is resolved." >&2
    exit 1
fi
if [[ -z "${NODE_AUTH_TOKEN:-}" ]]; then
    echo "REPOFLOW_NPM_TOKEN must be configured on the release environment." >&2
    exit 1
fi

spec="@criticalmanufacturing/codereview@$RELEASE_VERSION"
metadata_file="$(mktemp)"
tags_file="$(mktemp)"
cleanup() {
    local status=$?
    trap - EXIT
    rm -f -- "$metadata_file" "$tags_file"
    exit "$status"
}
trap cleanup EXIT

case "$RELEASE_CHANNEL" in
    next)
        archive="$(node scripts/release.mjs archive release "$RELEASE_VERSION" "$GITHUB_SHA")"
        lookup_status=0
        npm view "$spec" version --json --registry="$RELEASE_REGISTRY" > "$metadata_file" || lookup_status=$?
        if (( lookup_status == 0 )); then
            echo "This version already exists. Choose a new version; published packages are not overwritten." >&2
            exit 1
        fi
        node scripts/release.mjs unpublished "$metadata_file"
        npm publish "release/$archive" --tag next --registry="$RELEASE_REGISTRY"
        npm view "$spec" version dist.integrity --json --registry="$RELEASE_REGISTRY" > "$metadata_file"
        expected_integrity="$(node -p 'JSON.parse(require("fs").readFileSync("release/manifest.json", "utf8")).integrity')"
        node scripts/release.mjs registry "$metadata_file" "$RELEASE_VERSION" "$expected_integrity"
        ;;
    latest)
        npm view "$spec" version dist.integrity --json --registry="$RELEASE_REGISTRY" > "$metadata_file"
        npm view @criticalmanufacturing/codereview dist-tags --json --registry="$RELEASE_REGISTRY" > "$tags_file"
        node scripts/release.mjs promotion "$metadata_file" "$tags_file" "$RELEASE_VERSION"
        npm dist-tag add "$spec" latest --registry="$RELEASE_REGISTRY"
        npm view @criticalmanufacturing/codereview dist-tags --json --registry="$RELEASE_REGISTRY" > "$tags_file"
        node -e 'const fs=require("fs"); const tags=JSON.parse(fs.readFileSync(process.argv[1],"utf8")); if(tags.latest!==process.argv[2]) throw new Error("latest tag verification failed");' "$tags_file" "$RELEASE_VERSION"
        ;;
    *)
        echo "Release channel must be next or latest." >&2
        exit 1
        ;;
esac
printf 'Released %s with tag %s\n' "$spec" "$RELEASE_CHANNEL"
