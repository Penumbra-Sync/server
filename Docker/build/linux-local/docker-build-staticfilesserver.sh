#!/bin/sh
cd ../../../
docker build -t darkarchon/mare-synchronos-staticfilesserver:latest . -f ../Dockerfile-MareSynchronosStaticFilesServer --no-cache --pull --force-rm
cd Docker/build/linux-local