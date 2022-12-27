#!/bin/sh
docker build -t darkarchon/mare-synchronos-services:latest . -f ../Dockerfile-MareSynchronosServices-git --no-cache --pull --force-rm