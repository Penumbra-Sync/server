@echo off
cd ..\..\..\
docker build -t darkarchon/mare-synchronos-authservice:latest . -f Docker\build\Dockerfile-MareSynchronosAuthService --no-cache --pull --force-rm
cd Docker\build\windows-local