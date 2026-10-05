# Docker DNN experiment - ubuntu

- **Run**: 2026-10-05 14:42 UTC
- **Host**: Ubuntu 24.04.5 LTS
- **Kernel**: Linux 6.17.0-1022-azure
- **Docker**: server 28.0.4 (linux/amd64)
- **WindowsImageOnLinux**: refused
- **Kvm**: present

| Step | Expected | Result | Seconds | Detail |
|---|---|---|---|---|
| Pull the Windows image DNN needs (aspnet 4.8.1, Server Core ltsc2022) | fails: no linux/amd64 variant | done | 0 | exit 1: eb3bb45ca587: Waiting image operating system "windows" cannot be used on this platform: operating system is not supported |
| Ask for the Windows platform explicitly (--platform windows/amd64) | fails: the Linux kernel can't run it | done | 0 | exit 1: ltsc2022: Pulling from windows/nanoserver operating system is not supported |
| SQL Server 2022 in a Linux container (what DNN's database could use on Linux/macOS) | works | done | 22 | Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64) |
| Hardware virtualization for a Windows VM (/dev/kvm) | present on hosts that could run Windows in QEMU/KVM | done | 0 | /dev/kvm present; CPU flags vmx/svm: svm |

