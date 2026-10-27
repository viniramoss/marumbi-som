#!/usr/bin/env bash
# Instala o ffmpeg do Ubuntu para os testes do Hub no CI. Uso: instalar-ffmpeg.sh <pasta do cache do Actions>
#
# O espelho do apt às vezes trava (27/10/2026: três tentativas de 6 min esgotadas, no Ubuntu 24.04). Por isso:
# 1. com as listas e os pacotes da última instalação restaurados do cache do Actions, instala sem rede;
# 2. senão, apt pela rede só por IPv4, com tempo curto por conexão e até 3 tentativas;
# 3. depois de instalar pela rede, copia listas e pacotes para a pasta do cache e pede ao workflow para salvá-la.
# Falhas viram anotação (::warning::): o log do job nem sempre está acessível, as anotações sim.
set -uo pipefail

cache="${1:?informe a pasta do cache}"
command -v ffmpeg >/dev/null && command -v ffprobe >/dev/null && exit 0

if compgen -G "$cache/lists/*_Packages*" >/dev/null && compgen -G "$cache/archives/*.deb" >/dev/null; then
  sudo cp "$cache"/lists/* /var/lib/apt/lists/
  sudo cp "$cache"/archives/*.deb /var/cache/apt/archives/
  if sudo apt-get install -y --no-install-recommends --no-download ffmpeg; then
    echo "ffmpeg instalado com os pacotes do cache, sem rede."
    exit 0
  fi
  echo "::warning::Os pacotes do ffmpeg no cache não serviram; instalando pela rede."
fi

apt_opts=(-o Acquire::ForceIPv4=true -o Acquire::Retries=5 -o Acquire::http::Timeout=20 -o Acquire::https::Timeout=20
  -o APT::Keep-Downloaded-Packages=true)
for i in 1 2 3; do
  timeout 180 sudo apt-get "${apt_opts[@]}" update
  code=$?
  if [ "$code" -ne 0 ]; then
    echo "::warning::apt-get update falhou ou travou (tentativa $i de 3, código $code, $(lsb_release -ds))."
  else
    timeout 300 sudo apt-get "${apt_opts[@]}" install -y --no-install-recommends ffmpeg
    code=$?
    if [ "$code" -eq 0 ]; then
      mkdir -p "$cache/lists" "$cache/archives"
      sudo find /var/lib/apt/lists -maxdepth 1 -type f ! -name lock -exec cp -t "$cache/lists" {} +
      sudo find /var/cache/apt/archives -maxdepth 1 -name '*.deb' -exec cp -t "$cache/archives" {} +
      sudo chown -R "$(id -u):$(id -g)" "$cache"
      [ -n "${GITHUB_OUTPUT:-}" ] && echo "salvar=true" >> "$GITHUB_OUTPUT"
      exit 0
    fi
    echo "::warning::apt-get install ffmpeg falhou ou travou (tentativa $i de 3, código $code, $(lsb_release -ds))."
  fi
  sudo dpkg --configure -a || true
  sleep 15
done
exit 1
