#!/usr/bin/env bash
# Пересобрать обзор целиком: скриншоты → index.html + PDF.
# Нужны: python3, playwright (chromium), pikepdf, pypdf, Pillow.
#   ./docs/overview/make.sh            — снять скриншоты и собрать
#   ./docs/overview/make.sh --no-shots — только собрать из готовых скриншотов
set -euo pipefail
cd "$(dirname "$0")"
if [[ "${1:-}" != "--no-shots" ]]; then
  python3 tools/capture.py
fi
python3 tools/build.py
