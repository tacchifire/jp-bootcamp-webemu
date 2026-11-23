#!/usr/bin/env bash
# source（. emu/setup.sh）された場合は、下の set -euo pipefail や変数が呼び出し元の
# シェルに残らないよう、別プロセスの bash で実行し直し、その終了コードを返す。
if [ "${BASH_SOURCE[0]}" != "$0" ]; then
  printf '注記: emu/setup.sh は source せず ./emu/setup.sh で実行してください。今回は別プロセスで実行します。\n' >&2
  bash -- "${BASH_SOURCE[0]:-$0}" || return $?
  return 0
fi
set -euo pipefail

# 前提チェック：setup が使う git、curl、tar、sha256sum、python3 と、python3 で venv を
# 作れること（Debian／Ubuntu では python3.X-venv が無いと失敗する）を確かめる。
# 不足があれば apt-get と sudo で自動インストールを試み、入らなければ不足分と
# 手動の導入方法を表示して止まる。
prereq_missing=()
prereq_packages=()

check_prereqs() {
  local cmd tmp
  prereq_missing=()
  for cmd in git curl tar sha256sum python3; do
    command -v "$cmd" >/dev/null 2>&1 || prereq_missing+=("$cmd")
  done
  if ! command -v python3 >/dev/null 2>&1; then
    prereq_missing+=(venv)
  elif ! tmp="$(mktemp -d 2>/dev/null)"; then
    printf 'エラー: 一時ディレクトリを作れないため、python3 の venv 作成を確認できません。\n' >&2
    exit 1
  else
    python3 -m venv "$tmp/venv" >/dev/null 2>&1 || prereq_missing+=(venv)
    rm -rf -- "$tmp"
  fi
}

# venv 用は、python3 の版に合う python3.X-venv が apt の候補にあればそれ、無ければ python3-venv。
venv_package() {
  local ver policy
  if ver="$(python3 -c 'import sys; print("%d.%d" % sys.version_info[:2])' 2>/dev/null)" &&
    policy="$(LC_ALL=C apt-cache policy "python${ver}-venv" 2>/dev/null)" &&
    [[ "$policy" == *"Candidate:"* && "$policy" != *"Candidate: (none)"* ]]; then
    printf 'python%s-venv\n' "$ver"
  else
    printf 'python3-venv\n'
  fi
}

resolve_prereq_packages() {
  local item
  prereq_packages=()
  for item in "${prereq_missing[@]}"; do
    case "$item" in
      sha256sum) prereq_packages+=(coreutils) ;;
      venv) prereq_packages+=("$(venv_package)") ;;
      *) prereq_packages+=("$item") ;;
    esac
  done
}

print_prereq_missing() {
  local item
  printf '前提として必要な次のものが不足しています:\n' >&2
  for item in "${prereq_missing[@]}"; do
    [ "$item" != venv ] || item='python3 の venv 作成機能（python3 -m venv）'
    printf '  - %s\n' "$item" >&2
  done
}

stop_for_prereqs() {
  printf '%ssetup を中止します。\n' "$1" >&2
  print_prereq_missing
  printf '手動で導入する場合（Debian／Ubuntu）:\n  sudo apt install -y %s\n' "${prereq_packages[*]}" >&2
  printf '導入してから、もう一度 ./emu/setup.sh を実行してください。\n' >&2
  exit 1
}

check_prereqs
if [ "${#prereq_missing[@]}" -gt 0 ]; then
  resolve_prereq_packages
  if ! command -v apt-get >/dev/null 2>&1 || ! command -v sudo >/dev/null 2>&1; then
    stop_for_prereqs 'apt-get または sudo が見つからないため、自動インストールはできません。'
  fi
  print_prereq_missing
  printf '次のパッケージを apt で自動インストールします: %s\n' "${prereq_packages[*]}" >&2
  printf '実行するコマンド:\n  sudo apt-get update\n  sudo apt-get install -y %s\n' "${prereq_packages[*]}" >&2
  printf '（sudo のパスワード入力を求められる場合があります）\n' >&2
  if ! { sudo apt-get update && sudo apt-get install -y "${prereq_packages[@]}"; }; then
    stop_for_prereqs '自動インストールに失敗しました。'
  fi
  check_prereqs
  if [ "${#prereq_missing[@]}" -gt 0 ]; then
    resolve_prereq_packages
    stop_for_prereqs '自動インストールの後も不足が残っています。'
  fi
  printf '前提がそろいました。setup を続けます。\n' >&2
fi

EMU_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
RUN="$EMU_DIR/run"
RENODE_VERSION=1.17.0
RENODE_TGZ_SHA256=4ba7c68b59e2447f188ef4b4b112fcccd2802582c460beedceb63b84e5605a5f
FIRMWARE_BIN_SHA256=6adeee7be2d3987b47a8a4e9e47aca214ae6e29667811ff877bdfc17893549b8
UPSTREAM_REPO=https://github.com/tacchifire/JP-Bootcamp.git
UPSTREAM_COMMIT=1079ae9fcad20323161377be21e1db7a78f9f202
UP="$RUN/upstream/JP-Bootcamp"
mkdir -p "$RUN/logs" "$RUN/home" "$RUN/renode" "$RUN/upstream"
rm -f "$RUN/.setup-done"

if [ ! -f "$RUN/renode/renode_${RENODE_VERSION}-portable/renode" ]; then
  archive="$RUN/renode/renode-${RENODE_VERSION}.linux-portable.tar.gz"
  curl --fail --location --retry 3 \
    "https://github.com/renode/renode/releases/download/v${RENODE_VERSION}/renode-${RENODE_VERSION}.linux-portable.tar.gz" \
    --output "$archive"
  printf '%s  %s\n' "$RENODE_TGZ_SHA256" "$archive" | sha256sum --check -
  tar -xzf "$archive" -C "$RUN/renode"
fi

if [ ! -d "$UP/.git" ]; then
  git clone "$UPSTREAM_REPO" "$UP"
  git -C "$UP" checkout "$UPSTREAM_COMMIT"
elif [ "$(git -C "$UP" rev-parse HEAD)" != "$UPSTREAM_COMMIT" ]; then
  git -C "$UP" checkout "$UPSTREAM_COMMIT"
fi

if [ ! -f "$RUN/venv/bin/python" ]; then
  python3 -m venv "$RUN/venv"
fi
# Also retry installation if an earlier setup stopped after creating the venv.
if [ ! -f "$RUN/venv/.requirements-installed" ]; then
  "$RUN/venv/bin/pip" install -r "$UP/requirements.txt"
  touch "$RUN/venv/.requirements-installed"
fi

python3 "$EMU_DIR/uf2conv.py" \
  "$UP/firmware/binaries/flatsat-course-noradio-v1.0.5.uf2" "$RUN/firmware.bin"
printf '%s  %s\n' "$FIRMWARE_BIN_SHA256" "$RUN/firmware.bin" | sha256sum --check -
touch "$RUN/.setup-done"
printf 'セットアップが完了しました。./start-emu.sh で起動できます。\n'
