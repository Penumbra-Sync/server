@echo off
cd ..\..\..\
docker build -t darkarchon/mare-synchronos-staticfilesserver:latest . -f Docker\build\Dockerfile-MareSynchronosStaticFilesServer --no-cache --pull --force-rm
cd Docker\build\windows-local