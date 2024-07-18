#!/bin/sh
docker build -t darkarchon/mare-synchronos-authservice:latest . -f ../Dockerfile-MareSynchronosAuthService --no-cache --pull --force-rm