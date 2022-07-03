# Installing Mare Server on Ubuntu 20.04 Server LTS 

## Important
- **You _will_ need a valid Certificate for the server.**
- Set one up using LetsEncrypt or use the one provided by your hoster
- The server provided is only guaranteed to run under Ubuntu 20.04 Server LTS. For anything else, you are on your own. The server is provided as a standalone .NET application which does not require .NET Core to be installed.

## Copy files over
- Connect via SCP and copy over all files to some directory
- The directory will need to be writeable by the user

## Install MSSQL Server 2019 CU
- Import GPG keys
    ```sh
    sudo curl https://packages.microsoft.com/keys/microsoft.asc | sudo apt-key add -
    ```
- Add repository
    ```sh
    sudo add-apt-repository "$(curl https://packages.microsoft.com/config/ubuntu/20.04/mssql-server-2019.list)"
    ```
- Install SQL Server 2019
    ```
    sudo apt-get update
    sudo apt-get install -y mssql-server
    ```
- Run configuration
    ```
    sudo /opt/mssql/bin/mssql-conf setup
    ```
- Install "3) Express"
- Set a password
- Verify server is running
    ```
    systemctl status mssql-server --no-pager
    ```
- Optional: set up a separate user for Mare Synchronos
  - I'll let you figure that out yourself
  - The user will need database creation rights

## Configure Mare Server
- open provided appsettings.json
- edit `DefaultConnection` to `"Server=localhost;User=sa;Password=<sa password>;Database=mare;MultipleActiveResultSets=true"`
  - if you created a separate user for mare on the SQL Server, specify the username and password here
- edit `CacheDirectory` and set it to a path where the file cache is supposed to be located. i.e. `/home/<user>/servercache`
  - you will also need to create that folder
- optional: set Port under edit `Url` and change the `+:5000` to `+:<your port>`

  - Set up `Certificate`
  - Set `Path` to the certificate file path
  - Set `Password` to the password of the certificate
    - If the certificate file is split in private key and public, set `KeyPath` to the private key file
  - Delete all unused keys from `Certificate`

## Set up Mare Synchonos Server as a Service
- create new file `/etc/systemd/system/MareSynchronosServer.service` with following contents
    ```
    [Unit]
    Description=Mare Synchronos Service

    [Service]
    WorkingDirectory=<path to server files>
    ExecStart=<path to server files>/MareSynchronosServer
    SysLogIdentifier=MareSynchronosServer
    User=<the user to run the service with>
    Restart=always
    RestartSec=5

    [Install]
    WantedBy=multi-user.target
    ```
- Reload SystemD daemon
    ```
    sudo systemctl daemon-reload
    ```
- Enable the service with
  ```
  sudo systemctl enable MareSynchronosServer
  ```
- Start the service with
    ```
    sudo systemctl start MareSynchronosServer
    ```
- Log in ingame and add a custom server within the Mare Synchronos Plugin configuration under the address `wss://<your server ip>:<your server port>`
- That should be it and your server ready to go and running