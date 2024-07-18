#!/bin/sh
docker build -t darkarchon/mare-synchronos-authservice:latest . -f ../Dockerfile-MareSynchronosAuthService-git --no-cache --pull --force-rm