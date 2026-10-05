# Docker DNN experiment - ubuntu-vm

- **Run**: 2026-10-05 21:14 UTC
- **Host**: Ubuntu 24.04.5 LTS, 4 CPUs, 15.6 GB RAM
- **Kvm**: present
- **Guest**: Windows Server 2022 (evaluation) via dockurr/windows, 4 vCPU, 8 GB RAM
- **WindowsReadyAfter**: 6.1 min (first answer from the status site)
- **Stats**: dnn-vm-windows-1 cpu 2.73% mem 8.119GiB / 15.61GiB
- **Disk**: 11G	/mnt/dnn-vm
- **Summary**: 7 of 7 steps passed

| Step | Result | Minutes | Detail |
|---|---|---|---|
| Start the VM container (downloads and installs Windows Server 2022) | pass | 0.2 | started |
| Windows installed and provisioned (IIS, SQL Server Express, DNN unattended install) | pass | 13.3 |     0 s  Installing IIS with ASP.NET 4.8 /    59 s  IIS is up /    59 s  Installing SQL Server 2022 Express /   374 s  SQL Server Express is up /   375 s  Downloading DNN 10.3.3 /   392 s  Running DNN's unattended install /   450 s  DNN 10.3.3 installed /   490 s  First visit from inside the VM: HTTP 200 (/) /   490 s  Database: tabs = 14 /   490 s  Database: home tab = 21 /   490 s  Database: languages = 1 /   490 s  Database: skin = [G]Skins/Aperture/default.ascx /   493 s  READY |
| Load the home page from the Linux host (http://localhost:8080) | pass | 0.4 | HTTP 200 in 21.6 s |
| Sign in as the host | pass | 0.2 | signed in, Persona Bar shown |
| Install an extension (Google sign-in provider, Persona Bar API) | pass | 0 | HTTP 200: {"newPackageId":149,"success":true,"message":"","logs":[{"Type":"StartJob","Description":"Starting Installation"},{"Type":"Info","Description":"Starting Installation - DNN_GoogleAuthentication"},{"Typ |
| Restart the VM container (Windows reboots) - the site comes back | pass | 3 | back after 3 min |
| Resource use (docker stats) and the VM disk | pass | 0 | dnn-vm-windows-1 cpu 2.73% mem 8.119GiB / 15.61GiB / storage 11G	/mnt/dnn-vm |

