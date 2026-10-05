# Docker DNN experiment - ubuntu-vm

- **Run**: 2026-10-05 17:46 UTC
- **Host**: Ubuntu 24.04.5 LTS, 4 CPUs, 15.6 GB RAM
- **Kvm**: present
- **Guest**: Windows Server 2022 (evaluation) via dockurr/windows, 4 vCPU, 8 GB RAM
- **WindowsReadyAfter**: 6.7 min (first answer from the status site)
- **Summary**: 1 of 2 steps passed

| Step | Result | Minutes | Detail |
|---|---|---|---|
| Start the VM container (downloads and installs Windows Server 2022) | pass | 0.2 | started |
| Windows installed and provisioned (IIS, SQL Server Express, DNN unattended install) | FAIL | 12.5 |   370 s  Downloading DNN 10.3.3 /   388 s  Running DNN's unattended install /   449 s  FAILED: Cannot find path 'C:\inetpub\dnn\Install\DotNetNuke.install.config' because it does not exist. |

