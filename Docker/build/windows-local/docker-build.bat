@echo off

call docker-build-server.bat
call docker-build-authservice.bat
call docker-build-services.bat
call docker-build-staticfilesserver.bat