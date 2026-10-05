# Docker DNN experiment - ubuntu-vm

- **Run**: 2026-10-05 15:00 UTC
- **Host**: Ubuntu 24.04.5 LTS, 4 CPUs, 15.6 GB RAM
- **Kvm**: present
- **Guest**: Windows Server 2022 (evaluation) via dockurr/windows, 4 vCPU, 8 GB RAM
- **WindowsReadyAfter**: 6.5 min (first answer from the status site)
- **Summary**: 1 of 2 steps passed

| Step | Result | Minutes | Detail |
|---|---|---|---|
| Start the VM container (downloads and installs Windows Server 2022) | pass | 0.1 | started |
| Windows installed and provisioned (IIS, SQL Server Express, DNN unattended install) | FAIL | 11.3 |   327 s  SQL Server Express is up /   328 s  Downloading DNN 10.3.3 /   345 s  FAILED: Method invocation failed because [System.String] does not contain a method named 'AppendChild'. |

