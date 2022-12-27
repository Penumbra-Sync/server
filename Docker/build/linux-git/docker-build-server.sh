#!/bin/sh
docker build -t darkarchon/mare-synchronos-server:latest . -f ../Dockerfile-MareSynchronosServer-git --no-cache --pull --force-rm