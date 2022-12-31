# Mare Synchronos Docker Setup
This is primarily aimed at developers who want to spin up their own local server for development purposes without having to spin up a VM.
Obligatory requires Docker to be installed on the machine.

There are two directories: `build` and `run`

## 1. build images
There is two ways to build the necessary docker images which are differentiated by the folders `-local` and `-git`  
- -local will run the image build against the current locally present sources  
- -git will run the image build against the latest git main commit  
It is possible to build all required images at once by running `docker-build.bat/sh` (Server, Servies, StaticFilesServer) or all 3 separately with `docker-build-<whatever>.bat/sh`

## 2. Configure ports + token
You should set up 2 environment variables that hold server specific configuration and open up ports.  
The default ports used through the provided configuration are `6000` for the main server and `6200` as well as `6201` for the files downloads.
Both ports should be open to your computer through your router if you wish to test this with clients.

Furthermore there are two environment variables `DEV_MARE_CDNURL` and `DEV_MARE_DISCORDTOKEN` which you are required to set.  
`DEV_MARE_CDNURL` should point to `http://<yourip or dyndns>:6200/cache/` and `DEV_MARE_DISCORDTOKEN` is an oauth token from a bot you need to create through the Discord bot portal. 
You should also set `DEV_MARE_CDNURL2` to `http://<yourip or dyndns>:6201/cache/`
It is enough to set them as User variables. The compose files refer to those environment variables to overwrite configuration settings for the Server and Services to set those respective values.  
It is also possible to set those values in the configuration.json files themselves.  
Without a valid Discord bot you will not be able to register accounts without fumbling around in the PostgreSQL database.

## 3. Run Mare Server
The run folder contains two major Mare configurations which is `standalone` and `sharded`.  
Both configurations default to port `6000` for the main server connection and `6200` for the files downloads. Sharded configuration additionally uses `6201` for downloads. No HTTPS.  
All `appsettings.json` configurations provided are extensive at the point of writing, note the differences between the shard configurations and the main servers respectively.  
They can be used as examples if you want to spin up your own servers otherwise.

The scripts to start the respective services are divided by name, the `daemon-start/stop` files use `compose up -d` to run it in the background and to be able to stop the containers as well.  
The respective docker-compose files lie in the `compose` folder. I would not recommend editing them unless you know what you are doing.  
All data (postgresql and files uploads) will be thrown into the `data` folder after startup.  
All logs from the mare services will be thrown into `logs`, divided by shard, where applicable.

The `standalone` configuration features PostgeSQL, Mare Server, Mare StaticFilesServer and Mare Services.  
The `sharded` configuration features PostgreSQL, Redis, HAProxy, Mare Server Main, 2 Mare Server Shards, Mare Services, Mare StaticFilesServer Main and 2 Mare StaticFilesServer Shards.  
Haproxy is set up that it takes the same ports as the `standalone` configuration and distributes the connections between the shards.  
In theory it should be possible to switch between the `standalone` and `sharded` configuration by shutting down one composition container and starting up the other. They share the same Database.