# Docker DNN experiment - wsl-ubuntu

- **Run**: 2026-10-05 21:06 UTC
- **Host**: Ubuntu 24.04 LTS, 24 CPUs, 30.1 GB RAM
- **Kvm**: present
- **Guest**: Windows Server 2022 (evaluation) via dockurr/windows, 4 vCPU, 8 GB RAM
- **WindowsReadyAfter**: 5.3 min (first answer from the status site)
- **Stats**: dnn-vm-windows-1 cpu 1.75% mem 8.074GiB / 30.05GiB
- **Disk**: 11G	/var/lib/dnn-vm-test
- **Summary**: 6 of 7 steps passed

| Step | Result | Minutes | Detail |
|---|---|---|---|
| Start the VM container (downloads and installs Windows Server 2022) | pass | 0 | started |
| Windows installed and provisioned (IIS, SQL Server Express, DNN unattended install) | pass | 10.3 |     0 s  Installing IIS with ASP.NET 4.8 /    52 s  IIS is up /    52 s  Installing SQL Server 2022 Express /   242 s  SQL Server Express is up /   243 s  Downloading DNN 10.3.3 /   253 s  Running DNN's unattended install /   321 s  DNN 10.3.3 installed /   340 s  First visit from inside the VM: HTTP 200 (/) /   340 s  Database: tabs = 14 /   340 s  Database: home tab = 21 /   340 s  Database: languages = 1 /   340 s  Database: skin = [G]Skins/Aperture/default.ascx /   342 s  READY |
| Load the home page from the Linux host (http://localhost:8080) | pass | 0.2 | HTTP 200 in 12.4 s |
| Sign in as the host | pass | 0.1 | signed in, Persona Bar shown |
| Install an extension (Google sign-in provider, Persona Bar API) | pass | 0 | HTTP 200: {"newPackageId":149,"success":true,"message":"","logs":[{"Type":"StartJob","Description":"Starting Installation"},{"Type":"Info","Description":"Starting Installation - DNN_GoogleAuthentication"},{"Typ |
| Restart the VM container (Windows reboots) - the site comes back | FAIL | 20.4 | The site didn't come back within 20 minutes. |
| Resource use (docker stats) and the VM disk | pass | 0 | dnn-vm-windows-1 cpu 1.75% mem 8.074GiB / 30.05GiB / storage 11G	/var/lib/dnn-vm-test |

