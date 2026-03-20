#!/usr/bin/env bash

set -euo pipefail

configuration="Release"
output_dir="nupkg"
bump_version="true"
bump_part="patch"
skip_install="false"

usage() {
    cat <<'EOF'
Usage: ./deploy-mrwho-cli.sh [options]

Options:
  --configuration <Debug|Release>  Build configuration. Default: Release
  --output-dir <path>              Package output directory. Default: nupkg
    --bump-version                   Increment the package version before packing (default)
    --no-bump-version                Keep the current project version when packing
  --bump-part <major|minor|patch>  Version part to increment. Default: patch
  --skip-install                   Only produce the package; do not reinstall the global tool
  --help                           Show this help
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --configuration)
            configuration="${2:?Missing value for --configuration}"
            shift 2
            ;;
        --output-dir)
            output_dir="${2:?Missing value for --output-dir}"
            shift 2
            ;;
        --bump-version)
            bump_version="true"
            shift
            ;;
        --no-bump-version)
            bump_version="false"
            shift
            ;;
        --bump-part)
            bump_part="${2:?Missing value for --bump-part}"
            shift 2
            ;;
        --skip-install)
            skip_install="true"
            shift
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            usage >&2
            exit 1
            ;;
    esac
done

case "$configuration" in
    Debug|Release) ;;
    *)
        echo "Invalid configuration: $configuration" >&2
        exit 1
        ;;
esac

case "$bump_part" in
    major|minor|patch) ;;
    *)
        echo "Invalid bump part: $bump_part" >&2
        exit 1
        ;;
esac

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_path="$repo_root/MrWhoOidc.Cli/MrWhoOidc.Cli.csproj"
output_path="$repo_root/$output_dir"
nuget_config_path="$repo_root/NuGet.config"
package_id="MrWhoOidc.Cli"
local_source_name="MrWhoOidcLocal"

if [[ ! -f "$project_path" ]]; then
    echo "Project file not found: $project_path" >&2
    exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "The dotnet CLI is required but was not found in PATH" >&2
    exit 1
fi

get_project_version() {
    grep -oPm1 '(?<=<Version>)[^<]+' "$1"
}

set_project_version() {
    local file_path="$1"
    local new_version="$2"
    sed -i -E "0,/<Version>[^<]+<\/Version>/{s//<Version>${new_version}<\/Version>/}" "$file_path"
}

get_bumped_version() {
    local version="$1"
    local part="$2"
    local major minor patch

    IFS='.' read -r major minor patch <<< "$version"

    if [[ -z "$major" || -z "$minor" || -z "$patch" ]]; then
        echo "Version '$version' is not in major.minor.patch format" >&2
        exit 1
    fi

    case "$part" in
        major)
            major=$((major + 1))
            minor=0
            patch=0
            ;;
        minor)
            minor=$((minor + 1))
            patch=0
            ;;
        patch)
            patch=$((patch + 1))
            ;;
    esac

    printf '%s\n' "$major.$minor.$patch"
}

version="$(get_project_version "$project_path")"

if [[ "$bump_version" == "true" ]]; then
    version="$(get_bumped_version "$version" "$bump_part")"
    set_project_version "$project_path" "$version"
    echo "Updated package version to $version"
else
    echo "Using package version $version"
fi

mkdir -p "$output_path"

echo "Packing $package_id ($version)..."
dotnet pack "$project_path" -c "$configuration" -o "$output_path" /p:Version="$version"

if [[ -f "$nuget_config_path" ]]; then
    echo "Local NuGet source '$local_source_name' is available via $nuget_config_path"
fi

if [[ "$skip_install" == "true" ]]; then
    echo "Package created in $output_path"
    exit 0
fi

if dotnet tool list --global | grep -iq '^mrwhooidc\.cli[[:space:]]'; then
    echo "Removing existing global tool installation..."
    dotnet tool uninstall --global "$package_id"
fi

echo "Installing $package_id $version from $output_path..."
dotnet tool install --global --add-source "$output_path" --version "$version" "$package_id"

echo "mrwho-cli deployed successfully."
echo "CLI package source: $output_path"