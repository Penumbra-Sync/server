# Mare Synchronos Docker Setup
This is primarily aimed at developers who want to spin up their own local server for development purposes without having to spin up a VM.
Obligatory requires Docker to be installed on the machine.

There are two directories: `build` and `run`

## build
Used to build the Docker images that are required to run.

There is two ways to build them which is differentiated by folders `-local` and `-git`
- -local will run the image build against the current locally present sources
- -git will run the image build against the latest git main commit
It is possible to build all required images at once by running `docker-build.bat/sh` (Server, Servies, StaticFilesServer) or all 3 separately with `docker-build-whatever.bat/sh`

## run
This folder contains the environment to run Mare using Docker.

It contains two major configurations which is `standalone` and `sharded`.
Both configurations default to port `6000` for the main server connection and `6200` for the files downloads. No HTTPS.
Services requires a Discord Bot Token, for that you will have to make a Discord bot and copy the token in the `config/standalone/services-standalone.json` in the respective `DiscordBotToken` field. **Don't commit that file at any time.**
All configurations provided are extensive at the point of writing, note the differences between the shard configurations and the main servers respectively.
They can be used as examples if you want to spin up your own servers otherwise.

The scripts to start the respective services are divided by name, the `daemon-start/stop` files use `compose up -d` to run it in the background and to be able to stop the containers as well.
The respective docker-compose files lie in the `compose` folder. I would not recommend editing them unless you know what you are doing.
All data (postgresql and files uploads) will be thrown into the `data` folder after startup.
All logs from the mare services will be thrown into `logs`, divided by shard, where applicable.

The `standalone` configuration features PostgeSQL, Mare Server, Mare StaticFilesServer and Mare Services
The `sharded` configuration features PostgreSQL, Redis, HAProxy, Mare Server Main, 2 Mare Server Shards, Mare Services, Mare StaticFilesServer Main and 2 Mare StaticFilesServer Shards.
Haproxy is set up that it takes the same ports as the `standalone` configuration and distributes the connections between the shards.
In theory it should be possible to switch between the `standalone` and `sharded` configuration by shutting down one composition container and starting up the other. They share the same Database.