#!/bin/sh
docker build -t darkarchon/mare-synchronos-staticfilesserver:latest . -f ../Dockerfile-MareSynchronosStaticFilesServer-git --no-cache --pull --force-rm