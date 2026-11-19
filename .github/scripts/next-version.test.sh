#!/usr/bin/env bash
# Checks next-version.sh against fixed tag lists. Run by the Build job of ci.yml on every pull request.
set -euo pipefail
script="$(dirname "$0")/next-version.sh"
failures=0

check() {
  local expected=$1 tags=$2 actual
  actual=$(printf '%b' "$tags" | bash "$script")
  if [ "$actual" = "$expected" ]; then
    echo "ok    $expected"
  else
    echo "FAIL  expected $expected, got $actual, for tags: $(printf '%b' "$tags" | tr '\n' ' ')"
    failures=$((failures + 1))
  fi
}

check v0.1.0 ''                                         # no tags yet: the first release
check v0.1.0 'release-2\nv1.3\nv2.0.0-rc1\n'            # no tag of the right shape counts
check v0.1.1 'v0.1.0\n'
check v0.10.1 'v0.9.9\nv0.10.0\n'                       # numeric order, not text order
check v0.10.1 'v0.10.0\nv0.9.9\n'                       # input order doesn't matter
check v1.2.4 'v1.2.3\nrelease-2\nv1.3\nv2.0.0-rc1\n'    # other shapes are ignored
check v0.2.1 'v0.1.0\nv0.1.7\nv0.2.0\n'                 # a manual minor bump is continued from
check v1.0.1 'v0.9.12\nv1.0.0\n'
check v0.1.9 'v0.1.08\n'                                # a leading zero is decimal, not octal
check v0.1.1 'v0.1.0\r\n'                               # Windows line endings

if [ "$failures" -gt 0 ]; then
  echo "$failures case(s) failed"
  exit 1
fi
echo "all cases passed"
