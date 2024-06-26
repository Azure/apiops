#!/bin/bash

# Install Aspire
sudo /usr/share/dotnet/dotnet workload update --from-rollback-file "./.devcontainer/rollback.txt"
sudo /usr/share/dotnet/dotnet workload install aspire --from-rollback-file "./.devcontainer/rollback.txt"

# Install PowerShell modules
pwsh -Command "Install-Module -Name Az -Force
               Install-Module -Name Microsoft.Graph -Force"
