#!/bin/sh
./docker-build-server.sh
./docker-build-authservice.sh
./docker-build-services.sh
./docker-build-staticfilesserver.sh