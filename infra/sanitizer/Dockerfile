# syntax=docker/dockerfile:1
# E4-F04 — imagem do sanitizer (E4-D1, E4-D2, S-C01): ffmpeg MÍNIMO compilado + um script de shell. Roda sem rede (network_mode: none), sem segredo,
# read_only, uid 65532, e recodifica o áudio enviado para MP3. É o contêiner mais exposto a parser do sistema: a superfície de demuxer, decoder, filtro e
# protocolo é cortada na compilação, não por flag em tempo de execução.
# Contexto de build = RAIZ do repositório:  docker build -f infra/sanitizer/Dockerfile -t webradio-sanitizer:local .

# ---- 1) compilação: fontes por versão e SHA-256 (conferidos contra o site oficial na criação; o build falha se o hash mudar) ----
FROM alpine@sha256:f59abe656b692d901e184be3445e084786602108599d2a51d229c0f5a48c71bf AS build
ARG LAME_VERSION=3.100
ARG LAME_SHA256=ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e
ARG FFMPEG_VERSION=9.0.2
ARG FFMPEG_SHA256=8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e
RUN apk add --no-cache build-base curl xz
WORKDIR /src
RUN curl -fsSL -o lame.tar.gz "https://downloads.sourceforge.net/project/lame/lame/${LAME_VERSION}/lame-${LAME_VERSION}.tar.gz" \
 && echo "${LAME_SHA256}  lame.tar.gz" | sha256sum -c - \
 && tar xzf lame.tar.gz \
 && cd "lame-${LAME_VERSION}" \
 && ./configure --prefix=/opt/lame --disable-shared --enable-static --disable-frontend --disable-gtktest \
 && make -j"$(nproc)" && make install
RUN curl -fsSL -o ffmpeg.tar.xz "https://ffmpeg.org/releases/ffmpeg-${FFMPEG_VERSION}.tar.xz" \
 && echo "${FFMPEG_SHA256}  ffmpeg.tar.xz" | sha256sum -c - \
 && tar xJf ffmpeg.tar.xz
# E4-D2: --disable-everything e só o necessário. Demuxers mp3, ogg, wav e flac; parsers mpegaudio e flac (+ vorbis e opus: o ogg não separa os pacotes sem eles);
# decoders mp3, vorbis, opus, flac e pcm; filtros aresample, aformat e anull (+ abuffer e abuffersink: o ffmpeg não monta o grafo sem eles); encoder libmp3lame; muxer
# mp3; protocolo SÓ file (o `pipe` do S-C01 item 5 fica de fora de propósito: o protocolo é por arquivo). Sem rede, sem autodetect, sem asm, estático.
RUN cd "ffmpeg-${FFMPEG_VERSION}" \
 && ./configure --prefix=/opt/ffmpeg \
      --disable-everything --disable-autodetect --disable-network --disable-doc --disable-debug --disable-ffplay --disable-asm \
      --enable-static --disable-shared --enable-ffmpeg --enable-ffprobe \
      --enable-protocol=file \
      --enable-demuxer=mp3,ogg,wav,flac \
      --enable-parser=mpegaudio,flac,vorbis,opus \
      --enable-decoder=mp3,mp3float,vorbis,opus,flac,pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le \
      --enable-filter=aresample,aformat,anull,abuffer,abuffersink \
      --enable-libmp3lame --enable-encoder=libmp3lame --enable-muxer=mp3 \
      --extra-cflags="-I/opt/lame/include" --extra-ldflags="-L/opt/lame/lib -static" --extra-libs="-lm" \
 && make -j"$(nproc)" && make install

# ---- 2) execução: Alpine por digest (sh, timeout e stat do busybox) + os dois binários estáticos e o script ----
FROM alpine@sha256:f59abe656b692d901e184be3445e084786602108599d2a51d229c0f5a48c71bf
# Correções de segurança da base (o trivy falha em HIGH corrigível nesta imagem): sobe os pacotes já presentes para a revisão corrente do Alpine.
RUN apk upgrade --no-cache
COPY --from=build /opt/ffmpeg/bin/ffmpeg /opt/ffmpeg/bin/ffprobe /usr/local/bin/
COPY infra/sanitizer/sanitizar.sh /usr/local/bin/sanitizar.sh
RUN chmod 0555 /usr/local/bin/sanitizar.sh /usr/local/bin/ffmpeg /usr/local/bin/ffprobe
# O Worker e o sanitizer rodam com o mesmo uid (65532) e se encontram no tmpfs /jobs (E4-D1).
USER 65532:65532
WORKDIR /jobs
ENTRYPOINT ["/bin/sh", "/usr/local/bin/sanitizar.sh"]
