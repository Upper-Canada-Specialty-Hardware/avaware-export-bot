#!/usr/bin/env bash
# Prints the next release version for a merge to main: the highest vX.Y.Z tag read from stdin (one per line, as
# `git tag --list` prints them) with its patch number plus one, or v0.1.0 when there is none.
# Tags of any other shape (release-2, v1.3, v2.0.0-rc1) are ignored. Versions compare as numbers: v0.10.0 > v0.9.9.
set -euo pipefail

latest=$(tr -d '\r' | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | sed 's/^v//' | sort -t. -k1,1n -k2,2n -k3,3n | tail -n 1 || true)

if [ -z "$latest" ]; then
  echo "v0.1.0"
  exit 0
fi

IFS=. read -r major minor patch <<< "$latest"
# 10# reads each part as decimal, so a part with a leading zero is not taken as octal.
echo "v$((10#$major)).$((10#$minor)).$((10#$patch + 1))"
