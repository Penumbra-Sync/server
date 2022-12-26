@echo off
cd ..\..\..\
docker build -t darkarchon/mare-synchronos-server:latest . -f Docker\build\Dockerfile-MareSynchronosServer --no-cache --pull --force-rm
cd Docker\build\windows-local